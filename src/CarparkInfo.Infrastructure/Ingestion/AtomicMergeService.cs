using System.Globalization;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>How many rows the merge touched.</summary>
/// <param name="Inserted">Carparks that did not previously exist.</param>
/// <param name="Updated">Carparks whose content changed.</param>
/// <param name="Unchanged">Carparks whose row hash matched, so no write occurred.</param>
/// <param name="Deactivated">Carparks absent from a snapshot and soft-deactivated.</param>
public readonly record struct MergeCounts(int Inserted, int Updated, int Unchanged, int Deactivated);

/// <summary>
/// Applies a staged file to the carpark catalogue in one atomic step.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where requirement R7 lives</b> - "in the event there is an error processing the
/// records in the file, the entire file should rollback".
/// </para>
/// <para>
/// The volume has already been absorbed into <c>carpark_staging</c> in batches, against a table no
/// reader queries. What remains is a single set-based
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> reading from a local table: whole-file atomicity with a
/// write lock measured in milliseconds rather than minutes. A failure anywhere leaves
/// <c>carpark</c> byte-for-byte unchanged.
/// </para>
/// <para>
/// The <c>WHERE source_row_hash IS DISTINCT FROM excluded.source_row_hash</c> clause is what makes
/// ingestion cost proportional to change rather than to catalogue size: an unchanged row costs a
/// hash comparison and no write at all.
/// </para>
/// </remarks>
public sealed class AtomicMergeService
{
    private readonly CarparkDbContext _db;

    /// <summary>Creates the merge service.</summary>
    /// <param name="db">The database context.</param>
    public AtomicMergeService(CarparkDbContext db) => _db = db;

