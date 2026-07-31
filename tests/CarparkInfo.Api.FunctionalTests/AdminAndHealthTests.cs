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
}
