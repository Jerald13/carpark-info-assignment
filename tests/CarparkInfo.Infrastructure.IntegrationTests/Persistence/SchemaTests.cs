using CarparkInfo.Domain.Carparks;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Exercises the real schema against a real SQLite file.
/// </summary>
/// <remarks>
/// A file-backed database per test class, never the InMemory provider. InMemory does not enforce
/// foreign keys or unique constraints and produces no query plan, so it turns exactly the
/// assertions below into green tests that prove nothing.
/// </remarks>
public sealed class SchemaTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"carpark-schema-{Guid.NewGuid():N}.db");

    private CarparkDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        _db = new CarparkDbContext(new DbContextOptionsBuilder<CarparkDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options);

        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
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

    // ---------------------------------------------------------------------------------------
    // Seeded reference data - counts measured from the source file
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task All_four_lookup_tables_are_seeded()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _db.CarParkTypes.CountAsync(ct)).Should().Be(7);
        (await _db.ParkingSystemTypes.CountAsync(ct)).Should().Be(2);
        (await _db.ShortTermParkingTypes.CountAsync(ct)).Should().Be(4);
        (await _db.FreeParkingTypes.CountAsync(ct)).Should().Be(3);
    }

    [Fact]
    public async Task Free_parking_has_no_YES_value_and_two_offered_policies()
    {
        var ct = TestContext.Current.CancellationToken;
        var policies = await _db.FreeParkingTypes.ToListAsync(ct);

        policies.Should().NotContain(p => p.Code == "YES",
            "the source has no YES value; free parking is a schedule, and a filter written as "
            + "free_parking = 'YES' would silently match nothing");
        policies.Count(p => p.IsOffered).Should().Be(2);
        policies.Single(p => !p.IsOffered).Code.Should().Be("NONE");
    }

    [Fact]
    public async Task Lookup_ids_are_fixed_so_they_are_identical_across_environments()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _db.CarParkTypes.SingleAsync(t => t.Id == 1, ct)).Code.Should().Be("SURFACE");
        (await _db.FreeParkingTypes.SingleAsync(t => t.Id == 1, ct)).Code.Should().Be("NONE");
    }

    // ---------------------------------------------------------------------------------------
    // Constraints the schema must actually enforce
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        var ct = TestContext.Current.CancellationToken;

        // SQLite ignores foreign keys unless PRAGMA foreign_keys=ON is set per connection.
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        var enabled = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

        enabled.Should().Be(1,
            "a schema full of REFERENCES clauses that enforce nothing is worse than no "
            + "constraints, because it produces false confidence");
    }

    [Fact]
    public async Task Car_park_no_is_unique()
    {
        var ct = TestContext.Current.CancellationToken;

        _db.Carparks.Add(NewCarpark("ACB"));
        await _db.SaveChangesAsync(ct);

        _db.Carparks.Add(NewCarpark("ACB"));
        var act = async () => await _db.SaveChangesAsync(ct);

        await act.Should().ThrowAsync<DbUpdateException>(
            "car_park_no is the business key that ingestion matches on");
    }

    // ---------------------------------------------------------------------------------------
    // Value objects survive the round trip
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unrestricted_height_round_trips_as_unrestricted()
    {
        var ct = TestContext.Current.CancellationToken;

        _db.Carparks.Add(NewCarpark("SUR", gantryHeight: 0.00m));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        var loaded = await _db.Carparks.SingleAsync(c => c.CarParkNo == "SUR", ct);

        loaded.HeightRestriction.IsRestricted.Should().BeFalse();
        loaded.HeightRestriction.MaximumVehicleHeightMetres.Should().BeNull();
        loaded.HeightRestriction.RawSourceValue.Should().Be(0.00m, "the raw value is kept for audit");
        loaded.Accommodates(2.0m).Should().BeTrue();
    }

    [Fact]
    public async Task Decimal_precision_survives_the_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;

        _db.Carparks.Add(NewCarpark("MSC", gantryHeight: 2.15m));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        var loaded = await _db.Carparks.SingleAsync(c => c.CarParkNo == "MSC", ct);

        loaded.HeightRestriction.MaximumVehicleHeightMetres.Should().Be(2.15m,
            "SQLite's dynamic typing will store a REAL where a DECIMAL was intended and turn "
            + "2.15 into 2.1499999 unless precision is declared");
    }

    [Fact]
    public async Task Coordinates_round_trip_in_both_representations()
    {
        var ct = TestContext.Current.CancellationToken;

        _db.Carparks.Add(NewCarpark("ACB"));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        var loaded = await _db.Carparks.SingleAsync(c => c.CarParkNo == "ACB", ct);

        loaded.Location.Svy21X.Should().BeApproximately(30314.7936, 0.0001);
        loaded.Location.Latitude.Should().BeApproximately(1.301928, 0.000001);
    }

    // ---------------------------------------------------------------------------------------
    // Soft delete
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Deactivated_carparks_are_hidden_by_the_named_query_filter()
    {
        var ct = TestContext.Current.CancellationToken;

        var carpark = NewCarpark("OLD");
        _db.Carparks.Add(carpark);
        await _db.SaveChangesAsync(ct);

        carpark.Deactivate(DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        (await _db.Carparks.CountAsync(ct)).Should().Be(0, "the soft-delete filter applies by default");

        var withInactive = await _db.Carparks
            .IgnoreQueryFilters([CarparkDbContext.SoftDeleteFilter])
            .CountAsync(ct);

        withInactive.Should().Be(1,
            "EF Core 10 named filters let the admin path disable exactly this one filter");
    }

    private static Carpark NewCarpark(string carParkNo, decimal gantryHeight = 1.80m) =>
        new(carParkNo,
            "BLK 270/271 ALBERT CENTRE BASEMENT CAR PARK",
            Location.FromSvy21(30314.7936, 31490.4942),
            carParkTypeId: 1,
            parkingSystemTypeId: 1,
            shortTermParkingTypeId: 1,
            freeParkingTypeId: 1,
            hasNightParking: true,
            deckCount: 1,
            heightRestriction: HeightRestriction.FromSource(gantryHeight),
            hasBasement: true,
            sourceRowHash: $"hash-{carParkNo}",
            observedAt: new DateTimeOffset(2022, 8, 24, 1, 4, 0, TimeSpan.Zero));
}
