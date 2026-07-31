namespace CarparkInfo.Application.Carparks;

/// <summary>
/// What the caller is looking for, expressed in domain terms rather than SQL.
/// </summary>
/// <remarks>
/// <para>
/// This type is what makes the data-access swap real. Filter <i>intent</i> lives here, in the
/// Application layer; translating it into a query is Infrastructure's problem. A Dapper or
/// PostgreSQL adapter would consume the same filter and inherit the same semantics -- including
/// the height rule -- rather than reimplementing them.
/// </para>
/// <para>
/// Every filter is optional and they AND together. They are deliberately independent: 350 carparks
/// offer night parking while charging for it, so no combined "is it free" heuristic is correct.
/// </para>
/// </remarks>
public sealed record CarparkFilter
{
    /// <summary>
    /// Only carparks that offer free parking at some point (user story 1).
    /// </summary>
    /// <remarks>
    /// Maps to the free-parking policy being anything other than <c>NONE</c>. The source has no
    /// <c>YES</c> value - free parking is a schedule, and a filter written against a boolean
    /// column would match nothing.
    /// </remarks>
    public bool? FreeParkingOnly { get; init; }

    /// <summary>Only carparks that offer night parking (user story 2).</summary>
    public bool? NightParkingOnly { get; init; }

    /// <summary>
    /// Only carparks that admit a vehicle of this height in metres (user story 3).
    /// </summary>
    /// <remarks>
    /// <b>Includes carparks with no height restriction at all.</b> 477 surface carparks carry a
    /// source gantry height of 0.00, meaning there is no gantry rather than zero clearance. A
    /// literal comparison hides all of them - 23% of the catalogue, and precisely the ones that
    /// accommodate any vehicle.
    /// </remarks>
    public decimal? MinimumVehicleHeightMetres { get; init; }

    /// <summary>Restrict to these carpark type codes. Empty means no restriction.</summary>
    public IReadOnlyList<string> CarParkTypeCodes { get; init; } = [];

    /// <summary>Restrict to these parking system codes. Empty means no restriction.</summary>
    public IReadOnlyList<string> ParkingSystemCodes { get; init; } = [];

    /// <summary>Case-insensitive substring match on the address.</summary>
    public string? AddressContains { get; init; }

    /// <summary>Centre latitude for a radius search.</summary>
    public double? Latitude { get; init; }

    /// <summary>Centre longitude for a radius search.</summary>
    public double? Longitude { get; init; }

    /// <summary>Radius in kilometres. Requires latitude and longitude.</summary>
    public double? RadiusKilometres { get; init; }

    /// <summary>Whether a radius search was requested.</summary>
    public bool HasRadiusSearch =>
        Latitude.HasValue && Longitude.HasValue && RadiusKilometres.HasValue;
}

/// <summary>How results are ordered.</summary>
public enum CarparkSortOrder
{
    /// <summary>By business key. The default, and the only order that supports keyset paging.</summary>
    CarParkNo = 0,

    /// <summary>Nearest first. Requires a radius search.</summary>
    Distance = 1,
}

/// <summary>
/// A page request using an opaque cursor.
/// </summary>
/// <remarks>
/// Keyset rather than offset pagination. <c>OFFSET 100000</c> makes the engine read and discard
/// 100,000 rows, and - more importantly here - a concurrent write shifts every row, so a user
/// scrolling while the nightly job runs sees duplicates and misses others. A keyset seek is
/// constant-time at any depth and stable under concurrent writes.
/// </remarks>
/// <param name="Cursor">The last key from the previous page, or null for the first page.</param>
/// <param name="PageSize">How many results to return.</param>
/// <param name="SortOrder">Result ordering.</param>
/// <param name="IncludeTotalCount">
/// Whether to compute the total. Opt-in because it forces a full scan to produce a number most
/// clients never display.
/// </param>
public readonly record struct PageRequest(
    string? Cursor,
    int PageSize,
    CarparkSortOrder SortOrder = CarparkSortOrder.CarParkNo,
    bool IncludeTotalCount = false)
{
    /// <summary>Largest page a caller may request.</summary>
    public const int MaximumPageSize = 200;

    /// <summary>Page size used when the caller does not specify one.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The page size, clamped to the permitted range.</summary>
    public int EffectivePageSize => Math.Clamp(PageSize, 1, MaximumPageSize);
}

/// <summary>One page of results.</summary>
/// <typeparam name="T">The result type.</typeparam>
/// <param name="Items">The results.</param>
/// <param name="NextCursor">Cursor for the following page, or null when this is the last.</param>
/// <param name="HasMore">Whether more results exist.</param>
/// <param name="TotalCount">The total, when it was requested.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore,
    int? TotalCount);
