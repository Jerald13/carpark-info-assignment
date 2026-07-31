using System.Globalization;
using System.Runtime.CompilerServices;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;
using CsvHelper;
using CsvHelper.Configuration;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>
/// Reads carpark records from an RFC 4180 CSV file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A proper CSV parser is not optional here.</b> Addresses in the supplied file contain commas -
/// more than 30 rows, and <c>C10</c> contains four:
/// </para>
/// <code>
/// "C10","BLK 339,341,344-345,371-381 CLEMENTI AVENUE 5","20837.8461",...
/// </code>
/// <para>
/// A <c>line.Split(',')</c> implementation corrupts every field after <c>address</c> on those rows,
/// shifting <c>car_park_type</c> into <c>x_coord</c>. It does not throw; it produces plausible
/// nonsense. CsvHelper is RFC 4180 compliant and there is a regression test using the real
/// <c>C10</c> row.
/// </para>
/// <para>
/// Records are yielded lazily, so memory is O(1) in file size rather than O(n).
/// </para>
/// </remarks>
public sealed class CsvCarparkRecordSource : IRecordSource
{
    /// <summary>The columns this reader expects, in the order the source supplies them.</summary>
    private static readonly string[] ExpectedHeaders =
    [
        "car_park_no", "address", "x_coord", "y_coord", "car_park_type", "type_of_parking_system",
        "short_term_parking", "free_parking", "night_parking", "car_park_decks", "gantry_height",
        "car_park_basement",
    ];

    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        TrimOptions = TrimOptions.Trim,
        DetectDelimiter = false,
        Delimiter = ",",
        BadDataFound = null,
    };

    /// <inheritdoc />
    public string FormatName => "csv";

    /// <inheritdoc />
    public bool CanRead(string fileName) =>
        Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceRecord<CarparkSourceRecord>> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, leaveOpen: true);
        using var csv = new CsvReader(reader, Configuration);

        if (!await csv.ReadAsync().ConfigureAwait(false))
        {
            yield break;   // empty file
        }

        csv.ReadHeader();
        ValidateHeader(csv.HeaderRecord);

        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new SourceRecord<CarparkSourceRecord>(
                LineNumber: csv.Parser.Row,
                RawLine: csv.Parser.RawRecord.TrimEnd('\r', '\n'),
                Value: new CarparkSourceRecord
                {
                    CarParkNo = csv.GetField("car_park_no") ?? string.Empty,
                    Address = csv.GetField("address") ?? string.Empty,
                    XCoord = csv.GetField("x_coord") ?? string.Empty,
                    YCoord = csv.GetField("y_coord") ?? string.Empty,
                    CarParkType = csv.GetField("car_park_type") ?? string.Empty,
                    TypeOfParkingSystem = csv.GetField("type_of_parking_system") ?? string.Empty,
                    ShortTermParking = csv.GetField("short_term_parking") ?? string.Empty,
                    FreeParking = csv.GetField("free_parking") ?? string.Empty,
                    NightParking = csv.GetField("night_parking") ?? string.Empty,
                    CarParkDecks = csv.GetField("car_park_decks") ?? string.Empty,
                    GantryHeight = csv.GetField("gantry_height") ?? string.Empty,
                    CarParkBasement = csv.GetField("car_park_basement") ?? string.Empty,
                });
        }
    }

    /// <summary>
    /// Fails the run if the source's columns are not what we expect.
    /// </summary>
    /// <remarks>
    /// Deliberately fatal and deliberately early. If the provider renames or reorders a column,
    /// aborting with a clear message is correct; ingesting ten million silently shifted rows is
    /// not. Extra columns are tolerated - a provider adding a field should not break us.
    /// </remarks>
    private static void ValidateHeader(string[]? headers)
    {
        if (headers is null || headers.Length == 0)
        {
            throw new SchemaDriftException("The CSV file has no header row.");
        }

        var present = new HashSet<string>(
            headers.Select(h => h.Trim().Trim('"')), StringComparer.OrdinalIgnoreCase);

        var missing = ExpectedHeaders.Where(expected => !present.Contains(expected)).ToArray();

        if (missing.Length > 0)
        {
            throw new SchemaDriftException(
                $"The CSV file is missing expected column(s): {string.Join(", ", missing)}. "
                + $"Found: {string.Join(", ", headers)}.");
        }
    }
}
