using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>Operations endpoints and the readiness probe.</summary>
public sealed class AdminAndHealthTests : IClassFixture<CarparkApiFactory>
{
    private readonly CarparkApiFactory _factory;

    public AdminAndHealthTests(CarparkApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------
    // Health
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Liveness_is_anonymous_and_shallow()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/health/live", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "liveness answers 'should this instance be restarted?', which a probe must be able "
            + "to ask without credentials");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task Readiness_reports_the_catalogue_as_fresh_after_ingestion()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/health/ready", UriKind.Relative), Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("Healthy");

        var feed = body.GetProperty("feed");
        feed.GetProperty("isFresh").GetBoolean().Should().BeTrue(
            "the fixture ingested the dataset moments ago");
        feed.GetProperty("lastSuccessAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Readiness_exposes_the_SLA_it_degrades_against()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/health/ready", UriKind.Relative), Ct);

        body.GetProperty("feed").GetProperty("sla").GetString().Should().Be("1.02:00:00",
            "26 hours gives a daily feed two hours of slack. Alerting on ABSENCE of success is "
            + "the point: a job that fails loudly gets noticed, one that silently stops does not");
    }

    // ---------------------------------------------------------------------------------------
    // Admin authorisation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_endpoints_reject_anonymous_callers()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync(new Uri("/api/v1/admin/job-runs", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_endpoints_reject_an_authenticated_non_admin()
    {
        using var client = _factory.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync(new Uri("/api/v1/auth/register", UriKind.Relative),
            new { email, password = "correct-horse-battery-staple", displayName = "T" }, Ct);

        var login = await client.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email, password = "correct-horse-battery-staple" }, Ct);

        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", tokens.GetProperty("accessToken").GetString());

        var response = await client.GetAsync(new Uri("/api/v1/admin/job-runs", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "403 not 401: the caller IS authenticated, they simply are not an administrator. "
            + "Job history exposes source file names, host names and raw feed lines");
    }

    [Fact]
    public async Task Triggering_ingestion_requires_the_admin_role()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/admin/job-runs", UriKind.Relative), new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "triggering ingestion is a write against the entire catalogue");
    }

    // ---------------------------------------------------------------------------------------
    // Admin authorisation - the HAPPY path
    //
    // The three tests above all assert a FAILURE: anonymous is rejected, a normal user is
    // rejected. Not one of them asserted that an administrator SUCCEEDS - and because of that,
    // nobody noticed that nothing in the entire solution ever granted UserRoles.Admin.
    // Registration takes the User default, and there was no promotion endpoint and no seed.
    // All three admin endpoints were documented in the README and returned 403 to everybody.
    //
    // A suite that only proves the door is locked never checks that the key exists.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_seeded_administrator_can_sign_in_and_read_job_history()
    {
        using var client = await AdminClientAsync();

        var response = await client.GetAsync(new Uri("/api/v1/admin/job-runs", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Development seed exists precisely so this endpoint is reachable");

        var runs = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        runs.GetArrayLength().Should().BeGreaterThan(0, "the fixture ingested the dataset");
        runs[0].GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task The_seeded_administrator_can_read_the_defect_report()
    {
        using var client = await AdminClientAsync();

        var runs = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/admin/job-runs", UriKind.Relative), Ct);
        var runId = runs[0].GetProperty("id").GetInt32();

        var response = await client.GetAsync(
            new Uri($"/api/v1/admin/job-runs/{runId}/defects", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var defects = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        defects.GetArrayLength().Should().Be(3,
            "the supplied dataset contains three internally inconsistent rows - a MULTI-STOREY "
            + "carpark and two basements each reporting zero decks. They are warnings, not errors, "
            + "so they were ingested; R6 requires them to be reportable");

        defects.EnumerateArray().Select(d => d.GetProperty("severity").GetString())
            .Should().AllBe("Warning", "none of the three rejected a row");
    }

    [Fact]
    public async Task The_seeded_administrator_can_trigger_ingestion()
    {
        using var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/admin/job-runs", UriKind.Relative), new { force = false }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an empty inbox is a valid outcome - what matters is that the caller gets past "
            + "authorisation and receives a result rather than a 403");
    }

    /// <summary>Signs in as the Development-seeded administrator and returns an authorised client.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email = "admin@carpark.local", password = "Admin!ChangeMe123" }, Ct);

        login.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Development seed must create this account at startup, or every admin endpoint "
            + "is unreachable and the README documents three endpoints nobody can call");

        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", tokens.GetProperty("accessToken").GetString());

        return client;
    }
}
