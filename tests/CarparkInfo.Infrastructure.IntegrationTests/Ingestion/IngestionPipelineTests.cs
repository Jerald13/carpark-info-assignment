using System.Text;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarparkInfo.Infrastructure.IntegrationTests.Ingestion;

/// <summary>
/// End-to-end ingestion: a real file, the real reader, the real validator, real SQLite.
/// </summary>
public sealed class IngestionPipelineTests : IAsyncLifetime
{
    private const string Header =
        "\"car_park_no\",\"address\",\"x_coord\",\"y_coord\",\"car_park_type\","
        + "\"type_of_parking_system\",\"short_term_parking\",\"free_parking\",\"night_parking\","
        + "\"car_park_decks\",\"gantry_height\",\"car_park_basement\"";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"carpark-pipeline-{Guid.NewGuid():N}.db");

    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), $"carpark-files-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2022, 8, 24, 1, 4, 0, TimeSpan.Zero));

    private CarparkDbContext _db = null!;
    private IDbContextFactory<CarparkDbContext> _factory = null!;
    private CarparkIngestionService _service = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workingDirectory);

        var options = new DbContextOptionsBuilder<CarparkDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        _db = new CarparkDbContext(options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        _factory = new PooledFactory(options);

        var context = new IngestionContext(_clock);
        _service = new CarparkIngestionService(
            new RecordSourceFactory([new CsvCarparkRecordSource(), new JsonCarparkRecordSource()]),
            new RecordValidator(),
            new JobRunStore(_db, _factory, context, NullLogger<JobRunStore>.Instance),
            new CarparkStagingStore(_db, new AtomicMergeService(_db)),
            new LookupResolver(_db),
            context,
            NullLogger<CarparkIngestionService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_file_is_ingested_end_to_end()
    {
        var path = WriteCsv("feed.csv", Row("ACB", gantry: "0.00"), Row("ACM", gantry: "2.10"));

        var result = await _service.IngestAsync(path, new IngestionOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(JobRunStatus.Succeeded);
        result.Counts.Read.Should().Be(2);
        result.Counts.Inserted.Should().Be(2);

        _db.ChangeTracker.Clear();
        var ct = TestContext.Current.CancellationToken;
        (await _db.Carparks.CountAsync(ct)).Should().Be(2);

        var unrestricted = await _db.Carparks.SingleAsync(c => c.CarParkNo == "ACB", ct);
        unrestricted.HeightRestriction.IsRestricted.Should().BeFalse(
            "0.00 survives the whole pipeline as 'no gantry'");
        unrestricted.Accommodates(2.0m).Should().BeTrue();
        unrestricted.Location.Latitude.Should().BeApproximately(1.301928, 0.000001,
            "SVY21 was converted during ingestion, not on read");
    }

    // ---------------------------------------------------------------------------------------
    // Idempotency
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Reprocessing_the_same_file_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteCsv("feed.csv", Row("ACB"));

        await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);
        var second = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        second.Status.Should().Be(JobRunStatus.Skipped,
            "idempotency by file hash is the precondition that makes automated retry safe");
        second.JobRunId.Should().BeNull("no run is even started");
    }

    [Fact]
    public async Task Force_reprocesses_an_already_ingested_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteCsv("feed.csv", Row("ACB"));

        await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);
        var forced = await _service.IngestAsync(
            path, new IngestionOptions { Force = true }, cancellationToken: ct);

        forced.Status.Should().Be(JobRunStatus.Succeeded);
        forced.Counts.Unchanged.Should().Be(1, "the content is identical, so nothing is written");
    }

    // ---------------------------------------------------------------------------------------
    // R7 - rollback, and the audit that must survive it
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invalid_row_rolls_back_the_entire_file()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a known-good catalogue.
        await _service.IngestAsync(WriteCsv("day1.csv", Row("AAA"), Row("BBB")),
            new IngestionOptions(), cancellationToken: ct);

        _db.ChangeTracker.Clear();
        var before = await _db.Carparks.CountAsync(ct);

        // Day 2 has three good rows and one with an unparseable coordinate.
        var path = WriteCsv("day2.csv",
            Row("CCC"), Row("DDD"), Row("EEE", x: "not-a-number"), Row("FFF"));

        var result = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        result.Status.Should().Be(JobRunStatus.RolledBack);

        _db.ChangeTracker.Clear();
        (await _db.Carparks.CountAsync(ct)).Should().Be(before,
            "R7: one bad row means the ENTIRE file is rejected - not three of four");
    }

    [Fact]
    public async Task A_failed_run_is_recorded_even_though_the_data_rolled_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteCsv("bad.csv", Row("AAA"), Row("BBB", x: "not-a-number"));

        var result = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        result.Status.Should().Be(JobRunStatus.RolledBack);

        // The classic audit-logging bug: writing the failure inside the transaction being rolled
        // back destroys the evidence along with the data. The store uses a separate connection.
        await using var audit = await _factory.CreateDbContextAsync(ct);

        var run = await audit.JobRuns.OrderByDescending(r => r.Id).FirstAsync(ct);
        run.Status.Should().Be(JobRunStatus.RolledBack,
            "the run record must survive the rollback that discarded the data");
        run.ErrorSummary.Should().NotBeNullOrWhiteSpace();

        var errors = await audit.JobRunErrors.Where(e => e.JobRunId == run.Id).ToListAsync(ct);
        errors.Should().NotBeEmpty("otherwise nobody can say why the feed did not load");
        errors[0].LineNumber.Should().Be(3, "the operator needs the exact line");
        errors[0].RawLine.Should().Contain("not-a-number", "and the offending text");
    }

    [Fact]
    public async Task Every_defect_is_reported_not_just_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteCsv("many-bad.csv",
            Row("AAA", x: "bad"), Row("BBB"), Row("CCC", decks: "nonsense"), Row("DDD", gantry: "99"));

        var result = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        result.Defects.Count(d => d.Severity == ErrorSeverity.Error).Should().Be(3,
            "stopping at the first defect would make an operator re-run three times to find three "
            + "problems; collecting produces one complete report");
    }

    [Fact]
    public async Task Staging_is_cleared_after_a_failed_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteCsv("bad.csv", Row("AAA"), Row("BBB", x: "bad"));

        await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        _db.ChangeTracker.Clear();
        (await _db.CarparkStaging.CountAsync(ct)).Should().Be(0,
            "otherwise the next run merges this run's partial garbage");
    }

    // ---------------------------------------------------------------------------------------
    // Warnings do not block
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_inconsistent_but_readable_row_is_warned_about_and_still_ingested()
    {
        var ct = TestContext.Current.CancellationToken;

        // BM4's real shape: a MULTI-STOREY carpark reporting zero decks.
        var path = WriteCsv("warn.csv",
            Row("BM4", type: "MULTI-STOREY CAR PARK", decks: "0", gantry: "2.15"));

        var result = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        result.Status.Should().Be(JobRunStatus.Succeeded);
        result.Defects.Should().ContainSingle(d => d.Severity == ErrorSeverity.Warning);

        _db.ChangeTracker.Clear();
        (await _db.Carparks.CountAsync(ct)).Should().Be(1,
            "rejecting reference data for being internally inconsistent is how a nightly feed "
            + "takes down production");
    }

    [Fact]
    public async Task An_unknown_carpark_type_is_auto_registered_rather_than_blocking_the_feed()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = WriteCsv("new-type.csv", Row("ZZZ", type: "FLOATING CAR PARK"));

        var result = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        result.Status.Should().Be(JobRunStatus.Succeeded);
        result.Defects.Should().Contain(d => d.ErrorCode == "UNKNOWN_LOOKUP_VALUE");

        _db.ChangeTracker.Clear();
        (await _db.CarParkTypes.CountAsync(ct)).Should().Be(8,
            "HDB introducing an eighth carpark type must not stop the nightly job");
    }

    // ---------------------------------------------------------------------------------------
    // R17 - the format seam, demonstrated
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_JSON_feed_produces_an_identical_result_to_the_equivalent_CSV()
    {
        var ct = TestContext.Current.CancellationToken;

        var jsonPath = Path.Combine(_workingDirectory, "feed.json");
        await File.WriteAllTextAsync(jsonPath,
            """
            [
              {
                "car_park_no": "ACB",
                "address": "BLK 270/271 ALBERT CENTRE BASEMENT CAR PARK",
                "x_coord": "30314.7936",
                "y_coord": "31490.4942",
                "car_park_type": "BASEMENT CAR PARK",
                "type_of_parking_system": "ELECTRONIC PARKING",
                "short_term_parking": "WHOLE DAY",
                "free_parking": "NO",
                "night_parking": "YES",
                "car_park_decks": "1",
                "gantry_height": "0.00",
                "car_park_basement": "Y"
              }
            ]
            """, ct);

        var result = await _service.IngestAsync(jsonPath, new IngestionOptions(),
            cancellationToken: ct);

        result.Status.Should().Be(JobRunStatus.Succeeded,
            "adding JSON support required one class and one DI registration - the ingestion "
            + "service, validator, hasher, staging store and merge are all untouched");
        result.Counts.Inserted.Should().Be(1);

        _db.ChangeTracker.Clear();
        var carpark = await _db.Carparks.SingleAsync(c => c.CarParkNo == "ACB", ct);
        carpark.HeightRestriction.IsRestricted.Should().BeFalse(
            "the 0.00 rule applies regardless of which format the bytes arrived in");
        carpark.Location.Latitude.Should().BeApproximately(1.301928, 0.000001);
    }

    [Fact]
    public void An_unsupported_format_is_rejected_with_a_helpful_message()
    {
        var factory = new RecordSourceFactory(
            [new CsvCarparkRecordSource(), new JsonCarparkRecordSource()]);

        var act = () => factory.Resolve("carparks.xlsx");

        act.Should().Throw<UnsupportedFormatException>().WithMessage("*csv*json*");
    }

    // ---------------------------------------------------------------------------------------
    // Schema drift
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_column_fails_the_run_rather_than_ingesting_shifted_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_workingDirectory, "drift.csv");
        await File.WriteAllTextAsync(path, "\"car_park_no\",\"address\"\n\"AAA\",\"SOMEWHERE\"", ct);

        var result = await _service.IngestAsync(path, new IngestionOptions(), cancellationToken: ct);

        result.Status.Should().Be(JobRunStatus.Failed);
        result.Summary.Should().Contain("SchemaDrift");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private string WriteCsv(string fileName, params string[] rows)
    {
        var path = Path.Combine(_workingDirectory, fileName);
        File.WriteAllText(path, Header + "\n" + string.Join("\n", rows), Encoding.UTF8);
        return path;
    }

    private static string Row(
        string carParkNo,
        string address = "BLK 270/271 ALBERT CENTRE BASEMENT CAR PARK",
        string x = "30314.7936",
        string y = "31490.4942",
        string type = "SURFACE CAR PARK",
        string decks = "0",
        string gantry = "0.00") =>
        $"\"{carParkNo}\",\"{address}\",\"{x}\",\"{y}\",\"{type}\",\"ELECTRONIC PARKING\","
        + $"\"WHOLE DAY\",\"NO\",\"YES\",\"{decks}\",\"{gantry}\",\"N\"";

    /// <summary>Minimal context factory: the audit path needs an independent connection.</summary>
    private sealed class PooledFactory : IDbContextFactory<CarparkDbContext>
    {
        private readonly DbContextOptions<CarparkDbContext> _options;

        public PooledFactory(DbContextOptions<CarparkDbContext> options) => _options = options;

        public CarparkDbContext CreateDbContext() => new(_options);
    }
}
