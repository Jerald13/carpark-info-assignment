using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CarparkInfo.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting a host.
/// </summary>
/// <remarks>
/// Without this, the migrations tooling has to boot the API to find a <see cref="DbContext"/>,
/// which drags configuration and DI into what should be a design-time concern. The connection
/// string here is never used at runtime - migrations only need the provider to generate correct
/// SQL.
/// </remarks>
internal sealed class CarparkDbContextFactory : IDesignTimeDbContextFactory<CarparkDbContext>
{
    public CarparkDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CarparkDbContext>()
            .UseSqlite("Data Source=carpark-design-time.db")
            .Options;

        return new CarparkDbContext(options);
    }
}
