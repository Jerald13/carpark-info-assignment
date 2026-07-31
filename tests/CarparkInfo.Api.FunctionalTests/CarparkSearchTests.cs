using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>
/// The three user stories, exercised over real HTTP against all 2,181 carparks.
/// </summary>
public sealed class CarparkSearchTests : IClassFixture<CarparkApiFactory>
{
    private readonly HttpClient _client;

    public CarparkSearchTests(CarparkApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------
    // The user stories, with counts pinned to the profiled dataset
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_catalogue_is_searchable()
    {
        (await TotalForAsync("")).Should().Be(2181);
    }

    /// <summary>User story 1.</summary>
    [Fact]
    public async Task Free_parking_filter_returns_1605_carparks()
    {
        (await TotalForAsync("freeParking=true")).Should().Be(1605,
            "free parking is a schedule - 'offered' means the policy is not NONE. A filter "
            + "written against a YES value would match nothing, because the source has none");
    }

    /// <summary>User story 2.</summary>
    [Fact]
    public async Task Night_parking_filter_returns_1795_carparks()
    {
        (await TotalForAsync("nightParking=true")).Should().Be(1795);
    }

    /// <summary>User story 3 - the one this whole solution is organised around.</summary>
    [Fact]
    public async Task Vehicle_height_filter_includes_carparks_with_no_gantry()
    {
        var correct = await TotalForAsync("vehicleHeight=2.0");

        correct.Should().Be(2056,
            "477 surface carparks carry a source gantry height of 0.00, meaning there is no "
            + "gantry rather than zero clearance. A literal comparison returns 1,579 and "
            + "silently hides 23% of the catalogue - specifically the open-air carparks that "
            + "accommodate any vehicle");
    }

    [Theory]
    [InlineData(1.5, 2181)]   // below the lowest real gantry (1.70 m): everything fits
    [InlineData(2.0, 2056)]
    [InlineData(2.15, 1890)]  // 2.15 m is the most common clearance in the catalogue
    [InlineData(5.5, 544)]    // taller than every gantry: only the unrestricted ones remain
    public async Task The_height_filter_narrows_monotonically(double height, int expected)
    {
        (await TotalForAsync($"vehicleHeight={height}")).Should().Be(expected);
    }

    [Fact]
    public async Task A_vehicle_taller_than_every_gantry_still_finds_the_unrestricted_carparks()
    {
        (await TotalForAsync("vehicleHeight=9.0")).Should().Be(544,
            "544 carparks have no height limit at all - 477 with no gantry plus 67 flagged "
            + "unlimited. A naive filter would return zero and tell the user to go home");
    }

    [Fact]
    public async Task The_three_filters_combine_with_AND()
    {
        var combined = await TotalForAsync("freeParking=true&nightParking=true&vehicleHeight=2.0");

        combined.Should().Be(1348);
        combined.Should().BeLessThan(1605, "combining filters must narrow the result");
    }

    [Fact]
    public async Task Night_parking_and_free_parking_are_independent()
    {
        var night = await TotalForAsync("nightParking=true");
        var free = await TotalForAsync("freeParking=true");
        var both = await TotalForAsync("nightParking=true&freeParking=true");

        (night - both).Should().Be(350,
            "350 carparks offer night parking while charging for it, so no combined "
            + "'is it free' heuristic would be correct");
        free.Should().BeGreaterThan(both);
    }

    // ---------------------------------------------------------------------------------------
    // The response shape that stops clients re-implementing the bug
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unrestricted_carpark_reports_a_null_limit_rather_than_zero()
    {
        var carpark = await GetJsonAsync("/api/v1/carparks/AK19");
        var height = carpark.GetProperty("heightRestriction");

        height.GetProperty("isRestricted").GetBoolean().Should().BeFalse();
        height.GetProperty("maxVehicleHeightMetres").ValueKind.Should().Be(JsonValueKind.Null,
            "exposing 0.0 here would invite every client to re-implement the bug; null cannot "
            + "be misread");
    }

    [Fact]
    public async Task A_restricted_carpark_reports_its_limit()
    {
        var height = (await GetJsonAsync("/api/v1/carparks/ACB")).GetProperty("heightRestriction");

        height.GetProperty("isRestricted").GetBoolean().Should().BeTrue();
        height.GetProperty("maxVehicleHeightMetres").GetDecimal().Should().Be(1.80m);
    }

    [Fact]
    public async Task Results_carry_map_ready_coordinates()
    {
        var location = (await GetJsonAsync("/api/v1/carparks/ACB")).GetProperty("location");

        location.GetProperty("latitude").GetDouble().Should().BeApproximately(1.301928, 0.000001,
            "a front-end cannot plot SVY21, so the conversion happens here rather than there");
        location.GetProperty("longitude").GetDouble().Should().BeApproximately(103.854118, 0.000001);
        location.GetProperty("svy21X").GetDouble().Should().BeApproximately(30314.7936, 0.001,
            "the source values are retained as the record of truth");
    }

    [Fact]
    public async Task An_address_containing_commas_survives_the_whole_pipeline()
    {
        var carpark = await GetJsonAsync("/api/v1/carparks/C10");

        carpark.GetProperty("address").GetString()
            .Should().Be("BLK 339,341,344-345,371-381 CLEMENTI AVENUE 5",
                "four commas, parsed as one field, all the way from CSV to JSON");
    }

    // ---------------------------------------------------------------------------------------
    // Paging
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Keyset_pagination_walks_the_catalogue_without_repeating()
    {
        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 5; page++)
        {
            var url = $"/api/v1/carparks?pageSize=10{(cursor is null ? "" : $"&cursor={cursor}")}";
            var response = await GetJsonAsync(url);

            foreach (var item in response.GetProperty("data").EnumerateArray())
            {
                seen.Add(item.GetProperty("carParkNo").GetString()!);
            }

            var next = response.GetProperty("pagination").GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            cursor.Should().NotBeNull("there are 2,181 carparks, so five pages of ten is not the end");
        }

        seen.Should().HaveCount(50);
        seen.Should().OnlyHaveUniqueItems(
            "a cursor that does not advance would repeat the first page for ever");
        seen.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_cursor_is_url_safe()
    {
        var cursor = (await GetJsonAsync("/api/v1/carparks?pageSize=3"))
            .GetProperty("pagination").GetProperty("nextCursor").GetString();

        cursor.Should().NotBeNullOrEmpty();
        cursor.Should().NotContain("+", "standard Base64 emits characters a query string mangles");
        cursor.Should().NotContain("/");
        cursor.Should().NotContain("=");
    }

    [Fact]
    public async Task An_unreadable_cursor_starts_from_the_beginning_rather_than_failing()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/carparks?pageSize=3&cursor=not-a-real-cursor", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a malformed cursor is client input; the worst outcome should be a repeated page");
    }

    [Fact]
    public async Task Page_size_is_capped()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/carparks?pageSize=5000", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unbounded page size is a denial-of-service vector");
    }

    // ---------------------------------------------------------------------------------------
    // Radius search
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Radius_search_returns_nearby_carparks_ordered_by_distance()
    {
        // Albert Centre, Rochor.
        var response = await GetJsonAsync(
            "/api/v1/carparks?lat=1.3009&lon=103.8546&radiusKm=1&sort=distance&pageSize=20");

        var results = response.GetProperty("data").EnumerateArray().ToList();

        results.Should().NotBeEmpty();
        results.Select(r => r.GetProperty("carParkNo").GetString())
            .Should().Contain("ACB", "Albert Centre's own carpark is within 1 km of Albert Centre");

        var distances = results.Select(r => r.GetProperty("distanceKm").GetDouble()).ToList();
        distances.Should().BeInAscendingOrder();
        distances.Should().OnlyContain(d => d <= 1.0,
            "a bounding box alone would include corners 41% beyond the radius, so an exact "
            + "haversine pass filters the survivors");
    }

    [Fact]
    public async Task Distance_is_absent_when_no_radius_search_was_made()
    {
        var carpark = (await GetJsonAsync("/api/v1/carparks?pageSize=1"))
            .GetProperty("data")[0];

        carpark.GetProperty("distanceKm").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---------------------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("vehicleHeight=99")]
    [InlineData("vehicleHeight=0")]
    [InlineData("pageSize=0")]
    [InlineData("lat=1.3")]                       // incomplete radius search
    [InlineData("lat=1.3&lon=103.8&radiusKm=999")]
    [InlineData("sort=distance")]                 // distance sort with no centre
    public async Task Invalid_parameters_return_a_problem_details_response(string query)
    {
        var response = await _client.GetAsync(
            new Uri($"/api/v1/carparks?{query}", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_unknown_carpark_returns_404()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/carparks/NOPE", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------------------------------
    // Lookups
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Lookups_expose_the_real_distribution_so_the_filter_UI_is_data_driven()
    {
        var lookups = await GetJsonAsync("/api/v1/carparks/lookups");

        var types = lookups.GetProperty("carParkTypes").EnumerateArray()
            .ToDictionary(t => t.GetProperty("code").GetString()!, t => t.GetProperty("count").GetInt32());

        types.Should().HaveCount(7);
        types["SURFACE"].Should().Be(1087);
        types["MULTI_STOREY"].Should().Be(1033);
        types["MECHANISED"].Should().Be(1);

        var height = lookups.GetProperty("vehicleHeight");
        height.GetProperty("unrestrictedCount").GetInt32().Should().Be(544);
        height.GetProperty("minimumMetres").GetDecimal().Should().Be(1.70m);
        height.GetProperty("maximumMetres").GetDecimal().Should().Be(5.40m);
        height.GetProperty("commonPresets").EnumerateArray().Should().NotBeEmpty(
            "presets come from the real distribution so the picker offers plausible choices");
    }

    [Fact]
    public async Task Free_parking_lookups_never_contain_a_YES_value()
    {
        var codes = (await GetJsonAsync("/api/v1/carparks/lookups"))
            .GetProperty("freeParking").EnumerateArray()
            .Select(t => t.GetProperty("code").GetString())
            .ToList();

        codes.Should().NotContain("YES", "the source has no YES; free parking is a schedule");
        codes.Should().Contain("NONE");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<int> TotalForAsync(string query)
    {
        var response = await GetJsonAsync($"/api/v1/carparks?{query}&includeTotal=true&pageSize=1");
        return response.GetProperty("pagination").GetProperty("totalCount").GetInt32();
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
