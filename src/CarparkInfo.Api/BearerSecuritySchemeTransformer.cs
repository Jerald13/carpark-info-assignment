using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CarparkInfo.Api;

/// <summary>
/// Adds the JWT bearer scheme to the OpenAPI document.
/// </summary>
/// <remarks>
/// This is what puts the <b>Authorize</b> button on the Swagger page. Without it a reviewer can
/// see the protected endpoints but cannot call them, which makes the documentation half-useful: the
/// intended journey is register → login → Authorize → call <c>/favourites</c>, entirely in the
/// browser and with no client tooling.
/// </remarks>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private readonly IAuthenticationSchemeProvider _schemeProvider;

    public BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider schemeProvider) =>
        _schemeProvider = schemeProvider;

    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schemes = await _schemeProvider.GetAllSchemesAsync().ConfigureAwait(false);

        if (!schemes.Any(s => string.Equals(s.Name, "Bearer", StringComparison.Ordinal)))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);

        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "Paste the accessToken value returned by POST /api/v1/auth/login — the long "
                + "string beginning 'eyJ'.\n\n"
                + "Do NOT type 'Bearer ' in front; Swagger adds it.\n"
                + "Use accessToken, not refreshToken — the refresh token is for renewal and will "
                + "not authenticate a request.\n\n"
                + "No account yet? Call POST /api/v1/auth/register first. The password must be at "
                + "least 12 characters.",
        };

        document.Info.Title = "Carpark Information API";
        document.Info.Version = "v1";
        document.Info.Description =
            """
            Search Singapore HDB carparks and manage favourites.

            ### The vehicle-height filter

            `GET /api/v1/carparks?vehicleHeight=2.0` returns carparks that fit a 2.0 m vehicle,
            **including the 477 that have no height gantry at all**. Those carry a source
            `gantry_height` of `0.00`, which means *no gantry* rather than *zero clearance* —
            filtering on the raw number would silently hide 23% of the catalogue, and specifically
            the open-air carparks that fit anything. The correct answer is 2,056 carparks; a literal
            comparison returns 1,579.

            For the same reason `heightRestriction` is returned as an object
            (`{"isRestricted": false, "maxVehicleHeightMetres": null}`) rather than a bare number.

            ### Authentication

            Carpark search is anonymous. Favourites need a bearer token: register, log in, then use
            the **Authorize** button above. Signing in also adds `isFavourite` to every search
            result, so a client never needs to fetch its favourites separately and intersect.
            """;
    }
}
