using CarparkInfo.Application.Abstractions;
using CarparkInfo.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Application.Ingestion;

/// <summary>Retry behaviour for transient failures.</summary>
public sealed class RetryOptions
{
    /// <summary>How many times to attempt a file before quarantining it.</summary>
    public int MaximumAttempts { get; set; } = 3;

    /// <summary>The first backoff delay. Subsequent delays quadruple it.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Random jitter added to each delay so retrying hosts do not synchronise.</summary>
    public TimeSpan MaximumJitter { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs ingestion with recovery: lease reclaim, typed retry, and quarantine.
/// </summary>
/// <remarks>
/// <para>
/// This is the "minimal human intervention for job recovery" challenge, implemented rather than
/// discussed. Three mechanisms, each addressing a failure that would otherwise need somebody awake:
/// </para>
/// <list type="number">
///   <item>
///     <b>Lease reclaim.</b> A process killed mid-run leaves its row stuck in <c>Running</c> for
///     ever, and every subsequent night refuses to start. Startup reclaim marks expired leases
///     failed and retry-eligible, so nobody edits a database row at 03:00.
///   </item>
///   <item>
///     <b>Typed retry.</b> Only transient classes are retried. Retrying a malformed file three
///     times reproduces the same report twenty minutes later and delays the alert.
///   </item>
///   <item>
///     <b>Quarantine.</b> After the final attempt the file leaves the inbox, so tomorrow's file
///     processes normally. Without it, one bad file stops the pipeline permanently and silently.
///   </item>
/// </list>
/// </remarks>
public sealed class IngestionRunner
{
    private readonly CarparkIngestionService _ingestion;
    private readonly IJobRunStore _jobRuns;
    private readonly IFileIntake _intake;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IngestionRunner> _logger;

    /// <summary>Creates the runner.</summary>
    /// <param name="ingestion">The ingestion service.</param>
    /// <param name="jobRuns">Run store, for lease reclaim.</param>
    /// <param name="intake">File movement between inbox, processed and quarantine.</param>
    /// <param name="timeProvider">Clock, so backoff is testable without waiting.</param>
    /// <param name="logger">Structured logging.</param>
    public IngestionRunner(
        CarparkIngestionService ingestion,
        IJobRunStore jobRuns,
        IFileIntake intake,
        TimeProvider timeProvider,
        ILogger<IngestionRunner> logger)
    {
        _ingestion = ingestion;
        _jobRuns = jobRuns;
        _intake = intake;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Processes one file with retry and quarantine.
    /// </summary>
    /// <param name="filePath">The file to ingest.</param>
    /// <param name="options">Ingestion options.</param>
    /// <param name="retry">Retry behaviour.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome of the final attempt.</returns>
    /// <param name="archiveOnCompletion">
    /// Whether to move the file to processed/quarantine when the run finishes. True for files
    /// discovered in the inbox, which the job owns. False for a file named explicitly with
    /// --file: relocating a path the operator handed us is surprising at best and, if they
    /// pointed at a file the job does not own, actively wrong.
    /// </param>
    public async Task<IngestionResult> RunAsync(
        string filePath,
        IngestionOptions options,
        RetryOptions retry,
        bool archiveOnCompletion = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retry);

        var fileName = Path.GetFileName(filePath);

        // Anything the previous process abandoned is cleared before we start.
        await _jobRuns.ReclaimAbandonedRunsAsync(cancellationToken).ConfigureAwait(false);

        IngestionResult result;
        var attempt = 1;

        while (true)
        {
            result = await _ingestion
                .IngestAsync(filePath, options, attempt, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status is JobRunStatus.Succeeded or JobRunStatus.Skipped)
            {
                if (result.Status == JobRunStatus.Succeeded && archiveOnCompletion)
                {
                    await _intake.MoveToProcessedAsync(filePath, options, cancellationToken)
                        .ConfigureAwait(false);
                }

                return result;
            }

            // A validation failure is deterministic. Repeating it produces the same report later
            // and nothing else, so it goes straight to quarantine.
            if (result.Status == JobRunStatus.RolledBack)
            {
                IngestionLog.NotRetryable(_logger, fileName);
                break;
            }

            if (attempt >= retry.MaximumAttempts)
            {
                break;
            }

            var delay = BackoffFor(attempt, retry);
            IngestionLog.RetryScheduled(_logger, fileName, delay, attempt + 1, retry.MaximumAttempts);

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            attempt++;
        }

        if (archiveOnCompletion)
        {
            await _intake.MoveToQuarantineAsync(filePath, options, result, cancellationToken)
                .ConfigureAwait(false);

            IngestionLog.FileQuarantined(_logger, fileName, attempt);
        }

        return result;
    }

    /// <summary>
    /// Exponential backoff with jitter: roughly 1, 4 and 16 minutes.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, 1-based.</param>
    /// <param name="retry">Retry options.</param>
    /// <returns>How long to wait before the next attempt.</returns>
    /// <remarks>
    /// Jitter matters when several hosts retry after a shared outage: without it they all come
    /// back at the same instant and reproduce the pile-up that caused the failure.
    /// </remarks>
    internal static TimeSpan BackoffFor(int attempt, RetryOptions retry)
    {
        var multiplier = Math.Pow(4, attempt - 1);
        var baseDelay = retry.InitialDelay * multiplier;
        var jitter = TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * retry.MaximumJitter.TotalMilliseconds);

        return baseDelay + jitter;
    }
}

/// <summary>Moves source files between the inbox, processed and quarantine directories.</summary>
public interface IFileIntake
{
    /// <summary>Files awaiting ingestion, oldest first.</summary>
    /// <param name="options">Ingestion options, for directory paths.</param>
    /// <returns>Full paths of pending files.</returns>
    IReadOnlyList<string> DiscoverPending(IngestionOptions options);

    /// <summary>Moves a successfully ingested file out of the inbox.</summary>
    /// <param name="filePath">The file.</param>
    /// <param name="options">Ingestion options, for directory paths.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task MoveToProcessedAsync(string filePath, IngestionOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a file that could not be ingested to quarantine, with a sidecar defect report.
    /// </summary>
    /// <param name="filePath">The file.</param>
    /// <param name="options">Ingestion options, for directory paths.</param>
    /// <param name="result">The failing outcome, written alongside as JSON.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// <b>Getting the file out of the inbox is the point.</b> Leaving it there means every
    /// subsequent night retries the same broken file and no new data is ever loaded - the pipeline
    /// stops permanently, and silently.
    /// </remarks>
    Task MoveToQuarantineAsync(string filePath, IngestionOptions options, IngestionResult result,
        CancellationToken cancellationToken);
}
