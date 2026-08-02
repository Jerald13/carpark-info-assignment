using System.ComponentModel;
using System.Security.Claims;
using CarparkInfo.Application.Carparks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CarparkInfo.Api.Contracts;

/// <summary>Query parameters for carpark search.</summary>
public sealed class CarparkSearchRequest
{
    /// <summary>Only carparks that offer free parking at some point.</summary>
    /// <example>true</example>
    [DefaultValue(null)]
    public bool? FreeParking { get; init; }

    /// <summary>Only carparks that offer night parking.</summary>
    /// <example>true</example>
    [DefaultValue(null)]
    public bool? NightParking { get; init; }

    /// <summary>
    /// Vehicle height in metres. Returns carparks that fit it, <b>including those with no height
    /// restriction at all</b>.
    /// </summary>
    /// <example>2.0</example>
    [DefaultValue(null)]
    public decimal? VehicleHeight { get; init; }

    /// <summary>
    /// Restrict to these carpark types. Comma-separate for several, e.g.
    /// <c>MULTI_STOREY,BASEMENT</c>. Codes come from <c>GET /api/v1/carparks/lookups</c>.
    /// </summary>
    /// <example>MULTI_STOREY</example>
    /// <remarks>
    /// A comma-separated string rather than a repeated array parameter, deliberately. Swagger UI
    /// runs JSON.parse over any parameter typed as an array, so a plain value in the box throws
    /// "Could not parse parameter value string as JSON Object or JSON Array" and aborts the
    /// request before it is ever sent - the page just spins. A string renders as an ordinary text
    /// box and works. Repeated parameters still bind, because ASP.NET Core joins them with commas.
    /// </remarks>
    public string? CarParkType { get; init; }

    /// <summary>
    /// Restrict to these parking systems. Comma-separate for several, e.g.
    /// <c>ELECTRONIC,COUPON</c>.
    /// </summary>
    /// <example>ELECTRONIC</example>
    public string? ParkingSystem { get; init; }

    /// <summary>Case-insensitive substring match on the address.</summary>
    /// <example>BISHAN</example>
    public string? Address { get; init; }

    /// <summary>Centre latitude for a radius search.</summary>
    /// <example>1.3009</example>
    public double? Lat { get; init; }

    /// <summary>Centre longitude for a radius search.</summary>
    /// <example>103.8546</example>
    public double? Lon { get; init; }

    /// <summary>Radius in kilometres. Requires <c>lat</c> and <c>lon</c>.</summary>
    /// <example>1.5</example>
    public double? RadiusKm { get; init; }

    /// <summary>Sort order: <c>carParkNo</c> (default) or <c>distance</c>.</summary>
    /// <example>distance</example>
    public string? Sort { get; init; }

    /// <summary>Opaque cursor from the previous page's <c>nextCursor</c>.</summary>
    public string? Cursor { get; init; }

    /// <summary>Results per page, 1 to 200.</summary>
    /// <example>20</example>
    [DefaultValue(20)]
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    /// <summary>
    /// Include the total match count. Off by default: it forces a full scan to produce a number
    /// most clients never display.
    /// </summary>
    [DefaultValue(false)]
    public bool IncludeTotal { get; init; }

    /// <summary>Largest radius a caller may request, to bound query cost.</summary>
    public const double MaximumRadiusKilometres = 50.0;

