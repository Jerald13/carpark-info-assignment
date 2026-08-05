using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using CarparkInfo.Application.Carparks;

namespace CarparkInfo.Api.Contracts;

/// <summary>
/// Query parameters for carpark search. Every filter is optional.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>&lt;example&gt;</c> tags on the filters, deliberately.</b> Swagger UI does not render an
/// example as a hint - it pre-fills the input box with it. Every filter carrying one meant the page
/// opened with free parking AND night parking AND a 2 m vehicle AND multi-storey AND electronic AND
/// an address containing BISHAN AND a 1.5 km radius all applied at once, so a reviewer's first
/// Execute on the assignment's main endpoint returned nothing. The API was correct; the
/// documentation made it look broken and the filters look mandatory.
/// </para>
/// <para>
/// Examples now live in the description text instead, where they read as guidance rather than
/// becoming input. <see cref="PageSize"/> keeps its default because 20 genuinely is the default.
/// </para>
/// </remarks>
public sealed class CarparkSearchRequest : IValidatableObject
{
    /// <summary>Only carparks that offer free parking at some point. Omit for all carparks.</summary>
    [DefaultValue(null)]
    public bool? FreeParking { get; init; }

    /// <summary>Only carparks that offer night parking. Omit for all carparks.</summary>
    [DefaultValue(null)]
    public bool? NightParking { get; init; }

    /// <summary>
    /// Vehicle height in metres, e.g. <c>2.0</c>. Returns carparks that fit it, <b>including those
    /// with no height restriction at all</b>. Between 0.1 and 10.
    /// </summary>
    [DefaultValue(null)]
    public decimal? VehicleHeight { get; init; }

    /// <summary>
    /// Restrict to these carpark types. Comma-separate for several, e.g.
    /// <c>MULTI_STOREY,BASEMENT</c>. Codes come from <c>GET /api/v1/carparks/lookups</c>.
    /// </summary>
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
    public string? ParkingSystem { get; init; }

    /// <summary>Case-insensitive substring match on the address, e.g. <c>BISHAN</c>.</summary>
    public string? Address { get; init; }

    /// <summary>Centre latitude for a radius search, e.g. <c>1.3009</c>.</summary>
    public double? Lat { get; init; }

    /// <summary>Centre longitude for a radius search, e.g. <c>103.8546</c>.</summary>
    public double? Lon { get; init; }

    /// <summary>
    /// Radius in kilometres, e.g. <c>1.5</c>. Requires <c>lat</c> and <c>lon</c>.
    /// </summary>
    public double? RadiusKm { get; init; }

    /// <summary>Sort order: <c>carParkNo</c> (default) or <c>distance</c>.</summary>
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
    /// <param name="validationContext">The validation context. Unused.</param>
    /// <returns>One result per broken rule. Empty when the request is valid.</returns>
    /// <remarks>
    /// <para>
    /// <b>This used to take a <c>ModelStateDictionary</c> and return an <c>ActionResult</c>,</b> so
    /// every controller began with <c>if (request.Validate(ModelState) is { } problem) return
    /// problem;</c>. That put MVC result construction inside a request contract: the DTO knew what an
    /// HTTP 400 looked like, which is not its job, and every new endpoint had to remember the same
    /// two lines or silently skip validation entirely.
    /// </para>
    /// <para>
    /// <see cref="IValidatableObject"/> is the framework's own hook. Model binding calls it, and
    /// <c>[ApiController]</c> turns a failed ModelState into an RFC 7807
    /// <c>ValidationProblemDetails</c> automatically. Same status code, same body, same messages -
    /// but the contract no longer references MVC, and validation cannot be forgotten because nobody
    /// has to remember to call it.
    /// </para>
    /// <para>
    /// The rules stay here rather than becoming <c>[Range]</c> attributes because four of the seven
    /// are relationships BETWEEN fields - a radius needs all three of lat, lon and radius; sorting by
    /// distance needs a centre. An attribute can only see one property, so splitting them across two
    /// mechanisms would scatter the rules for no gain.
    /// </para>
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (VehicleHeight is { } height && height is <= 0 or > 10)
        {
            yield return new ValidationResult(
                "Vehicle height must be between 0.1 and 10.0 metres.", [nameof(VehicleHeight)]);
        }

        if (PageSize is < 1 or > PageRequest.MaximumPageSize)
        {
            yield return new ValidationResult(
                $"Page size must be between 1 and {PageRequest.MaximumPageSize}.", [nameof(PageSize)]);
        }

        var geoParts = new[] { Lat.HasValue, Lon.HasValue, RadiusKm.HasValue };
        if (geoParts.Any(p => p) && !geoParts.All(p => p))
        {
            yield return new ValidationResult(
                "A radius search needs all three of lat, lon and radiusKm.", [nameof(RadiusKm)]);
        }

        if (RadiusKm is { } radius && radius is <= 0 or > MaximumRadiusKilometres)
        {
            yield return new ValidationResult(
                $"Radius must be between 0 and {MaximumRadiusKilometres} kilometres.", [nameof(RadiusKm)]);
        }

        if (Lat is { } latitude && latitude is < -90 or > 90)
        {
            yield return new ValidationResult("Latitude must be between -90 and 90.", [nameof(Lat)]);
        }

        if (Lon is { } longitude && longitude is < -180 or > 180)
        {
            yield return new ValidationResult("Longitude must be between -180 and 180.", [nameof(Lon)]);
        }

        if (string.Equals(Sort, "distance", StringComparison.OrdinalIgnoreCase)
            && !(Lat.HasValue && Lon.HasValue))
        {
            yield return new ValidationResult("Sorting by distance requires lat and lon.", [nameof(Sort)]);
        }
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
