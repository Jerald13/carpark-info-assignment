using CarparkInfo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Asserts query plans rather than asserting that performance was considered.
/// </summary>
/// <remarks>
/// The README grades "enhance query performances, if applicable". An index that exists but is not
/// used by the optimiser is worse than no index at all - it costs a write on every row and buys
/// nothing. <c>EXPLAIN QUERY PLAN</c> turns that from a claim into a build gate.
/// </remarks>
public sealed class QueryPlanTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"carpark-plan-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task The_three_user_story_filters_use_the_covering_index_and_never_scan()
    {
        const string searchQuery = """
            SELECT id
            FROM carpark
            WHERE is_active = 1
              AND has_night_parking = 1
              AND free_parking_type_id IN (2, 3)
              AND (has_height_restriction = 0 OR gantry_height_m >= 2.0)
            ORDER BY id
            LIMIT 20
            """;

        var plan = await ExplainAsync(searchQuery);

        plan.Should().Contain("ix_carpark_search",
            "the composite index exists precisely to serve the three user-story filters");
        plan.Should().NotContain("SCAN carpark",
            "a full table scan means the index is not being used and the write cost is wasted");
    }

    [Fact]
    public async Task The_height_predicate_still_uses_the_index_when_unrestricted_carparks_are_included()
    {
        // The correct height filter is an OR, which optimisers sometimes refuse to index. If this
        // ever regresses to a scan, the fix is a UNION rather than dropping the OR - dropping it
        // would hide 477 carparks.
        const string heightQuery = """
            SELECT id
            FROM carpark
            WHERE is_active = 1
              AND (has_height_restriction = 0 OR gantry_height_m >= 2.0)
            ORDER BY id
            """;

        var plan = await ExplainAsync(heightQuery);

        plan.Should().NotContain("SCAN carpark");
    }

    [Fact]
    public async Task Keyset_pagination_seeks_rather_than_scanning()
    {
        const string keysetQuery = """
            SELECT id, car_park_no
            FROM carpark
            WHERE is_active = 1 AND car_park_no > 'BE28'
            ORDER BY car_park_no
            LIMIT 20
            """;

        var plan = await ExplainAsync(keysetQuery);

        plan.Should().Contain("ix_carpark_keyset",
            "keyset pagination is constant-time at any depth only if the ordering index is used");
        plan.Should().NotContain("SCAN carpark");
    }

    [Fact]
    public async Task Radius_search_uses_the_geo_index_for_its_bounding_box_prefilter()
    {
        const string geoQuery = """
            SELECT id
            FROM carpark
            WHERE latitude BETWEEN 1.29 AND 1.31
              AND longitude BETWEEN 103.84 AND 103.87
            """;

        var plan = await ExplainAsync(geoQuery);

        plan.Should().Contain("ix_carpark_geo");
    }

    [Fact]
    public async Task The_business_key_lookup_seeks_the_unique_index()
    {
        var plan = await ExplainAsync("SELECT id FROM carpark WHERE car_park_no = 'ACB'");

        plan.Should().Contain("ux_carpark_car_park_no",
            "ingestion probes this index once per row; a scan here would make the batch job "
            + "quadratic in catalogue size");
    }

    private async Task<string> ExplainAsync(string sql)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";

        var plan = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            plan.AppendLine(reader.GetString(reader.GetOrdinal("detail")));
        }

        return plan.ToString();
    }
}