    /// <summary>
    /// Merges a run's staged rows into the catalogue inside one transaction.
    /// </summary>
    /// <param name="jobRunId">The run whose staged rows to apply.</param>
    /// <param name="mode">Delta or snapshot semantics.</param>
    /// <param name="observedAt">The ingestion timestamp stamped onto affected rows.</param>
    /// <param name="maximumDeactivationRatio">
    /// In snapshot mode, the largest fraction of the active catalogue that may be deactivated
    /// before the run aborts. Guards against a truncated or partially-transferred file wiping the
    /// catalogue.
    /// </param>
    /// <param name="cancellationToken">Cancels the merge.</param>
    /// <returns>How many rows were inserted, updated, left alone and deactivated.</returns>
    /// <exception cref="DeactivationGuardException">
    /// Snapshot mode would deactivate more than <paramref name="maximumDeactivationRatio"/> of the
    /// catalogue.
    /// </exception>
    public async Task<MergeCounts> MergeAsync(
        int jobRunId,
        IngestionMode mode,
        DateTimeOffset observedAt,
        double maximumDeactivationRatio,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var timestamp = observedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

            var staged = await CountStagedAsync(jobRunId, cancellationToken).ConfigureAwait(false);
            var matching = await CountMatchingAsync(jobRunId, cancellationToken).ConfigureAwait(false);
            var unchanged = await CountUnchangedAsync(jobRunId, cancellationToken).ConfigureAwait(false);

            var inserted = staged - matching;
            var updated = matching - unchanged;

            var deactivated = 0;
            if (mode == IngestionMode.Snapshot)
            {
                deactivated = await DeactivateAbsentAsync(
                    jobRunId, timestamp, maximumDeactivationRatio, cancellationToken).ConfigureAwait(false);
            }

            await UpsertAsync(jobRunId, timestamp, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new MergeCounts(inserted, updated, unchanged, deactivated);
        }
        catch
        {
            // Leaves `carpark` byte-for-byte as it was. The failure record itself is written by
            // JobRunRecorder on a SEPARATE connection - writing it here would roll back the
            // evidence along with the data.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Empties the staging table for a run. Called at the start and end of every run.</summary>
    /// <param name="jobRunId">The run whose staged rows to discard.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were discarded.</returns>
    public async Task<int> TruncateStagingAsync(int jobRunId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM carpark_staging WHERE job_run_id = {jobRunId}",
            cancellationToken).ConfigureAwait(false);

    private async Task UpsertAsync(int jobRunId, string timestamp, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO carpark (
                car_park_no, address, svy21_x, svy21_y, latitude, longitude,
                car_park_type_id, parking_system_type_id, short_term_parking_type_id,
                free_parking_type_id, has_night_parking, deck_count,
                gantry_height_m, has_height_restriction, gantry_height_raw, has_basement,
                source_row_hash, is_active,
                first_seen_at, last_seen_at, last_modified_at, last_job_run_id)
            SELECT
                s.car_park_no, s.address, s.svy21_x, s.svy21_y, s.latitude, s.longitude,
                s.car_park_type_id, s.parking_system_type_id, s.short_term_parking_type_id,
                s.free_parking_type_id, s.has_night_parking, s.deck_count,
                s.gantry_height_m, s.has_height_restriction, s.gantry_height_raw, s.has_basement,
                s.source_row_hash, 1,
                {timestamp}, {timestamp}, {timestamp}, s.job_run_id
            FROM carpark_staging s
            WHERE s.job_run_id = {jobRunId}
            ON CONFLICT (car_park_no) DO UPDATE SET
                address                    = excluded.address,
                svy21_x                    = excluded.svy21_x,
                svy21_y                    = excluded.svy21_y,
                latitude                   = excluded.latitude,
                longitude                  = excluded.longitude,
                car_park_type_id           = excluded.car_park_type_id,
                parking_system_type_id     = excluded.parking_system_type_id,
                short_term_parking_type_id = excluded.short_term_parking_type_id,
                free_parking_type_id       = excluded.free_parking_type_id,
                has_night_parking          = excluded.has_night_parking,
                deck_count                 = excluded.deck_count,
                gantry_height_m            = excluded.gantry_height_m,
                has_height_restriction     = excluded.has_height_restriction,
                gantry_height_raw          = excluded.gantry_height_raw,
                has_basement               = excluded.has_basement,
                source_row_hash            = excluded.source_row_hash,
                is_active                  = 1,
                last_seen_at               = excluded.last_seen_at,
                last_job_run_id            = excluded.last_job_run_id,
                -- Only move last_modified_at when something actually changed, and note that
                -- reactivating a previously inactive carpark IS a change.
                last_modified_at = CASE
                    WHEN carpark.source_row_hash <> excluded.source_row_hash OR carpark.is_active = 0
                    THEN excluded.last_modified_at
                    ELSE carpark.last_modified_at
                END
            """,
            cancellationToken).ConfigureAwait(false);

    private async Task<int> DeactivateAbsentAsync(
        int jobRunId, string timestamp, double maximumRatio, CancellationToken cancellationToken)
    {
        var active = await _db.Carparks.CountAsync(cancellationToken).ConfigureAwait(false);

        if (active == 0)
        {
            return 0;   // first ever run; nothing to protect
        }

        var wouldDeactivate = await _db.Carparks
            .Where(c => !_db.Set<CarparkStagingRow>()
                .Any(s => s.JobRunId == jobRunId && s.CarParkNo == c.CarParkNo))
            .CountAsync(cancellationToken).ConfigureAwait(false);

        var ratio = (double)wouldDeactivate / active;

        if (ratio > maximumRatio)
        {
            throw new DeactivationGuardException(
                $"Snapshot mode would deactivate {wouldDeactivate} of {active} active carparks "
                + $"({ratio:P1}), which exceeds the configured limit of {maximumRatio:P1}. "
                + "This usually means the file was truncated or only partially transferred.");
        }

        return await _db.Database.ExecuteSqlAsync(
            $"""
            UPDATE carpark
            SET is_active = 0, last_modified_at = {timestamp}, last_job_run_id = {jobRunId}
            WHERE is_active = 1
              AND car_park_no NOT IN (
                  SELECT car_park_no FROM carpark_staging WHERE job_run_id = {jobRunId})
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CountStagedAsync(int jobRunId, CancellationToken cancellationToken) =>
        await _db.Set<CarparkStagingRow>()
            .CountAsync(s => s.JobRunId == jobRunId, cancellationToken).ConfigureAwait(false);

    private async Task<int> CountMatchingAsync(int jobRunId, CancellationToken cancellationToken) =>
        await _db.Set<CarparkStagingRow>()
            .Where(s => s.JobRunId == jobRunId)
            .Join(_db.Carparks.IgnoreQueryFilters([CarparkDbContext.SoftDeleteFilter]),
                  s => s.CarParkNo, c => c.CarParkNo, (s, c) => s.Id)
            .CountAsync(cancellationToken).ConfigureAwait(false);

    private async Task<int> CountUnchangedAsync(int jobRunId, CancellationToken cancellationToken) =>
        await _db.Set<CarparkStagingRow>()
            .Where(s => s.JobRunId == jobRunId)
            .Join(_db.Carparks.IgnoreQueryFilters([CarparkDbContext.SoftDeleteFilter]),
                  s => s.CarParkNo, c => c.CarParkNo, (s, c) => new { s, c })
            .CountAsync(x => x.s.SourceRowHash == x.c.SourceRowHash && x.c.IsActive, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Thrown when snapshot mode would deactivate too much of the catalogue.</summary>
public sealed class DeactivationGuardException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">How many rows would have been deactivated, and the limit.</param>
    public DeactivationGuardException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public DeactivationGuardException()
        : base("Snapshot mode would deactivate more of the catalogue than the configured limit allows.") { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">How many rows would have been deactivated, and the limit.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DeactivationGuardException(string message, Exception innerException)
        : base(message, innerException) { }
}
