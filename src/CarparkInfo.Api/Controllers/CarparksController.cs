using CarparkInfo.Api.Contracts;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Carparks;
using Microsoft.AspNetCore.Mvc;

namespace CarparkInfo.Api.Controllers;

/// <summary>
/// Search and retrieve HDB carparks.
/// </summary>
/// <remarks>
/// Read-only and anonymous. Authenticating is optional and adds one thing: <c>isFavourite</c> on
/// every result, so the client does not have to fetch its favourites separately and intersect.
/// </remarks>
[ApiController]
[Route("api/v1/carparks")]
[Produces("application/json")]
public sealed class CarparksController : ControllerBase
{
    private readonly ICarparkRepository _carparks;

    /// <summary>Creates the controller.</summary>
    /// <param name="carparks">Carpark reads.</param>
    public CarparksController(ICarparkRepository carparks) => _carparks = carparks;

    /// <summary>
    /// Searches carparks by the user-story filters.
    /// </summary>
    /// <param name="request">Filter, paging and sort options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One page of matching carparks.</returns>
    /// <remarks>
    /// Every filter is optional and they combine with AND. They are deliberately independent:
    /// 350 carparks offer night parking while charging for it, so no combined "is it free"
    /// shortcut would be correct.
    ///
    /// **On `vehicleHeight`** — this returns carparks that have *no height restriction at all*
    /// as well as those whose gantry is tall enough. 477 surface carparks in the source data
    /// carry a gantry height of `0.00`, which means there is no gantry rather than zero
    /// clearance. Filtering on the raw number instead would silently drop 23% of the catalogue,
    /// and specifically the open-air carparks that fit any vehicle. For a 2.0 m van the correct
    /// answer is 2,056 carparks; a literal comparison returns 1,579.
    ///
    /// Results are keyset-paginated. Follow `pagination.nextCursor` to page; unlike an offset,
    /// a cursor stays stable while the nightly ingestion job writes.
    /// </remarks>
    /// <response code="200">Matching carparks. May be empty.</response>
    /// <response code="400">A parameter was outside its permitted range.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<CarparkSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<CarparkSummary>>> Search(
        [FromQuery] CarparkSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Validate(ModelState) is { } problem)
        {
            return problem;
        }

        var result = await _carparks
            .SearchAsync(request.ToFilter(), request.ToPageRequest(), CurrentUserId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(PagedResponse.From(result, request.PageSize));
    }

    /// <summary>
    /// Gets one carpark by its identifier.
    /// </summary>
    /// <param name="carParkNo">The carpark identifier, e.g. <c>ACB</c>. Case-insensitive.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The carpark.</returns>
    /// <remarks>
    /// The identifier is the source feed's <c>car_park_no</c> — stable, meaningful, and the same
    /// value that appears in search results. The internal surrogate key is never exposed.
    /// </remarks>
    /// <response code="200">The carpark.</response>
    /// <response code="404">No active carpark has that identifier.</response>
    [HttpGet("{carParkNo}")]
    [ProducesResponseType(typeof(CarparkSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CarparkSummary>> GetByCarParkNo(
        string carParkNo, CancellationToken cancellationToken)
    {
        var carpark = await _carparks
            .FindByCarParkNoAsync(carParkNo, CurrentUserId, cancellationToken)
            .ConfigureAwait(false);

        return carpark is null
            ? Problem(
                title: "Carpark not found",
                detail: $"No active carpark with identifier '{carParkNo}'.",
                statusCode: StatusCodes.Status404NotFound)
            : Ok(carpark);
    }

    /// <summary>
    /// Gets the values available for each filter, with live counts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Filter metadata and vehicle-height guidance.</returns>
    /// <remarks>
    /// Intended to drive the filter UI from data rather than hard-coded enums, so HDB adding an
    /// eighth carpark type needs no client release. Counts let the UI grey out empty facets, and
    /// `vehicleHeight.commonPresets` comes from the real distribution — 2.15 m alone covers 807
    /// carparks — so the height picker offers plausible choices instead of an arbitrary slider.
    /// </remarks>
    /// <response code="200">Filter metadata.</response>
    [HttpGet("lookups")]
    [ProducesResponseType(typeof(LookupsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LookupsResponse>> GetLookups(CancellationToken cancellationToken) =>
        Ok(await _carparks.GetLookupsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The authenticated user's id, or null when anonymous.
    /// </summary>
    /// <remarks>
    /// Derived from the token's <c>sub</c> claim and never from the request. See
    /// <c>FavouritesController</c> for why that matters.
    /// </remarks>
    private int? CurrentUserId => User.GetUserId();
}
