using CarparkInfo.Api;
using CarparkInfo.Infrastructure;
using Microsoft.AspNetCore.Mvc;

// Composition root for the Carpark Information API.
//
// MIDDLEWARE ORDER IS LOAD-BEARING - see ARCHITECTURE.md section 9. Two placements in particular:
//
//   * The exception handler sits OUTSIDE auth and rate limiting, so a failure in either still
//     returns a well-formed ProblemDetails rather than a bare 500 with a stack trace.
//
//   * Rate limiting precedes authentication, so unauthenticated flood traffic is shed before the
//     cost of signature validation and a database lookup is paid.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration);

// RFC 7807 for every error shape, including ones the framework raises.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        // The trace id is the whole point: it leaks nothing - no stack trace, no SQL, no type
        // names - while giving support a single token that jumps straight to the correlated logs.
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    });

// OpenAPI 3.1 document generation via the FIRST-PARTY package, not Swashbuckle's generator
// (ADR-012). ASP.NET Core dropped Swashbuckle from its templates in .NET 9; the built-in
// generator is source-generated, reads XML doc comments natively in .NET 10, and cannot lag
// the framework. Swashbuckle is still used - but only for its UI.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

var app = builder.Build();

// Migrations run at startup so a reviewer can clone and run with no database setup step.
await InfrastructureSetup.MigrateAsync(app.Services).ConfigureAwait(false);

// --- pipeline ---------------------------------------------------------------------------------

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();

if (app.Environment.IsDevelopment())
{
    // AllowAnonymous: the fallback policy requires authentication, and a contract
    // document a reviewer cannot fetch without first authenticating is useless.
    app.MapOpenApi().AllowAnonymous();      // the document: /openapi/v1.json

    app.UseSwaggerUI(options =>             // the interactive page: /swagger
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Carpark Information API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Carpark Information API";
    });
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// The global limiter in AddApiSecurity already partitions every request, so no
// per-endpoint policy is needed here.
app.MapControllers();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in functional tests.
/// A top-level-statements program is otherwise internal and cannot be referenced from a test project.
/// </summary>
public partial class Program;
