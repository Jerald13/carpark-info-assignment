using CarparkInfo.Domain.Ingestion;

namespace CarparkInfo.Application.Abstractions;

/// <summary>A summary of one ingestion run, for the operations view.</summary>
/// <param name="Id">The run's id.</param>
/// <param name="FileName">The source file.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Mode">Delta or snapshot semantics.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="CompletedAt">When it finished.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="HostName">Which machine ran it.</param>
/// <param name="AttemptNumber">Which attempt this was.</param>
/// <param name="Counts">What it did.</param>
/// <param name="ErrorCount">Blocking defects found.</param>
/// <param name="WarningCount">Non-blocking defects found.</param>
/// <param name="ErrorSummary">Why it failed, if it did.</param>
public sealed record JobRunSummary(
    int Id,
    string FileName,
    JobRunStatus Status,
    IngestionMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? Duration,
    string HostName,
    int AttemptNumber,
    JobRunCounts Counts,
    int ErrorCount,
    int WarningCount,
    string? ErrorSummary);

/// <summary>What a run did to the catalogue.</summary>
/// <param name="Read">Rows read from the file.</param>
/// <param name="Inserted">Carparks created.</param>
/// <param name="Updated">Carparks changed.</param>
/// <param name="Unchanged">Carparks whose hash matched, so nothing was written.</param>
/// <param name="Deactivated">Carparks absent from a snapshot.</param>
/// <param name="Rejected">Rows rejected by validation.</param>
public sealed record JobRunCounts(
    int Read, int Inserted, int Updated, int Unchanged, int Deactivated, int Rejected);

/// <summary>One defect from a run's report.</summary>
/// <param name="LineNumber">The line in the source file.</param>
/// <param name="CarParkNo">The business key, when it could be read.</param>
/// <param name="FieldName">The offending field.</param>
/// <param name="ErrorCode">A stable machine-readable code.</param>
/// <param name="Severity">Whether this blocked ingestion.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="RawLine">The offending line, verbatim.</param>
public sealed record JobRunDefect(
    int LineNumber, string? CarParkNo, string? FieldName, string ErrorCode,
    ErrorSeverity Severity, string Message, string? RawLine);

/// <summary>Whether the catalogue is fresh enough to trust.</summary>
/// <param name="IsFresh">Whether the last success is within the SLA.</param>
/// <param name="LastSuccessAt">When ingestion last succeeded.</param>
/// <param name="Age">How long ago that was.</param>
/// <param name="Sla">How stale the data may be before readiness degrades.</param>
/// <param name="LastRunStatus">How the most recent run ended, whatever its outcome.</param>
public sealed record FeedFreshness(
    bool IsFresh,
    DateTimeOffset? LastSuccessAt,
    TimeSpan? Age,
    TimeSpan Sla,
    JobRunStatus? LastRunStatus);

/// <summary>Reads ingestion history for the operations endpoints and health checks.</summary>
public interface IJobRunQueries
{
    /// <summary>Lists recent runs, newest first.</summary>
    /// <param name="limit">How many to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Recent runs with their counts.</returns>
    Task<IReadOnlyList<JobRunSummary>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Gets one run.</summary>
    /// <param name="jobRunId">The run id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The run, or null.</returns>
    Task<JobRunSummary?> FindAsync(int jobRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a run's complete defect report.
    /// </summary>
    /// <param name="jobRunId">The run id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Every defect, with line numbers and the offending text.</returns>
    /// <remarks>
    /// This is what lets an operator fix a source file without reading a log. Validation collects
    /// every defect rather than stopping at the first, so one run produces the complete list.
    /// </remarks>
    Task<IReadOnlyList<JobRunDefect>> GetDefectsAsync(int jobRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether ingestion has succeeded recently enough.
    /// </summary>
    /// <param name="sla">How stale the data may be.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The freshness of the catalogue.</returns>
    /// <remarks>
    /// <b>Alerting on absence rather than on failure.</b> A job that fails loudly gets noticed; a
    /// job that silently stops running does not, and that is the more dangerous outcome. This is
    /// what readiness degrades on.
    /// </remarks>
    Task<FeedFreshness> GetFreshnessAsync(TimeSpan sla, CancellationToken cancellationToken);
}
