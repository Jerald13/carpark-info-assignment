using CarparkInfo.Application.Abstractions;
using CarparkInfo.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Application.Ingestion;

/// <summary>Counts describing what a run did.</summary>
/// <param name="Read">Rows read from the file.</param>
/// <param name="Inserted">Carparks that did not previously exist.</param>
/// <param name="Updated">Carparks whose content changed.</param>
/// <param name="Unchanged">Carparks whose hash matched, so no write occurred.</param>
/// <param name="Deactivated">Carparks absent from a snapshot.</param>
/// <param name="Rejected">Rows rejected by validation.</param>
public readonly record struct IngestionCounts(
    int Read, int Inserted, int Updated, int Unchanged, int Deactivated, int Rejected)
{
    /// <summary>An empty set of counts.</summary>
    public static IngestionCounts Empty => default;
}

/// <summary>The outcome of one ingestion attempt.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="JobRunId">The run's id, when one was created.</param>
/// <param name="Counts">What the run did.</param>
/// <param name="Defects">Every defect found.</param>
/// <param name="Summary">A human-readable outcome.</param>
public sealed record IngestionResult(
    JobRunStatus Status,
    int? JobRunId,
    IngestionCounts Counts,
    IReadOnlyList<RecordDefect> Defects,
    string Summary)
{
    /// <summary>Whether the catalogue was actually updated.</summary>
    public bool Succeeded => Status == JobRunStatus.Succeeded;
}

/// <summary>Configuration for the ingestion job.</summary>
public sealed class IngestionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ingestion";

    /// <summary>
    /// How to interpret absence from the file.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="IngestionMode.Delta"/> because the README describes the feed as a
    /// "daily delta file". The supplied sample is in fact a full inventory, so this is a genuine
    /// ambiguity - and guessing snapshot would deactivate 2,178 carparks on a three-row delta.
    /// </remarks>
    public IngestionMode Mode { get; set; } = IngestionMode.Delta;

    /// <summary>Rows buffered before each staging write.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// The largest fraction of the catalogue snapshot mode may deactivate before aborting.
    /// </summary>
    public double MaximumDeactivationRatio { get; set; } = 0.05;

    /// <summary>Reprocess a file even if it has already been ingested successfully.</summary>
    public bool Force { get; set; }

    /// <summary>Where the provider drops files.</summary>
    public string InboxDirectory { get; set; } = "intake/inbox";

    /// <summary>Where successfully ingested files are moved.</summary>
    public string ProcessedDirectory { get; set; } = "intake/processed";

    /// <summary>Where files that could not be ingested are moved.</summary>
    public string QuarantineDirectory { get; set; } = "intake/quarantine";

    /// <summary>How stale the last success may be before readiness degrades.</summary>
    public TimeSpan FreshnessSla { get; set; } = TimeSpan.FromHours(26);
}

/// <summary>
/// Orchestrates one ingestion run: discover, claim, stream, validate, stage, swap.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only implementation of ingestion in the solution.</b> The scheduled hosted
/// service, the CLI and the admin endpoint are three thin adapters that all call
/// <see cref="IngestAsync"/>; none of them contains logic. That is what keeps the core testable
/// without a host, and what stops the three paths drifting apart.
/// </para>
/// <para>
/// It lives in the Application layer and names no EF Core type. Everything it touches is a port -
/// which is what makes "changing of data access technology" a new adapter rather than a rewrite.
/// </para>
/// </remarks>
public sealed class CarparkIngestionService
{
    private readonly IRecordSourceFactory _sourceFactory;
    private readonly RecordValidator _validator;
    private readonly IJobRunStore _jobRuns;
    private readonly ICarparkStagingStore _staging;
    private readonly ILookupResolver _lookups;
    private readonly IIngestionContext _context;
    private readonly ILogger<CarparkIngestionService> _logger;

