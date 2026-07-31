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
    /// <summary>Default database file, used when configuration supplies no connection string.</summary>
    public const string DefaultConnectionString = "Data Source=carpark.db";

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

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}
