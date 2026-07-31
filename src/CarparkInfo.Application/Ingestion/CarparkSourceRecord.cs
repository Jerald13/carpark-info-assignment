namespace CarparkInfo.Application.Ingestion;

/// <summary>
/// One row of the source feed, exactly as supplied and before any interpretation.
/// </summary>
/// <remarks>
/// <para>
/// Every field is a string because that is what the source actually contains, and because parsing
/// failures must be reportable with a line number rather than thrown from inside a CSV reader. The
/// validator turns this into domain types and records a defect for anything it cannot convert.
/// </para>
/// <para>
/// This type is format-agnostic on purpose: the CSV reader and the JSON reader both produce it, so
/// nothing downstream of <see cref="Abstractions.IRecordSource"/> knows which format the bytes
/// arrived in. That is the seam the README grades as "changing of interface file format from csv
/// to JSON".
/// </para>
/// </remarks>
public sealed record CarparkSourceRecord
{
    /// <summary>The business key, e.g. <c>ACB</c>.</summary>
    public required string CarParkNo { get; init; }

    /// <summary>Free-text address. Contains commas on 30+ rows of the supplied file.</summary>
    public required string Address { get; init; }

    /// <summary>SVY21 easting, in metres, as text.</summary>
    public required string XCoord { get; init; }

    /// <summary>SVY21 northing, in metres, as text.</summary>
    public required string YCoord { get; init; }

    /// <summary>Carpark type, e.g. <c>SURFACE CAR PARK</c>.</summary>
    public required string CarParkType { get; init; }

    /// <summary>Parking system, e.g. <c>ELECTRONIC PARKING</c>.</summary>
    public required string TypeOfParkingSystem { get; init; }

    /// <summary>Short-term parking window, e.g. <c>WHOLE DAY</c>.</summary>
    public required string ShortTermParking { get; init; }

    /// <summary>Free parking window, e.g. <c>SUN &amp; PH FR 7AM-10.30PM</c>. Never <c>YES</c>.</summary>
    public required string FreeParking { get; init; }

    /// <summary>Night parking, <c>YES</c> or <c>NO</c>.</summary>
    public required string NightParking { get; init; }

    /// <summary>Number of decks, as text.</summary>
    public required string CarParkDecks { get; init; }

    /// <summary>
    /// Gantry height, as text. <c>0.00</c> means no gantry and <c>9.99</c> means unlimited - see
    /// <c>HeightRestriction</c>.
    /// </summary>
    public required string GantryHeight { get; init; }

    /// <summary>Basement flag, <c>Y</c> or <c>N</c>.</summary>
    public required string CarParkBasement { get; init; }
}

/// <summary>
/// A record together with where it came from, so a defect can name the exact line.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <param name="LineNumber">The line in the source file, 1-based and including the header.</param>
/// <param name="RawLine">The line verbatim, so an operator can see the offending text.</param>
/// <param name="Value">The parsed record.</param>
public readonly record struct SourceRecord<T>(int LineNumber, string RawLine, T Value);