    /// <summary>Validates the request.</summary>
    /// <param name="modelState">Model state to populate with any errors.</param>
    /// <returns>A problem result when invalid, otherwise null.</returns>
    public ActionResult? Validate(ModelStateDictionary modelState)
    {
        ArgumentNullException.ThrowIfNull(modelState);

        if (VehicleHeight is { } height && height is <= 0 or > 10)
        {
            modelState.AddModelError(nameof(VehicleHeight),
                "Vehicle height must be between 0.1 and 10.0 metres.");
        }

        if (PageSize is < 1 or > PageRequest.MaximumPageSize)
        {
            modelState.AddModelError(nameof(PageSize),
                $"Page size must be between 1 and {PageRequest.MaximumPageSize}.");
        }

        var geoParts = new[] { Lat.HasValue, Lon.HasValue, RadiusKm.HasValue };
        if (geoParts.Any(p => p) && !geoParts.All(p => p))
        {
            modelState.AddModelError(nameof(RadiusKm),
                "A radius search needs all three of lat, lon and radiusKm.");
        }

        if (RadiusKm is { } radius && radius is <= 0 or > MaximumRadiusKilometres)
        {
            modelState.AddModelError(nameof(RadiusKm),
                $"Radius must be between 0 and {MaximumRadiusKilometres} kilometres.");
        }

        if (Lat is { } latitude && latitude is < -90 or > 90)
        {
            modelState.AddModelError(nameof(Lat), "Latitude must be between -90 and 90.");
        }

        if (Lon is { } longitude && longitude is < -180 or > 180)
        {
            modelState.AddModelError(nameof(Lon), "Longitude must be between -180 and 180.");
        }

        if (string.Equals(Sort, "distance", StringComparison.OrdinalIgnoreCase)
            && !(Lat.HasValue && Lon.HasValue))
        {
            modelState.AddModelError(nameof(Sort),
                "Sorting by distance requires lat and lon.");
        }

        return modelState.IsValid ? null : new BadRequestObjectResult(new ValidationProblemDetails(modelState));
    }

    /// <summary>Converts to the domain filter.</summary>
    /// <returns>The filter, expressed in domain terms.</returns>
    public CarparkFilter ToFilter() => new()
    {
        FreeParkingOnly = FreeParking,
        NightParkingOnly = NightParking,
        MinimumVehicleHeightMetres = VehicleHeight,
        CarParkTypeCodes = SplitCodes(CarParkType),
        ParkingSystemCodes = SplitCodes(ParkingSystem),
        AddressContains = Address,
        Latitude = Lat,
        Longitude = Lon,
        RadiusKilometres = RadiusKm,
    };

    /// <summary>Splits a comma-separated list into codes, ignoring blanks and whitespace.</summary>
    /// <param name="value">The raw query-string value.</param>
    /// <returns>The codes, uppercased and trimmed.</returns>
    private static IReadOnlyList<string> SplitCodes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(code => code.ToUpperInvariant())];

    /// <summary>Converts to a page request.</summary>
    /// <returns>The page request.</returns>
    public PageRequest ToPageRequest() => new(
        Cursor,
        PageSize,
        string.Equals(Sort, "distance", StringComparison.OrdinalIgnoreCase)
            ? CarparkSortOrder.Distance
            : CarparkSortOrder.CarParkNo,
        IncludeTotal);
}

/// <summary>A page of results with its paging metadata.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Data">The results.</param>
/// <param name="Pagination">How to fetch the next page.</param>
public sealed record PagedResponse<T>(IReadOnlyList<T> Data, PaginationDto Pagination);

/// <summary>Builds paged responses from domain results.</summary>
public static class PagedResponse
{
    /// <summary>Builds a response from a domain result.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="result">The paged result.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <returns>The response.</returns>
    public static PagedResponse<T> From<T>(PagedResult<T> result, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PagedResponse<T>(
            result.Items,
            new PaginationDto(pageSize, result.NextCursor, result.HasMore, result.TotalCount));
    }
}

/// <summary>Paging metadata.</summary>
/// <param name="PageSize">Results per page.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> to fetch the next page. Null on the last page.</param>
/// <param name="HasMore">Whether more results exist.</param>
/// <param name="TotalCount">Total matches, when <c>includeTotal</c> was requested.</param>
public sealed record PaginationDto(int PageSize, string? NextCursor, bool HasMore, int? TotalCount);

/// <summary>Reads the authenticated user from the request principal.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The authenticated user's id, or null when anonymous.
    /// </summary>
    /// <param name="principal">The request principal.</param>
    /// <returns>The user id from the token's subject claim.</returns>
    /// <remarks>
    /// <b>This is the only place a user id enters the application.</b> It comes from the signed
    /// token and never from a route, query string or body, which is what removes OWASP API1
    /// (Broken Object Level Authorization) rather than defending against it: there is no parameter
    /// for an attacker to tamper with.
    /// </remarks>
    public static int? GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal?.FindFirstValue("sub");

        return int.TryParse(value, out var id) ? id : null;
    }
}