    /// <summary>Creates the ingestion service.</summary>
    /// <param name="sourceFactory">Resolves the reader for a file's format.</param>
    /// <param name="validator">Validates source rows.</param>
    /// <param name="jobRuns">Run lifecycle and audit.</param>
    /// <param name="staging">Staging and the atomic merge.</param>
    /// <param name="lookups">Lookup code resolution.</param>
    /// <param name="context">Clock and host identity.</param>
    /// <param name="logger">Structured logging.</param>
    public CarparkIngestionService(
        IRecordSourceFactory sourceFactory,
        RecordValidator validator,
        IJobRunStore jobRuns,
        ICarparkStagingStore staging,
        ILookupResolver lookups,
        IIngestionContext context,
        ILogger<CarparkIngestionService> logger)
    {
        _sourceFactory = sourceFactory;
        _validator = validator;
        _jobRuns = jobRuns;
        _staging = staging;
        _lookups = lookups;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Ingests one file.
    /// </summary>
    /// <param name="filePath">Path to the source file.</param>
    /// <param name="options">Ingestion options for this run.</param>
    /// <param name="attemptNumber">Which attempt this is, for retries.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What happened.</returns>
    public async Task<IngestionResult> IngestAsync(
        string filePath,
        IngestionOptions options,
        int attemptNumber = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        var fileName = Path.GetFileName(filePath);

        // --- idempotency ---------------------------------------------------------------------
        // Computed before anything else, so an already-ingested file costs one hash and one query.
        // This is also the precondition that makes automated retry safe.
        var fileHash = await ComputeFileHashAsync(filePath, cancellationToken).ConfigureAwait(false);

        if (!options.Force
            && await _jobRuns.HasSucceededAsync(fileHash, cancellationToken).ConfigureAwait(false))
        {
            IngestionLog.FileAlreadyIngested(_logger, fileName);

            return new IngestionResult(JobRunStatus.Skipped, null, IngestionCounts.Empty, [],
                $"'{fileName}' has already been ingested. Use Force to reprocess it.");
        }

        var source = _sourceFactory.Resolve(fileName);
        var jobRunId = await _jobRuns
            .StartAsync(fileName, fileHash, options.Mode, attemptNumber, cancellationToken)
            .ConfigureAwait(false);

        IngestionLog.RunStarted(_logger, jobRunId, fileName, source.FormatName,
            options.Mode, attemptNumber);

        var defects = new List<RecordDefect>();
        var counts = IngestionCounts.Empty;

        try
        {
            await _staging.TruncateAsync(jobRunId, cancellationToken).ConfigureAwait(false);
            await _lookups.LoadAsync(cancellationToken).ConfigureAwait(false);

            var (read, rejected) = await StreamIntoStagingAsync(
                filePath, source, jobRunId, options, defects, cancellationToken).ConfigureAwait(false);

            counts = counts with { Read = read, Rejected = rejected };

            // --- decide, having seen every defect ---------------------------------------------
            // Validation deliberately did not stop at the first error, so the report is complete.
            if (defects.Exists(d => d.Severity == ErrorSeverity.Error))
            {
                var errorCount = defects.Count(d => d.Severity == ErrorSeverity.Error);
                var summary = $"{errorCount} record(s) failed validation. "
                    + "The entire file was rolled back and the catalogue is unchanged.";

                await _staging.TruncateAsync(jobRunId, cancellationToken).ConfigureAwait(false);
                await _jobRuns.RecordFailureAsync(
                    jobRunId, JobRunStatus.RolledBack, summary, defects, counts, cancellationToken)
                    .ConfigureAwait(false);

                IngestionLog.RunRolledBack(_logger, jobRunId, errorCount, fileName);

                return new IngestionResult(JobRunStatus.RolledBack, jobRunId, counts, defects, summary);
            }

            await _lookups.SaveNewlyRegisteredAsync(cancellationToken).ConfigureAwait(false);

            // --- the only exclusive write window ----------------------------------------------
            var merged = await _staging.MergeAsync(
                jobRunId, options.Mode, _context.UtcNow, options.MaximumDeactivationRatio,
                cancellationToken).ConfigureAwait(false);

            counts = counts with
            {
                Inserted = merged.Inserted,
                Updated = merged.Updated,
                Unchanged = merged.Unchanged,
                Deactivated = merged.Deactivated,
            };

            await _staging.TruncateAsync(jobRunId, cancellationToken).ConfigureAwait(false);
            await _jobRuns.CompleteAsync(jobRunId, counts, defects, cancellationToken)
                .ConfigureAwait(false);

            IngestionLog.RunSucceeded(_logger, jobRunId, counts.Read, counts.Inserted,
                counts.Updated, counts.Unchanged, counts.Deactivated, defects.Count);

            return new IngestionResult(JobRunStatus.Succeeded, jobRunId, counts, defects,
                $"Ingested {counts.Read} record(s) from '{fileName}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Transient or unexpected: the catalogue is untouched, and the run stays retry-eligible.
            var summary = $"{exception.GetType().Name}: {exception.Message}";

            await SafelyTruncateAsync(jobRunId, cancellationToken).ConfigureAwait(false);
            await _jobRuns.RecordFailureAsync(
                jobRunId, JobRunStatus.Failed, summary, defects, counts, cancellationToken)
                .ConfigureAwait(false);

            IngestionLog.RunFailed(_logger, exception, jobRunId, fileName);

            return new IngestionResult(JobRunStatus.Failed, jobRunId, counts, defects, summary);
        }
    }

    private async Task<(int Read, int Rejected)> StreamIntoStagingAsync(
        string filePath,
        IRecordSource source,
        int jobRunId,
        IngestionOptions options,
        List<RecordDefect> defects,
        CancellationToken cancellationToken)
    {
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<ValidatedCarparkRecord>(options.BatchSize);
        var read = 0;
        var rejected = 0;
        var sinceHeartbeat = 0;

        await using var stream = File.OpenRead(filePath);

        // Streamed, never materialised. Memory is O(1) in file size rather than O(n).
        await foreach (var record in source.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            read++;

            if (_validator.TryValidate(record, seenKeys, out var validated, out var found))
            {
                batch.Add(validated!);
            }
            else
            {
                rejected++;
            }

            defects.AddRange(found);

            if (batch.Count >= options.BatchSize)
            {
                await _staging.StageBatchAsync(jobRunId, batch, _lookups, cancellationToken)
                    .ConfigureAwait(false);
                batch.Clear();
            }

            // Keep the lease alive so a long but healthy run is not reclaimed as abandoned.
            if (++sinceHeartbeat >= 5000)
            {
                await _jobRuns.HeartbeatAsync(jobRunId, cancellationToken).ConfigureAwait(false);
                sinceHeartbeat = 0;
            }
        }

        if (batch.Count > 0)
        {
            await _staging.StageBatchAsync(jobRunId, batch, _lookups, cancellationToken)
                .ConfigureAwait(false);
        }

        return (read, rejected);
    }

    private async Task SafelyTruncateAsync(int jobRunId, CancellationToken cancellationToken)
    {
        try
        {
            await _staging.TruncateAsync(jobRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Never let cleanup mask the original failure.
            IngestionLog.StagingCleanupFailed(_logger, exception, jobRunId);
        }
    }

    /// <summary>SHA-256 of a file's bytes, streamed rather than loaded.</summary>
    /// <param name="filePath">The file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A lowercase hexadecimal digest.</returns>
    public static async Task<string> ComputeFileHashAsync(
        string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = System.Security.Cryptography.SHA256.Create();

        var digest = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexStringLower(digest);
    }
}
