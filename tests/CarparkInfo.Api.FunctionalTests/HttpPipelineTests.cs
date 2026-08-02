using System.Net;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>
/// Pipeline behaviour a browser sees, which in-process tests cannot observe.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a defect that 39 passing endpoint tests missed: in Development the
/// pipeline issued a 307 from <c>http://localhost:5106</c> to <c>https://localhost:7293</c> on
/// every request. In a browser the redirect leads to the self-signed development certificate, and
/// unless <c>dotnet dev-certs https --trust</c> has been run the request dies with no usable
/// error - Swagger spins on "LOADING" for ever, which reads as a broken API.
/// </para>
/// <para>
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> could never
/// catch it. It invokes the pipeline in-process: no socket, no TLS handshake, and its client
/// follows redirects transparently. The endpoint tests were all correct and all blind to this.
/// </para>
/// </remarks>
public sealed class HttpPipelineTests : IClassFixture<CarparkApiFactory>
{
    private readonly CarparkApiFactory _factory;

    public HttpPipelineTests(CarparkApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client that reports a redirect instead of quietly following it.</summary>
    private HttpClient CreateNonFollowingClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    [Fact]
    public async Task Development_does_not_redirect_plain_HTTP_to_HTTPS()
    {
        using var client = CreateNonFollowingClient();

        var response = await client.GetAsync(
            new Uri("/api/v1/carparks?pageSize=1", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a 307 here sends a browser to the self-signed dev certificate, where the request "
            + "dies silently and Swagger appears to hang for ever");

        ((int)response.StatusCode).Should().NotBeInRange(300, 399,
            "a reviewer should not have to trust a certificate before clicking Execute");
    }

    [Theory]
    [InlineData("/api/v1/carparks?pageSize=1")]
    [InlineData("/api/v1/carparks/lookups")]
    [InlineData("/api/v1/carparks/ACB")]
    [InlineData("/api/v1/health/live")]
    [InlineData("/openapi/v1.json")]
    public async Task Every_anonymous_endpoint_answers_directly_without_a_redirect(string route)
    {
        using var client = CreateNonFollowingClient();

        var response = await client.GetAsync(new Uri(route, UriKind.Relative), Ct);

        ((int)response.StatusCode).Should().NotBeInRange(300, 399,
            $"'{route}' must answer directly; a redirect is where a browser silently stalls");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Swagger_is_reachable_and_serves_its_page()
    {
        using var client = CreateNonFollowingClient();

        var response = await client.GetAsync(new Uri("/swagger/index.html", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the assignment names Swagger documentation as a required deliverable, so the page "
            + "itself is part of the contract");
    }

    [Fact]
    public async Task The_OpenAPI_document_is_readable_without_authenticating()
    {
        using var client = CreateNonFollowingClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the fallback policy requires authentication, so the document opts out explicitly - "
            + "a contract a reviewer cannot fetch without first authenticating is useless");

        var document = await response.Content.ReadAsStringAsync(Ct);
        document.Should().Contain("\"openapi\"");
        document.Should().Contain("/api/v1/carparks");
    }

    [Fact]
    public async Task Security_headers_are_present_on_API_responses()
    {
        using var client = CreateNonFollowingClient();

        var response = await client.GetAsync(
            new Uri("/api/v1/carparks?pageSize=1", UriKind.Relative), Ct);

        response.Headers.Should().Contain(h => h.Key == "X-Content-Type-Options");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff",
            "stops a browser second-guessing Content-Type, which is how a JSON response ends up "
            + "executed as script");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
    }

    [Fact]
    public async Task The_Swagger_page_is_not_blocked_by_its_own_content_security_policy()
    {
        using var client = CreateNonFollowingClient();

        var response = await client.GetAsync(new Uri("/swagger/index.html", UriKind.Relative), Ct);

        response.Headers.Contains("Content-Security-Policy").Should().BeFalse(
            "the API sends default-src 'none', which would stop the Swagger page loading its own "
            + "scripts and styles - so /swagger is deliberately excluded");
    }
}
