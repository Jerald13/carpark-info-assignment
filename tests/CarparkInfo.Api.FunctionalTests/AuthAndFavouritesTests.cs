using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>
/// Authentication, token rotation and favourites, over real HTTP.
/// </summary>
public sealed class AuthAndFavouritesTests : IClassFixture<CarparkApiFactory>
{
    private readonly CarparkApiFactory _factory;

    public AuthAndFavouritesTests(CarparkApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------
    // Registration and sign-in
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_registered_user_can_sign_in_and_receives_both_tokens()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();

        await RegisterAsync(client, email);
        var tokens = await LoginAsync(client, email);

        tokens.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        tokens.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
        tokens.GetProperty("tokenType").GetString().Should().Be("Bearer");
        // 30 days, because these run in Development and appsettings.Development.json overrides the
        // lifetime so a reviewer clicking through Swagger is not logged out mid-session. The
        // PRODUCTION-facing property - that the default is 15 minutes - is asserted by
        // AuthOptionsTests, where it belongs: it is a property of the code, not of dev config.
        tokens.GetProperty("expiresInSeconds").GetInt32().Should().Be(2_592_000,
            "Development deliberately issues a long-lived token; the 15-minute default is asserted "
            + "against AuthOptions itself so that dev convenience can never quietly become the "
            + "shipped behaviour");
    }

    [Fact]
    public async Task Registering_an_existing_address_returns_the_same_message_as_a_new_one()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();

        var first = await RegisterAsync(client, email);
        var second = await RegisterAsync(client, email);

