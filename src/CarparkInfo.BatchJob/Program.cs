// Entry point for the carpark ingestion batch job.
//
// One CarparkIngestionService is driven by three thin adapters - this CLI, a scheduled
// IHostedService, and POST /admin/job-runs on the API. No ingestion logic lives in any of
// them, which is what keeps the core service unit-testable without a host.
//
// See ARCHITECTURE.md section 6 and PLAN.md section 11.4.

var builder = Host.CreateApplicationBuilder(args);

// Ingestion services are registered here once CarparkInfo.Infrastructure exposes them (phase 3).

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
