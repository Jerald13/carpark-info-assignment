using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CarparkInfo.Api;

/// <summary>
/// OpenTelemetry tracing, metrics and log correlation.
/// </summary>
/// <remarks>
/// <para>
/// OpenTelemetry rather than a vendor SDK: instrument once, export anywhere. Jaeger, Grafana
/// Tempo, Datadog and anything else speaking OTLP consume the same signals with no code change,
/// which keeps the choice of backend an operational decision rather than an architectural one.
/// </para>
/// <para>
/// Three practices make this useful rather than decorative:
/// </para>
/// <list type="number">
///   <item>
///     Every log line carries <c>TraceId</c> and <c>SpanId</c>, so the <c>traceId</c> returned in
///     an error response jumps straight to the distributed trace and every correlated log line.
///     "The user saw an error" to "here is the exact SQL that failed" is one lookup.
///   </item>
///   <item>
///     Business metrics, not just technical ones. Request duration tells you the API is slow;
///     <c>carpark.ingestion.last_success_age</c> tells you the data is a week old, which is the
///     failure nobody notices.
///   </item>
///   <item>
///     PII never reaches the exporter. EF Core 10 redacts inlined SQL constants by default, and
///     query parameters are not logged.
///   </item>
/// </list>
/// </remarks>
public static class ApiObservability
{
    /// <summary>The service name reported to the telemetry backend.</summary>
    public const string ServiceName = "carpark-info-api";

    /// <summary>Meter name for the application's own metrics.</summary>
    public const string MeterName = "CarparkInfo";

    /// <summary>Registers tracing and metrics.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddApiObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<CarparkMetrics>();

        builder.Logging.AddOpenTelemetry(logging =>
        {
            // Without these the trace id is absent from the log line, and the correlation that
            // makes the whole arrangement worthwhile does not exist.
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(ServiceName, serviceVersion: typeof(ApiObservability).Assembly
                    .GetName().Version?.ToString() ?? "1.0.0"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health probes fire every few seconds. Tracing them buries the requests that
                    // matter and costs real money at any volume.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/api/v1/health");
                })
                .AddHttpClientInstrumentation()
                .AddSource(MeterName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // ASP.NET Core 10 ships authentication and authorisation metrics out of the box:
                // challenge, forbid, sign-in and sign-out counts, with no custom instrumentation.
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("Microsoft.AspNetCore.Authorization")
                .AddMeter("Microsoft.AspNetCore.RateLimiting")
                .AddMeter(MeterName));

        return builder;
    }
}

/// <summary>
/// The application's own metrics.
/// </summary>
/// <remarks>
/// Deliberately about the domain rather than the runtime. Request duration and error rate come
/// free from the framework instrumentation; what the framework cannot know is that a search
/// returned zero results because the height filter regressed, or that ingestion last succeeded
/// six days ago.
/// </remarks>
public sealed class CarparkMetrics
{
    private readonly Counter<long> _searches;
    private readonly Counter<long> _heightFilteredSearches;
    private readonly Histogram<double> _searchResultCount;
    private readonly Counter<long> _favouritesAdded;
    private readonly Counter<long> _refreshTokenReuseDetected;

    /// <summary>Creates the metrics.</summary>
    /// <param name="meterFactory">The meter factory.</param>
    public CarparkMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(ApiObservability.MeterName);

        _searches = meter.CreateCounter<long>(
            "carpark.search.count", description: "Carpark searches performed.");

        _heightFilteredSearches = meter.CreateCounter<long>(
            "carpark.search.height_filtered",
            description: "Searches that applied a vehicle-height filter.");

        _searchResultCount = meter.CreateHistogram<double>(
            "carpark.search.result_count", unit: "{carparks}",
            description: "How many carparks a search returned.");

        _favouritesAdded = meter.CreateCounter<long>(
            "carpark.favourite.added", description: "Favourites added.");

        _refreshTokenReuseDetected = meter.CreateCounter<long>(
            "auth.refresh_token.reuse_detected",
            description: "Refresh tokens presented after already being used. Each one is a "
                       + "credential compromise, and this should alert at any value above zero.");
    }

    /// <summary>Records a search.</summary>
    /// <param name="resultCount">How many carparks it returned.</param>
    /// <param name="usedHeightFilter">Whether a vehicle-height filter was applied.</param>
    /// <remarks>
    /// The result-count histogram is the early warning for a height-rule regression: if the
    /// distribution of height-filtered searches suddenly shifts down by roughly a fifth, the
    /// unrestricted carparks have stopped being included.
    /// </remarks>
    public void RecordSearch(int resultCount, bool usedHeightFilter)
    {
        _searches.Add(1);
        _searchResultCount.Record(resultCount);

        if (usedHeightFilter)
        {
            _heightFilteredSearches.Add(1);
        }
    }

    /// <summary>Records a favourite being added.</summary>
    public void RecordFavouriteAdded() => _favouritesAdded.Add(1);

    /// <summary>Records a detected refresh-token reuse.</summary>
    public void RecordRefreshTokenReuse() => _refreshTokenReuseDetected.Add(1);
}
