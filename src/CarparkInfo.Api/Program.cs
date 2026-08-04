using CarparkInfo.Api;
using CarparkInfo.Infrastructure;
using CarparkInfo.Infrastructure.Auth;
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

// Enums serialise as their NAME, not their ordinal.
//
// By default System.Text.Json emits `"status": 3`, which tells a client nothing, cannot be
// validated, and silently changes meaning the day somebody inserts a member into the enum. It was
// also inconsistent: the trigger endpoint already returned Status.ToString(), so the same concept
// was a string in one response and a number in another.
//
// Names also make the OpenAPI document self-describing - the allowed values appear in Swagger
// instead of an unexplained integer.
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration);
builder.AddApiObservability();

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

// The administrator account, in Development only. Three admin endpoints existed with nothing in
// the solution granting the role, so a reviewer could reach none of them. Fails closed: any
// environment other than Development seeds nothing. See DevelopmentSeeder.
await DevelopmentSeeder.SeedAdminAsync(app.Services, app.Environment.EnvironmentName).ConfigureAwait(false);

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
    // HSTS and HTTPS redirection are PRODUCTION behaviour and stay out of Development.
    //
    // With them on locally, Swagger served over http://localhost:5106 issues a 307 to
    // https://localhost:7293 on every Execute. The browser follows it, meets the self-signed
    // development certificate, and - unless `dotnet dev-certs https --trust` has been run - the
    // request dies with no usable error. Swagger just spins on "LOADING" for ever, which reads as
    // a broken API rather than an untrusted certificate.
    //
    // A reviewer should not have to trust a certificate to click Execute. Production still gets
    // both, where a plaintext request is a genuine problem rather than a local convenience.
    //
    // The environment check is not enough on its own. Running the app with --no-launch-profile and
    // no ASPNETCORE_ENVIRONMENT set defaults to Production, so a casual local run would send HSTS
    // for the host "localhost" - and browsers cache HSTS PER HOST, IGNORING THE PORT. One such run
    // silently upgrades every http://localhost:* URL to https:// from then on, across every
    // project on the machine, until the user finds chrome://net-internals/#hsts and clears it.
    //
    // So HSTS is suppressed for loopback regardless of environment. It protects nothing there -
    // traffic to localhost never crosses a network - and the failure it causes is both confusing
    // and persistent.
    var isLoopbackOnly = app.Urls.Count > 0
        && app.Urls.All(url => url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                            || url.Contains("127.0.0.1", StringComparison.Ordinal)
                            || url.Contains("[::1]", StringComparison.Ordinal));

    if (!isLoopbackOnly)
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();
}

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
