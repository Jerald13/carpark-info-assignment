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

    /// <summary>
    /// The batch job and the API must default to the same database file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default was <c>"Data Source=carpark.db"</c>. SQLite resolves a relative path against the
    /// process working directory, and <c>dotnet run --project X</c> sets that to X's own folder, so
    /// the two commands the README gives a reviewer used two different databases:
    /// </para>
    /// <code>
    /// src/CarparkInfo.BatchJob/carpark.db   1344 KB   2,181 carparks
    /// src/CarparkInfo.Api/carpark.db           4 KB           0
    /// </code>
    /// <para>
    /// Clone the repository, run both documented commands, and every search returns an empty list.
    /// It survived 255 tests because every test supplies its own connection string - including
    /// smoke.ps1, whose entire purpose is to prove a reviewer can clone and run, and which set
    /// <c>ConnectionStrings__CarparkDatabase</c> for both processes. The harness was configuring the
    /// one thing the README never mentions, so the defect was invisible from inside it.
    /// </para>
    /// <para>
    /// An absolute path is the property that matters. A relative one is correct only by coincidence
    /// of where the process happened to start.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_default_database_path_is_absolute_and_shared_by_every_host()
    {
        const string prefix = "Data Source=";

        var connectionString = DependencyInjection.DefaultConnectionString;
        connectionString.Should().StartWith(prefix);

        var file = connectionString[prefix.Length..];

        Path.IsPathFullyQualified(file).Should().BeTrue(
            "a relative path resolves against each process's working directory, so the batch job "
            + "and the API would write to and read from different files. This is the defect that "
            + "made a freshly cloned repository serve an empty catalogue");

        Path.GetFileName(file).Should().Be("carpark.db");

        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        directory.EnumerateFiles("*.sln").Any().Should().BeTrue(
            "the shared database belongs at the repository root, which is the one directory both "
            + "hosts can agree on without either of them knowing where the other was launched from");
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
