using CarparkInfo.Application.Abstractions;
using CarparkInfo.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.Persistence;

/// <summary>EF Core implementation of ingestion history reads.</summary>
public sealed class JobRunQueries : IJobRunQueries
{
    private readonly CarparkDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the queries.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="timeProvider">Clock.</param>
    public JobRunQueries(CarparkDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRunSummary>> ListRecentAsync(
        int limit, CancellationToken cancellationToken)
    {
        var runs = await _db.JobRuns
            .AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(r => new
            {
                Run = r,
                Errors = r.Errors.Count(e => e.Severity == ErrorSeverity.Error),
                Warnings = r.Errors.Count(e => e.Severity == ErrorSeverity.Warning),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. runs.Select(x => Map(x.Run, x.Errors, x.Warnings))];
    }

    /// <inheritdoc />
    public async Task<JobRunSummary?> FindAsync(int jobRunId, CancellationToken cancellationToken)
    {
        var run = await _db.JobRuns
            .AsNoTracking()
            .Where(r => r.Id == jobRunId)
            .Select(r => new
            {
                Run = r,
                Errors = r.Errors.Count(e => e.Severity == ErrorSeverity.Error),
                Warnings = r.Errors.Count(e => e.Severity == ErrorSeverity.Warning),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return run is null ? null : Map(run.Run, run.Errors, run.Warnings);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRunDefect>> GetDefectsAsync(
        int jobRunId, CancellationToken cancellationToken) =>
        await _db.JobRunErrors
            .AsNoTracking()
            .Where(e => e.JobRunId == jobRunId)
            .OrderBy(e => e.LineNumber)
            .Select(e => new JobRunDefect(
                e.LineNumber, e.CarParkNo, e.FieldName, e.ErrorCode, e.Severity, e.Message, e.RawLine))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<FeedFreshness> GetFreshnessAsync(
        TimeSpan sla, CancellationToken cancellationToken)
    {
        // Completed timestamps are DateTimeOffset, which SQLite cannot ORDER BY, so ordering is by
        // id - monotonic, and equivalent for this purpose.
        var lastSuccess = await _db.JobRuns
            .AsNoTracking()
            .Where(r => r.Status == JobRunStatus.Succeeded)
            .OrderByDescending(r => r.Id)
            .Select(r => (DateTimeOffset?)r.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var lastRunStatus = await _db.JobRuns
            .AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Select(r => (JobRunStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var age = lastSuccess.HasValue ? _timeProvider.GetUtcNow() - lastSuccess.Value : (TimeSpan?)null;

        return new FeedFreshness(
            IsFresh: age.HasValue && age.Value <= sla,
            LastSuccessAt: lastSuccess,
            Age: age,
            Sla: sla,
            LastRunStatus: lastRunStatus);
    }

    private static JobRunSummary Map(JobRun run, int errors, int warnings) => new(
        run.Id,
        run.FileName,
        run.Status,
        run.Mode,
        run.StartedAt,
        run.CompletedAt,
        run.CompletedAt.HasValue ? run.CompletedAt.Value - run.StartedAt : null,
        run.HostName,
        run.AttemptNumber,
        new JobRunCounts(
            run.RecordsRead, run.RecordsInserted, run.RecordsUpdated,
            run.RecordsUnchanged, run.RecordsDeactivated, run.RecordsRejected),
        errors,
        warnings,
        run.ErrorSummary);
}
