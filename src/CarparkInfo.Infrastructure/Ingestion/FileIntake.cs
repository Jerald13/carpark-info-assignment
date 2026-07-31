using System.Text.Json;
using CarparkInfo.Application.Ingestion;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>
/// File-system implementation of the inbox / processed / quarantine lifecycle.
/// </summary>
public sealed class FileIntake : IFileIntake
{
    private static readonly JsonSerializerOptions ReportOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the intake.</summary>
    /// <param name="timeProvider">Clock, used to stamp archived file names.</param>
    public FileIntake(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <inheritdoc />
    public IReadOnlyList<string> DiscoverPending(IngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(options.InboxDirectory))
        {
            return [];
        }

        return [.. new DirectoryInfo(options.InboxDirectory)
            .EnumerateFiles()
            .Where(f => f.Extension is ".csv" or ".json")
            .OrderBy(f => f.CreationTimeUtc)
            .Select(f => f.FullName)];
    }

    /// <inheritdoc />
    public Task MoveToProcessedAsync(string filePath, IngestionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Move(filePath, options.ProcessedDirectory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task MoveToQuarantineAsync(string filePath, IngestionOptions options,
        IngestionResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(result);

        var quarantined = Move(filePath, options.QuarantineDirectory);

        // The sidecar report is what lets an operator fix the source file without reading a log:
        // every defect, with its line number, field and the offending text.
        var report = new
        {
            file = Path.GetFileName(filePath),
            quarantinedAt = _timeProvider.GetUtcNow(),
            status = result.Status.ToString(),
            summary = result.Summary,
            jobRunId = result.JobRunId,
            counts = result.Counts,
            defects = result.Defects.Select(d => new
            {
                line = d.LineNumber,
                carParkNo = d.CarParkNo,
                field = d.FieldName,
                code = d.ErrorCode,
                severity = d.Severity.ToString(),
                message = d.Message,
                rawLine = d.RawLine,
            }),
        };

        await File.WriteAllTextAsync(
            quarantined + ".error.json",
            JsonSerializer.Serialize(report, ReportOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private string Move(string filePath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        var name = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture);

        var destination = Path.Combine(targetDirectory, $"{name}.{stamp}{extension}");

        File.Move(filePath, destination, overwrite: true);

        return destination;
    }
}
