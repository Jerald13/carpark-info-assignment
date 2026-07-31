using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CarparkInfo.Api.Controllers;

/// <summary>Liveness and readiness probes.</summary>
[ApiController]
[Route("api/v1/[controller]")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class HealthController : ControllerBase
{
    private readonly IJobRunQueries _jobRuns;
    private readonly IOptions<IngestionOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the controller.</summary>
    /// <param name="jobRuns">Ingestion history, for the freshness check.</param>
    /// <param name="options">Ingestion options, for the SLA.</param>
    /// <param name="timeProvider">Clock.</param>
    public HealthController(
        IJobRunQueries jobRuns, IOptions<IngestionOptions> options, TimeProvider timeProvider)
    {
        _jobRuns = jobRuns;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Reports whether the API process is alive and serving requests.</summary>
    /// <returns>The service name, status and UTC timestamp.</returns>
    /// <remarks>
    /// Deliberately shallow: liveness answers "should this instance be restarted?", and a
    /// dependency being down is not a reason to restart a healthy process. Readiness is where
    /// dependencies are checked.
    /// </remarks>
    /// <response code="200">The API is running.</response>
    [HttpGet("live")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> GetLiveness() =>
        Ok(new HealthResponse("carpark-info-api", "Healthy", _timeProvider.GetUtcNow()));

    /// <summary>
    /// Reports whether the API should receive traffic, including how fresh the catalogue is.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Readiness, with feed freshness.</returns>
    /// <remarks>
    /// Returns `503` and `Degraded` when the last successful ingestion is older than the SLA
    /// (26 hours by default, giving a daily feed two hours of slack).
    ///
    /// **This is the check monitoring should alert on, and it deliberately alerts on absence
    /// rather than on failure.** A job that fails loudly gets noticed — someone sees the error.
    /// A job that silently stops running does not, and that is the more dangerous outcome: the
    /// API keeps serving happily, and nobody realises the data has been frozen for a week.
    /// </remarks>
    /// <response code="200">Ready, and the catalogue is fresh.</response>
    /// <response code="503">Degraded: the last successful ingestion is older than the SLA.</response>
    [HttpGet("ready")]
    [ProducesResponseType(typeof(ReadinessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ReadinessResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ReadinessResponse>> GetReadiness(CancellationToken cancellationToken)
    {
        var freshness = await _jobRuns
            .GetFreshnessAsync(_options.Value.FreshnessSla, cancellationToken)
            .ConfigureAwait(false);

        var response = new ReadinessResponse(
            "carpark-info-api",
            freshness.IsFresh ? "Healthy" : "Degraded",
            _timeProvider.GetUtcNow(),
            new FeedStatus(
                freshness.IsFresh,
                freshness.LastSuccessAt,
                freshness.Age,
                freshness.Sla,
                freshness.LastRunStatus?.ToString(),
                freshness.LastSuccessAt is null
                    ? "No ingestion has succeeded yet. Run the batch job."
                    : freshness.IsFresh
                        ? "The catalogue is within its freshness SLA."
                        : "The last successful ingestion is older than the SLA. The feed may have "
                          + "stopped running; check /api/v1/admin/job-runs."));

        return freshness.IsFresh
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}

/// <summary>A health probe result.</summary>
/// <param name="Service">The name of the service reporting.</param>
/// <param name="Status">One of <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
/// <param name="CheckedAt">When the check ran, in UTC.</param>
public sealed record HealthResponse(string Service, string Status, DateTimeOffset CheckedAt);

/// <summary>A readiness result, including catalogue freshness.</summary>
/// <param name="Service">The name of the service reporting.</param>
/// <param name="Status">One of <c>Healthy</c> or <c>Degraded</c>.</param>
/// <param name="CheckedAt">When the check ran, in UTC.</param>
/// <param name="Feed">How fresh the ingested catalogue is.</param>
public sealed record ReadinessResponse(
    string Service, string Status, DateTimeOffset CheckedAt, FeedStatus Feed);

/// <summary>How fresh the ingested catalogue is.</summary>
/// <param name="IsFresh">Whether the last success is within the SLA.</param>
/// <param name="LastSuccessAt">When ingestion last succeeded.</param>
/// <param name="Age">How long ago that was.</param>
/// <param name="Sla">How stale the data may be before this degrades.</param>
/// <param name="LastRunStatus">How the most recent run ended, whatever its outcome.</param>
/// <param name="Detail">A human-readable explanation.</param>
public sealed record FeedStatus(
    bool IsFresh, DateTimeOffset? LastSuccessAt, TimeSpan? Age, TimeSpan Sla,
    string? LastRunStatus, string Detail);
