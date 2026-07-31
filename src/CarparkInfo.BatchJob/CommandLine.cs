using CarparkInfo.Domain.Ingestion;

namespace CarparkInfo.BatchJob;

/// <summary>Options parsed from the command line.</summary>
public sealed record CommandLineOptions
{
    /// <summary>A specific file to ingest, or null to drain the inbox.</summary>
    public string? FilePath { get; init; }

    /// <summary>Delta or snapshot semantics.</summary>
    public IngestionMode Mode { get; init; } = IngestionMode.Delta;

    /// <summary>Reprocess even if the file has already been ingested successfully.</summary>
    public bool Force { get; init; }

    /// <summary>Run continuously on a timer rather than once.</summary>
    public bool Scheduled { get; init; }

    /// <summary>Print usage and exit.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Generate a synthetic load-test file of this many rows instead of ingesting.</summary>
    public int? GenerateRows { get; init; }
}

/// <summary>Parses the batch job's command line.</summary>
/// <remarks>
/// Hand-rolled rather than pulling in a parsing library: six options do not justify a dependency,
/// and the batch job's surface should stay obvious to whoever is reading it during an incident.
/// </remarks>
public static class CommandLine
{
    /// <summary>Parses arguments.</summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <returns>The parsed options.</returns>
    public static CommandLineOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? filePath = null;
        var mode = IngestionMode.Delta;
        var force = false;
        var scheduled = false;
        int? generateRows = null;
        var help = args.Length == 0 && !Console.IsInputRedirected;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "--FILE" or "-F" when i + 1 < args.Length:
                    filePath = args[++i];
                    break;

                case "--MODE" or "-M" when i + 1 < args.Length:
                    mode = Enum.TryParse<IngestionMode>(args[++i], ignoreCase: true, out var parsed)
                        ? parsed
                        : IngestionMode.Delta;
                    break;

                case "--FORCE":
                    force = true;
                    break;

                case "--SCHEDULED":
                    scheduled = true;
                    break;

                case "--GENERATE-LOAD-TEST-FILE" when i + 1 < args.Length:
                    generateRows = int.TryParse(args[++i], out var rows) ? rows : null;
                    break;

                case "--HELP" or "-H" or "-?":
                    help = true;
                    break;

                default:
                    break;
            }
        }

        return new CommandLineOptions
        {
            FilePath = filePath,
            Mode = mode,
            Force = force,
            Scheduled = scheduled,
            ShowHelp = help,
            GenerateRows = generateRows,
        };
    }

    /// <summary>Prints usage.</summary>
    public static void PrintUsage()
    {
        Console.WriteLine("""
            Carpark ingestion batch job

            Usage:
              dotnet run --project src/CarparkInfo.BatchJob -- [options]

            Options:
              -f, --file <path>     Ingest one specific file. Omit to drain the inbox directory.
              -m, --mode <mode>     Delta (default) or Snapshot.

                                    Delta    absence from the file means unchanged.
                                    Snapshot absence means gone, and matching rows are
                                             deactivated - guarded by MaxDeactivationRatio so a
                                             truncated transfer cannot wipe the catalogue.

                  --force           Reprocess even if this exact file already succeeded.
                  --scheduled       Run continuously on a timer instead of once.

                  --generate-load-test-file <rows>
                                    Write a synthetic CSV of <rows> rows sampled from the real
                                    dataset's distribution, then exit. Preserves the ~25% share of
                                    unrestricted gantry heights and the ~8% share of addresses
                                    containing commas, so a load test exercises the paths that
                                    matter rather than a uniform synthetic one.

              -h, --help            Show this message.

            Examples:
              dotnet run --project src/CarparkInfo.BatchJob -- \
                  --file hdb-carpark-information-20220824010400.csv

              dotnet run --project src/CarparkInfo.BatchJob -- --mode Snapshot --force
            """);
    }
}
