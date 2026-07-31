using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>
/// EF Core implementation of the run lifecycle and audit trail.
/// </summary>
public sealed class JobRunStore : IJobRunStore
{
    private readonly CarparkDbContext _db;
    private readonly IDbContextFactory<CarparkDbContext> _contextFactory;
    private readonly IIngestionContext _context;
    private readonly ILogger<JobRunStore> _logger;

    /// <summary>Creates the store.</summary>
    /// <param name="db">The context used for normal operations.</param>
    /// <param name="contextFactory">
    /// Creates an independent context for failure recording, so the audit trail survives the
    /// rollback that discards the data.
    /// </param>
    /// <param name="context">Clock and host identity.</param>
    /// <param name="logger">Structured logging.</param>
    public JobRunStore(
        CarparkDbContext db,
        IDbContextFactory<CarparkDbContext> contextFactory,
        IIngestionContext context,
        ILogger<JobRunStore> logger)
    {
        _db = db;
        _contextFactory = contextFactory;
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> HasSucceededAsync(string fileHash, CancellationToken cancellationToken) =>
        await _db.JobRuns
            .AsNoTracking()
            .AnyAsync(r => r.FileHash == fileHash && r.Status == JobRunStatus.Succeeded,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> StartAsync(string fileName, string fileHash, IngestionMode mode,
        int attemptNumber, CancellationToken cancellationToken)
    {
        var run = new JobRun("carpark-ingestion", fileName, fileHash, mode,
            _context.HostName, _context.UtcNow, attemptNumber);

        _db.JobRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run.Id;
    }

    /// <inheritdoc />
    public async Task HeartbeatAsync(int jobRunId, CancellationToken cancellationToken)
    {
        var run = await _db.JobRuns.FindAsync([jobRunId], cancellationToken).ConfigureAwait(false);

        if (run is null)
        {
            return;
        }

        run.Heartbeat(_context.UtcNow);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(int jobRunId, IngestionCounts counts,
        IReadOnlyList<RecordDefect> defects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defects);

        var run = await _db.JobRuns.FindAsync([jobRunId], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job run {jobRunId} not found.");

        run.RecordCounts(counts.Read, counts.Inserted, counts.Updated, counts.Unchanged,
            counts.Deactivated, counts.Rejected);
        run.MarkSucceeded(_context.UtcNow);

        // Warnings are persisted on the SUCCESS path as well. A run that ingested 2,181 rows and
        // flagged three inconsistent ones has something an operator should see; a defect report
        // that only exists when the run fails hides exactly that case.
        foreach (var defect in defects)
        {
            run.AddError(new JobRunError(
                defect.LineNumber, defect.CarParkNo, defect.FieldName, defect.ErrorCode,
                defect.Severity, defect.Message, Truncate(defect.RawLine, 2000)));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(int jobRunId, JobRunStatus status, string summary,
        IReadOnlyList<RecordDefect> defects, IngestionCounts counts,
        CancellationToken cancellationToken)
    {
        // ---------------------------------------------------------------------------------
        // A SEPARATE CONTEXT, AND THEREFORE A SEPARATE CONNECTION AND TRANSACTION.
        //
        // Writing this on _db would enlist the audit record in the very transaction being
        // rolled back, so the rollback that discards the bad data would also discard the
        // explanation of why it was discarded. The result is a clean database, an empty error
        // table, and nobody able to say what happened.
        //
        // Guarded by Failed_run_is_recorded_even_though_data_rolled_back.
        // ---------------------------------------------------------------------------------
        await using var audit = await _contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var run = await audit.JobRuns.FindAsync([jobRunId], cancellationToken).ConfigureAwait(false);

        if (run is null)
        {
            JobRunStoreLog.JobRunVanished(_logger, jobRunId);
            return;
        }

        run.RecordCounts(counts.Read, counts.Inserted, counts.Updated, counts.Unchanged,
            counts.Deactivated, counts.Rejected);

        if (status == JobRunStatus.RolledBack)
        {
            run.MarkRolledBack(_context.UtcNow, summary);
        }
        else
        {
            run.MarkFailed(_context.UtcNow, summary);
        }

        // Added through the aggregate rather than the DbSet: JobRunId is private-set, and going
        // via the parent lets EF populate the foreign key from the relationship.
        foreach (var defect in defects)
        {
            run.AddError(new JobRunError(
                defect.LineNumber, defect.CarParkNo, defect.FieldName, defect.ErrorCode,
                defect.Severity, defect.Message, Truncate(defect.RawLine, 2000)));
        }

        await audit.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ReclaimAbandonedRunsAsync(CancellationToken cancellationToken)
    {
        var now = _context.UtcNow;

        // The status filter translates (it is stored as text); the lease comparison is applied in
        // memory because SQLite stores DateTimeOffset as TEXT and EF cannot translate the
        // comparison reliably. That is fine here rather than a compromise: the lease guarantees at
        // most one Running row per host, so this materialises a handful of rows at most.
        var running = await _db.JobRuns
            .Where(r => r.Status == JobRunStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var abandoned = running.Where(r => r.HasExpiredLease(now)).ToList();

        if (abandoned.Count == 0)
        {
            return 0;
        }

        foreach (var run in abandoned)
        {
            run.MarkFailed(now,
                $"LEASE_EXPIRED: host '{run.HostName}' stopped heartbeating at "
                + $"{run.LeaseExpiresAt:O}. Reclaimed automatically and eligible for retry.");

            JobRunStoreLog.ReclaimedAbandonedRun(_logger, run.Id, run.FileName, run.HostName);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Any rows the dead process staged are now orphaned.
        await _db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM carpark_staging
            WHERE job_run_id IN (SELECT id FROM job_run WHERE status = 'Failed')
            """,
            cancellationToken).ConfigureAwait(false);

        return abandoned.Count;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];
}

/// <summary>Source-generated log messages for the job run store.</summary>
internal static partial class JobRunStoreLog
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning,
        Message = "Job run {JobRunId} vanished before its failure could be recorded.")]
    public static partial void JobRunVanished(ILogger logger, int jobRunId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Reclaimed abandoned run {JobRunId} for {FileName}, last held by {HostName}.")]
    public static partial void ReclaimedAbandonedRun(
        ILogger logger, int jobRunId, string fileName, string hostName);
}
