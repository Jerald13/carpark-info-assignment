using CarparkInfo.Application.Ingestion;
using CarparkInfo.Domain.Ingestion;

namespace CarparkInfo.Application.Abstractions;

/// <summary>
/// Manages the lifecycle of an ingestion run.
/// </summary>
/// <remarks>
/// Separated from the staging store because of one specific requirement: the failure record must
/// survive the rollback that discards the data. See <see cref="RecordFailureAsync"/>.
/// </remarks>
public interface IJobRunStore
{
    /// <summary>Whether this exact file has already been ingested successfully.</summary>
    /// <param name="fileHash">SHA-256 of the file's bytes.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns><see langword="true"/> when a successful run already exists for this hash.</returns>
    Task<bool> HasSucceededAsync(string fileHash, CancellationToken cancellationToken);

    /// <summary>Starts a run and acquires its lease.</summary>
    /// <param name="fileName">The file being processed.</param>
    /// <param name="fileHash">SHA-256 of the file's bytes.</param>
    /// <param name="mode">Delta or snapshot semantics.</param>
    /// <param name="attemptNumber">Which attempt this is.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new run's id.</returns>
    Task<int> StartAsync(string fileName, string fileHash, IngestionMode mode,
        int attemptNumber, CancellationToken cancellationToken);

    /// <summary>Extends the run's lease so a healthy run is not reclaimed as abandoned.</summary>
    /// <param name="jobRunId">The run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task HeartbeatAsync(int jobRunId, CancellationToken cancellationToken);

    /// <summary>Marks a run successful and records its counts.</summary>
    /// <param name="jobRunId">The run.</param>
    /// <param name="counts">What the run did.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task CompleteAsync(int jobRunId, IngestionCounts counts, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure and its defect report <b>on a connection independent of the data
    /// transaction</b>.
    /// </summary>
    /// <param name="jobRunId">The run.</param>
    /// <param name="status">Whether the run rolled back or failed transiently.</param>
    /// <param name="summary">Why it failed.</param>
    /// <param name="defects">Every defect found.</param>
    /// <param name="counts">What the run managed before failing.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// <b>The separate connection is the point of this method.</b> Writing the failure record
    /// inside the transaction that is being rolled back rolls back the error log too, leaving a
    /// clean database and no explanation of what happened. It is a classic audit-logging bug that
    /// only surfaces at 03:00 when somebody asks why the feed did not load.
    /// </remarks>
    Task RecordFailureAsync(int jobRunId, JobRunStatus status, string summary,
        IReadOnlyList<RecordDefect> defects, IngestionCounts counts,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reclaims runs left <c>Running</c> by a process that died, marking them failed and
    /// retry-eligible.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many runs were reclaimed.</returns>
    /// <remarks>
    /// Without this, a killed process leaves a row stuck in <c>Running</c> for ever and every
    /// subsequent night refuses to start, requiring somebody to hand-edit a database row.
    /// </remarks>
    Task<int> ReclaimAbandonedRunsAsync(CancellationToken cancellationToken);
}

/// <summary>Writes validated rows to staging and applies them atomically.</summary>
public interface ICarparkStagingStore
{
    /// <summary>Bulk-inserts a batch of validated rows into staging.</summary>
    /// <param name="jobRunId">The run staging them.</param>
    /// <param name="records">The batch.</param>
    /// <param name="lookups">Resolved lookup ids.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task StageBatchAsync(int jobRunId, IReadOnlyList<ValidatedCarparkRecord> records,
        ILookupResolver lookups, CancellationToken cancellationToken);

    /// <summary>Applies a run's staged rows to the catalogue in one atomic step.</summary>
    /// <param name="jobRunId">The run.</param>
    /// <param name="mode">Delta or snapshot semantics.</param>
    /// <param name="observedAt">The ingestion timestamp.</param>
    /// <param name="maximumDeactivationRatio">Snapshot-mode safety limit.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were inserted, updated, unchanged and deactivated.</returns>
    Task<IngestionCounts> MergeAsync(int jobRunId, IngestionMode mode, DateTimeOffset observedAt,
        double maximumDeactivationRatio, CancellationToken cancellationToken);

    /// <summary>Empties staging for a run. Called at the start and end of every run, success or not.</summary>
    /// <param name="jobRunId">The run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task TruncateAsync(int jobRunId, CancellationToken cancellationToken);
}

/// <summary>
/// Turns lookup codes into ids, registering values the feed has not used before.
/// </summary>
/// <remarks>
/// Auto-registration is deliberate. HDB introducing an eighth carpark type must not take the
/// nightly feed down; the value is recorded with a warning and ingestion continues.
/// </remarks>
public interface ILookupResolver
{
    /// <summary>Loads the current lookup values.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Resolves a carpark type code to its id, registering it if unknown.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The lookup id.</returns>
    int CarParkTypeId(string code);

    /// <summary>Resolves a parking system code to its id, registering it if unknown.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The lookup id.</returns>
    int ParkingSystemTypeId(string code);

    /// <summary>Resolves a short-term parking code to its id, registering it if unknown.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The lookup id.</returns>
    int ShortTermParkingTypeId(string code);

    /// <summary>Resolves a free parking code to its id, registering it if unknown.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The lookup id.</returns>
    int FreeParkingTypeId(string code);

    /// <summary>Persists any values auto-registered during this run.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task SaveNewlyRegisteredAsync(CancellationToken cancellationToken);
}

/// <summary>Provides the current time, and the host's identity for lease ownership.</summary>
public interface IIngestionContext
{
    /// <summary>The current time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The machine running this job, recorded on the lease.</summary>
    string HostName { get; }
}
