using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CarparkInfo.Api;

/// <summary>
/// Marks every protected operation as requiring the bearer scheme.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaring the scheme is not enough.</b> <see cref="BearerSecuritySchemeTransformer"/> puts
/// <c>Bearer</c> into <c>components.securitySchemes</c>, which is what draws the <b>Authorize</b>
/// button. But a scheme in <c>components</c> is only a definition - it says the API understands
/// bearer tokens, not that any particular operation wants one. Swagger UI attaches the header to an
/// operation only when that operation carries a <c>security</c> requirement referring to the scheme.
/// </para>
/// <para>
/// Without this, the failure is silent and extremely convincing: the button appears, the token is
/// accepted, the padlocks look right - and every request goes out with no <c>Authorization</c>
/// header at all, so every protected endpoint answers 401. The generated curl block on the page
/// shows only <c>-H 'accept: application/json'</c>, which is the tell.
/// </para>
/// <para>
/// Nothing caught it because no test used a browser. The functional tests set
/// <c>DefaultRequestHeaders.Authorization</c> themselves and smoke.ps1 builds its own headers, so
/// both bypass the exact mechanism that was broken - and
/// <c>OpenApiContractTests.The_document_declares_the_bearer_security_scheme</c> asserted the scheme
/// existed in <c>components</c>, which was true the whole time. That test now has a partner which
/// asserts the operations actually reference it.
/// </para>
/// <para>
/// The requirement is applied per operation rather than globally, because a global
/// <c>security</c> block would also mark the anonymous endpoints - search, health, login,
/// register - as needing a token, which is a documentation lie in the opposite direction.
/// Authorisation metadata is read from the endpoint itself, so the document can never disagree with
/// what the pipeline enforces.
/// </para>
/// </remarks>
internal sealed class BearerSecurityRequirementTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // AllowAnonymous wins, exactly as it does in the pipeline: carpark search opts out of the
        // fallback policy, and documenting it as protected would send a reviewer hunting for a
        // token it does not need.
        var requiresToken = metadata.OfType<IAuthorizeData>().Any()
            && !metadata.OfType<IAllowAnonymous>().Any();

        if (!requiresToken)
        {
            return Task.CompletedTask;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
            },
        ];

        return Task.CompletedTask;
    }
}
