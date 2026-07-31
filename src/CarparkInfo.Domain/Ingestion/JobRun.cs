namespace CarparkInfo.Domain.Ingestion;

/// <summary>How a source file's contents relate to the existing catalogue.</summary>
/// <remarks>
/// <para>
/// Named <c>IngestionMode</c> rather than <c>FileMode</c> to avoid colliding with
/// <see cref="System.IO.FileMode"/>, which implicit usings bring into every file.
/// </para>
/// <para>
/// A genuine ambiguity in the brief, handled explicitly. The README calls the feed a "daily delta
/// file", but the supplied file contains 2,181 rows -- the complete HDB inventory, which is a
/// snapshot. The two readings disagree on one question: what does <i>absence</i> from the file mean?
/// </para>
/// <para>
/// Guessing <see cref="IngestionMode.Snapshot"/> and then receiving a genuine three-row delta would deactivate
/// 2,178 carparks. <see cref="IngestionMode.Delta"/> is therefore the default, and snapshot deactivation is
/// capped by a ratio guard. The mode is recorded on every run so past runs stay auditable.
/// </para>
/// </remarks>
public enum IngestionMode
{
    /// <summary>Absence means unchanged. Rows not in the file are left alone. The default.</summary>
    Delta = 0,

    /// <summary>Absence means gone. Rows not in the file are soft-deactivated, subject to a ratio guard.</summary>
    Snapshot = 1,
}

/// <summary>The lifecycle of an ingestion run.</summary>
public enum JobRunStatus
{
    /// <summary>Created but not yet started.</summary>
    Pending = 0,

    /// <summary>Executing. Holds a heartbeated lease.</summary>
    Running = 1,

    /// <summary>Completed and committed.</summary>
    Succeeded = 2,

    /// <summary>Failed for a transient reason. Eligible for automatic retry.</summary>
    Failed = 3,

    /// <summary>Aborted on validation. The database was left untouched.</summary>
    RolledBack = 4,

    /// <summary>The file had already been ingested successfully. A no-op.</summary>
    Skipped = 5,
}

/// <summary>Whether a defect blocks ingestion or is merely recorded.</summary>
public enum ErrorSeverity
{
    /// <summary>Recorded, but the row is still ingested. Source inconsistencies, not corruption.</summary>
    Warning = 0,

    /// <summary>Blocks the run. The whole file rolls back.</summary>
    Error = 1,
}

/// <summary>
/// One execution of the ingestion job, with its outcome and counts.
/// </summary>
public sealed class JobRun
{
    /// <summary>How long a lease is held before it is considered abandoned.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly List<JobRunError> _errors = [];

    private JobRun() { }   // EF Core materialisation

    /// <summary>Starts a run and acquires its lease.</summary>
    /// <param name="jobName">The job's name.</param>
    /// <param name="fileName">The source file being processed.</param>
    /// <param name="fileHash">SHA-256 of the file's bytes. The idempotency key.</param>
    /// <param name="mode">Delta or snapshot semantics.</param>
    /// <param name="hostName">The machine executing the run.</param>
    /// <param name="startedAt">When the run started.</param>
    /// <param name="attemptNumber">Which attempt this is, for retries.</param>
    public JobRun(string jobName, string fileName, string fileHash, IngestionMode mode,
        string hostName, DateTimeOffset startedAt, int attemptNumber = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);

        JobName = jobName;
        FileName = fileName;
        FileHash = fileHash;
        Mode = mode;
        HostName = hostName;
        StartedAt = startedAt;
        AttemptNumber = attemptNumber;
        Status = JobRunStatus.Running;
        LeaseExpiresAt = startedAt.Add(LeaseDuration);
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>The job's name.</summary>
    public string JobName { get; private set; } = string.Empty;

    /// <summary>The source file processed.</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the file's bytes. Reprocessing a succeeded hash is a no-op.</summary>
    public string FileHash { get; private set; } = string.Empty;

    /// <summary>Current status.</summary>
    public JobRunStatus Status { get; private set; }

