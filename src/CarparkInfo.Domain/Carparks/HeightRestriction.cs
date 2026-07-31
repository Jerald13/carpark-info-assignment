namespace CarparkInfo.Domain.Carparks;

/// <summary>
/// The vehicle height limit imposed by a carpark's entrance gantry, if it has one.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because the HDB source data encodes "no limit" as a number, and reading that
/// number literally is the single most consequential defect available in this system.
/// </para>
/// <para>
/// In <c>hdb-carpark-information-20220824010400.csv</c>, <c>gantry_height</c> is <c>0.00</c> on
/// <b>477 of 2,181 rows - and every one of them is a SURFACE CAR PARK</b> (477/477). A further 67
/// rows carry <c>9.99</c>, also exclusively surface carparks. Neither is a measurement:
/// <c>0.00</c> means <i>there is no gantry</i>, and <c>9.99</c> is the source system's sentinel
/// for <i>effectively unlimited</i>.
/// </para>
/// <para>
/// A filter written as <c>gantry_height &gt;= vehicleHeight</c> therefore returns <b>1,579</b>
/// carparks for a 2.0 m vehicle where the correct answer is <b>2,056</b> - silently hiding 23% of
/// the dataset, and hiding precisely the open-air carparks that accommodate <i>any</i> vehicle.
/// The wrong result is non-empty and entirely plausible, which is why it survives casual review.
/// </para>
/// <para>
/// Normalising here, at the domain boundary, means every consumer inherits the correct semantics.
/// A rule applied in one query is a rule the next query will omit.
/// </para>
/// <para>See PLAN.md section 2 and ADR-006. The raw source value is retained for audit.</para>
/// </remarks>
public readonly record struct HeightRestriction
{
    /// <summary>Source value meaning the carpark has no entrance gantry at all.</summary>
    public const decimal NoGantrySentinel = 0.00m;

    /// <summary>Source value meaning the clearance is effectively unlimited.</summary>
    public const decimal UnlimitedSentinel = 9.99m;

    /// <summary>Lowest clearance treated as a genuine measurement. Observed minimum is 1.70 m.</summary>
    public const decimal MinimumPlausibleMetres = 1.0m;

    /// <summary>Highest clearance treated as a genuine measurement. Observed maximum is 5.40 m.</summary>
    public const decimal MaximumPlausibleMetres = 20.0m;

    private HeightRestriction(decimal? maximumVehicleHeightMetres, decimal rawSourceValue)
    {
        MaximumVehicleHeightMetres = maximumVehicleHeightMetres;
        RawSourceValue = rawSourceValue;
    }

    /// <summary>
    /// The tallest vehicle the gantry admits, in metres, or <see langword="null"/> when no
    /// restriction exists.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is semantically honest here in a way that <c>0</c> is not: it states
    /// that no limit exists, rather than that the limit is zero.
    /// </remarks>
    public decimal? MaximumVehicleHeightMetres { get; }

    /// <summary>The unmodified <c>gantry_height</c> value from the source file, kept for audit.</summary>
    public decimal RawSourceValue { get; }

    /// <summary>Whether a height limit applies at all.</summary>
    public bool IsRestricted => MaximumVehicleHeightMetres.HasValue;

    /// <summary>An unrestricted carpark, as if read from a source row with no gantry.</summary>
    public static HeightRestriction Unrestricted { get; } = new(null, NoGantrySentinel);

    /// <summary>
    /// Interprets a raw <c>gantry_height</c> value, normalising the source's sentinels.
    /// </summary>
    /// <param name="rawGantryHeight">The value exactly as it appeared in the source file.</param>
    /// <returns>An unrestricted or restricted height limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative, or is a non-sentinel outside the plausible physical range. Ingestion
    /// validates ranges before reaching this point, so a throw here indicates a source defect that
    /// slipped the validator rather than an expected condition.
    /// </exception>
    public static HeightRestriction FromSource(decimal rawGantryHeight)
    {
        if (!TryFromSource(rawGantryHeight, out var restriction))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawGantryHeight),
                rawGantryHeight,
                $"Gantry height must be {NoGantrySentinel} (no gantry), {UnlimitedSentinel} "
                + $"(unlimited), or between {MinimumPlausibleMetres} and {MaximumPlausibleMetres} metres.");
        }

        return restriction;
    }

    /// <summary>
    /// Interprets a raw <c>gantry_height</c> value without throwing, for use by the ingestion
    /// validator which collects every defect in a file before deciding whether to abort.
    /// </summary>
    /// <param name="rawGantryHeight">The value exactly as it appeared in the source file.</param>
    /// <param name="restriction">The normalised result when the value is valid.</param>
    /// <returns><see langword="true"/> when the value could be interpreted.</returns>
    public static bool TryFromSource(decimal rawGantryHeight, out HeightRestriction restriction)
    {
        restriction = default;

        if (rawGantryHeight is NoGantrySentinel or UnlimitedSentinel)
        {
            restriction = new HeightRestriction(null, rawGantryHeight);
            return true;
        }

        if (rawGantryHeight < MinimumPlausibleMetres || rawGantryHeight > MaximumPlausibleMetres)
        {
            return false;
        }

        restriction = new HeightRestriction(rawGantryHeight, rawGantryHeight);
        return true;
    }

    /// <summary>
    /// Whether a vehicle of the given height can enter.
    /// </summary>
    /// <param name="vehicleHeightMetres">The vehicle's height in metres.</param>
    /// <returns>
    /// <see langword="true"/> when no restriction applies, or when the gantry is at least as tall
    /// as the vehicle.
    /// </returns>
    /// <remarks>
    /// This method <b>is</b> the user story "carpark that can meet my vehicle height requirement".
    /// Expressing it here rather than in a query is what makes the 477 unrestricted carparks
    /// impossible to lose.
    /// </remarks>
    public bool Accommodates(decimal vehicleHeightMetres) =>
        !IsRestricted || MaximumVehicleHeightMetres >= vehicleHeightMetres;

    /// <summary>Returns a readable description of the restriction.</summary>
    public override string ToString() =>
        IsRestricted ? $"{MaximumVehicleHeightMetres:0.00} m" : "unrestricted";
}
