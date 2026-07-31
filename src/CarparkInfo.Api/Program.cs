using CarparkInfo.Infrastructure;

// Composition root for the Carpark Information API.
//
// Middleware ORDER is load-bearing - see ARCHITECTURE.md section 9. In particular the exception
// handler sits outside auth and rate limiting so that a failure in either still returns a
// well-formed ProblemDetails rather than a bare 500, and rate limiting precedes authentication
// so unauthenticated flood traffic is shed before signature validation is paid for.
//
// Built out across phases 5-9; this is the phase 0 skeleton.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();

// OpenAPI 3.1 document generation via the FIRST-PARTY package, not Swashbuckle's generator
// (ADR-012). ASP.NET Core dropped Swashbuckle from its templates in .NET 9; the built-in
// generator is source-generated, reads XML doc comments natively in .NET 10, and cannot lag
// the framework. Swashbuckle is still used - but only for its UI.
builder.Services.AddOpenApi();

var app = builder.Build();

// Migrations run at startup so a reviewer can clone and run with no database setup step.
await InfrastructureSetup.MigrateAsync(app.Services).ConfigureAwait(false);

if (app.Environment.IsDevelopment())
{
    // The document itself: /openapi/v1.json
    app.MapOpenApi();

    // The interactive page the assignment asks for: /swagger
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Carpark Information API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Carpark Information API";
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in functional tests.
/// A top-level-statements program is otherwise internal and cannot be referenced from a test project.
/// </summary>
public partial class Program;
