using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Carparks;
using CarparkInfo.Domain.Carparks;
using Microsoft.EntityFrameworkCore;

namespace CarparkInfo.Infrastructure.Persistence;

/// <summary>EF Core implementation of carpark reads.</summary>
public sealed class CarparkRepository : ICarparkRepository
{
    private readonly CarparkDbContext _db;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The database context.</param>
    public CarparkRepository(CarparkDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<PagedResult<CarparkSummary>> SearchAsync(
        CarparkFilter filter, PageRequest page, int? userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = ApplyFilters(_db.Carparks.AsNoTracking(), filter);

        int? total = null;
        if (page.IncludeTotalCount)
        {
            total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        }

        var pageSize = page.EffectivePageSize;

        // Distance ordering cannot be expressed as a keyset over car_park_no: sorting a page that
        // was selected by key gives "the nearest of an arbitrary alphabetical slice", not the
        // nearest overall. Because a radius search is bounded (radiusKm is capped), the correct
        // and still-bounded approach is to materialise the bounding-box matches, order them by
        // true distance, and take the page from that. Cursor paging is therefore not offered for
        // distance order - the first page IS the answer to "what is near me".
        if (page.SortOrder == CarparkSortOrder.Distance && filter.HasRadiusSearch)
        {
            var candidates = await Project(query.OrderBy(c => c.CarParkNo), userId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var nearest = candidates
                .Select(r => r.ToSummary(filter))
                .Where(c => c.DistanceKm <= filter.RadiusKilometres)
                .OrderBy(c => c.DistanceKm)
                .Take(pageSize)
                .ToList();

            return new PagedResult<CarparkSummary>(nearest, null, false, total);
        }

        if (!string.IsNullOrEmpty(page.Cursor))
        {
            var after = Cursor.Decode(page.Cursor);

            // The two-argument overload is the one EF Core translates (to a plain SQL '>').
            // string.Compare(a, b, StringComparison.Ordinal) is NOT translatable and throws at
            // query time, which surfaced as a 500 on any request carrying a cursor.
            query = query.Where(c => string.Compare(c.CarParkNo, after) > 0);
        }

        // One extra row answers "is there more?" without a second COUNT query. It is discarded.
        var rows = await Project(query.OrderBy(c => c.CarParkNo).Take(pageSize + 1), userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var results = rows.Select(r => r.ToSummary(filter)).ToList();

        // Radius search: the SQL bounding box is an index-seekable prefilter, but a box returns a
        // square whose corners are 41% further away than the radius asked for, so an exact
        // haversine pass runs over the survivors.
        if (filter.HasRadiusSearch)
        {
            results = [.. results.Where(c => c.DistanceKm <= filter.RadiusKilometres)];

            if (page.SortOrder == CarparkSortOrder.Distance)
            {
                results = [.. results.OrderBy(c => c.DistanceKm)];
            }
        }

        var nextCursor = hasMore && results.Count > 0
            ? Cursor.Encode(rows[^1].CarParkNo)
            : null;

        return new PagedResult<CarparkSummary>(results, nextCursor, hasMore, total);
    }

    /// <inheritdoc />
    public async Task<CarparkSummary?> FindByCarParkNoAsync(
        string carParkNo, int? userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(carParkNo);

        var normalised = carParkNo.Trim().ToUpperInvariant();

        var row = await Project(
                _db.Carparks.AsNoTracking().Where(c => c.CarParkNo == normalised), userId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row?.ToSummary(new CarparkFilter());
    }

    /// <inheritdoc />
    public async Task<LookupsResponse> GetLookupsAsync(CancellationToken cancellationToken)
    {
        var carParkTypes = await _db.Carparks
            .GroupBy(c => c.CarParkType!.Code)
            .Select(g => new { Code = g.Key, Name = g.First().CarParkType!.Name, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var parkingSystems = await _db.Carparks
            .GroupBy(c => c.ParkingSystemType!.Code)
            .Select(g => new { Code = g.Key, Name = g.First().ParkingSystemType!.Name, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var shortTerm = await _db.Carparks
            .GroupBy(c => c.ShortTermParkingType!.Code)
            .Select(g => new { Code = g.Key, Name = g.First().ShortTermParkingType!.Description, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var freeParking = await _db.Carparks
            .GroupBy(c => c.FreeParkingType!.Code)
            .Select(g => new { Code = g.Key, Name = g.First().FreeParkingType!.Description, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var restricted = _db.Carparks.Where(c => c.HeightRestriction.IsRestricted);

        var unrestrictedCount = await _db.Carparks
            .CountAsync(c => !c.HeightRestriction.IsRestricted, cancellationToken).ConfigureAwait(false);

        var anyRestricted = await restricted.AnyAsync(cancellationToken).ConfigureAwait(false);

        var minimum = anyRestricted
            ? await restricted.MinAsync(c => c.HeightRestriction.MaximumVehicleHeightMetres!.Value,
                cancellationToken).ConfigureAwait(false)
            : 0m;

        var maximum = anyRestricted
            ? await restricted.MaxAsync(c => c.HeightRestriction.MaximumVehicleHeightMetres!.Value,
                cancellationToken).ConfigureAwait(false)
            : 0m;

        // Presets come from what the catalogue actually contains, so the picker offers real
        // choices rather than an arbitrary slider. 2.15 m alone covers 807 carparks.
        var presets = await restricted
            .GroupBy(c => c.HeightRestriction.MaximumVehicleHeightMetres!.Value)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var total = await _db.Carparks.CountAsync(cancellationToken).ConfigureAwait(false);

        return new LookupsResponse(
            [.. carParkTypes.OrderByDescending(t => t.Count)
                .Select(t => new LookupCountDto(t.Code, t.Name, t.Count))],
            [.. parkingSystems.OrderByDescending(t => t.Count)
                .Select(t => new LookupCountDto(t.Code, t.Name, t.Count))],
            [.. shortTerm.OrderByDescending(t => t.Count)
                .Select(t => new LookupCountDto(t.Code, t.Name, t.Count))],
            [.. freeParking.OrderByDescending(t => t.Count)
                .Select(t => new LookupCountDto(t.Code, t.Name, t.Count))],
            new VehicleHeightGuidance(minimum, maximum, unrestrictedCount, [.. presets.Order()]),
            total);
    }

    /// <summary>
    /// Translates the domain filter into SQL.
    /// </summary>
    /// <remarks>
    /// The height predicate is the one worth reading twice. See <see cref="CarparkFilter"/>.
    /// </remarks>
    private static IQueryable<Carpark> ApplyFilters(IQueryable<Carpark> query, CarparkFilter filter)
    {
        // User story 1. The lookup's IsOffered flag, not a boolean column - the source has no YES.
        if (filter.FreeParkingOnly == true)
        {
            query = query.Where(c => c.FreeParkingType!.IsOffered);
        }

        // User story 2.
        if (filter.NightParkingOnly == true)
        {
            query = query.Where(c => c.HasNightParking);
        }

        // User story 3. THE predicate this entire solution is organised around: a carpark with no
        // gantry accommodates any vehicle, and omitting the first clause hides 477 of them.
        if (filter.MinimumVehicleHeightMetres is { } height)
        {
            query = query.Where(c =>
                !c.HeightRestriction.IsRestricted
                || c.HeightRestriction.MaximumVehicleHeightMetres >= height);
        }

        if (filter.CarParkTypeCodes.Count > 0)
        {
            query = query.Where(c => filter.CarParkTypeCodes.Contains(c.CarParkType!.Code));
        }

        if (filter.ParkingSystemCodes.Count > 0)
        {
            query = query.Where(c => filter.ParkingSystemCodes.Contains(c.ParkingSystemType!.Code));
        }

        if (!string.IsNullOrWhiteSpace(filter.AddressContains))
        {
            var term = filter.AddressContains.Trim();
            query = query.Where(c => EF.Functions.Like(c.Address, $"%{term}%"));
        }

        // Bounding-box prefilter: index-seekable, and cheap enough to discard most of the
        // catalogue before the exact distance is computed.
        if (filter.HasRadiusSearch)
        {
            var latitude = filter.Latitude!.Value;
            var longitude = filter.Longitude!.Value;
            var radius = filter.RadiusKilometres!.Value;

            var latitudeDelta = radius / 111.0;
            var longitudeDelta = radius / (111.0 * Math.Cos(latitude * Math.PI / 180.0));

            query = query.Where(c =>
                c.Location.Latitude >= latitude - latitudeDelta
                && c.Location.Latitude <= latitude + latitudeDelta
                && c.Location.Longitude >= longitude - longitudeDelta
                && c.Location.Longitude <= longitude + longitudeDelta);
        }

        return query;
    }

    private IQueryable<CarparkRow> Project(IQueryable<Carpark> query, int? userId) =>
        query.Select(c => new CarparkRow
        {
            CarParkNo = c.CarParkNo,
            Address = c.Address,
            Latitude = c.Location.Latitude,
            Longitude = c.Location.Longitude,
            Svy21X = c.Location.Svy21X,
            Svy21Y = c.Location.Svy21Y,
            CarParkTypeCode = c.CarParkType!.Code,
            CarParkTypeName = c.CarParkType.Name,
            ParkingSystemCode = c.ParkingSystemType!.Code,
            ParkingSystemName = c.ParkingSystemType.Name,
            ShortTermCode = c.ShortTermParkingType!.Code,
            ShortTermDescription = c.ShortTermParkingType.Description,
            FreeParkingCode = c.FreeParkingType!.Code,
            FreeParkingDescription = c.FreeParkingType.Description,
            FreeParkingIsOffered = c.FreeParkingType.IsOffered,
            FreeParkingStart = c.FreeParkingType.StartTime,
            FreeParkingEnd = c.FreeParkingType.EndTime,
            HasNightParking = c.HasNightParking,
            DeckCount = c.DeckCount,
            IsHeightRestricted = c.HeightRestriction.IsRestricted,
            MaxVehicleHeightMetres = c.HeightRestriction.MaximumVehicleHeightMetres,
            HasBasement = c.HasBasement,

            // Computed server-side. Returning it inline saves the client fetching /favourites and
            // intersecting two lists on every render - an N+1 by another name.
            IsFavourite = userId == null
                ? null
                : _db.Favourites.Any(f => f.UserId == userId && f.Carpark!.CarParkNo == c.CarParkNo),
        });

    /// <summary>Flat projection shape. Kept private: DTO assembly happens in memory.</summary>
    private sealed class CarparkRow
    {
        public string CarParkNo { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double Svy21X { get; init; }
        public double Svy21Y { get; init; }
        public string CarParkTypeCode { get; init; } = string.Empty;
        public string CarParkTypeName { get; init; } = string.Empty;
        public string ParkingSystemCode { get; init; } = string.Empty;
        public string ParkingSystemName { get; init; } = string.Empty;
        public string ShortTermCode { get; init; } = string.Empty;
        public string ShortTermDescription { get; init; } = string.Empty;
        public string FreeParkingCode { get; init; } = string.Empty;
        public string FreeParkingDescription { get; init; } = string.Empty;
        public bool FreeParkingIsOffered { get; init; }
        public TimeOnly? FreeParkingStart { get; init; }
        public TimeOnly? FreeParkingEnd { get; init; }
        public bool HasNightParking { get; init; }
        public int DeckCount { get; init; }
        public bool IsHeightRestricted { get; init; }
        public decimal? MaxVehicleHeightMetres { get; init; }
        public bool HasBasement { get; init; }
        public bool? IsFavourite { get; init; }

        public CarparkSummary ToSummary(CarparkFilter filter)
        {
            double? distance = null;

            if (filter.HasRadiusSearch)
            {
                distance = Math.Round(
                    Location.FromSvy21(Svy21X, Svy21Y)
                        .DistanceInKilometresTo(filter.Latitude!.Value, filter.Longitude!.Value),
                    3);
            }

            return new CarparkSummary(
                CarParkNo,
                Address,
                new LocationDto(Latitude, Longitude, Svy21X, Svy21Y),
                new LookupDto(CarParkTypeCode, CarParkTypeName),
                new LookupDto(ParkingSystemCode, ParkingSystemName),
                new LookupDto(ShortTermCode, ShortTermDescription),
                new FreeParkingDto(FreeParkingCode, FreeParkingDescription, FreeParkingIsOffered,
                    FreeParkingStart, FreeParkingEnd),
                HasNightParking,
                DeckCount,
                new HeightRestrictionDto(IsHeightRestricted, MaxVehicleHeightMetres),
                HasBasement,
                IsFavourite,
                distance);
        }
    }
}

/// <summary>
/// Encodes and decodes the opaque pagination cursor.
/// </summary>
/// <remarks>
/// <para>
/// Base64<b>Url</b> rather than the raw key, so the cursor reads as a token rather than an
/// invitation to hand-craft one. It is obfuscation, not security - the value it carries is a
/// public identifier, and the query is bounded regardless of what a caller puts here.
/// </para>
/// <para>
/// The URL-safe alphabet is not optional: standard Base64 emits '+' and '/', which a client
/// pasting the cursor straight back into a query string will mangle. The bug only appears on the
/// subset of keys whose encoding happens to contain those characters, which is exactly the kind
/// of intermittent paging failure that is miserable to diagnose later.
/// </para>
/// </remarks>
internal static class Cursor
{
    /// <summary>Encodes a key as a cursor.</summary>
    /// <param name="key">The last key on the page.</param>
    /// <returns>An opaque cursor.</returns>
    public static string Encode(string key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CursorPayload(key))));

    /// <summary>Decodes a cursor back to a key.</summary>
    /// <param name="cursor">The cursor.</param>
    /// <returns>The key, or an empty string when the cursor is unreadable.</returns>
    public static string Decode(string cursor)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            return JsonSerializer.Deserialize<CursorPayload>(json)?.K ?? string.Empty;
        }
        catch (Exception)
        {
            // Deliberately broad. This parses UNTRUSTED client input, the failure modes across
            // Base64Url and JSON are several and version-dependent, and the fallback is
            // well-defined and harmless: start from the beginning. A malformed cursor must never
            // become a 500, which is both a poor experience and a free signal to an attacker
            // probing the parameter.
            return string.Empty;
        }
    }

    private sealed record CursorPayload(string K);
}
