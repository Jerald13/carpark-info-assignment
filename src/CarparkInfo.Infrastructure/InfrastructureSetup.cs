using CarparkInfo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarparkInfo.Infrastructure;

/// <summary>Startup helpers that need a built service provider.</summary>
public static class InfrastructureSetup
{
    /// <summary>
    /// Applies any pending migrations.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// Called at startup so a reviewer can clone the repository and run, with no database setup
    /// step. In a production topology this would be a deployment gate rather than an app concern.
    /// </remarks>
    public static async Task MigrateAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarparkDbContext>();

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
