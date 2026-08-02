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
