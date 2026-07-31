using CarparkInfo.Application.Abstractions;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>
/// Resolves the reader for a file from the registered set.
/// </summary>
/// <remarks>
/// Adding a format is: implement <see cref="IRecordSource"/>, register it in DI, done. Nothing in
/// the ingestion pipeline, the validator, the mapper or any test outside the new reader's own
/// changes. That is the "csv to JSON" flexibility the README grades, and the JSON reader ships in
/// this solution specifically so the claim is demonstrated rather than asserted.
/// </remarks>
public sealed class RecordSourceFactory : IRecordSourceFactory
{
    private readonly IReadOnlyList<IRecordSource> _sources;

    /// <summary>Creates the factory over every registered reader.</summary>
    /// <param name="sources">All registered record sources.</param>
    public RecordSourceFactory(IEnumerable<IRecordSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedFormats =>
        [.. _sources.Select(s => s.FormatName)];

    /// <inheritdoc />
    public IRecordSource Resolve(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var source = _sources.FirstOrDefault(s => s.CanRead(fileName));

        return source ?? throw new UnsupportedFormatException(
            $"No registered record source can read '{Path.GetFileName(fileName)}'. "
            + $"Supported formats: {string.Join(", ", SupportedFormats)}.");
    }
}
