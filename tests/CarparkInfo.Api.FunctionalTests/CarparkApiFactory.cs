using CarparkInfo.Application.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Api.FunctionalTests;

/// <summary>
/// Boots the real API over a temporary SQLite database loaded from the real dataset.
/// </summary>
/// <remarks>
/// <para>
/// The whole supplied CSV is ingested through the real pipeline once per test class, so the
/// assertions below run against 2,181 genuine carparks rather than a handful of fixtures. That is
/// what lets the tests pin the exact counts measured during the original profiling - a fixture of
/// five rows could not catch the height-rule regression these tests exist to prevent.
/// </para>
/// </remarks>
public sealed class CarparkApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"carpark-api-{Guid.NewGuid():N}.db");

    /// <summary>Ingests the real dataset once, before any test in the class runs.</summary>
    public async ValueTask InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<CarparkIngestionService>();

        var result = await ingestion.IngestAsync(
            LocateDataset(), new IngestionOptions(), cancellationToken: TestContext.Current.CancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Test fixture could not load the dataset: {result.Summary}");
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        builder.ConfigureServices(services =>
        {
            // Point the real registration at a throwaway file rather than the developer's database.
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<CarparkDbContext>));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<CarparkDbContext>(
                options => options.UseSqlite($"Data Source={_databasePath}"));

            services.AddDbContextFactory<CarparkDbContext>(
                options => options.UseSqlite($"Data Source={_databasePath}"),
                lifetime: ServiceLifetime.Scoped);

            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        });
    }

    /// <summary>Walks up from the test binaries to the repository root to find the dataset.</summary>
    private static string LocateDataset()
    {
        const string fileName = "hdb-carpark-information-20220824010400.csv";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} above {AppContext.BaseDirectory}.");
    }
}
