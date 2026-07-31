using CarparkInfo.Application.Ingestion;
using CarparkInfo.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Infrastructure.IntegrationTests;

/// <summary>
/// Asserts that each host can build its container.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a defect that 215 passing tests did not catch: <c>AddInfrastructure</c>
/// registered JWT services that depend on <c>AuthOptions</c>, which only an HTTP host configures.
/// The batch job therefore failed at startup under DI validation - the very first command in the
/// README.
/// </para>
/// <para>
/// Nothing caught it because every test either hosted the full API (where both registrations run)
/// or constructed services directly, bypassing the container entirely. It surfaced only from
/// cloning the repository fresh and following the README.
/// </para>
/// <para>
/// <c>ValidateOnBuild</c> is what makes these assertions meaningful: it resolves every registration
/// eagerly, so a dependency nothing can satisfy fails here rather than at 02:00 in a batch window.
/// </para>
/// </remarks>
public sealed class HostCompositionTests
{
    [Fact]
    public void The_batch_job_container_builds_without_any_HTTP_registrations()
    {
        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure(Configuration());

        // Exactly what CarparkInfo.BatchJob registers - deliberately no AddApiSecurity.
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<CarparkIngestionService>().Should().NotBeNull(
            "the batch job must start without knowing anything about JWT or HTTP");
        scope.ServiceProvider.GetRequiredService<IngestionRunner>().Should().NotBeNull();
    }

    [Fact]
    public void Auth_services_are_not_registered_by_AddInfrastructure_alone()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(Configuration());

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<Application.Abstractions.ITokenService>()
            .Should().BeNull(
                "token issuance belongs to an HTTP host. Registering it here made the batch job "
                + "depend on AuthOptions, which it has no way to configure");
    }

    [Fact]
    public void Adding_auth_infrastructure_completes_the_graph()
    {
        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure(Configuration());
        services.AddSingleton(new Application.Auth.AuthOptions
        {
            SigningKey = new string('k', 64),
        });
        services.AddAuthInfrastructure();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<Application.Auth.AuthenticationService>()
            .Should().NotBeNull();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CarparkDatabase"] =
                    $"Data Source={Path.Combine(Path.GetTempPath(), $"compose-{Guid.NewGuid():N}.db")}",
            })
            .Build();
}
