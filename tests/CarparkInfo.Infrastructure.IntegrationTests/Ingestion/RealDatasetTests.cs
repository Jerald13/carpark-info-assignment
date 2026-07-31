using CarparkInfo.Application.Ingestion;
using CarparkInfo.Domain.Ingestion;
using CarparkInfo.Infrastructure.Ingestion;

namespace CarparkInfo.Infrastructure.IntegrationTests.Ingestion;

/// <summary>
/// Runs the real reader and validator over the actual supplied dataset and pins every count.
/// </summary>
/// <remarks>
/// <para>
/// These assertions are the reason the design decisions in this solution are defensible rather than
/// plausible. Each number was measured by profiling the file before any code was written; this test
/// proves the implementation reproduces them.
/// </para>
/// <para>
/// It is also the regression guard with the widest blast radius. If the height rule, the free
/// parking mapping, the CSV parsing or the validation severities ever drift, a number here moves
/// and the build fails.
/// </para>
/// </remarks>
public sealed class RealDatasetTests
{
    private const string DatasetFileName = "hdb-carpark-information-20220824010400.csv";

    // Measured directly from the file. See PLAN.md section 1.
    private const int TotalRows = 2181;
    private const int UnrestrictedCarparks = 544;      // 477 with 0.00 + 67 with 9.99
    private const int FreeParkingCarparks = 1605;      // everything except the 576 with NO
    private const int NightParkingCarparks = 1795;
    private const int FitsTwoMetreVehicle = 2056;      // the CORRECT answer
    private const int NaiveFitsTwoMetreVehicle = 1579; // what a literal comparison returns
    private const int ExpectedWarnings = 3;

    [Fact]
    public async Task The_whole_dataset_validates_with_no_errors()
    {
        var outcome = await ProcessAsync();

        outcome.Read.Should().Be(TotalRows);
        outcome.Valid.Should().Be(TotalRows);
        outcome.Errors.Should().Be(0,
            "every row of the supplied file is ingestible; a hard error here means the validator "
            + "has become stricter than the data it exists to accept");
    }

    [Fact]
    public async Task Exactly_three_rows_are_flagged_as_inconsistent_but_still_ingested()
    {
        var outcome = await ProcessAsync();

        outcome.Warnings.Should().Be(ExpectedWarnings,
            "BM4 is a MULTI-STOREY carpark reporting 0 decks, and two BASEMENT carparks do the "
            + "same. They are real rows in a reference feed: rejecting them because they are "
            + "internally inconsistent is how a nightly job takes down production at 02:00");
        outcome.Valid.Should().Be(TotalRows, "warnings must not prevent ingestion");
    }

    [Fact]
    public async Task Five_hundred_and_forty_four_carparks_have_no_height_restriction()
    {
        var outcome = await ProcessAsync();

        outcome.Unrestricted.Should().Be(UnrestrictedCarparks,
            "477 rows carry 0.00 (no gantry) and 67 carry the 9.99 unlimited sentinel");
    }

    /// <summary>The regression guard for the single most consequential rule in the system.</summary>
    [Fact]
    public async Task A_two_metre_vehicle_fits_2056_carparks_not_1579()
    {
        var outcome = await ProcessAsync();

        outcome.FitsTwoMetres.Should().Be(FitsTwoMetreVehicle,
            "the correct filter is 'unrestricted OR limit >= height'");

        outcome.NaiveFitsTwoMetres.Should().Be(NaiveFitsTwoMetreVehicle,
            "and this is what a literal gantry_height >= 2.0 returns");

        (outcome.FitsTwoMetres - outcome.NaiveFitsTwoMetres).Should().Be(477,
            "the difference is exactly the 477 open-air carparks a naive filter hides - 23% of "
            + "the dataset, silently, behind an entirely plausible-looking result");
    }

    [Fact]
    public async Task The_free_parking_and_night_parking_filters_match_the_measured_counts()
    {
        var outcome = await ProcessAsync();

        outcome.FreeParking.Should().Be(FreeParkingCarparks,
            "free parking is a schedule; 'offers free parking' means the policy is not NONE");
        outcome.NightParking.Should().Be(NightParkingCarparks);
    }

    [Fact]
    public async Task Night_parking_and_free_parking_are_genuinely_independent()
    {
        var outcome = await ProcessAsync();

        outcome.NightParkingButNotFree.Should().Be(350,
            "350 carparks offer night parking while charging for it, so no 'is it free' heuristic "
            + "may combine the two filters");
    }

    private static async Task<Outcome> ProcessAsync()
    {
        var source = new CsvCarparkRecordSource();
        var validator = new RecordValidator();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outcome = new Outcome();

        await using var stream = File.OpenRead(LocateDataset());

        await foreach (var record in source.ReadAsync(stream, TestContext.Current.CancellationToken))
        {
            outcome.Read++;

            if (validator.TryValidate(record, seen, out var validated, out var defects))
            {
                outcome.Valid++;
                var v = validated!;

                if (!v.HeightRestriction.IsRestricted)
                {
                    outcome.Unrestricted++;
                }

                if (v.HeightRestriction.Accommodates(2.0m))
                {
                    outcome.FitsTwoMetres++;
                }

                if (v.HeightRestriction.RawSourceValue >= 2.0m)
                {
                    outcome.NaiveFitsTwoMetres++;
                }

                var offersFree = v.FreeParkingCode != "NONE";
                if (offersFree)
                {
                    outcome.FreeParking++;
                }

                if (v.HasNightParking)
                {
                    outcome.NightParking++;

                    if (!offersFree)
                    {
                        outcome.NightParkingButNotFree++;
                    }
                }
            }

            foreach (var defect in defects)
            {
                if (defect.Severity == ErrorSeverity.Error)
                {
                    outcome.Errors++;
                }
                else
                {
                    outcome.Warnings++;
                }
            }
        }

        return outcome;
    }

    /// <summary>Walks up from the test binaries to the repository root to find the dataset.</summary>
    private static string LocateDataset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, DatasetFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {DatasetFileName} above {AppContext.BaseDirectory}.");
    }

    private sealed class Outcome
    {
        public int Read { get; set; }
        public int Valid { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public int Unrestricted { get; set; }
        public int FreeParking { get; set; }
        public int NightParking { get; set; }
        public int NightParkingButNotFree { get; set; }
        public int FitsTwoMetres { get; set; }
        public int NaiveFitsTwoMetres { get; set; }
    }
}
