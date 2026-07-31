using Microsoft.AspNetCore.Mvc;

namespace CarparkInfo.Api.Controllers;

/// <summary>
/// Liveness and readiness probes for the Carpark Information API.
/// </summary>
/// <remarks>
/// Readiness deliberately reports <c>Degraded</c> when the last successful ingestion run is older
/// than the configured SLA (default 26 hours). A batch job that silently stops running is more
/// dangerous than one that fails loudly, because nothing fires - so absence of success is what
/// monitoring alerts on. The feed check is wired up in phase 4; see PLAN.md section 11.3.
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Reports whether the API process is alive and serving requests.</summary>
    /// <returns>The service name, status and UTC timestamp.</returns>
    /// <response code="200">The API is running.</response>
    [HttpGet("live")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> GetLiveness() =>
        Ok(new HealthResponse("carpark-info-api", "Healthy", DateTimeOffset.UtcNow));
}

/// <summary>A health probe result.</summary>
/// <param name="Service">The name of the service reporting.</param>
/// <param name="Status">One of <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
/// <param name="CheckedAt">When the check ran, in UTC.</param>
public sealed record HealthResponse(string Service, string Status, DateTimeOffset CheckedAt);
