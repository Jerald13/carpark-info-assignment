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

        // CarParkNo is the tie-break, not decoration: two favourites added in the same tick would
        // otherwise order arbitrarily between calls, and a keyset cursor over an unstable order
        // silently skips and repeats rows.
        var ordered = favourites
            .OrderByDescending(f => f.CreatedAt)
            .ThenBy(f => f.CarParkNo, StringComparer.Ordinal)
            .ToList();

        // --- paging -----------------------------------------------------------------------------
        // This method used to end `new PagedResult<>(summaries, null, false, summaries.Count)`:
        // nextCursor hard-coded null, hasMore hard-coded false, and "total" set to the size of the
        // page rather than the number of favourites. page.Cursor was never read at all, so Take()
        // simply truncated. A user with 50 favourites asking for 20 got 20, was told there were 20
        // and that no more existed, and had no way to reach the other 30.
        //
        // The order is computed in memory because SQLite stores DateTimeOffset as TEXT with an
        // offset suffix, so ORDER BY would be lexical rather than chronological and EF refuses to
        // translate it. The whole list is therefore already materialised, which makes both the exact
        // total and the cursor position free - a favourites list is short by nature and pageSize is
        // capped regardless.
        var start = 0;

        if (PageCursor.TryDecode(page.Cursor, out var afterCarParkNo))
        {
            var index = ordered.FindIndex(f =>
                string.Equals(f.CarParkNo, afterCarParkNo, StringComparison.Ordinal));

            // A cursor whose carpark has since been un-favourited restarts from the beginning
            // rather than throwing. The API rejects cursors it never issued; this one it did.
            start = index >= 0 ? index + 1 : 0;
        }

        var carParkNumbers = ordered
            .Skip(start)
            .Take(pageSize)
            .Select(f => f.CarParkNo)
            .ToList();

        var hasMore = start + carParkNumbers.Count < ordered.Count;

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

        var nextCursor = hasMore && carParkNumbers.Count > 0
            ? PageCursor.Encode(carParkNumbers[^1])
            : null;

        // The total is the user's whole favourites list, not the size of this page.
        return new PagedResult<CarparkSummary>(summaries, nextCursor, hasMore, ordered.Count);
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
