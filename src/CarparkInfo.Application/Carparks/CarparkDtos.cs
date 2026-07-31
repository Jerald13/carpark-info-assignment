namespace CarparkInfo.Application.Carparks;

/// <summary>A carpark as returned by the API.</summary>
/// <param name="CarParkNo">The public identifier, e.g. <c>ACB</c>.</param>
/// <param name="Address">Free-text address.</param>
/// <param name="Location">Position, in both coordinate systems.</param>
/// <param name="CarParkType">Physical form of the carpark.</param>
/// <param name="ParkingSystem">How parking is charged.</param>
/// <param name="ShortTermParking">When short-term parking is available.</param>
/// <param name="FreeParking">When free parking is offered.</param>
/// <param name="NightParking">Whether night parking is offered.</param>
/// <param name="DeckCount">Number of decks; 0 for surface carparks.</param>
/// <param name="HeightRestriction">The gantry limit, if any.</param>
/// <param name="HasBasement">Whether the carpark has a basement.</param>
/// <param name="IsFavourite">Whether the caller has favourited it. Null when not authenticated.</param>
/// <param name="DistanceKm">Distance from the search centre. Null unless a radius search was made.</param>
public sealed record CarparkSummary(
    string CarParkNo,
    string Address,
    LocationDto Location,
    LookupDto CarParkType,
    LookupDto ParkingSystem,
    LookupDto ShortTermParking,
    FreeParkingDto FreeParking,
    bool NightParking,
    int DeckCount,
    HeightRestrictionDto HeightRestriction,
    bool HasBasement,
    bool? IsFavourite,
    double? DistanceKm);

/// <summary>A position, in both the source and map-ready coordinate systems.</summary>
/// <param name="Latitude">WGS84 latitude, ready for a map SDK.</param>
/// <param name="Longitude">WGS84 longitude.</param>
/// <param name="Svy21X">Source easting, in metres.</param>
/// <param name="Svy21Y">Source northing, in metres.</param>
/// <remarks>
/// Latitude and longitude are the point of this type. The feed supplies SVY21 projected metres,
/// which no map SDK understands, so converting once at ingestion means a front-end developer never
/// has to learn what SVY21 is.
/// </remarks>
public sealed record LocationDto(double Latitude, double Longitude, double Svy21X, double Svy21Y);

/// <summary>A lookup value, exposed as a stable code plus display text.</summary>
/// <param name="Code">Stable machine-readable code, safe to switch on.</param>
/// <param name="Name">The source text, suitable for display.</param>
public sealed record LookupDto(string Code, string Name);

/// <summary>The free-parking policy.</summary>
/// <param name="Code">Stable code.</param>
/// <param name="Description">The source text.</param>
/// <param name="IsOffered">Whether free parking is offered at all.</param>
/// <param name="StartTime">When the free window starts, if one applies.</param>
/// <param name="EndTime">When the free window ends.</param>
/// <remarks>
/// Returned as an object rather than a boolean because free parking is a <i>schedule</i>. The
/// times let a client answer "is it free right now?" without another round trip.
/// </remarks>
public sealed record FreeParkingDto(
    string Code, string Description, bool IsOffered, TimeOnly? StartTime, TimeOnly? EndTime);

/// <summary>
/// The height limit, if the carpark has one.
/// </summary>
/// <param name="IsRestricted">Whether a gantry limits vehicle height.</param>
/// <param name="MaxVehicleHeightMetres">The limit, or null when unrestricted.</param>
/// <remarks>
/// <b>An object, deliberately, rather than a bare number.</b> Exposing <c>"gantryHeight": 0.0</c>
/// would invite every client to re-implement the bug this whole design exists to prevent - 0.00 in
/// the source means "no gantry", not "nothing fits". <c>{"isRestricted": false, "maxVehicle
/// HeightMetres": null}</c> cannot be misread. The API's job is to make the wrong thing hard to
/// express.
/// </remarks>
public sealed record HeightRestrictionDto(bool IsRestricted, decimal? MaxVehicleHeightMetres);

/// <summary>Filter metadata, so a front-end can build its filter UI from data.</summary>
/// <param name="CarParkTypes">Available carpark types with counts.</param>
/// <param name="ParkingSystems">Available parking systems with counts.</param>
/// <param name="ShortTermParking">Available short-term parking policies.</param>
/// <param name="FreeParking">Available free-parking policies.</param>
/// <param name="VehicleHeight">Observed height range and useful presets.</param>
/// <param name="TotalCarparks">How many active carparks exist.</param>
/// <remarks>
/// Counts let the UI grey out or annotate empty facets. Because the list is data rather than a
/// hard-coded enum, HDB adding an eighth carpark type needs no mobile release.
/// </remarks>
public sealed record LookupsResponse(
    IReadOnlyList<LookupCountDto> CarParkTypes,
    IReadOnlyList<LookupCountDto> ParkingSystems,
    IReadOnlyList<LookupCountDto> ShortTermParking,
    IReadOnlyList<LookupCountDto> FreeParking,
    VehicleHeightGuidance VehicleHeight,
    int TotalCarparks);

/// <summary>A lookup value with how many carparks currently use it.</summary>
/// <param name="Code">Stable code.</param>
/// <param name="Name">Display text.</param>
/// <param name="Count">How many active carparks have this value.</param>
public sealed record LookupCountDto(string Code, string Name, int Count);

/// <summary>Guidance for a vehicle-height picker.</summary>
/// <param name="MinimumMetres">Lowest genuine clearance in the catalogue.</param>
/// <param name="MaximumMetres">Highest genuine clearance.</param>
/// <param name="UnrestrictedCount">How many carparks have no height limit at all.</param>
/// <param name="CommonPresets">
/// The most frequently occurring clearances, so the picker offers real-world choices rather than
/// an arbitrary slider.
/// </param>
public sealed record VehicleHeightGuidance(
    decimal MinimumMetres,
    decimal MaximumMetres,
    int UnrestrictedCount,
    IReadOnlyList<decimal> CommonPresets);