        second.Should().Be(first,
            "distinguishing 'already registered' from 'created' turns this endpoint into a way "
            + "to discover which email addresses have accounts");
    }

    [Fact]
    public async Task An_unknown_account_and_a_wrong_password_fail_identically()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var wrongPassword = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email, password = "definitely-not-the-password" }, Ct);

        var unknownAccount = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email = NewEmail(), password = "definitely-not-the-password" }, Ct);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownAccount.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var a = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var b = await unknownAccount.Content.ReadFromJsonAsync<JsonElement>(Ct);

        a.GetProperty("detail").GetString().Should().Be(b.GetProperty("detail").GetString(),
            "the two failures must be indistinguishable to the caller");
    }

    // ---------------------------------------------------------------------------------------
    // Token rotation and reuse detection
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Refreshing_issues_a_new_pair_and_retires_the_old_refresh_token()
    {
        using var client = _factory.CreateClient();
        var tokens = await RegisterAndLoginAsync(client);
        var original = tokens.GetProperty("refreshToken").GetString()!;

        var refreshed = await RefreshAsync(client, original);

        refreshed.GetProperty("refreshToken").GetString().Should().NotBe(original,
            "single-use tokens are rotated on every exchange");
        refreshed.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>The mechanism that turns a stolen token into a detected incident.</summary>
    [Fact]
    public async Task Presenting_a_refresh_token_twice_revokes_every_session()
    {
        using var client = _factory.CreateClient();
        var tokens = await RegisterAndLoginAsync(client);
        var stolen = tokens.GetProperty("refreshToken").GetString()!;

        // The legitimate client refreshes.
        var legitimate = await RefreshAsync(client, stolen);
        var newToken = legitimate.GetProperty("refreshToken").GetString()!;

        // The attacker replays the copy they exfiltrated earlier.
        var replay = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), new { refreshToken = stolen }, Ct);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // ...and the legitimate client's brand-new token is dead too. That is the trade: a forced
        // re-login beats an attacker holding a working session for the rest of the week.
        var afterRevocation = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), new { refreshToken = newToken }, Ct);

        afterRevocation.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a single-use token presented twice proves a copy exists, so the whole chain goes");
    }

    [Fact]
    public async Task Logging_out_retires_the_refresh_token()
    {
        using var client = _factory.CreateClient();
        var tokens = await RegisterAndLoginAsync(client);
        var refreshToken = tokens.GetProperty("refreshToken").GetString()!;

        var logout = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative), new { refreshToken }, Ct);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterLogout = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), new { refreshToken }, Ct);
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logging_out_an_unknown_token_still_returns_204()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative),
            new { refreshToken = "never-issued" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "reporting 'no such token' would let a caller probe which tokens are valid");
    }

    // ---------------------------------------------------------------------------------------
    // Authorisation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Favourites_require_authentication()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync(new Uri("/api/v1/favourites", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.PutAsync(new Uri("/api/v1/favourites/ACB", UriKind.Relative), null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Carpark_search_stays_anonymous()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync(new Uri("/api/v1/carparks?pageSize=1", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "the fallback policy requires authentication, so search opts out explicitly");
    }

    [Fact]
    public async Task A_tampered_token_is_rejected()
    {
        using var client = _factory.CreateClient();
        var tokens = await RegisterAndLoginAsync(client);
        var accessToken = tokens.GetProperty("accessToken").GetString()!;

        // Flip the last character of the signature.
        var tampered = accessToken[..^1] + (accessToken[^1] == 'A' ? 'B' : 'A');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        (await client.GetAsync(new Uri("/api/v1/favourites", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>OWASP API1:2023 - Broken Object Level Authorization.</summary>
    [Fact]
    public async Task No_endpoint_accepts_a_user_id_as_input()
    {
        using var client = _factory.CreateClient();
        var tokens = await RegisterAndLoginAsync(client);
        Authorise(client, tokens);

        // The textbook vulnerable route shapes simply do not exist, so there is no identifier for
        // an attacker to manipulate and no ownership check for a future handler to forget.
        foreach (var route in new[] { "/api/v1/users/1/favourites", "/api/v1/users/1" })
        {
            var response = await client.GetAsync(new Uri(route, UriKind.Relative), Ct);

            response.StatusCode.Should().BeOneOf(
                [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
                $"'{route}' must not be routable - the user comes from the token's sub claim only");
        }

        // And a userId supplied as a query parameter is simply ignored: the endpoint has no such
        // parameter to bind, so the caller gets their OWN favourites regardless of what they ask
        // for. Ignoring an unknown query parameter is correct REST behaviour; what matters is that
        // it changes nothing.
        var smuggled = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/favourites?userId=1", UriKind.Relative), Ct);

        smuggled.GetProperty("data").GetArrayLength().Should().Be(0,
            "this caller has no favourites, and asking for user 1's does not change that");
    }

    [Fact]
    public async Task One_users_favourites_are_invisible_to_another()
    {
        using var alice = _factory.CreateClient();
        using var bob = _factory.CreateClient();

        Authorise(alice, await RegisterAndLoginAsync(alice));
        Authorise(bob, await RegisterAndLoginAsync(bob));

        (await alice.PutAsync(new Uri("/api/v1/favourites/ACB", UriKind.Relative), null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var bobsFavourites = await bob.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/favourites", UriKind.Relative), Ct);

        bobsFavourites.GetProperty("data").GetArrayLength().Should().Be(0,
            "favourites are scoped to the token's subject, and Bob's token is not Alice's");
    }

    // ---------------------------------------------------------------------------------------
    // Favourites
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Favouriting_is_idempotent()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        var first = await client.PutAsync(new Uri("/api/v1/favourites/ACM", UriKind.Relative), null, Ct);
        var second = await client.PutAsync(new Uri("/api/v1/favourites/ACM", UriKind.Relative), null, Ct);
        var third = await client.PutAsync(new Uri("/api/v1/favourites/ACM", UriKind.Relative), null, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "never 409 - a double-tap is not a conflict");
        third.StatusCode.Should().Be(HttpStatusCode.OK);

        var favourites = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/favourites", UriKind.Relative), Ct);

        favourites.GetProperty("data").GetArrayLength().Should().Be(1,
            "three requests, one favourite - the composite primary key makes a duplicate "
            + "impossible even by direct SQL");
    }

    // -------------------------------------------------------------------------------------------
    // Favourites paging.
    //
    // ListAsync ended `new PagedResult<>(summaries, null, false, summaries.Count)` - nextCursor
    // hard-coded null, hasMore hard-coded false, and "total" set to the size of the PAGE rather
    // than the number of favourites. page.Cursor was never read, so Take() simply truncated: a user
    // with 50 favourites who asked for 20 received 20, was told there were 20 and that no more
    // existed, and had no way to reach the other 30.
    //
    // Every existing favourites test used the default page size against one or two favourites, so
    // the page was always the whole list and the lie was always accidentally true.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_favourite_is_reachable_by_paging_one_at_a_time()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        string[] carparks = ["ACB", "ACM", "AH1", "AK19", "A100"];

        foreach (var carPark in carparks)
        {
            await client.PutAsync(new Uri($"/api/v1/favourites/{carPark}", UriKind.Relative), null, Ct);
        }

        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var url = "/api/v1/favourites?pageSize=1"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

            var body = await client.GetFromJsonAsync<JsonElement>(new Uri(url, UriKind.Relative), Ct);
            var pagination = body.GetProperty("pagination");

            pagination.GetProperty("totalCount").GetInt32().Should().Be(carparks.Length,
                "totalCount is the user's whole list, not the size of the page just returned");

            seen.AddRange(body.GetProperty("data").EnumerateArray()
                .Select(c => c.GetProperty("carParkNo").GetString()!));

            if (!pagination.GetProperty("hasMore").GetBoolean())
            {
                pagination.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null,
                    "the last page must not offer a cursor to a page that does not exist");
                break;
            }

            cursor = pagination.GetProperty("nextCursor").GetString();
            cursor.Should().NotBeNullOrEmpty("hasMore is true, so there must be a way to get there");
        }

        seen.Should().BeEquivalentTo(carparks,
            "paging one at a time must reach every favourite exactly once - no row stranded "
            + "beyond the first page, and none returned twice");
    }

    [Fact]
    public async Task A_single_page_holding_everything_reports_no_more()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        await client.PutAsync(new Uri("/api/v1/favourites/ACB", UriKind.Relative), null, Ct);
        await client.PutAsync(new Uri("/api/v1/favourites/ACM", UriKind.Relative), null, Ct);

        var body = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/favourites?pageSize=20", UriKind.Relative), Ct);

        var pagination = body.GetProperty("pagination");

        pagination.GetProperty("totalCount").GetInt32().Should().Be(2);
        pagination.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        pagination.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("data").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Favourites_are_listed_most_recently_added_first()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        await client.PutAsync(new Uri("/api/v1/favourites/ACB", UriKind.Relative), null, Ct);
        await client.PutAsync(new Uri("/api/v1/favourites/ACM", UriKind.Relative), null, Ct);
        await client.PutAsync(new Uri("/api/v1/favourites/AH1", UriKind.Relative), null, Ct);

        var body = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/favourites", UriKind.Relative), Ct);

        body.GetProperty("data")[0].GetProperty("carParkNo").GetString().Should().Be("AH1",
            "the newest favourite leads, which is what a Favourites screen shows first");
    }

    [Fact]
    public async Task Unfavouriting_is_idempotent()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        await client.PutAsync(new Uri("/api/v1/favourites/AH1", UriKind.Relative), null, Ct);

        (await client.DeleteAsync(new Uri("/api/v1/favourites/AH1", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.DeleteAsync(new Uri("/api/v1/favourites/AH1", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent,
                "a retry after a dropped connection needs no special handling");
    }

    [Fact]
    public async Task Favouriting_an_unknown_carpark_returns_404()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        (await client.PutAsync(new Uri("/api/v1/favourites/NOPE", UriKind.Relative), null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Favourites_return_full_carpark_objects_not_identifiers()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        await client.PutAsync(new Uri("/api/v1/favourites/ACB", UriKind.Relative), null, Ct);

        var favourites = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/favourites", UriKind.Relative), Ct);

        var carpark = favourites.GetProperty("data")[0];
        carpark.GetProperty("address").GetString().Should().NotBeNullOrEmpty(
            "a Favourites screen should render in one round trip, not one request per item");
        carpark.GetProperty("location").GetProperty("latitude").GetDouble().Should().BeGreaterThan(1.0);
        carpark.GetProperty("heightRestriction").GetProperty("isRestricted").GetBoolean()
            .Should().BeTrue("the shape is identical to a search result, so one component renders both");
    }

    [Fact]
    public async Task Search_results_report_whether_each_carpark_is_a_favourite()
    {
        using var client = _factory.CreateClient();
        Authorise(client, await RegisterAndLoginAsync(client));

        await client.PutAsync(new Uri("/api/v1/favourites/ACB", UriKind.Relative), null, Ct);

        var favourited = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/carparks/ACB", UriKind.Relative), Ct);
        var notFavourited = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/carparks/ACM", UriKind.Relative), Ct);

        favourited.GetProperty("isFavourite").GetBoolean().Should().BeTrue(
            "computed server-side with a join, so the client does not fetch its favourites "
            + "separately and intersect on every render");
        notFavourited.GetProperty("isFavourite").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Anonymous_search_reports_no_favourite_state_at_all()
    {
        using var client = _factory.CreateClient();

        var carpark = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/carparks/ACB", UriKind.Relative), Ct);

        carpark.GetProperty("isFavourite").ValueKind.Should().Be(JsonValueKind.Null,
            "null means 'unknown because you are anonymous', which is not the same as false");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private const string Password = "correct-horse-battery-staple";

    private static async Task<string> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/register", UriKind.Relative),
            new { email, password = Password, displayName = "Test User" }, Ct);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("message").GetString()!;
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email, password = Password }, Ct);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), new { refreshToken }, Ct);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> RegisterAndLoginAsync(HttpClient client)
    {
        var email = NewEmail();
        await RegisterAsync(client, email);
        return await LoginAsync(client, email);
    }

    private static void Authorise(HttpClient client, JsonElement tokens) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokens.GetProperty("accessToken").GetString());
}
