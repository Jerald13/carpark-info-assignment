using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Carparks;
using CarparkInfo.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.Persistence;

/// <summary>EF Core implementation of favourites.</summary>
public sealed class FavouriteRepository : IFavouriteRepository
{
    private readonly CarparkDbContext _db;
    private readonly ICarparkRepository _carparks;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="carparks">Carpark reads, reused so favourites return full objects.</param>
    /// <param name="timeProvider">Clock.</param>
    public FavouriteRepository(
        CarparkDbContext db, ICarparkRepository carparks, TimeProvider timeProvider)
    {
        _db = db;
        _carparks = carparks;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<PagedResult<CarparkSummary>> ListAsync(
        int userId, PageRequest page, CancellationToken cancellationToken)
    {
        var pageSize = page.EffectivePageSize;

        // SQLite cannot ORDER BY a DateTimeOffset: it stores them as TEXT with an offset suffix,
        // so the ordering would be lexical rather than chronological and EF refuses to translate
        // it. Materialising first and ordering in memory is exact, and bounded - a user's
        // favourites are a short list by nature, and pageSize is capped regardless.
        var favourites = await _db.Favourites
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => new { f.Carpark!.CarParkNo, f.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var carParkNumbers = favourites
            .OrderByDescending(f => f.CreatedAt)
            .Take(pageSize)
            .Select(f => f.CarParkNo)
            .ToList();

        // Reuses the carpark projection so a favourite carries exactly the same shape as a search
        // result - the client renders both with one component rather than two.
        var summaries = new List<CarparkSummary>(carParkNumbers.Count);

        foreach (var carParkNo in carParkNumbers)
        {
            var summary = await _carparks
                .FindByCarParkNoAsync(carParkNo, userId, cancellationToken)
                .ConfigureAwait(false);

            if (summary is not null)
            {
                summaries.Add(summary);
            }
        }

        return new PagedResult<CarparkSummary>(summaries, null, false, summaries.Count);
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(int userId, string carParkNo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(carParkNo);

        var normalised = carParkNo.Trim().ToUpperInvariant();

        var carparkId = await _db.Carparks
            .Where(c => c.CarParkNo == normalised)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (carparkId == 0)
        {
            throw new CarparkNotFoundException(carParkNo);
        }

        var alreadyFavourite = await _db.Favourites
            .AnyAsync(f => f.UserId == userId && f.CarparkId == carparkId, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyFavourite)
        {
            // Not an error. Favouriting twice is favouriting once, and the composite primary key
            // on (user_id, carpark_id) would reject a duplicate anyway.
            return false;
        }

        _db.Favourites.Add(new Favourite(userId, carparkId, _timeProvider.GetUtcNow()));

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Two concurrent requests can both pass the check above; the primary key resolves the
            // race. The caller asked for the carpark to be a favourite, and it is.
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(int userId, string carParkNo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(carParkNo);

        var normalised = carParkNo.Trim().ToUpperInvariant();

        var favourite = await _db.Favourites
            .FirstOrDefaultAsync(
                f => f.UserId == userId && f.Carpark!.CarParkNo == normalised, cancellationToken)
            .ConfigureAwait(false);

        if (favourite is null)
        {
            return false;
        }

        _db.Favourites.Remove(favourite);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
