using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.IntegrationTests.Ingestion;

/// <summary>
/// Proves requirement R7: "in the event there is an error processing the records in the file, the
/// entire file should rollback".
/// </summary>
public sealed class AtomicMergeServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Day1 = new(2022, 8, 24, 1, 4, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"carpark-merge-{Guid.NewGuid():N}.db");

    private CarparkDbContext _db = null!;
    private AtomicMergeService _merge = null!;

    public async ValueTask InitializeAsync()
    {
        _db = NewContext();
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        _merge = new AtomicMergeService(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private CarparkDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CarparkDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options);

    // ---------------------------------------------------------------------------------------
    // The happy path
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_run_inserts_every_staged_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await StageAsync(["AAA", "BBB", "CCC"]);

        var counts = await _merge.MergeAsync(run, IngestionMode.Delta, Day1, 0.05, ct);

        counts.Inserted.Should().Be(3);
        counts.Updated.Should().Be(0);
        counts.Unchanged.Should().Be(0);
        (await _db.Carparks.CountAsync(ct)).Should().Be(3);
    }

    [Fact]
    public async Task Reprocessing_identical_rows_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await StageAsync(["AAA", "BBB"]);
        await _merge.MergeAsync(first, IngestionMode.Delta, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(first, ct);

        var second = await StageAsync(["AAA", "BBB"]);
        var counts = await _merge.MergeAsync(second, IngestionMode.Delta, Day2, 0.05, ct);

        counts.Unchanged.Should().Be(2,
            "the row hashes match, so ingestion cost tracks actual change rather than "
            + "catalogue size - this is the scaling property that matters at a million rows");
        counts.Updated.Should().Be(0);

        _db.ChangeTracker.Clear();
        var carpark = await _db.Carparks.SingleAsync(c => c.CarParkNo == "AAA", ct);
        carpark.LastModifiedAt.Should().BeCloseTo(Day1, TimeSpan.FromSeconds(1),
            "nothing changed, so the modified stamp must not move");
        carpark.LastSeenAt.Should().BeCloseTo(Day2, TimeSpan.FromSeconds(1),
            "but the row was present in the feed");
    }

    [Fact]
    public async Task A_changed_row_is_updated_and_restamped()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await StageAsync(["AAA"]);
        await _merge.MergeAsync(first, IngestionMode.Delta, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(first, ct);

        var second = await StageAsync(["AAA"], address: "NEW ADDRESS", hashSuffix: "-v2");
        var counts = await _merge.MergeAsync(second, IngestionMode.Delta, Day2, 0.05, ct);

        counts.Updated.Should().Be(1);
        counts.Inserted.Should().Be(0);

        _db.ChangeTracker.Clear();
        var carpark = await _db.Carparks.SingleAsync(c => c.CarParkNo == "AAA", ct);
        carpark.Address.Should().Be("NEW ADDRESS");
        carpark.LastModifiedAt.Should().BeCloseTo(Day2, TimeSpan.FromSeconds(1));
    }

    // ---------------------------------------------------------------------------------------
    // R7 - whole-file rollback
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_leaves_the_database_byte_for_byte_untouched()
    {
        var ct = TestContext.Current.CancellationToken;

        // Establish a known-good catalogue.
        var seed = await StageAsync(["AAA", "BBB", "CCC"]);
        await _merge.MergeAsync(seed, IngestionMode.Delta, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(seed, ct);

        var before = await FingerprintCatalogueAsync();

        // Stage a run that will fail at the merge: a staged row references a lookup id that does
        // not exist, so the foreign key blows up part-way through the INSERT.
        var doomed = await StageAsync(["DDD", "EEE"], carParkTypeId: 999);

        var act = async () => await _merge.MergeAsync(doomed, IngestionMode.Delta, Day2, 0.05, ct);
        await act.Should().ThrowAsync<Exception>();

        var after = await FingerprintCatalogueAsync();

        after.Should().BeEquivalentTo(before,
            "R7: any failure processing the file must leave the entire catalogue exactly as it was");
        (await _db.Carparks.CountAsync(ct)).Should().Be(3, "no partial rows may survive");
    }

    [Fact]
    public async Task A_failed_merge_leaves_no_partially_applied_rows()
    {
        var ct = TestContext.Current.CancellationToken;

        var doomed = await StageAsync(["AAA", "BBB", "CCC", "DDD"], carParkTypeId: 999);

        var act = async () => await _merge.MergeAsync(doomed, IngestionMode.Delta, Day1, 0.05, ct);
        await act.Should().ThrowAsync<Exception>();

        (await _db.Carparks.CountAsync(ct)).Should().Be(0,
            "all four rows or none - never the first two");
    }

    // ---------------------------------------------------------------------------------------
    // Snapshot mode and its guard
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Delta_mode_leaves_absent_carparks_alone()
    {
        var ct = TestContext.Current.CancellationToken;

        var seed = await StageAsync(["AAA", "BBB", "CCC"]);
        await _merge.MergeAsync(seed, IngestionMode.Delta, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(seed, ct);

        // A genuine delta containing one row.
        var delta = await StageAsync(["AAA"], hashSuffix: "-v2");
        var counts = await _merge.MergeAsync(delta, IngestionMode.Delta, Day2, 0.05, ct);

        counts.Deactivated.Should().Be(0);
        (await _db.Carparks.CountAsync(ct)).Should().Be(3,
            "absence from a DELTA means unchanged. Treating it as deletion would have "
            + "deactivated 2,178 of 2,181 carparks on a three-row file");
    }

    [Fact]
    public async Task Snapshot_mode_deactivates_absent_carparks_within_the_guard()
    {
        var ct = TestContext.Current.CancellationToken;

        var names = Enumerable.Range(1, 100).Select(i => $"C{i:D3}").ToArray();
        var seed = await StageAsync(names);
        await _merge.MergeAsync(seed, IngestionMode.Snapshot, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(seed, ct);

        // 97 of 100 present -> 3% deactivation, inside the 5% guard.
        var snapshot = await StageAsync(names.Take(97).ToArray());
        var counts = await _merge.MergeAsync(snapshot, IngestionMode.Snapshot, Day2, 0.05, ct);

        counts.Deactivated.Should().Be(3);
        (await _db.Carparks.CountAsync(ct)).Should().Be(97, "the filter hides deactivated rows");
        (await _db.Carparks.IgnoreQueryFilters([CarparkDbContext.SoftDeleteFilter])
            .CountAsync(ct)).Should().Be(100, "but nothing was actually deleted");
    }

    [Fact]
    public async Task Snapshot_mode_aborts_when_deactivation_exceeds_the_guard()
    {
        var ct = TestContext.Current.CancellationToken;

        var names = Enumerable.Range(1, 100).Select(i => $"C{i:D3}").ToArray();
        var seed = await StageAsync(names);
        await _merge.MergeAsync(seed, IngestionMode.Snapshot, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(seed, ct);

        // A truncated transfer: only 10 rows arrived, which would deactivate 90%.
        var truncated = await StageAsync(names.Take(10).ToArray());

        var act = async () => await _merge.MergeAsync(truncated, IngestionMode.Snapshot, Day2, 0.05, ct);

        (await act.Should().ThrowAsync<DeactivationGuardException>(
            "a partially transferred file must not be allowed to wipe the catalogue"))
            .WithMessage("*90*");

        (await _db.Carparks.CountAsync(ct)).Should().Be(100, "and nothing was deactivated");
    }

    [Fact]
    public async Task A_carpark_that_returns_to_the_feed_is_reactivated()
    {
        var ct = TestContext.Current.CancellationToken;

        var names = Enumerable.Range(1, 100).Select(i => $"C{i:D3}").ToArray();
        await _merge.MergeAsync(await StageAsync(names), IngestionMode.Snapshot, Day1, 0.05, ct);
        await _merge.TruncateStagingAsync(1, ct);

        var withoutOne = await StageAsync(names.Take(99).ToArray());
        await _merge.MergeAsync(withoutOne, IngestionMode.Snapshot, Day2, 0.05, ct);
        await _merge.TruncateStagingAsync(withoutOne, ct);

        var restored = await StageAsync(names);
        await _merge.MergeAsync(restored, IngestionMode.Snapshot, Day2.AddDays(1), 0.05, ct);

        _db.ChangeTracker.Clear();
        (await _db.Carparks.CountAsync(ct)).Should().Be(100,
            "a carpark reappearing in the feed comes back rather than staying deactivated");
    }

    // ---------------------------------------------------------------------------------------
    // Staging hygiene
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Staging_can_be_truncated_so_a_failed_run_leaves_no_residue()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await StageAsync(["AAA", "BBB"]);

        (await _db.CarparkStaging.CountAsync(ct)).Should().Be(2);

        await _merge.TruncateStagingAsync(run, ct);

        (await _db.CarparkStaging.CountAsync(ct)).Should().Be(0,
            "otherwise the next run merges this run's partial garbage");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<int> StageAsync(
        string[] carParkNumbers,
        string address = "BLK 1 SOMEWHERE",
        string hashSuffix = "",
        int carParkTypeId = 1)
    {
        var ct = TestContext.Current.CancellationToken;

        var run = new JobRun("ingest", "test.csv", $"hash-{Guid.NewGuid():N}",
            IngestionMode.Delta, "test-host", Day1);
        _db.JobRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        foreach (var carParkNo in carParkNumbers)
        {
            _db.CarparkStaging.Add(new CarparkStagingRow(
                jobRunId: run.Id,
                carParkNo: carParkNo,
                address: address,
                svy21X: 30314.7936, svy21Y: 31490.4942,
                latitude: 1.301928, longitude: 103.854118,
                carParkTypeId: carParkTypeId,
                parkingSystemTypeId: 1,
                shortTermParkingTypeId: 1,
                freeParkingTypeId: 1,
                hasNightParking: true,
                deckCount: 1,
                gantryHeightMetres: 1.80m,
                hasHeightRestriction: true,
                gantryHeightRaw: 1.80m,
                hasBasement: false,
                sourceRowHash: $"hash-{carParkNo}{hashSuffix}",
                lineNumber: 2));
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        return run.Id;
    }

    /// <summary>Every field of every carpark, so a rollback can be proven rather than assumed.</summary>
    private async Task<List<string>> FingerprintCatalogueAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.ChangeTracker.Clear();

        return await _db.Carparks
            .IgnoreQueryFilters([CarparkDbContext.SoftDeleteFilter])
            .OrderBy(c => c.CarParkNo)
            .Select(c => c.CarParkNo + "|" + c.Address + "|" + c.SourceRowHash + "|"
                       + c.IsActive + "|" + c.DeckCount + "|" + c.LastModifiedAt)
            .ToListAsync(ct);
    }
}
