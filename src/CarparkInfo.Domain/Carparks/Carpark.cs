namespace CarparkInfo.Domain.Carparks;

/// <summary>
/// An HDB carpark. The aggregate root of the carpark model.
/// </summary>
/// <remarks>
/// <para>
/// Identity is <see cref="CarParkNo"/>, the natural business key from the source feed. It is what
/// the API exposes and what ingestion matches on; <see cref="Id"/> is a surrogate that exists only
/// because an integer primary key is the fastest thing SQLite can index.
/// </para>
/// <para>
/// Carparks are never hard-deleted. Users favourite them, and a foreign key with
/// <c>ON DELETE RESTRICT</c> from the favourites table enforces that at the schema level - so a
/// carpark that disappears from the feed is deactivated, not removed.
/// </para>
/// </remarks>
public sealed class Carpark
{
    private Carpark() { }   // EF Core materialisation

    /// <summary>Creates a carpark from a validated source record.</summary>
    /// <param name="carParkNo">The natural business key, e.g. <c>ACB</c>.</param>
    /// <param name="address">Free-text address as supplied.</param>
    /// <param name="location">Position in both SVY21 and WGS84.</param>
    /// <param name="carParkTypeId">Foreign key to the carpark type.</param>
    /// <param name="parkingSystemTypeId">Foreign key to the parking system.</param>
    /// <param name="shortTermParkingTypeId">Foreign key to the short-term parking policy.</param>
    /// <param name="freeParkingTypeId">Foreign key to the free parking policy.</param>
    /// <param name="hasNightParking">Whether night parking is offered.</param>
    /// <param name="deckCount">Number of decks; 0 for surface carparks.</param>
    /// <param name="heightRestriction">The gantry limit, already normalised.</param>
    /// <param name="hasBasement">Whether the carpark has a basement.</param>
    /// <param name="sourceRowHash">Hash of the source row, for change detection.</param>
    /// <param name="observedAt">When this record was seen in a feed.</param>
    /// <param name="jobRunId">The ingestion run that wrote this record.</param>
    public Carpark(
        string carParkNo,
        string address,
        Location location,
        int carParkTypeId,
        int parkingSystemTypeId,
        int shortTermParkingTypeId,
        int freeParkingTypeId,
        bool hasNightParking,
        int deckCount,
        HeightRestriction heightRestriction,
        bool hasBasement,
        string sourceRowHash,
        DateTimeOffset observedAt,
        int? jobRunId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(carParkNo);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRowHash);
        ArgumentOutOfRangeException.ThrowIfNegative(deckCount);

