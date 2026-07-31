namespace CarparkInfo.Domain.Ingestion;

/// <summary>
/// A validated source row parked in the staging table, awaiting the atomic merge.
/// </summary>
/// <remarks>
/// <para>
/// This table is the mechanism that lets whole-file rollback and bounded lock time coexist.
/// </para>
/// <para>
/// The obvious way to satisfy "the entire file should rollback" is to wrap every row's UPDATE in
/// one transaction. That works at 2,181 rows and fails at scale: a multi-million-row transaction
/// holds a write lock for minutes, bloats the WAL, and blocks every API read for the duration.
/// </para>
/// <para>
/// Instead the volume is absorbed here, in batches with their own transactions, against a table no
/// reader queries -- so the API stays fully available throughout. Only the final set-based merge
/// from staging into <c>carpark</c> needs to be atomic, and that is a single statement measured in
/// milliseconds.
/// </para>
/// <para>Truncated at the start and end of every run. Never a source of truth.</para>
/// </remarks>
public sealed class CarparkStagingRow
{
    private CarparkStagingRow() { }   // EF Core materialisation

    /// <summary>Stages a validated row.</summary>
    /// <param name="jobRunId">The run that staged it.</param>
    /// <param name="carParkNo">The business key.</param>
    /// <param name="address">The address.</param>
    /// <param name="svy21X">SVY21 easting.</param>
    /// <param name="svy21Y">SVY21 northing.</param>
    /// <param name="latitude">Derived WGS84 latitude.</param>
    /// <param name="longitude">Derived WGS84 longitude.</param>
    /// <param name="carParkTypeId">Resolved carpark type.</param>
    /// <param name="parkingSystemTypeId">Resolved parking system.</param>
    /// <param name="shortTermParkingTypeId">Resolved short-term parking policy.</param>
    /// <param name="freeParkingTypeId">Resolved free parking policy.</param>
    /// <param name="hasNightParking">Whether night parking is offered.</param>
    /// <param name="deckCount">Number of decks.</param>
    /// <param name="gantryHeightMetres">The normalised limit, or null when unrestricted.</param>
    /// <param name="hasHeightRestriction">Whether a limit applies.</param>
    /// <param name="gantryHeightRaw">The raw source value.</param>
    /// <param name="hasBasement">Whether the carpark has a basement.</param>
    /// <param name="sourceRowHash">Fingerprint for change detection.</param>
    /// <param name="lineNumber">The line this came from.</param>
    public CarparkStagingRow(
        int jobRunId, string carParkNo, string address,
        double svy21X, double svy21Y, double latitude, double longitude,
        int carParkTypeId, int parkingSystemTypeId, int shortTermParkingTypeId, int freeParkingTypeId,
        bool hasNightParking, int deckCount,
        decimal? gantryHeightMetres, bool hasHeightRestriction, decimal gantryHeightRaw,
        bool hasBasement, string sourceRowHash, int lineNumber)
    {
        JobRunId = jobRunId;
        CarParkNo = carParkNo;
        Address = address;
        Svy21X = svy21X;
        Svy21Y = svy21Y;
        Latitude = latitude;
        Longitude = longitude;
        CarParkTypeId = carParkTypeId;
        ParkingSystemTypeId = parkingSystemTypeId;
        ShortTermParkingTypeId = shortTermParkingTypeId;
        FreeParkingTypeId = freeParkingTypeId;
        HasNightParking = hasNightParking;
        DeckCount = deckCount;
        GantryHeightMetres = gantryHeightMetres;
        HasHeightRestriction = hasHeightRestriction;
        GantryHeightRaw = gantryHeightRaw;
        HasBasement = hasBasement;
        SourceRowHash = sourceRowHash;
        LineNumber = lineNumber;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>The run that staged this row.</summary>
    public int JobRunId { get; private set; }

    /// <summary>The business key.</summary>
    public string CarParkNo { get; private set; } = string.Empty;

    /// <summary>The address.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>SVY21 easting.</summary>
    public double Svy21X { get; private set; }

    /// <summary>SVY21 northing.</summary>
    public double Svy21Y { get; private set; }

    /// <summary>Derived WGS84 latitude.</summary>
    public double Latitude { get; private set; }

    /// <summary>Derived WGS84 longitude.</summary>
    public double Longitude { get; private set; }

    /// <summary>Resolved carpark type.</summary>
    public int CarParkTypeId { get; private set; }

    /// <summary>Resolved parking system.</summary>
    public int ParkingSystemTypeId { get; private set; }

    /// <summary>Resolved short-term parking policy.</summary>
    public int ShortTermParkingTypeId { get; private set; }

    /// <summary>Resolved free parking policy.</summary>
    public int FreeParkingTypeId { get; private set; }

    /// <summary>Whether night parking is offered.</summary>
    public bool HasNightParking { get; private set; }

    /// <summary>Number of decks.</summary>
    public int DeckCount { get; private set; }

    /// <summary>The normalised limit, or null when unrestricted.</summary>
    public decimal? GantryHeightMetres { get; private set; }

    /// <summary>Whether a limit applies.</summary>
    public bool HasHeightRestriction { get; private set; }

    /// <summary>The raw source value, retained for audit.</summary>
    public decimal GantryHeightRaw { get; private set; }

    /// <summary>Whether the carpark has a basement.</summary>
    public bool HasBasement { get; private set; }

    /// <summary>Fingerprint for change detection.</summary>
    public string SourceRowHash { get; private set; } = string.Empty;

    /// <summary>The line this row came from, for the defect report.</summary>
    public int LineNumber { get; private set; }
}
