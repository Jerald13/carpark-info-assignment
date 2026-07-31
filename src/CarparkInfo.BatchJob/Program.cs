using CarparkInfo.Application.Ingestion;
using CarparkInfo.BatchJob;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

// Entry point for the carpark ingestion batch job.
//
// One CarparkIngestionService is driven by three thin adapters - this CLI, the scheduled
// IHostedService below, and POST /admin/job-runs on the API. No ingestion logic lives in any of
// them, which is what keeps the core service unit-testable without a host and stops the three
// paths drifting apart.
//
// See ARCHITECTURE.md section 6 and PLAN.md section 11.4.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IFileIntake, CarparkInfo.Infrastructure.Ingestion.FileIntake>();
builder.Services.AddScoped<IngestionRunner>();
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));

var options = CommandLine.Parse(args);

if (options.ShowHelp)
{
    CommandLine.PrintUsage();
    return 0;
}

if (options.GenerateRows is { } rowCount)
{
    var target = options.FilePath ?? $"load-test-{rowCount}.csv";
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    Console.WriteLine($"Generating {rowCount:N0} rows into {target}...");
    var bytes = LoadTestGenerator.Generate(target, rowCount);
    stopwatch.Stop();

    Console.WriteLine($"  {bytes / 1024.0 / 1024.0:N1} MB in {stopwatch.Elapsed.TotalSeconds:N1}s");
    Console.WriteLine($"  Ingest it with: --file {target}");
    return 0;
}

// Scheduled mode runs as a long-lived worker; otherwise this is a one-shot CLI invocation.
if (options.Scheduled)
{
    builder.Services.AddHostedService<ScheduledIngestionService>();

    using var scheduledHost = builder.Build();
    await InfrastructureSetup.MigrateAsync(scheduledHost.Services).ConfigureAwait(false);
    await scheduledHost.RunAsync().ConfigureAwait(false);

    return 0;
}

using var host = builder.Build();
await InfrastructureSetup.MigrateAsync(host.Services).ConfigureAwait(false);

await using var scope = host.Services.CreateAsyncScope();
var runner = scope.ServiceProvider.GetRequiredService<IngestionRunner>();
var intake = scope.ServiceProvider.GetRequiredService<IFileIntake>();

var ingestionOptions = new IngestionOptions
{
    Mode = options.Mode,
    Force = options.Force,
};

// A file named with --file belongs to the operator and is left where it is. Files discovered in
// the inbox belong to the job, so they are archived to processed/ or quarantine/ when done.
var isExplicitFile = options.FilePath is not null;
var files = options.FilePath is { } explicitFile
    ? [ResolveOrExit(explicitFile)]
    : intake.DiscoverPending(ingestionOptions);

if (files.Count == 0)
{
    Console.WriteLine("No files to process.");
    return 0;
}

var exitCode = 0;

foreach (var file in files)
{
    Console.WriteLine($"Processing {Path.GetFileName(file)} ({ingestionOptions.Mode})...");

    var result = await runner
        .RunAsync(file, ingestionOptions, new RetryOptions(), archiveOnCompletion: !isExplicitFile)
        .ConfigureAwait(false);

    Console.WriteLine($"  status      : {result.Status}");
    Console.WriteLine($"  read        : {result.Counts.Read}");
    Console.WriteLine($"  inserted    : {result.Counts.Inserted}");
    Console.WriteLine($"  updated     : {result.Counts.Updated}");
    Console.WriteLine($"  unchanged   : {result.Counts.Unchanged}");
    Console.WriteLine($"  deactivated : {result.Counts.Deactivated}");
    Console.WriteLine($"  rejected    : {result.Counts.Rejected}");

    var warnings = result.Defects.Count(d => d.Severity == ErrorSeverity.Warning);
    var errors = result.Defects.Count(d => d.Severity == ErrorSeverity.Error);
    Console.WriteLine($"  warnings    : {warnings}");
    Console.WriteLine($"  errors      : {errors}");

    if (errors > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  First 10 defects:");
        foreach (var defect in result.Defects.Where(d => d.Severity == ErrorSeverity.Error).Take(10))
        {
            Console.WriteLine($"    line {defect.LineNumber,6}  {defect.ErrorCode,-24} {defect.Message}");
        }
    }

    if (result.Status is not (JobRunStatus.Succeeded or JobRunStatus.Skipped))
    {
        exitCode = 1;
    }
}

return exitCode;

// Resolves a user-supplied path, searching upwards so the job works whether it is launched from
// the repository root or from the project directory. A missing file reports both paths tried
// rather than a bare FileNotFoundException.
static string ResolveOrExit(string path)
{
    if (File.Exists(path))
    {
        return Path.GetFullPath(path);
    }

    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, path);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    Console.Error.WriteLine($"File not found: '{path}'.");
    Console.Error.WriteLine($"  Searched from: {Directory.GetCurrentDirectory()} upwards.");
    Environment.Exit(2);
    return string.Empty;
}
