using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using CarparkInfo.Infrastructure.Ingestion;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CarparkInfo.Infrastructure;

/// <summary>
/// The single entry point through which a host wires up Infrastructure.
/// </summary>
/// <remarks>
/// Both hosts reference this project solely to call <see cref="AddInfrastructure"/>. No controller,
/// use case or entity anywhere else in the solution names an EF Core type, and an architecture test
/// fails the build if that changes.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Default database file, used when configuration supplies no connection string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was <c>"Data Source=carpark.db"</c>, and that single relative path broke the
    /// reviewer's entire journey.</b> SQLite resolves a relative path against the process working
    /// directory, and <c>dotnet run --project X</c> sets that to X's own project folder. So the two
    /// commands in the README wrote to, and read from, two different files:
    /// </para>
    /// <code>
    /// src/CarparkInfo.BatchJob/carpark.db   1344 KB   2,181 carparks   &lt;- the batch job filled this
    /// src/CarparkInfo.Api/carpark.db           4 KB           0        &lt;- the API served this
    /// </code>
    /// <para>
    /// Clone, run the two documented commands, and the API answers every search with an empty list.
    /// Nothing caught it: the functional tests point at their own temporary file, and smoke.ps1 sets
    /// <c>ConnectionStrings__CarparkDatabase</c> to one absolute path for both processes - so the
    /// script that exists to prove a reviewer can clone and run was configuring the very thing the
    /// README never tells them to configure.
    /// </para>
    /// <para>
    /// The default now resolves to a single file at the repository root, found by walking up from
    /// the running assembly to the solution file, so both hosts agree regardless of working
    /// directory. Configuration still wins: any real deployment supplies a connection string and
    /// never reaches this.
    /// </para>
    /// </remarks>
    public static string DefaultConnectionString => $"Data Source={DefaultDatabasePath()}";

    /// <summary>Locates the shared database file both hosts must agree on.</summary>
    /// <returns>An absolute path at the repository root, or a bare file name if it cannot be found.</returns>
    private static string DefaultDatabasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any())
            {
                return Path.Combine(directory.FullName, "carpark.db");
            }

            directory = directory.Parent;
        }

        // Published output has no solution file. Every real deployment configures a connection
        // string, so this fallback only ever applies to a stray local run.
        return "carpark.db";
    }

    /// <summary>Configuration key for the database connection string.</summary>
    public const string ConnectionStringName = "CarparkDatabase";

    /// <summary>Registers persistence and the services that back the Application layer's ports.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString =
            configuration.GetConnectionString(ConnectionStringName) ?? DefaultConnectionString;

        services.AddDbContext<CarparkDbContext>(options => options.UseSqlite(connectionString));

        // A factory as well as the scoped context: failure auditing needs an INDEPENDENT
        // connection so the record survives the rollback that discards the data.
        services.AddDbContextFactory<CarparkDbContext>(
            options => options.UseSqlite(connectionString),
            lifetime: ServiceLifetime.Scoped);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IIngestionContext, IngestionContext>();

        // Record sources. Registering IRecordSource as a collection is what makes the format seam
        // real: adding a format is one more line here and nothing else in the solution changes.
        services.AddScoped<IRecordSource, CsvCarparkRecordSource>();
        services.AddScoped<IRecordSource, JsonCarparkRecordSource>();
        services.AddScoped<IRecordSourceFactory, RecordSourceFactory>();

        services.AddScoped<RecordValidator>();
        services.AddScoped<AtomicMergeService>();
        services.AddScoped<IJobRunStore, JobRunStore>();
        services.AddScoped<ICarparkStagingStore, CarparkStagingStore>();
        services.AddScoped<ILookupResolver, LookupResolver>();
        services.AddScoped<CarparkIngestionService>();

        services.AddScoped<ICarparkRepository, Persistence.CarparkRepository>();
        services.AddScoped<IFavouriteRepository, Persistence.FavouriteRepository>();
        services.AddScoped<IJobRunQueries, Persistence.JobRunQueries>();
        services.AddSingleton<IFileIntake, Ingestion.FileIntake>();
        services.AddScoped<IngestionRunner>();
        services.AddSingleton(new RetryOptions());

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers authentication services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Separate from <see cref="AddInfrastructure"/> because <b>the batch job has no business
    /// knowing about JWT</b>. These services depend on <c>AuthOptions</c>, which only an HTTP host
    /// configures; registering them unconditionally made the batch job fail at startup under DI
    /// validation, since it registered a dependency nothing could satisfy.
    ///
    /// Found by cloning the repository fresh and following the README, which is the only way that
    /// class of defect surfaces - every test either hosts the full API or bypasses the container.
    /// </remarks>
    public static IServiceCollection AddAuthInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IUserRepository, Auth.UserRepository>();
        services.AddSingleton<IPasswordHasher, Auth.Pbkdf2PasswordHasher>();
        services.AddScoped<ITokenService, Auth.JwtTokenService>();
        services.AddScoped<CarparkInfo.Application.Auth.AuthenticationService>();

        return services;
    }
}
