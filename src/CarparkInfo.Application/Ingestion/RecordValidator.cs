using System.Globalization;
using CarparkInfo.Domain.Carparks;
using CarparkInfo.Domain.Ingestion;

namespace CarparkInfo.Application.Ingestion;

/// <summary>A defect found while validating one source row.</summary>
/// <param name="LineNumber">The line in the source file.</param>
/// <param name="CarParkNo">The business key, when it could be read.</param>
/// <param name="FieldName">The offending field.</param>
/// <param name="ErrorCode">A stable machine-readable code.</param>
/// <param name="Severity">Whether this blocks ingestion.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="RawLine">The offending line, verbatim.</param>
public sealed record RecordDefect(
    int LineNumber,
    string? CarParkNo,
    string? FieldName,
    string ErrorCode,
    ErrorSeverity Severity,
    string Message,
    string? RawLine);

/// <summary>A validated row, ready to be staged.</summary>
/// <param name="CarParkNo">The normalised business key.</param>
/// <param name="Address">The address.</param>
/// <param name="Location">Position in both coordinate systems.</param>
/// <param name="CarParkTypeCode">Resolved carpark type code.</param>
/// <param name="ParkingSystemCode">Resolved parking system code.</param>
/// <param name="ShortTermParkingCode">Resolved short-term parking code.</param>
/// <param name="FreeParkingCode">Resolved free parking code.</param>
/// <param name="HasNightParking">Whether night parking is offered.</param>
/// <param name="DeckCount">Number of decks.</param>
/// <param name="HeightRestriction">The normalised gantry limit.</param>
/// <param name="HasBasement">Whether the carpark has a basement.</param>
/// <param name="SourceRowHash">Fingerprint for change detection.</param>
/// <param name="LineNumber">The line this came from.</param>
public sealed record ValidatedCarparkRecord(
    string CarParkNo,
    string Address,
    Location Location,
    string CarParkTypeCode,
    string ParkingSystemCode,
    string ShortTermParkingCode,
    string FreeParkingCode,
    bool HasNightParking,
    int DeckCount,
    HeightRestriction HeightRestriction,
    bool HasBasement,
    string SourceRowHash,
    int LineNumber);

/// <summary>
/// Turns raw source rows into validated records, accumulating every defect rather than throwing on
/// the first one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Collecting rather than throwing is the whole point.</b> Aborting on the first bad row means
/// an operator fixes one defect, re-runs, waits, and discovers the next - however many times it
/// takes. Collecting produces the complete report in a single pass, which is what "minimal human
/// intervention for job recovery" means in practice.
/// </para>
/// <para>
/// Severity is deliberate. Structural problems reject the row; semantic oddities are recorded as
/// warnings and the row is still ingested. Rejecting upstream reference data for being internally
/// inconsistent is how a nightly feed takes down a production system at 02:00 - and the supplied
/// file genuinely contains three such rows.
/// </para>
/// </remarks>
public sealed class RecordValidator
{
    private const int MaximumCarParkNoLength = 10;
    private const int MaximumAddressLength = 200;
    private const int MaximumDeckCount = 100;

    // Singapore's SVY21 extent, with generous margin. Guards against unit and projection errors.
    private const double MinimumSvy21Coordinate = 1_000;
    private const double MaximumSvy21Coordinate = 60_000;

