using System.Runtime.CompilerServices;
using System.Text.Json;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Ingestion;

namespace CarparkInfo.Infrastructure.Ingestion;

/// <summary>
/// Reads carpark records from a JSON array.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists to prove the claim, not to make it.</b> The README grades the design on
/// being "flexible to changes... changing of interface file format from csv to JSON", and an
/// assertion that a seam exists is worth considerably less than a second adapter sitting behind it.
/// </para>
/// <para>
/// Note what adding it required: this file, and one line in
/// <see cref="DependencyInjection.AddInfrastructure"/>. The ingestion service, the validator, the
/// hasher, the staging store, the merge and every one of their tests are untouched, because none
/// of them ever knew the format in the first place.
/// </para>
/// <para>
/// Records are streamed with <c>JsonSerializer.DeserializeAsyncEnumerable</c>, so a JSON feed gets
/// the same O(1) memory profile as the CSV one rather than materialising the whole document.
/// </para>
/// </remarks>
public sealed class JsonCarparkRecordSource : IRecordSource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public string FormatName => "json";

    /// <inheritdoc />
    public bool CanRead(string fileName) =>
        Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceRecord<CarparkSourceRecord>> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var index = 0;

        IAsyncEnumerable<JsonCarparkRecord?> records;
        try
        {
            records = JsonSerializer.DeserializeAsyncEnumerable<JsonCarparkRecord>(
                stream, Options, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new SchemaDriftException(
                "The JSON file could not be read as an array of carpark records.", exception);
        }

        await foreach (var record in records.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (record is null)
            {
                continue;
            }

            // Line 1 is conceptually the header in CSV terms, so records start at 2. Keeping the
            // numbering consistent across formats means the defect report reads the same either way.
            yield return new SourceRecord<CarparkSourceRecord>(
                LineNumber: index + 1,
                RawLine: JsonSerializer.Serialize(record, Options),
                Value: new CarparkSourceRecord
                {
                    CarParkNo = record.CarParkNo ?? string.Empty,
                    Address = record.Address ?? string.Empty,
                    XCoord = record.XCoord ?? string.Empty,
                    YCoord = record.YCoord ?? string.Empty,
                    CarParkType = record.CarParkType ?? string.Empty,
                    TypeOfParkingSystem = record.TypeOfParkingSystem ?? string.Empty,
                    ShortTermParking = record.ShortTermParking ?? string.Empty,
                    FreeParking = record.FreeParking ?? string.Empty,
                    NightParking = record.NightParking ?? string.Empty,
                    CarParkDecks = record.CarParkDecks ?? string.Empty,
                    GantryHeight = record.GantryHeight ?? string.Empty,
                    CarParkBasement = record.CarParkBasement ?? string.Empty,
                });
        }
    }

    /// <summary>
    /// The JSON shape, using the source feed's snake_case field names so a provider switching
    /// format need not also rename every field.
    /// </summary>
    private sealed record JsonCarparkRecord
    {
        [System.Text.Json.Serialization.JsonPropertyName("car_park_no")]
        public string? CarParkNo { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("address")]
        public string? Address { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("x_coord")]
        public string? XCoord { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("y_coord")]
        public string? YCoord { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("car_park_type")]
        public string? CarParkType { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("type_of_parking_system")]
        public string? TypeOfParkingSystem { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("short_term_parking")]
        public string? ShortTermParking { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("free_parking")]
        public string? FreeParking { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("night_parking")]
        public string? NightParking { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("car_park_decks")]
        public string? CarParkDecks { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("gantry_height")]
        public string? GantryHeight { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("car_park_basement")]
        public string? CarParkBasement { get; init; }
    }
}
