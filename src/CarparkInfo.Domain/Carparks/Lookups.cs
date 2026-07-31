namespace CarparkInfo.Domain.Carparks;

/// <summary>
/// The physical form of a carpark. Seven distinct values across the dataset.
/// </summary>
/// <remarks>
/// Extracted from the flat source column into a lookup table: <c>"SURFACE CAR PARK"</c> appears on
/// 1,087 rows, and storing that string 1,087 times is a transitive dependency on a non-key
/// attribute. See docs/er-diagram.md section 1, ADR-003.
/// </remarks>
public sealed class CarParkType
{
    private CarParkType() { }   // EF Core materialisation

    /// <summary>Creates a carpark type.</summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="name">The source text, verbatim.</param>
    public CarParkType(string code, string name)
    {
        Code = code;
        Name = name;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>Stable machine-readable code, e.g. <c>MULTI_STOREY</c>. This is what the API exposes.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>The source text verbatim, e.g. <c>MULTI-STOREY CAR PARK</c>.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the type is currently in use. An unrecognised type arriving in a feed is
    /// auto-registered with a warning rather than failing the run - HDB introducing an eighth
    /// carpark type must not take the nightly ingestion down.
    /// </summary>
    public bool IsActive { get; private set; } = true;
}

/// <summary>
/// How parking is charged: electronic gantry or paper coupon. Two distinct values.
/// </summary>
public sealed class ParkingSystemType
{
    private ParkingSystemType() { }

    /// <summary>Creates a parking system type.</summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="name">The source text, verbatim.</param>
    public ParkingSystemType(string code, string name)
    {
        Code = code;
        Name = name;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>Stable machine-readable code: <c>ELECTRONIC</c> or <c>COUPON</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>The source text verbatim, e.g. <c>ELECTRONIC PARKING</c>.</summary>
    public string Name { get; private set; } = string.Empty;
}

/// <summary>
/// When short-term parking is available. Four distinct values.
/// </summary>
/// <remarks>
/// Like free parking, this is a schedule rather than a flag. The window is decomposed into times so
/// a future "open right now" story needs no schema change.
/// </remarks>
public sealed class ShortTermParkingType
{
    private ShortTermParkingType() { }

    /// <summary>Creates a short-term parking policy.</summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="description">The source text, verbatim.</param>
    /// <param name="startTime">Window start, or <see langword="null"/> when whole-day or unavailable.</param>
    /// <param name="endTime">Window end, or <see langword="null"/> when whole-day or unavailable.</param>
    /// <param name="isWholeDay">Whether short-term parking runs all day.</param>
    /// <param name="isAvailable">Whether short-term parking is offered at all.</param>
    public ShortTermParkingType(
        string code, string description, TimeOnly? startTime, TimeOnly? endTime,
        bool isWholeDay, bool isAvailable)
    {
        Code = code;
        Description = description;
        StartTime = startTime;
        EndTime = endTime;
        IsWholeDay = isWholeDay;
        IsAvailable = isAvailable;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>Stable code: <c>WHOLE_DAY</c>, <c>T0700_2230</c>, <c>T0700_1900</c> or <c>NONE</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>The source text verbatim, e.g. <c>7AM-10.30PM</c>.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Start of the window, when one applies.</summary>
    public TimeOnly? StartTime { get; private set; }

    /// <summary>End of the window, when one applies.</summary>
    public TimeOnly? EndTime { get; private set; }

    /// <summary>Whether short-term parking runs all day.</summary>
    public bool IsWholeDay { get; private set; }

    /// <summary>Whether short-term parking is offered at all. False only for <c>NONE</c>.</summary>
    public bool IsAvailable { get; private set; }
}

/// <summary>
/// When free parking is offered. Three distinct values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Free parking is a schedule, not a boolean.</b> The source has no <c>YES</c> value at all -
/// the three values are <c>NO</c> (576 rows), <c>SUN &amp; PH FR 7AM-10.30PM</c> (1,594) and
/// <c>SUN &amp; PH FR 1PM-10.30PM</c> (11). A filter written as <c>free_parking = 'YES'</c>
/// silently matches nothing.
/// </para>
/// <para>
/// The user story "carpark that offer free parking" therefore maps to
/// <see cref="IsOffered"/>, which is false only for <c>NONE</c>. Times are decomposed so that a
/// future "free parking right now" story needs no schema change.
/// </para>
/// </remarks>
public sealed class FreeParkingType
{
    private FreeParkingType() { }

    /// <summary>Creates a free parking policy.</summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="description">The source text, verbatim.</param>
    /// <param name="startTime">Window start, or <see langword="null"/> when not offered.</param>
    /// <param name="endTime">Window end, or <see langword="null"/> when not offered.</param>
    /// <param name="appliesOnSundaysAndPublicHolidays">Whether the window is limited to Sundays and public holidays.</param>
    /// <param name="isOffered">Whether free parking is offered at all.</param>
    public FreeParkingType(
        string code, string description, TimeOnly? startTime, TimeOnly? endTime,
        bool appliesOnSundaysAndPublicHolidays, bool isOffered)
    {
        Code = code;
        Description = description;
        StartTime = startTime;
        EndTime = endTime;
        AppliesOnSundaysAndPublicHolidays = appliesOnSundaysAndPublicHolidays;
        IsOffered = isOffered;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>Stable code: <c>NONE</c>, <c>SUN_PH_0700_2230</c> or <c>SUN_PH_1300_2230</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>The source text verbatim, e.g. <c>SUN &amp; PH FR 7AM-10.30PM</c>.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Start of the free window, when one applies.</summary>
    public TimeOnly? StartTime { get; private set; }

    /// <summary>End of the free window, when one applies.</summary>
    public TimeOnly? EndTime { get; private set; }

    /// <summary>Whether the free window is limited to Sundays and public holidays.</summary>
    public bool AppliesOnSundaysAndPublicHolidays { get; private set; }

    /// <summary>
    /// Whether free parking is offered at all. <b>This drives the user-story filter</b> - false
    /// only for <c>NONE</c>.
    /// </summary>
    public bool IsOffered { get; private set; }
}
