using CarparkInfo.Api.Contracts;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Carparks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CarparkInfo.Api.Controllers;

/// <summary>
/// The signed-in user's favourite carparks.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no endpoint here that accepts a user identifier.</b> The user is always taken from
/// the token's <c>sub</c> claim.
/// </para>
/// <para>
/// That is a deliberate response to OWASP API1:2023 — Broken Object Level Authorization — which
/// has been the top API risk since 2019 and accounts for roughly 40% of API attacks. The textbook
/// vulnerable shape is exactly this feature: <c>GET /users/{userId}/favourites</c>, where changing
/// one digit reads somebody else's data. The usual mitigation is an ownership check in every
/// handler, which works only for as long as every future handler remembers to write one — and the
/// one that forgets is the breach.
/// </para>
/// <para>
/// Removing the parameter removes the attack. There is nothing to tamper with and no check to omit.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/favourites")]
[Produces("application/json")]
[Authorize]
public sealed class FavouritesController : ControllerBase
{
    private readonly IFavouriteRepository _favourites;

    /// <summary>Creates the controller.</summary>
    /// <param name="favourites">Favourite storage.</param>
    public FavouritesController(IFavouriteRepository favourites) => _favourites = favourites;

    /// <summary>
    /// Lists the signed-in user's favourites, most recently added first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="request">Page size and cursor. Both optional.</param>
    /// <returns>One page of favourited carparks.</returns>
    /// <remarks>
    /// Returns **full carpark objects**, not identifiers, so a Favourites screen renders in a
    /// single round trip instead of one request per item.
    /// </remarks>
    /// <response code="200">The user's favourites.</response>
    /// <response code="400">The page size or cursor was invalid.</response>
    /// <response code="401">No valid bearer token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<CarparkSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResponse<CarparkSummary>>> List(
        [FromQuery] FavouritesListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _favourites
            .ListAsync(UserId, request.ToPageRequest(), cancellationToken)
            .ConfigureAwait(false);

        return Ok(PagedResponse.From(result, request.PageSize));
    }

    /// <summary>
    /// Adds a carpark to the signed-in user's favourites.
    /// </summary>
    /// <param name="carParkNo">The carpark identifier, e.g. <c>ACB</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>No content.</returns>
    /// <remarks>
    /// **`PUT`, not `POST`, and that is deliberate.** Adding a favourite is naturally idempotent —
    /// favouriting twice is favouriting once. This returns `201` the first time and `200`
    /// afterwards, and never `409`.
    ///
    /// It matters in practice: a double-tap on a phone, or a retry after a dropped connection,
    /// converges on the same state. The client can flip the icon optimistically and needs no
    /// reconciliation logic, because there is no conflict to explain to the user.
    ///
    /// The database agrees: `user_favourite` is keyed on `(user_id, carpark_id)`, so a duplicate
    /// is impossible even by direct SQL.
    /// </remarks>
    /// <response code="201">Added.</response>
    /// <response code="200">Already a favourite. No change.</response>
    /// <response code="401">No valid bearer token.</response>
    /// <response code="404">No active carpark has that identifier.</response>
    [HttpPut("{carParkNo}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(string carParkNo, CancellationToken cancellationToken)
    {
        try
        {
            var added = await _favourites.AddAsync(UserId, carParkNo, cancellationToken)
                .ConfigureAwait(false);

            return added ? StatusCode(StatusCodes.Status201Created) : Ok();
        }
        catch (CarparkNotFoundException exception)
        {
            return Problem(title: "Carpark not found", detail: exception.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Removes a carpark from the signed-in user's favourites.
    /// </summary>
    /// <param name="carParkNo">The carpark identifier.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>No content.</returns>
    /// <remarks>
    /// Idempotent, like `PUT`. Returns `204` whether or not it was a favourite, so a retry after a
    /// dropped connection needs no special handling.
    /// </remarks>
    /// <response code="204">It is no longer a favourite.</response>
    /// <response code="401">No valid bearer token.</response>
    [HttpDelete("{carParkNo}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Remove(string carParkNo, CancellationToken cancellationToken)
    {
        await _favourites.RemoveAsync(UserId, carParkNo, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// The signed-in user, from the token's subject claim.
    /// </summary>
    /// <remarks>
    /// The only source of identity in this controller. <c>[Authorize]</c> guarantees a principal
    /// is present, so the claim is always readable.
    /// </remarks>
    private int UserId =>
        User.GetUserId() ?? throw new UnauthorizedAccessException("No user id in the token.");
}
