namespace CarparkInfo.Domain.Carparks;

/// <summary>
/// Where a carpark is, in both the coordinate system the source provides and the one a map needs.
/// </summary>
/// <remarks>
/// Both representations are stored deliberately. SVY21 is retained because it is the source of
/// truth and must survive round-tripping back to the provider; WGS84 is derived once at ingestion
/// because deriving it per request costs CPU on every row and, more importantly, cannot be
/// indexed - which would make radius search a full scan. See ADR-010.
/// </remarks>
public readonly record struct Location
{
    /// <summary>Singapore's approximate WGS84 bounds, used to reject coordinates the feed should never contain.</summary>
    private const double MinimumLatitude = 1.15;
    private const double MaximumLatitude = 1.50;
    private const double MinimumLongitude = 103.55;
    private const double MaximumLongitude = 104.15;

    private const double EarthRadiusKilometres = 6371.0088;

    private Location(double svy21X, double svy21Y, double latitude, double longitude)
    {
        Svy21X = svy21X;
        Svy21Y = svy21Y;
        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>SVY21 easting in metres, exactly as supplied in the source <c>x_coord</c> column.</summary>
    public double Svy21X { get; }

    /// <summary>SVY21 northing in metres, exactly as supplied in the source <c>y_coord</c> column.</summary>
    public double Svy21Y { get; }

    /// <summary>WGS84 latitude in decimal degrees, derived at ingestion.</summary>
    public double Latitude { get; }

    /// <summary>WGS84 longitude in decimal degrees, derived at ingestion.</summary>
    public double Longitude { get; }

    /// <summary>
    /// Builds a location from the source file's projected coordinates, deriving latitude and
    /// longitude.
    /// </summary>
    /// <param name="svy21X">The source <c>x_coord</c> value (easting, metres).</param>
    /// <param name="svy21Y">The source <c>y_coord</c> value (northing, metres).</param>
    /// <returns>A location carrying both representations.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The converted position falls outside Singapore, which indicates swapped axes or a unit
    /// error rather than a genuinely remote carpark.
    /// </exception>
    public static Location FromSvy21(double svy21X, double svy21Y)
    {
        if (!TryFromSvy21(svy21X, svy21Y, out var location))
        {
            throw new ArgumentOutOfRangeException(
                nameof(svy21X),
                new { svy21X, svy21Y },
                "SVY21 coordinates convert to a position outside Singapore. This usually means the "
                + "easting and northing were swapped, or the values are in the wrong unit.");
        }

        return location;
    }

    /// <summary>
    /// Builds a location without throwing, for the ingestion validator which collects every defect
    /// in a file before deciding whether to abort.
    /// </summary>
    /// <param name="svy21X">The source <c>x_coord</c> value (easting, metres).</param>
    /// <param name="svy21Y">The source <c>y_coord</c> value (northing, metres).</param>
    /// <param name="location">The resulting location when the coordinates are plausible.</param>
    /// <returns><see langword="true"/> when the position falls inside Singapore.</returns>
    public static bool TryFromSvy21(double svy21X, double svy21Y, out Location location)
    {
        location = default;

        var (latitude, longitude) = Svy21Converter.ToWgs84(northing: svy21Y, easting: svy21X);

        if (double.IsNaN(latitude) || double.IsNaN(longitude)
            || latitude is < MinimumLatitude or > MaximumLatitude
            || longitude is < MinimumLongitude or > MaximumLongitude)
        {
            return false;
        }

        location = new Location(svy21X, svy21Y, latitude, longitude);
        return true;
    }

    /// <summary>
    /// Great-circle distance from this location to another, in kilometres.
    /// </summary>
    /// <param name="latitude">The other point's latitude in decimal degrees.</param>
    /// <param name="longitude">The other point's longitude in decimal degrees.</param>
    /// <returns>The distance in kilometres.</returns>
    /// <remarks>
    /// Radius search prefilters with an indexable bounding box and then applies this haversine pass
    /// to the survivors. A bounding box alone returns a square, whose corners are 41% further away
    /// than the radius the user asked for.
    /// </remarks>
    public double DistanceInKilometresTo(double latitude, double longitude) =>
        DistanceInKilometresBetween(Latitude, Longitude, latitude, longitude);

    /// <summary>
    /// Great-circle distance between two points, without needing a <see cref="Location"/>.
    /// </summary>
    /// <param name="latitude">First point's latitude in decimal degrees.</param>
    /// <param name="longitude">First point's longitude in decimal degrees.</param>
    /// <param name="otherLatitude">Second point's latitude in decimal degrees.</param>
    /// <param name="otherLongitude">Second point's longitude in decimal degrees.</param>
    /// <returns>The distance in kilometres.</returns>
    /// <remarks>
    /// Exists so a radius search can COUNT its matches without materialising a full
    /// <see cref="Location"/> - and, more importantly, without a second implementation of the
    /// formula. <see cref="DistanceInKilometresTo"/> delegates here, so the count and the results
    /// can never be computed two different ways and disagree.
    /// </remarks>
    public static double DistanceInKilometresBetween(
        double latitude, double longitude, double otherLatitude, double otherLongitude)
    {
        var deltaLatitude = ToRadians(otherLatitude - latitude);
        var deltaLongitude = ToRadians(otherLongitude - longitude);

        var a = (Math.Sin(deltaLatitude / 2.0) * Math.Sin(deltaLatitude / 2.0))
            + (Math.Cos(ToRadians(latitude)) * Math.Cos(ToRadians(otherLatitude))
               * Math.Sin(deltaLongitude / 2.0) * Math.Sin(deltaLongitude / 2.0));

        return EarthRadiusKilometres * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Returns the WGS84 position in a readable form.</summary>
    public override string ToString() => $"{Latitude:F6}, {Longitude:F6}";
}
