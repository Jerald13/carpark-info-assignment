using System.Net;
using System.Net.Http.Json;
using CarparkInfo.Api.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>
/// Boots the real API through <see cref="WebApplicationFactory{TEntryPoint}"/> and exercises it
/// over HTTP. Beyond checking the probe itself, this is the smoke test that the composition root,
/// controller routing and JSON serialisation are all wired correctly - a failure here means
/// nothing else in the API is trustworthy.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>xUnit v3 supplies a token so a cancelled run stops promptly (xUnit1051).</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_probe_reports_healthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/health/live", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Ct);
        body.Should().NotBeNull();
        body!.Service.Should().Be("carpark-info-api");
        body.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_the_api()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the OpenAPI document backs the Swagger UI that the assignment requires (R9)");

        var document = await response.Content.ReadAsStringAsync(Ct);
        document.Should().Contain("openapi", "the response must be an OpenAPI document");
        document.Should().Contain("/api/v1/health/live",
            "declared endpoints must appear in the generated contract, and in the SAME casing the "
            + "README documents. [Route(\"api/v1/[controller]\")] expanded to the class name and "
            + "published '/api/v1/Health/live' - case-insensitive routing hid it, but the contract "
            + "and the documentation disagreed");
    }
}
