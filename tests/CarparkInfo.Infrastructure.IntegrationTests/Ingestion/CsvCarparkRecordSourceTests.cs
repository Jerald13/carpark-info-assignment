using System.Text;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using CarparkInfo.Infrastructure.Ingestion;

namespace CarparkInfo.Infrastructure.IntegrationTests.Ingestion;

/// <summary>
/// Guards the CSV reader against the defect that a naive implementation ships with: addresses in
/// the supplied file contain commas, and splitting on them corrupts every subsequent field without
/// throwing.
/// </summary>
public sealed class CsvCarparkRecordSourceTests
{
    private const string Header =
        "\"car_park_no\",\"address\",\"x_coord\",\"y_coord\",\"car_park_type\","
        + "\"type_of_parking_system\",\"short_term_parking\",\"free_parking\",\"night_parking\","
        + "\"car_park_decks\",\"gantry_height\",\"car_park_basement\"";

    private readonly CsvCarparkRecordSource _source = new();

    // ---------------------------------------------------------------------------------------
    // The regression this reader exists for
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Addresses_containing_commas_do_not_corrupt_later_fields()
    {
        // The real C10 row from hdb-carpark-information-20220824010400.csv. Its address contains
        // FOUR commas. A line.Split(',') implementation shifts car_park_type into x_coord here and
        // produces plausible nonsense rather than an error.
        const string c10 =
            "\"C10\",\"BLK 339,341,344-345,371-381 CLEMENTI AVENUE 5\",\"20837.8461\",\"33414.5726\","
            + "\"SURFACE CAR PARK\",\"ELECTRONIC PARKING\",\"WHOLE DAY\",\"SUN & PH FR 7AM-10.30PM\","
            + "\"YES\",\"0\",\"4.50\",\"N\"";

        var records = await ReadAllAsync(Header + "\n" + c10);

        records.Should().HaveCount(1);
        var record = records[0].Value;

        record.CarParkNo.Should().Be("C10");
        record.Address.Should().Be("BLK 339,341,344-345,371-381 CLEMENTI AVENUE 5",
            "the address is a single field despite containing four commas");
        record.XCoord.Should().Be("20837.8461",
            "if this reads 'SURFACE CAR PARK' the parser split on commas inside the quoted address");
        record.CarParkType.Should().Be("SURFACE CAR PARK");
        record.CarParkBasement.Should().Be("N", "the final field must not have drifted");
    }

    [Theory]
    // Every distinct comma-in-address shape found in the supplied file.
    [InlineData("BLK 213-215,218-227 BISHAN STREET 23")]
    [InlineData("BLK 145-150A, 151 BISHAN STREET 11")]
    [InlineData("BLK 22/24, 59/63, 803/805 CHAI CHEE ROAD")]
    [InlineData("BLK 135-138,141,142 & 145 TECK WHYE LANE/AVE")]
    [InlineData("BLK 512 TO 518, 554 BEDOK NORTH AVE 2")]
    public async Task Every_comma_shape_in_the_dataset_parses_as_one_field(string address)
    {
        var row = $"\"XX1\",\"{address}\",\"30000.0\",\"31000.0\",\"SURFACE CAR PARK\","
            + "\"ELECTRONIC PARKING\",\"WHOLE DAY\",\"NO\",\"YES\",\"0\",\"0.00\",\"N\"";

        var records = await ReadAllAsync(Header + "\n" + row);

        records[0].Value.Address.Should().Be(address);
        records[0].Value.CarParkBasement.Should().Be("N");
    }

    [Fact]
    public async Task Ampersands_in_free_parking_survive_parsing()
    {
        var row = "\"ACM\",\"BLK 98A ALJUNIED CRESCENT\",\"33758.4143\",\"33695.5198\","
            + "\"MULTI-STOREY CAR PARK\",\"ELECTRONIC PARKING\",\"WHOLE DAY\","
            + "\"SUN & PH FR 7AM-10.30PM\",\"YES\",\"5\",\"2.10\",\"N\"";

        var records = await ReadAllAsync(Header + "\n" + row);

        records[0].Value.FreeParking.Should().Be("SUN & PH FR 7AM-10.30PM");
    }

    // ---------------------------------------------------------------------------------------
    // Provenance
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Records_carry_their_line_number_and_raw_text()
    {
        var csv = Header + "\n" + Row("AAA") + "\n" + Row("BBB");

        var records = await ReadAllAsync(csv);

        records[0].LineNumber.Should().Be(2, "line 1 is the header");
        records[1].LineNumber.Should().Be(3);
        records[1].RawLine.Should().Contain("BBB",
            "the raw line goes into the defect report so an operator can see the offending text");
    }

    // ---------------------------------------------------------------------------------------
    // Schema drift
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_column_aborts_the_read()
    {
        const string headerWithoutGantry =
            "\"car_park_no\",\"address\",\"x_coord\",\"y_coord\",\"car_park_type\","
            + "\"type_of_parking_system\",\"short_term_parking\",\"free_parking\",\"night_parking\","
            + "\"car_park_decks\",\"car_park_basement\"";

        var act = async () => await ReadAllAsync(headerWithoutGantry);

        (await act.Should().ThrowAsync<SchemaDriftException>(
            "if the provider drops a column, aborting is correct - ingesting millions of "
            + "silently shifted rows is not"))
            .WithMessage("*gantry_height*");
    }

    [Fact]
    public async Task An_extra_column_is_tolerated()
    {
        var extendedHeader = Header + ",\"new_field_from_provider\"";
        var row = Row("AAA") + ",\"something\"";

        var records = await ReadAllAsync(extendedHeader + "\n" + row);

        records.Should().HaveCount(1,
            "a provider adding a field must not break us; only missing fields are fatal");
    }

    [Fact]
    public async Task An_empty_file_yields_no_records()
    {
        (await ReadAllAsync(string.Empty)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_header_with_no_rows_yields_no_records()
    {
        (await ReadAllAsync(Header)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // Format detection
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("hdb-carpark-information-20220824010400.csv", true)]
    [InlineData("DATA.CSV", true)]
    [InlineData("carparks.json", false)]
    [InlineData("carparks.txt", false)]
    public void The_reader_claims_only_csv_files(string fileName, bool expected)
    {
        _source.CanRead(fileName).Should().Be(expected);
    }

    private static string Row(string carParkNo) =>
        $"\"{carParkNo}\",\"BLK 1 SOMEWHERE\",\"30000.0\",\"31000.0\",\"SURFACE CAR PARK\","
        + "\"ELECTRONIC PARKING\",\"WHOLE DAY\",\"NO\",\"YES\",\"0\",\"0.00\",\"N\"";

    private async Task<List<SourceRecord<CarparkSourceRecord>>> ReadAllAsync(string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var records = new List<SourceRecord<CarparkSourceRecord>>();
        await foreach (var record in _source.ReadAsync(stream, TestContext.Current.CancellationToken))
        {
            records.Add(record);
        }

        return records;
    }
}
