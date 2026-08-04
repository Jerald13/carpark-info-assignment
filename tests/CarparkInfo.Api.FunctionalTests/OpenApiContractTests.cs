using System.Net.Http.Json;
using System.Text.Json;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>
/// Asserts properties of the generated OpenAPI document that Swagger UI depends on.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a defect no endpoint test could see: <c>carParkType</c> and
/// <c>parkingSystem</c> were declared as <b>array</b> query parameters. Swagger UI runs
/// <c>JSON.parse</c> over the value of any array-typed parameter, so a plain string in the box -
/// or an empty box - threw
/// <c>"Could not parse parameter value string as JSON Object or JSON Array"</c> and aborted
/// <c>buildRequest</c>. <b>The request was never sent.</b> The page simply spun on "LOADING", with
/// no network call and nothing in the server log.
/// </para>
/// <para>
/// Every endpoint test passed throughout, because they call the API directly with an HttpClient
/// and never go near Swagger's request builder. The contract was valid OpenAPI; it was just not
/// usable from the documentation UI the assignment requires.
/// </para>
/// </remarks>
public sealed class OpenApiContractTests : IClassFixture<CarparkApiFactory>
{
    private readonly CarparkApiFactory _factory;

    public OpenApiContractTests(CarparkApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_query_parameter_is_typed_as_an_array()
    {
        var document = await GetDocumentAsync();

        var offenders = new List<string>();

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("parameters", out var parameters))
                {
                    continue;
                }

                foreach (var parameter in parameters.EnumerateArray())
                {
                    if (!parameter.TryGetProperty("schema", out var schema))
                    {
                        continue;
                    }

                    var isArray = schema.TryGetProperty("items", out _)
                        || (schema.TryGetProperty("type", out var type)
                            && type.ValueKind == JsonValueKind.String
                            && type.GetString() == "array");

                    if (isArray)
                    {
                        offenders.Add($"{path.Name} → {parameter.GetProperty("name").GetString()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "Swagger UI runs JSON.parse over array parameter values, so a plain string in the box "
            + "throws and the request is never sent - the page spins for ever with no network call. "
            + "Use a comma-separated string instead; ASP.NET Core still binds repeated parameters "
            + "to it. Offenders: {0}", string.Join(", ", offenders));
    }

    [Fact]
    public async Task The_search_endpoint_exposes_every_user_story_filter()
    {
        var document = await GetDocumentAsync();

        var names = document
            .GetProperty("paths").GetProperty("/api/v1/carparks")
            .GetProperty("get").GetProperty("parameters")
            .EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!.ToUpperInvariant())
            .ToList();

        names.Should().Contain("FREEPARKING", "user story 1 must be discoverable from the contract");
        names.Should().Contain("NIGHTPARKING", "user story 2");
        names.Should().Contain("VEHICLEHEIGHT", "user story 3");
    }

    [Fact]
    public async Task The_document_declares_the_bearer_security_scheme()
    {
        var document = await GetDocumentAsync();

        document.GetProperty("components").GetProperty("securitySchemes")
            .TryGetProperty("Bearer", out var scheme).Should().BeTrue(
                "without it there is no Authorize button, so a reviewer can see the protected "
                + "endpoints but cannot call them");

        scheme.GetProperty("scheme").GetString().Should().Be("bearer");
    }

    /// <summary>
    /// The partner assertion to <see cref="The_document_declares_the_bearer_security_scheme"/>.
    /// </summary>
    /// <remarks>
    /// That test passed while every protected endpoint was uncallable from Swagger UI. Declaring
    /// <c>Bearer</c> in <c>components.securitySchemes</c> only DEFINES the scheme - it draws the
    /// Authorize button. Swagger attaches the header to an operation only if that OPERATION carries
    /// a <c>security</c> requirement, and none of them did. The button worked, the token was
    /// accepted, the padlocks looked right, and every request went out with no Authorization header.
    /// Both admin endpoints and favourites answered 401 to a correctly signed admin token.
    ///
    /// Neither the functional tests nor smoke.ps1 could see it: both set the header themselves, so
    /// both bypassed the mechanism that was broken. Only a browser exercises it.
    /// </remarks>
    [Fact]
    public async Task Protected_operations_declare_the_security_requirement()
    {
        var document = await GetDocumentAsync();

        string[] mustBeProtected =
        [
            "/api/v1/favourites",
            "/api/v1/favourites/{carParkNo}",
            "/api/v1/admin/job-runs",
        ];

        var unprotected = new List<string>();

        foreach (var path in mustBeProtected)
        {
            foreach (var operation in document.GetProperty("paths").GetProperty(path).EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("security", out var security)
                    || security.GetArrayLength() == 0)
                {
                    unprotected.Add($"{operation.Name.ToUpperInvariant()} {path}");
                    continue;
                }

                security[0].TryGetProperty("Bearer", out _).Should().BeTrue(
                    "the requirement must reference the scheme declared in components");
            }
        }

        unprotected.Should().BeEmpty(
            "an operation with no security requirement gets NO Authorization header from Swagger "
            + "UI, however correct the token is, so it answers 401 and the reviewer cannot tell "
            + "why. Offenders: {0}", string.Join(", ", unprotected));
    }

    [Fact]
    public async Task Anonymous_operations_do_not_demand_a_token()
    {
        var document = await GetDocumentAsync();

        string[] mustStayOpen =
        [
            "/api/v1/carparks",
            "/api/v1/auth/login",
            "/api/v1/auth/register",
            "/api/v1/health/live",
        ];

        foreach (var path in mustStayOpen)
        {
            foreach (var operation in document.GetProperty("paths").GetProperty(path).EnumerateObject())
            {
                operation.Value.TryGetProperty("security", out var security).Should().BeFalse(
                    $"{path} is anonymous. Marking it as protected is a documentation lie in the "
                    + "other direction - it sends a reviewer hunting for a token they do not need");

                _ = security;
            }
        }
    }

    /// <summary>
    /// Every request body describes the body, not the cancellation token.
    /// </summary>
    /// <remarks>
    /// The generator assigns <c>requestBody.description</c> from the <b>last</b> <c>&lt;param&gt;</c>
    /// tag on the action. <c>CancellationToken</c> is conventionally the final parameter, so the
    /// conventional documentation order published <i>"Cancels the request."</i> as the description
    /// of <b>every</b> request body in the API - all five of them, including the sign-in payload.
    /// The fix is to document <c>cancellationToken</c> first; this test exists because that ordering
    /// looks like an accident and would otherwise be tidied back within a week.
    /// </remarks>
    [Fact]
    public async Task No_request_body_is_described_as_the_cancellation_token()
    {
        var document = await GetDocumentAsync();

        var offenders = new List<string>();
        var bodies = 0;

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("requestBody", out var body)
                    || !body.TryGetProperty("description", out var description))
                {
                    continue;
                }

                bodies++;

                if (description.GetString()?.Contains("Cancels the request", StringComparison.Ordinal) == true)
                {
                    offenders.Add(path.Name);
                }
            }
        }

        bodies.Should().BeGreaterThan(0, "the API has endpoints that take a body");
        offenders.Should().BeEmpty(
            "the description belongs to the payload, not to the CancellationToken parameter. "
            + "Document cancellationToken BEFORE the body parameter - the generator uses the last "
            + "<param> tag. Offenders: {0}", string.Join(", ", offenders));
    }

    [Fact]
    public async Task The_document_is_OpenAPI_3_1()
    {
        var document = await GetDocumentAsync();

        document.GetProperty("openapi").GetString().Should().StartWith("3.1");
    }

    private async Task<JsonElement> GetDocumentAsync()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
