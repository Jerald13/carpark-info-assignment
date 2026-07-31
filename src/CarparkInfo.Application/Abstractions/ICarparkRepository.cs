using CarparkInfo.Application.Carparks;

namespace CarparkInfo.Application.Abstractions;

/// <summary>
/// Reads carparks.
/// </summary>
/// <remarks>
/// <para>
/// Note what this interface does <b>not</b> expose: <c>IQueryable</c>. If it did, every caller
/// would silently depend on the provider's translation capabilities and the "changing of data
/// access technology" flexibility the README grades would be fiction - swapping EF Core for Dapper
/// would break every call site. Methods return materialised, projected DTOs, and an architecture
/// test fails the build if an <c>IQueryable</c> ever appears here.
/// </para>
/// </remarks>
public interface ICarparkRepository
{
    /// <summary>Searches the active catalogue.</summary>
    /// <param name="filter">What to look for, in domain terms.</param>
    /// <param name="page">Paging and ordering.</param>
    /// <param name="userId">The caller, so <c>IsFavourite</c> can be populated. Null when anonymous.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of matching carparks.</returns>
    Task<PagedResult<CarparkSummary>> SearchAsync(
        CarparkFilter filter, PageRequest page, int? userId, CancellationToken cancellationToken);

    /// <summary>Finds one carpark by its public identifier.</summary>
    /// <param name="carParkNo">The business key, e.g. <c>ACB</c>.</param>
    /// <param name="userId">The caller, so <c>IsFavourite</c> can be populated.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The carpark, or null when it does not exist or is inactive.</returns>
    Task<CarparkSummary?> FindByCarParkNoAsync(
        string carParkNo, int? userId, CancellationToken cancellationToken);

    /// <summary>Filter metadata with live counts, for building a data-driven filter UI.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Available filter values and vehicle-height guidance.</returns>
    Task<LookupsResponse> GetLookupsAsync(CancellationToken cancellationToken);
}

/// <summary>Reads and writes a user's favourites.</summary>
/// <remarks>
/// Every method takes the user id as its first parameter, and callers may only ever supply the one
/// derived from the authenticated token. There is no method that accepts an arbitrary user id from
/// a request, which is what removes OWASP API1 (Broken Object Level Authorization) rather than
/// guarding against it.
/// </remarks>
public interface IFavouriteRepository
{
    /// <summary>Lists a user's favourites, most recent first.</summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="page">Paging.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of favourited carparks, as full objects rather than ids.</returns>
    Task<PagedResult<CarparkSummary>> ListAsync(
        int userId, PageRequest page, CancellationToken cancellationToken);

    /// <summary>Adds a favourite, idempotently.</summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="carParkNo">The carpark to favourite.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when it was newly added, <see langword="false"/> when it was already
    /// a favourite. Never an error: favouriting twice is favouriting once.
    /// </returns>
    /// <exception cref="CarparkNotFoundException">No active carpark has that identifier.</exception>
    Task<bool> AddAsync(int userId, string carParkNo, CancellationToken cancellationToken);

    /// <summary>Removes a favourite, idempotently.</summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="carParkNo">The carpark to unfavourite.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    Task<bool> RemoveAsync(int userId, string carParkNo, CancellationToken cancellationToken);
}

/// <summary>Thrown when a carpark identifier does not match an active carpark.</summary>
public sealed class CarparkNotFoundException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="carParkNo">The identifier that did not match.</param>
    public CarparkNotFoundException(string carParkNo)
        : base($"No active carpark with identifier '{carParkNo}'.") => CarParkNo = carParkNo;

    /// <summary>Creates the exception.</summary>
    public CarparkNotFoundException() : base("No active carpark with that identifier.") { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CarparkNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>The identifier that did not match, when one was supplied.</summary>
    public string? CarParkNo { get; }
}