    private static readonly Dictionary<string, string> CarParkTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SURFACE CAR PARK"] = "SURFACE",
        ["MULTI-STOREY CAR PARK"] = "MULTI_STOREY",
        ["BASEMENT CAR PARK"] = "BASEMENT",
        ["SURFACE/MULTI-STOREY CAR PARK"] = "SURFACE_MULTI_STOREY",
        ["COVERED CAR PARK"] = "COVERED",
        ["MECHANISED AND SURFACE CAR PARK"] = "MECHANISED_AND_SURFACE",
        ["MECHANISED CAR PARK"] = "MECHANISED",
    };

    private static readonly Dictionary<string, string> ParkingSystemCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ELECTRONIC PARKING"] = "ELECTRONIC",
        ["COUPON PARKING"] = "COUPON",
    };

    private static readonly Dictionary<string, string> ShortTermParkingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WHOLE DAY"] = "WHOLE_DAY",
        ["7AM-10.30PM"] = "T0700_2230",
        ["7AM-7PM"] = "T0700_1900",
        ["NO"] = "NONE",
    };

    private static readonly Dictionary<string, string> FreeParkingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NO"] = "NONE",
        ["SUN & PH FR 7AM-10.30PM"] = "SUN_PH_0700_2230",
        ["SUN & PH FR 1PM-10.30PM"] = "SUN_PH_1300_2230",
    };

    /// <summary>
    /// Validates one row.
    /// </summary>
    /// <param name="record">The row and its provenance.</param>
    /// <param name="seenCarParkNumbers">Keys already seen in this file, for duplicate detection.</param>
    /// <param name="validated">The validated record, when the row is usable.</param>
    /// <param name="defects">Every defect found in this row.</param>
    /// <returns><see langword="true"/> when the row can be ingested.</returns>
    public bool TryValidate(
        SourceRecord<CarparkSourceRecord> record,
        ISet<string> seenCarParkNumbers,
        out ValidatedCarparkRecord? validated,
        out IReadOnlyList<RecordDefect> defects)
    {
        ArgumentNullException.ThrowIfNull(seenCarParkNumbers);

        var found = new List<RecordDefect>();
        validated = null;
        defects = found;

        var source = record.Value;
        var line = record.LineNumber;
        var raw = record.RawLine;
        var carParkNo = source.CarParkNo?.Trim().ToUpperInvariant() ?? string.Empty;

        void Reject(string field, string code, string message) =>
            found.Add(new RecordDefect(line, carParkNo, field, code, ErrorSeverity.Error, message, raw));

        void Warn(string field, string code, string message) =>
            found.Add(new RecordDefect(line, carParkNo, field, code, ErrorSeverity.Warning, message, raw));

        // --- business key ------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(carParkNo))
        {
            Reject(nameof(source.CarParkNo), "MISSING_KEY", "car_park_no is required.");
        }
        else if (carParkNo.Length > MaximumCarParkNoLength)
        {
            Reject(nameof(source.CarParkNo), "KEY_TOO_LONG",
                $"car_park_no exceeds {MaximumCarParkNoLength} characters.");
        }
        else if (!seenCarParkNumbers.Add(carParkNo))
        {
            Reject(nameof(source.CarParkNo), "DUPLICATE_KEY",
                $"car_park_no '{carParkNo}' appears more than once in this file.");
        }

        // --- address -----------------------------------------------------------------------
        var address = source.Address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            Reject(nameof(source.Address), "MISSING_ADDRESS", "address is required.");
        }
        else if (address.Length > MaximumAddressLength)
        {
            Reject(nameof(source.Address), "ADDRESS_TOO_LONG",
                $"address exceeds {MaximumAddressLength} characters.");
        }

        // --- coordinates -------------------------------------------------------------------
        var location = default(Location);
        var hasLocation = false;

        if (!TryParseDouble(source.XCoord, out var x))
        {
            Reject(nameof(source.XCoord), "UNPARSEABLE_NUMBER", $"x_coord '{source.XCoord}' is not a number.");
        }
        else if (!TryParseDouble(source.YCoord, out var y))
        {
            Reject(nameof(source.YCoord), "UNPARSEABLE_NUMBER", $"y_coord '{source.YCoord}' is not a number.");
        }
        else if (IsOutsideSvy21Extent(x) || IsOutsideSvy21Extent(y))
        {
            Reject(nameof(source.XCoord), "COORDINATE_OUT_OF_RANGE",
                $"({x}, {y}) is outside Singapore's SVY21 extent.");
        }
        else if (!Location.TryFromSvy21(x, y, out location))
        {
            Reject(nameof(source.XCoord), "COORDINATE_OUT_OF_RANGE",
                $"({x}, {y}) converts to a position outside Singapore.");
        }
        else
        {
            hasLocation = true;
        }

        // --- lookups -----------------------------------------------------------------------
        // An unrecognised value is a WARNING, not a rejection. HDB introducing an eighth carpark
        // type must not take the nightly feed down; the code is auto-registered downstream.
        var carParkTypeCode = ResolveOrAutoRegister(
            CarParkTypeCodes, source.CarParkType, nameof(source.CarParkType), Warn);
        var parkingSystemCode = ResolveOrAutoRegister(
            ParkingSystemCodes, source.TypeOfParkingSystem, nameof(source.TypeOfParkingSystem), Warn);
        var shortTermCode = ResolveOrAutoRegister(
            ShortTermParkingCodes, source.ShortTermParking, nameof(source.ShortTermParking), Warn);
        var freeParkingCode = ResolveOrAutoRegister(
            FreeParkingCodes, source.FreeParking, nameof(source.FreeParking), Warn);

        // --- flags -------------------------------------------------------------------------
        if (!TryParseYesNo(source.NightParking, out var hasNightParking))
        {
            Reject(nameof(source.NightParking), "UNPARSEABLE_FLAG",
                $"night_parking '{source.NightParking}' is not YES or NO.");
        }

        if (!TryParseYesNo(source.CarParkBasement, out var hasBasement))
        {
            Reject(nameof(source.CarParkBasement), "UNPARSEABLE_FLAG",
                $"car_park_basement '{source.CarParkBasement}' is not Y or N.");
        }

        // --- decks -------------------------------------------------------------------------
        if (!int.TryParse(source.CarParkDecks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deckCount))
        {
            Reject(nameof(source.CarParkDecks), "UNPARSEABLE_NUMBER",
                $"car_park_decks '{source.CarParkDecks}' is not an integer.");
        }
        else if (deckCount is < 0 or > MaximumDeckCount)
        {
            Reject(nameof(source.CarParkDecks), "OUT_OF_RANGE",
                $"car_park_decks {deckCount} is outside 0-{MaximumDeckCount}.");
        }

        // --- gantry height -----------------------------------------------------------------
        var heightRestriction = default(HeightRestriction);
        if (!decimal.TryParse(source.GantryHeight, NumberStyles.Number, CultureInfo.InvariantCulture, out var rawHeight))
        {
            Reject(nameof(source.GantryHeight), "UNPARSEABLE_NUMBER",
                $"gantry_height '{source.GantryHeight}' is not a number.");
        }
        else if (!HeightRestriction.TryFromSource(rawHeight, out heightRestriction))
        {
            Reject(nameof(source.GantryHeight), "OUT_OF_RANGE",
                $"gantry_height {rawHeight} is neither a sentinel (0.00, 9.99) nor a plausible clearance.");
        }

        // --- cross-field consistency: warn, never reject -----------------------------------
        // The supplied file genuinely contains these. BM4 is a MULTI-STOREY CAR PARK with 0 decks,
        // and two BASEMENT carparks also report 0 decks. They are real rows and must still ingest.
        if (carParkTypeCode is "MULTI_STOREY" or "BASEMENT" && deckCount == 0)
        {
            Warn(nameof(source.CarParkDecks), "INCONSISTENT_DECK_COUNT",
                $"{carParkTypeCode} carpark reports 0 decks. Ingested as supplied.");
        }

        if (found.Exists(d => d.Severity == ErrorSeverity.Error))
        {
            return false;
        }

        var hash = SourceRowHasher.Compute(
            carParkNo, address, hasLocation ? location.Svy21X : 0, hasLocation ? location.Svy21Y : 0,
            carParkTypeCode, parkingSystemCode, shortTermCode, freeParkingCode,
            hasNightParking, deckCount, rawHeight, hasBasement);

        validated = new ValidatedCarparkRecord(
            carParkNo, address, location, carParkTypeCode, parkingSystemCode, shortTermCode,
            freeParkingCode, hasNightParking, deckCount, heightRestriction, hasBasement, hash, line);

        return true;
    }

    private static string ResolveOrAutoRegister(
        Dictionary<string, string> known, string? value, string fieldName,
        Action<string, string, string> warn)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        if (known.TryGetValue(trimmed, out var code))
        {
            return code;
        }

        warn(fieldName, "UNKNOWN_LOOKUP_VALUE",
            $"'{trimmed}' is not a recognised {fieldName}. Auto-registered so the feed is not blocked.");

        return ToCode(trimmed);
    }

    /// <summary>Derives a stable code from unrecognised source text.</summary>
    /// <param name="value">The source text.</param>
    /// <returns>An uppercase, underscore-separated code.</returns>
    internal static string ToCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "UNKNOWN";
        }

        var characters = value.Trim().ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();

        var code = new string(characters);
        while (code.Contains("__", StringComparison.Ordinal))
        {
            code = code.Replace("__", "_", StringComparison.Ordinal);
        }

        return code.Trim('_');
    }

    private static bool IsOutsideSvy21Extent(double value) =>
        value is < MinimumSvy21Coordinate or > MaximumSvy21Coordinate;

    private static bool TryParseDouble(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static bool TryParseYesNo(string? value, out bool result)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "Y" or "YES":
                result = true;
                return true;
            case "N" or "NO":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