        CarParkNo = carParkNo.Trim().ToUpperInvariant();
        Address = address.Trim();
        Location = location;
        CarParkTypeId = carParkTypeId;
        ParkingSystemTypeId = parkingSystemTypeId;
        ShortTermParkingTypeId = shortTermParkingTypeId;
        FreeParkingTypeId = freeParkingTypeId;
        HasNightParking = hasNightParking;
        DeckCount = deckCount;
        HeightRestriction = heightRestriction;
        HasBasement = hasBasement;
        SourceRowHash = sourceRowHash;
        IsActive = true;
        FirstSeenAt = observedAt;
        LastSeenAt = observedAt;
        LastModifiedAt = observedAt;
        LastJobRunId = jobRunId;
    }

    /// <summary>Surrogate key. Never exposed by the API.</summary>
    public int Id { get; private set; }

    /// <summary>The natural business key from the source feed, e.g. <c>ACB</c>. Unique.</summary>
    public string CarParkNo { get; private set; } = string.Empty;

    /// <summary>Free-text address. Deliberately not decomposed - see ADR-004.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>Position, carrying both the source SVY21 coordinates and derived WGS84.</summary>
    public Location Location { get; private set; }

    /// <summary>Foreign key to <see cref="Carparks.CarParkType"/>.</summary>
    public int CarParkTypeId { get; private set; }

    /// <summary>Navigation to the carpark type.</summary>
    public CarParkType? CarParkType { get; private set; }

    /// <summary>Foreign key to <see cref="Carparks.ParkingSystemType"/>.</summary>
    public int ParkingSystemTypeId { get; private set; }

    /// <summary>Navigation to the parking system.</summary>
    public ParkingSystemType? ParkingSystemType { get; private set; }

    /// <summary>Foreign key to <see cref="Carparks.ShortTermParkingType"/>.</summary>
    public int ShortTermParkingTypeId { get; private set; }

    /// <summary>Navigation to the short-term parking policy.</summary>
    public ShortTermParkingType? ShortTermParkingType { get; private set; }

    /// <summary>Foreign key to <see cref="Carparks.FreeParkingType"/>.</summary>
    public int FreeParkingTypeId { get; private set; }

    /// <summary>Navigation to the free parking policy.</summary>
    public FreeParkingType? FreeParkingType { get; private set; }

    /// <summary>Whether night parking is offered. Note this is independent of free parking.</summary>
    public bool HasNightParking { get; private set; }

    /// <summary>Number of decks. Zero for surface carparks.</summary>
    public int DeckCount { get; private set; }

    /// <summary>The gantry height limit, with the source's sentinels already normalised.</summary>
    public HeightRestriction HeightRestriction { get; private set; }

    /// <summary>Whether the carpark has a basement.</summary>
    public bool HasBasement { get; private set; }

    /// <summary>
    /// Hash of the normalised source fields. An unchanged daily row costs a hash comparison and no
    /// write at all, so ingestion cost tracks actual change rather than catalogue size.
    /// </summary>
    public string SourceRowHash { get; private set; } = string.Empty;

    /// <summary>Whether the carpark is currently in the catalogue. Soft delete only.</summary>
    public bool IsActive { get; private set; }

    /// <summary>When this carpark first appeared in a feed.</summary>
    public DateTimeOffset FirstSeenAt { get; private set; }

    /// <summary>When this carpark was last present in a feed.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>When any field of this carpark last changed.</summary>
    public DateTimeOffset LastModifiedAt { get; private set; }

    /// <summary>The ingestion run that last wrote this record. Full lineage back to a source file.</summary>
    public int? LastJobRunId { get; private set; }

    /// <summary>
    /// Whether a vehicle of the given height can enter. Delegates to the normalised restriction so
    /// the 477 unrestricted carparks cannot be lost.
    /// </summary>
    /// <param name="vehicleHeightMetres">The vehicle's height in metres.</param>
    /// <returns><see langword="true"/> when the vehicle fits.</returns>
    public bool Accommodates(decimal vehicleHeightMetres) =>
        HeightRestriction.Accommodates(vehicleHeightMetres);

    /// <summary>
    /// Applies an incoming source record to this carpark.
    /// </summary>
    /// <param name="incoming">The carpark as it appears in the current feed.</param>
    /// <param name="observedAt">When the feed was processed.</param>
    /// <param name="jobRunId">The ingestion run applying the change.</param>
    /// <returns><see langword="true"/> when a field actually changed.</returns>
    /// <remarks>
    /// Returns false for an unchanged row so the caller can skip the write entirely. On a real
    /// daily delta most rows are unchanged, and this is what keeps ingestion cost proportional to
    /// change rather than to catalogue size.
    /// </remarks>
    public bool ApplyUpdate(Carpark incoming, DateTimeOffset observedAt, int? jobRunId = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        LastSeenAt = observedAt;
        LastJobRunId = jobRunId ?? LastJobRunId;

        var reactivated = !IsActive;
        IsActive = true;

        if (!reactivated && SourceRowHash == incoming.SourceRowHash)
        {
            return false;
        }

        Address = incoming.Address;
        Location = incoming.Location;
        CarParkTypeId = incoming.CarParkTypeId;
        ParkingSystemTypeId = incoming.ParkingSystemTypeId;
        ShortTermParkingTypeId = incoming.ShortTermParkingTypeId;
        FreeParkingTypeId = incoming.FreeParkingTypeId;
        HasNightParking = incoming.HasNightParking;
        DeckCount = incoming.DeckCount;
        HeightRestriction = incoming.HeightRestriction;
        HasBasement = incoming.HasBasement;
        SourceRowHash = incoming.SourceRowHash;
        LastModifiedAt = observedAt;

        return true;
    }

    /// <summary>
    /// Removes the carpark from the active catalogue without deleting it.
    /// </summary>
    /// <param name="observedAt">When the deactivation happened.</param>
    /// <param name="jobRunId">The ingestion run applying the change.</param>
    /// <remarks>
    /// Used only in snapshot mode, when a carpark is absent from a file that is known to be a full
    /// inventory. Hard deletion is impossible by design - users favourite carparks, and history has
    /// audit value.
    /// </remarks>
    public void Deactivate(DateTimeOffset observedAt, int? jobRunId = null)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        LastModifiedAt = observedAt;
        LastJobRunId = jobRunId ?? LastJobRunId;
    }

    /// <summary>Returns the business key and address.</summary>
    public override string ToString() => $"{CarParkNo} - {Address}";
}
