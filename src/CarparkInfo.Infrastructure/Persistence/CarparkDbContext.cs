using CarparkInfo.Domain.Carparks;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.Persistence;

/// <summary>
/// The EF Core model for the carpark catalogue, users and ingestion audit.
/// </summary>
/// <remarks>
/// This type and everything under <c>Persistence</c> is the only place in the solution that names
/// an EF Core type. The Application layer talks to repository ports instead, which is what makes
/// swapping the data-access technology a new adapter rather than a rewrite - and an architecture
/// test fails the build if that ever stops being true.
/// </remarks>
public sealed class CarparkDbContext : DbContext
{
    /// <summary>
    /// Name of the soft-delete query filter, so callers can disable <i>only</i> that filter.
    /// </summary>
    /// <remarks>
    /// EF Core 10 introduced named query filters. Before that, one unnamed filter per entity made
    /// <c>IgnoreQueryFilters</c> all-or-nothing; now the admin and audit paths can see deactivated
    /// carparks without also discarding any other filter that may be added later.
    /// </remarks>
    public const string SoftDeleteFilter = "SoftDelete";

    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection options.</param>
    public CarparkDbContext(DbContextOptions<CarparkDbContext> options) : base(options) { }

    /// <summary>The carpark catalogue.</summary>
    public DbSet<Carpark> Carparks => Set<Carpark>();

    /// <summary>Carpark type lookup.</summary>
    public DbSet<CarParkType> CarParkTypes => Set<CarParkType>();

    /// <summary>Parking system lookup.</summary>
    public DbSet<ParkingSystemType> ParkingSystemTypes => Set<ParkingSystemType>();

    /// <summary>Short-term parking policy lookup.</summary>
    public DbSet<ShortTermParkingType> ShortTermParkingTypes => Set<ShortTermParkingType>();

    /// <summary>Free parking policy lookup.</summary>
    public DbSet<FreeParkingType> FreeParkingTypes => Set<FreeParkingType>();

    /// <summary>User accounts.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>User favourites.</summary>
    public DbSet<Favourite> Favourites => Set<Favourite>();

    /// <summary>Refresh tokens, stored hashed.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>Ingestion run history.</summary>
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    /// <summary>Defects found during ingestion.</summary>
    public DbSet<JobRunError> JobRunErrors => Set<JobRunError>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CarparkDbContext).Assembly);
    }
}
