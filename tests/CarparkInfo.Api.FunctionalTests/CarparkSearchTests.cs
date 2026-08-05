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

    // -------------------------------------------------------------------------------------------
    // The boolean filters are TRI-STATE, and only two of the three states were ever tested.
    //
    // ApplyFilters read `if (filter.NightParkingOnly == true)`, so false collapsed into null and
    // applied no filter at all: ?nightParking=false returned the whole catalogue, including the
    // 1,795 carparks that DO offer night parking. Every existing test passed, because every one
    // of them asked for true or omitted the parameter. Nobody asked for false.
    //
    // A parameter the API accepts and silently ignores is worse than one it rejects - the caller
    // gets 200 and a plausible-looking list, and has no way to know it is the wrong list.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Free_parking_false_returns_only_carparks_WITHOUT_free_parking()
    {
        var without = await TotalForAsync("freeParking=false");

        without.Should().Be(576,
            "false is a real request, not the absence of one: 2,181 total minus the 1,605 that "
            + "offer free parking");

        (without + await TotalForAsync("freeParking=true")).Should().Be(2181,
            "true and false must partition the catalogue exactly, with nothing counted twice "
            + "and nothing missed");
    }

    [Fact]
    public async Task Night_parking_false_returns_only_carparks_WITHOUT_night_parking()
    {
        var without = await TotalForAsync("nightParking=false");

        without.Should().Be(386, "2,181 total minus the 1,795 that offer night parking");

        (without + await TotalForAsync("nightParking=true")).Should().Be(2181);
    }

    [Fact]
    public async Task Omitting_a_boolean_filter_is_not_the_same_as_sending_false()
    {
        var omitted = await TotalForAsync("");
        var sentFalse = await TotalForAsync("nightParking=false");

        omitted.Should().Be(2181, "omitted means 'do not filter'");
        sentFalse.Should().Be(386, "false means 'only those without'");

        sentFalse.Should().NotBe(omitted,
            "if these are equal the parameter is being ignored, which is exactly the defect "
            + "this test exists to catch");
    }

    [Fact]
    public async Task The_two_boolean_filters_combine_correctly()
    {
        // 1,445 + 226 + 160 + 350 = 2,181. Every carpark falls in exactly one quadrant.
        (await TotalForAsync("freeParking=true&nightParking=true")).Should().Be(1445);
        (await TotalForAsync("freeParking=true&nightParking=false")).Should().Be(160);
        (await TotalForAsync("freeParking=false&nightParking=true")).Should().Be(350);
        (await TotalForAsync("freeParking=false&nightParking=false")).Should().Be(226);
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

    /// <summary>
    /// An unreadable cursor is rejected, not quietly treated as no cursor at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserted the opposite until it was shown to be wrong in use.</b> It read
    /// <c>An_unreadable_cursor_starts_from_the_beginning_rather_than_failing</c>, on the reasoning
    /// that a malformed cursor is client input and the worst outcome should be a repeated page.
    /// </para>
    /// <para>
    /// The premise was right - a malformed cursor must never be a 500 - but the conclusion did not
    /// follow. Decode swallowed every failure and returned an empty key, which the repository turned
    /// into <c>car_park_no &gt; ''</c>: every row. So <c>?cursor=100</c> returned page one with a
    /// 200, indistinguishable from a real page. A client whose cursor was truncated in transit would
    /// re-read page one for ever, reporting success the whole time, and someone experimenting in
    /// Swagger has no way to learn that the value they typed meant nothing.
    /// </para>
    /// <para>
    /// 400 is not "failing" in the sense the old test meant. It is the difference between a 500 -
    /// which leaks and alarms - and an honest answer: the request was malformed, and no page of
    /// results is the right response to it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unreadable_cursor_is_rejected_rather_than_silently_ignored()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/carparks?pageSize=3&cursor=not-a-real-cursor", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "returning page one for a cursor the API never issued is a silent lie: the caller "
            + "gets a 200 and a plausible page, and no way to know their cursor meant nothing");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errors").TryGetProperty("Cursor", out _).Should().BeTrue(
            "the error must name the parameter that was wrong");
    }

    [Fact]
    public async Task A_cursor_the_API_issued_is_accepted()
    {
        var first = await GetJsonAsync("/api/v1/carparks?pageSize=3");
        var cursor = first.GetProperty("pagination").GetProperty("nextCursor").GetString()!;

        var second = await _client.GetAsync(
            new Uri($"/api/v1/carparks?pageSize=3&cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), Ct);

        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "rejecting invented cursors must not also reject real ones");
    }

    [Fact]
    public async Task An_unrecognised_sort_is_rejected_rather_than_falling_back()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/carparks?sort=distence", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a typo used to fall through to carParkNo and return an ordinary alphabetical page, so "
            + "'sort=distence' looked like a working request that simply found nothing near you");
    }

    /// <summary>
    /// Walks every page of the whole catalogue and checks the set that came back.
    /// </summary>
    /// <remarks>
    /// The other paging tests check one page, or one boundary. This one walks all 2,181 carparks and
    /// asserts the union is exactly the catalogue - every row once, none twice, none stranded. That
    /// is the only assertion that can catch an off-by-one at a page boundary, which is where keyset
    /// paging goes wrong: an inclusive comparison repeats the boundary row on every page, and a
    /// mis-encoded cursor drops it.
    /// <para>
    /// Page size 200 keeps this to 11 requests. The rate limiter permits 100 per minute per caller,
    /// so a walk at pageSize=1 would exhaust the budget and fail with 429 rather than a paging
    /// error - which is the limiter working, not a defect.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Paging_through_the_whole_catalogue_returns_every_carpark_exactly_once()
    {
        var seen = new List<string>();
        string? cursor = null;
        var requests = 0;

        do
        {
            var url = "/api/v1/carparks?pageSize=200"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

            var body = await GetJsonAsync(url);
            requests++;

            seen.AddRange(body.GetProperty("data").EnumerateArray()
                .Select(c => c.GetProperty("carParkNo").GetString()!));

            var pagination = body.GetProperty("pagination");
            cursor = pagination.GetProperty("hasMore").GetBoolean()
                ? pagination.GetProperty("nextCursor").GetString()
                : null;

            requests.Should().BeLessThan(50, "2,181 rows at 200 per page is 11 requests; more means "
                + "the walk is not advancing and the cursor is stuck");
        }
        while (cursor is not null);

        seen.Should().HaveCount(2181, "every carpark must be reached exactly once");
        seen.Distinct().Should().HaveCount(2181, "a boundary row repeated on two pages is the "
            + "classic keyset off-by-one");
        seen.Should().BeInAscendingOrder(StringComparer.Ordinal,
            "the cursor advances by key, so the walk is ordered by definition. If it is not, the "
            + "cursor and the ORDER BY disagree");
    }

    [Fact]
    public async Task Pagination_is_the_first_field_in_the_response()
    {
        var raw = await _client.GetStringAsync(
            new Uri("/api/v1/carparks?pageSize=1", UriKind.Relative), Ct);

        raw.IndexOf("\"pagination\"", StringComparison.Ordinal)
            .Should().BeLessThan(raw.IndexOf("\"data\"", StringComparison.Ordinal),
                "whether there is another page is the one field read on every response. Behind "
                + "twenty full carpark objects it means scrolling to the bottom every time");
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
