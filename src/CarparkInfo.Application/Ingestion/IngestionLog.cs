using CarparkInfo.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Application.Ingestion;

/// <summary>
/// Source-generated log messages for the ingestion pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <c>[LoggerMessage]</c> generates the logging code at compile time. Compared with
/// <c>logger.LogInformation("... {X}", x)</c> this avoids boxing value-type arguments and the
/// params array allocation, and skips argument evaluation entirely when the level is disabled.
/// </para>
/// <para>
/// It matters here specifically: ingestion runs per-file, but the heartbeat and batch paths are
/// on the hot loop, and a job processing a million rows should not be allocating on every log
/// call. It also gives every message a stable EventId, which is what an operations dashboard
/// filters on rather than matching message text.
/// </para>
/// </remarks>
internal static partial class IngestionLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Skipping {FileName}: an identical file has already been ingested successfully.")]
    public static partial void FileAlreadyIngested(ILogger logger, string fileName);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Run {JobRunId} started for {FileName} ({Format}, {Mode}, attempt {Attempt}).")]
    public static partial void RunStarted(
        ILogger logger, int jobRunId, string fileName, string format, IngestionMode mode,
        int attempt);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Run {JobRunId} succeeded. Read {Read}, inserted {Inserted}, updated {Updated}, "
                + "unchanged {Unchanged}, deactivated {Deactivated}, warnings {Warnings}.")]
    public static partial void RunSucceeded(
        ILogger logger, int jobRunId, int read, int inserted, int updated, int unchanged,
        int deactivated, int warnings);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error,
        Message = "Run {JobRunId} rolled back: {ErrorCount} invalid record(s) in {FileName}. "
                + "The catalogue is unchanged.")]
    public static partial void RunRolledBack(
        ILogger logger, int jobRunId, int errorCount, string fileName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Run {JobRunId} failed processing {FileName}.")]
    public static partial void RunFailed(
        ILogger logger, Exception exception, int jobRunId, string fileName);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Could not clear staging for run {JobRunId}; the next run will clear it.")]
    public static partial void StagingCleanupFailed(
        ILogger logger, Exception exception, int jobRunId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning,
        Message = "Reclaimed abandoned run {JobRunId} for {FileName}, last held by {HostName}.")]
    public static partial void ReclaimedAbandonedRun(
        ILogger logger, int jobRunId, string fileName, string hostName);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning,
        Message = "Job run {JobRunId} vanished before its failure could be recorded.")]
    public static partial void JobRunVanished(ILogger logger, int jobRunId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning,
        Message = "Quarantined {FileName} after {Attempts} attempt(s). The inbox is clear, so "
                + "tomorrow's file will process normally.")]
    public static partial void FileQuarantined(ILogger logger, string fileName, int attempts);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Information,
        Message = "Retrying {FileName} in {Delay} (attempt {Attempt} of {MaxAttempts}).")]
    public static partial void RetryScheduled(
        ILogger logger, string fileName, TimeSpan delay, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Validation failure is not retryable; {FileName} goes straight to quarantine.")]
    public static partial void NotRetryable(ILogger logger, string fileName);
}