    /// <summary>Whether the file was treated as a delta or a snapshot.</summary>
    public IngestionMode Mode { get; private set; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>When the run finished, however it finished.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// When this run's lease expires. Heartbeated while running; an expired lease on a Running row
    /// means the process died, and the next startup reclaims it automatically.
    /// </summary>
    public DateTimeOffset LeaseExpiresAt { get; private set; }

    /// <summary>The machine executing the run. Also prevents two hosts ingesting the same file.</summary>
    public string HostName { get; private set; } = string.Empty;

    /// <summary>Rows read from the file.</summary>
    public int RecordsRead { get; private set; }

    /// <summary>Rows that did not previously exist.</summary>
    public int RecordsInserted { get; private set; }

    /// <summary>Rows whose content changed.</summary>
    public int RecordsUpdated { get; private set; }

    /// <summary>Rows whose hash matched, so no write occurred.</summary>
    public int RecordsUnchanged { get; private set; }

    /// <summary>Rows soft-deactivated because they were absent from a snapshot.</summary>
    public int RecordsDeactivated { get; private set; }

    /// <summary>Rows rejected by validation.</summary>
    public int RecordsRejected { get; private set; }

    /// <summary>Which attempt this is.</summary>
    public int AttemptNumber { get; private set; }

    /// <summary>A short description of why the run failed, if it did.</summary>
    public string? ErrorSummary { get; private set; }

    /// <summary>Every defect found, with line numbers. The operator's report.</summary>
    public IReadOnlyCollection<JobRunError> Errors => _errors;

    /// <summary>Extends the lease. Called periodically while the run executes.</summary>
    /// <param name="now">The current time.</param>
    public void Heartbeat(DateTimeOffset now) => LeaseExpiresAt = now.Add(LeaseDuration);

    /// <summary>Whether the lease has lapsed, indicating the owning process died.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when a Running row is abandoned.</returns>
    public bool HasExpiredLease(DateTimeOffset now) =>
        Status == JobRunStatus.Running && LeaseExpiresAt <= now;

    /// <summary>Records the counts observed during processing.</summary>
    /// <param name="read">Rows read.</param>
    /// <param name="inserted">Rows inserted.</param>
    /// <param name="updated">Rows updated.</param>
    /// <param name="unchanged">Rows skipped as unchanged.</param>
    /// <param name="deactivated">Rows deactivated.</param>
    /// <param name="rejected">Rows rejected.</param>
    public void RecordCounts(int read, int inserted, int updated, int unchanged,
        int deactivated, int rejected)
    {
        RecordsRead = read;
        RecordsInserted = inserted;
        RecordsUpdated = updated;
        RecordsUnchanged = unchanged;
        RecordsDeactivated = deactivated;
        RecordsRejected = rejected;
    }

    /// <summary>Marks the run successful.</summary>
    /// <param name="completedAt">When it finished.</param>
    public void MarkSucceeded(DateTimeOffset completedAt)
    {
        Status = JobRunStatus.Succeeded;
        CompletedAt = completedAt;
    }

    /// <summary>Marks the run as aborted on validation, with the database untouched.</summary>
    /// <param name="completedAt">When it finished.</param>
    /// <param name="summary">Why it aborted.</param>
    public void MarkRolledBack(DateTimeOffset completedAt, string summary)
    {
        Status = JobRunStatus.RolledBack;
        CompletedAt = completedAt;
        ErrorSummary = summary;
    }

    /// <summary>Marks the run failed for a transient reason, leaving it eligible for retry.</summary>
    /// <param name="completedAt">When it finished.</param>
    /// <param name="summary">Why it failed.</param>
    public void MarkFailed(DateTimeOffset completedAt, string summary)
    {
        Status = JobRunStatus.Failed;
        CompletedAt = completedAt;
        ErrorSummary = summary;
    }

    /// <summary>Marks the file as already ingested, so this run did nothing.</summary>
    /// <param name="completedAt">When it finished.</param>
    public void MarkSkipped(DateTimeOffset completedAt)
    {
        Status = JobRunStatus.Skipped;
        CompletedAt = completedAt;
    }

    /// <summary>Adds a defect to the run's report.</summary>
    /// <param name="error">The defect.</param>
    public void AddError(JobRunError error) => _errors.Add(error);
}

/// <summary>
/// One defect found while processing a source file.
/// </summary>
/// <remarks>
/// Validation collects every one of these before deciding whether to abort. Stopping at the first
/// defect means an operator fixes one, re-runs, waits, and finds the next -- however many times it
/// takes. Collecting produces the complete report in a single pass, which is what "minimal human
/// intervention" means in practice.
/// </remarks>
public sealed class JobRunError
{
    private JobRunError() { }   // EF Core materialisation

    /// <summary>Records a defect.</summary>
    /// <param name="lineNumber">The exact line in the source file.</param>
    /// <param name="carParkNo">The business key, when it could be read.</param>
    /// <param name="fieldName">The offending field.</param>
    /// <param name="errorCode">A stable machine-readable code.</param>
    /// <param name="severity">Whether this blocks ingestion.</param>
    /// <param name="message">A human-readable explanation.</param>
    /// <param name="rawLine">The offending line, verbatim.</param>
    public JobRunError(int lineNumber, string? carParkNo, string? fieldName, string errorCode,
        ErrorSeverity severity, string message, string? rawLine)
    {
        LineNumber = lineNumber;
        CarParkNo = carParkNo;
        FieldName = fieldName;
        ErrorCode = errorCode;
        Severity = severity;
        Message = message;
        RawLine = rawLine;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>The run that found this defect.</summary>
    public int JobRunId { get; private set; }

    /// <summary>The exact line in the source file, so the operator can fix it without reading a log.</summary>
    public int LineNumber { get; private set; }

    /// <summary>The business key, when it could be read.</summary>
    public string? CarParkNo { get; private set; }

    /// <summary>The offending field.</summary>
    public string? FieldName { get; private set; }

    /// <summary>A stable machine-readable code, e.g. <c>OUT_OF_RANGE</c>.</summary>
    public string ErrorCode { get; private set; } = string.Empty;

    /// <summary>Whether this blocks ingestion or is merely recorded.</summary>
    public ErrorSeverity Severity { get; private set; }

    /// <summary>A human-readable explanation.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>The offending line, verbatim.</summary>
    public string? RawLine { get; private set; }

    /// <summary>Navigation to the owning run.</summary>
    public JobRun? JobRun { get; private set; }
}
