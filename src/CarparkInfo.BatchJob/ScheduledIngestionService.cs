using CarparkInfo.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarparkInfo.BatchJob;

/// <summary>How often the scheduled worker checks the inbox.</summary>
public sealed class ScheduleOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Schedule";

    /// <summary>Interval between inbox checks.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Drains the inbox on a timer.
/// </summary>
/// <remarks>
/// One of three trigger adapters over the same <see cref="IngestionRunner"/> - the others are the
/// CLI and the admin endpoint. This class contains scheduling and nothing else; every line of
/// ingestion behaviour lives in the service it calls.
/// </remarks>
public sealed class ScheduledIngestionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ScheduleOptions> _schedule;
    private readonly IOptions<IngestionOptions> _ingestion;
    private readonly IOptions<RetryOptions> _retry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduledIngestionService> _logger;

    /// <summary>Creates the scheduled service.</summary>
    /// <param name="scopeFactory">Creates a scope per run, so each gets its own DbContext.</param>
    /// <param name="schedule">Polling interval.</param>
    /// <param name="ingestion">Ingestion options.</param>
    /// <param name="retry">Retry options.</param>
    /// <param name="timeProvider">Clock, so the timer is testable.</param>
    /// <param name="logger">Structured logging.</param>
    public ScheduledIngestionService(
        IServiceScopeFactory scopeFactory,
        IOptions<ScheduleOptions> schedule,
        IOptions<IngestionOptions> ingestion,
        IOptions<RetryOptions> retry,
        TimeProvider timeProvider,
        ILogger<ScheduledIngestionService> logger)
    {
        _scopeFactory = scopeFactory;
        _schedule = schedule;
        _ingestion = ingestion;
        _retry = retry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ScheduleLog.WorkerStarted(_logger, _schedule.Value.PollInterval);

        using var timer = new PeriodicTimer(_schedule.Value.PollInterval, _timeProvider);

        // Drain once at startup rather than waiting a full interval - this is also what performs
        // the lease reclaim after a crash, so recovery does not wait on the clock.
        await DrainInboxAsync(stoppingToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await DrainInboxAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DrainInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            var runner = scope.ServiceProvider.GetRequiredService<IngestionRunner>();
            var intake = scope.ServiceProvider.GetRequiredService<IFileIntake>();

            var pending = intake.DiscoverPending(_ingestion.Value);

            if (pending.Count == 0)
            {
                return;
            }

            ScheduleLog.FilesDiscovered(_logger, pending.Count);

            foreach (var file in pending)
            {
                await runner.RunAsync(file, _ingestion.Value, _retry.Value,
                        archiveOnCompletion: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A failure here must never kill the worker: tomorrow's file still needs processing.
            ScheduleLog.DrainFailed(_logger, exception);
        }
    }
}

/// <summary>Source-generated log messages for the scheduled worker.</summary>
internal static partial class ScheduleLog
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Information,
        Message = "Scheduled ingestion started; polling the inbox every {Interval}.")]
    public static partial void WorkerStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Found {Count} file(s) awaiting ingestion.")]
    public static partial void FilesDiscovered(ILogger logger, int count);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error,
        Message = "Inbox drain failed. The worker continues; the next tick will retry.")]
    public static partial void DrainFailed(ILogger logger, Exception exception);
}
