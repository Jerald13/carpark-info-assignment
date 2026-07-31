using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CarparkInfo.Api.Controllers;

/// <summary>
/// Ingestion history and manual triggering.
/// </summary>
/// <remarks>
/// Administrators only. Job history exposes source file names, host names and raw lines from the
/// feed, and triggering ingestion is a write against the whole catalogue — neither belongs on a
/// public endpoint.
/// </remarks>
[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
[Authorize(Policy = ApiSecurity.AdminPolicy)]
public sealed class AdminController : ControllerBase
{
    private readonly IJobRunQueries _jobRuns;
    private readonly IngestionRunner _runner;
    private readonly IFileIntake _intake;
    private readonly IOptions<IngestionOptions> _options;

    /// <summary>Creates the controller.</summary>
    /// <param name="jobRuns">Ingestion history.</param>
    /// <param name="runner">The ingestion runner.</param>
    /// <param name="intake">File discovery.</param>
    /// <param name="options">Ingestion options.</param>
    public AdminController(
        IJobRunQueries jobRuns,
        IngestionRunner runner,
        IFileIntake intake,
        IOptions<IngestionOptions> options)
    {
        _jobRuns = jobRuns;
        _runner = runner;
        _intake = intake;
        _options = options;
    }

    /// <summary>
    /// Lists recent ingestion runs, newest first.
    /// </summary>
    /// <param name="limit">How many to return, 1 to 200.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Recent runs with their counts.</returns>
    /// <remarks>
    /// The counts tell the operational story at a glance. On a healthy daily delta most rows are
    /// `unchanged` — that is the row-hash change detection working, and ingestion cost tracking
    /// actual change rather than catalogue size. A run where `rejected` is non-zero rolled the
    /// whole file back and left the catalogue untouched.
    /// </remarks>
    /// <response code="200">Recent runs.</response>
    /// <response code="401">No valid bearer token.</response>
    /// <response code="403">Not an administrator.</response>
    [HttpGet("job-runs")]
    [ProducesResponseType(typeof(IReadOnlyList<JobRunSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<JobRunSummary>>> ListJobRuns(
        [FromQuery] int limit = 20, CancellationToken cancellationToken = default) =>
        Ok(await _jobRuns.ListRecentAsync(limit, cancellationToken).ConfigureAwait(false));

    /// <summary>Gets one ingestion run.</summary>
    /// <param name="id">The run id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The run.</returns>
    /// <response code="200">The run.</response>
    /// <response code="404">No such run.</response>
    [HttpGet("job-runs/{id:int}")]
    [ProducesResponseType(typeof(JobRunSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobRunSummary>> GetJobRun(
        int id, CancellationToken cancellationToken)
    {
        var run = await _jobRuns.FindAsync(id, cancellationToken).ConfigureAwait(false);

        return run is null
            ? Problem(title: "Job run not found", statusCode: StatusCodes.Status404NotFound)
            : Ok(run);
    }

    /// <summary>
    /// Gets a run's complete defect report.
    /// </summary>
    /// <param name="id">The run id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every defect found, with line numbers.</returns>
    /// <remarks>
    /// Each entry carries the **exact line number**, the offending field and the raw line, so a
    /// source file can be corrected without reading a log.
    ///
    /// Validation deliberately collects every defect rather than stopping at the first, so one run
    /// yields the complete list. Stopping early would mean fixing one problem, re-running, waiting,
    /// and discovering the next — however many times it takes.
    ///
    /// Severity matters: `Warning` rows were **ingested anyway**. The supplied dataset contains
    /// three (a MULTI-STOREY carpark reporting zero decks, and two basements doing the same).
    /// Rejecting reference data for being internally inconsistent is how a nightly feed takes down
    /// production at 02:00.
    /// </remarks>
    /// <response code="200">The defect report. May be empty.</response>
    /// <response code="404">No such run.</response>
    [HttpGet("job-runs/{id:int}/defects")]
    [ProducesResponseType(typeof(IReadOnlyList<JobRunDefect>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<JobRunDefect>>> GetDefects(
        int id, CancellationToken cancellationToken)
    {
        if (await _jobRuns.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return Problem(title: "Job run not found", statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(await _jobRuns.GetDefectsAsync(id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Triggers ingestion of whatever is waiting in the inbox.
    /// </summary>
    /// <param name="request">Optional overrides for this run.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What each file's run did.</returns>
    /// <remarks>
    /// The third of three trigger adapters, alongside the scheduled worker and the CLI. All three
    /// call the same `IngestionRunner`, so none of them contains ingestion logic and the paths
    /// cannot drift apart.
    ///
    /// Safe to call repeatedly: a file whose SHA-256 has already succeeded returns `Skipped`
    /// without starting a run. Pass `force: true` to reprocess deliberately.
    /// </remarks>
    /// <response code="200">Every discovered file's outcome.</response>
    /// <response code="401">No valid bearer token.</response>
    /// <response code="403">Not an administrator.</response>
    [HttpPost("job-runs")]
    [ProducesResponseType(typeof(IReadOnlyList<TriggerResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<TriggerResult>>> TriggerIngestion(
        [FromBody] TriggerIngestionRequest? request, CancellationToken cancellationToken)
    {
        var options = new IngestionOptions
        {
            Mode = request?.Mode ?? _options.Value.Mode,
            Force = request?.Force ?? false,
            InboxDirectory = _options.Value.InboxDirectory,
            ProcessedDirectory = _options.Value.ProcessedDirectory,
            QuarantineDirectory = _options.Value.QuarantineDirectory,
            BatchSize = _options.Value.BatchSize,
            MaximumDeactivationRatio = _options.Value.MaximumDeactivationRatio,
        };

        var pending = _intake.DiscoverPending(options);
        var results = new List<TriggerResult>(pending.Count);

        foreach (var file in pending)
        {
            var result = await _runner
                .RunAsync(file, options, new RetryOptions(), archiveOnCompletion: true, cancellationToken)
                .ConfigureAwait(false);

            results.Add(new TriggerResult(
                Path.GetFileName(file),
                result.Status.ToString(),
                result.JobRunId,
                result.Counts.Read,
                result.Counts.Inserted,
                result.Counts.Updated,
                result.Counts.Unchanged,
                result.Summary));
        }

        return Ok(results);
    }
}

/// <summary>Optional overrides for a manually triggered run.</summary>
/// <param name="Mode">Delta or snapshot. Defaults to the configured mode.</param>
/// <param name="Force">Reprocess even if the file has already been ingested successfully.</param>
public sealed record TriggerIngestionRequest(
    Domain.Ingestion.IngestionMode? Mode, bool? Force);

/// <summary>What one triggered file's run did.</summary>
/// <param name="FileName">The file.</param>
/// <param name="Status">How the run ended.</param>
/// <param name="JobRunId">The run id, when one was created.</param>
/// <param name="Read">Rows read.</param>
/// <param name="Inserted">Carparks created.</param>
/// <param name="Updated">Carparks changed.</param>
/// <param name="Unchanged">Carparks left alone because their hash matched.</param>
/// <param name="Summary">A human-readable outcome.</param>
public sealed record TriggerResult(
    string FileName, string Status, int? JobRunId,
    int Read, int Inserted, int Updated, int Unchanged, string Summary);
