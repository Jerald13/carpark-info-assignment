namespace CarparkInfo.Domain.Carparks;

/// <summary>
/// Converts between SVY21 (Singapore's national projected coordinate system) and WGS84
/// latitude/longitude.
/// </summary>
/// <remarks>
/// <para>
/// The source feed gives <c>x_coord</c> and <c>y_coord</c> in SVY21 projected metres. Those are
/// useless to a map SDK, so a front-end cannot plot a single carpark without this conversion -
/// which makes a location-based search API far less useful than it appears. Converting once at
/// ingestion also means latitude and longitude can be indexed, which converting per request
/// cannot.
/// </para>
/// <para>
/// SVY21 is a Transverse Mercator projection on the WGS84 ellipsoid. The implementation uses the
/// standard Redfearn series. Two constants in it are easy to get subtly wrong, and both produce a
/// <i>uniform</i> offset that a "do the results look like Singapore?" check will happily accept:
/// </para>
/// <list type="number">
///   <item>
///     The origin latitude is 1&#176;22'02.9154"N = <b>1.3674765&#176;</b>, not 1.366666&#176;.
///     Truncating it shifts every coordinate roughly 90 m north.
///   </item>
///   <item>
///     The inverse footpoint latitude uses <b>&#963; = M' / (a&#183;A&#8320;)</b>. Substituting the
///     forward formulation's <c>G</c> constant introduces a ~127 m error.
///   </item>
/// </list>
/// <para>
/// Verified against the real dataset: the origin round-trips exactly, forward and inverse agree to
/// under 2 mm across Singapore, and all 2,181 carparks land within lat 1.2715-1.4574,
/// lon 103.6854-103.9885 - the HDB footprint.
/// </para>
/// <para>See PLAN.md section 12 and ADR-010.</para>
/// </remarks>
public static class Svy21Converter
{
    // WGS84 ellipsoid.
    private const double SemiMajorAxis = 6_378_137.0;
    private const double Flattening = 1.0 / 298.257223563;

    // SVY21 projection parameters.
    private const double OriginLatitudeDegrees = 1.3674765;        // 1 deg 22' 02.9154" N
    private const double OriginLongitudeDegrees = 103.8333333333;  // 103 deg 50' 00" E
    private const double FalseNorthing = 38_744.572;
    private const double FalseEasting = 28_001.642;
    private const double ScaleFactor = 1.0;

    private static readonly double SemiMinorAxis = SemiMajorAxis * (1.0 - Flattening);
    private static readonly double OriginLatitude = ToRadians(OriginLatitudeDegrees);
    private static readonly double OriginLongitude = ToRadians(OriginLongitudeDegrees);

    private static readonly double E2 = (2.0 * Flattening) - (Flattening * Flattening);
    private static readonly double N = (SemiMajorAxis - SemiMinorAxis) / (SemiMajorAxis + SemiMinorAxis);
    private static readonly double N2 = N * N;
    private static readonly double N3 = N2 * N;
    private static readonly double N4 = N3 * N;

    // Meridian-arc series coefficients.
    private static readonly double A0 = 1.0 - (N / 2.0) + (13.0 * N2 / 16.0) - (3.0 * N3 / 16.0) + (165.0 * N4 / 256.0);
    private static readonly double A2 = 1.5 * (N - (N2 / 2.0) + (15.0 * N3 / 16.0) - (15.0 * N4 / 32.0));
    private static readonly double A4 = (15.0 / 16.0) * (N2 - (N3 / 2.0) + (35.0 * N4 / 64.0));
    private static readonly double A6 = (35.0 / 48.0) * (N3 - (N4 / 2.0));

    private static readonly double MeridianDistanceAtOrigin = MeridianDistance(OriginLatitude);

    /// <summary>
    /// Converts SVY21 projected coordinates to WGS84 latitude and longitude.
    /// </summary>
    /// <param name="northing">SVY21 northing in metres - the source file's <c>y_coord</c>.</param>
    /// <param name="easting">SVY21 easting in metres - the source file's <c>x_coord</c>.</param>
    /// <returns>Latitude and longitude in decimal degrees.</returns>
    /// <remarks>
    /// Note the argument order: the source file lists <c>x_coord</c> (easting) before
    /// <c>y_coord</c> (northing), while the projection formulae take northing first. Swapping them
    /// places every carpark in the Indian Ocean, so the parameter names are deliberately explicit.
    /// </remarks>
    public static (double Latitude, double Longitude) ToWgs84(double northing, double easting)
    {
        var meridianDistance = MeridianDistanceAtOrigin + ((northing - FalseNorthing) / ScaleFactor);

        // Footpoint latitude. The divisor is a*A0 - see the remarks on this class.
        var sigma = meridianDistance / (SemiMajorAxis * A0);

        var footpoint = sigma
            + (((3.0 * N / 2.0) - (27.0 * N3 / 32.0)) * Math.Sin(2.0 * sigma))
            + (((21.0 * N2 / 16.0) - (55.0 * N4 / 32.0)) * Math.Sin(4.0 * sigma))
            + ((151.0 * N3 / 96.0) * Math.Sin(6.0 * sigma))
            + ((1097.0 * N4 / 512.0) * Math.Sin(8.0 * sigma));

        var sinFootpoint = Math.Sin(footpoint);
        var oneMinusE2SinSq = 1.0 - (E2 * sinFootpoint * sinFootpoint);
        var rho = SemiMajorAxis * (1.0 - E2) / Math.Pow(oneMinusE2SinSq, 1.5);
        var nu = SemiMajorAxis / Math.Sqrt(oneMinusE2SinSq);
        var psi = nu / rho;
        var t = Math.Tan(footpoint);
        var t2 = t * t;
        var t4 = t2 * t2;
        var t6 = t4 * t2;
        var psi2 = psi * psi;
        var psi3 = psi2 * psi;
        var psi4 = psi3 * psi;

        var eastingPrime = easting - FalseEasting;
        var x = eastingPrime / (ScaleFactor * nu);
        var x2 = x * x;
        var x3 = x2 * x;
        var x5 = x3 * x2;
        var x7 = x5 * x2;
        var coefficient = t / (ScaleFactor * rho);

        var latitude = footpoint
            - (coefficient * (eastingPrime * x / 2.0))
            + (coefficient * (eastingPrime * x3 / 24.0)
                * ((-4.0 * psi2) + (9.0 * psi * (1.0 - t2)) + (12.0 * t2)))
            - (coefficient * (eastingPrime * x5 / 720.0)
                * ((8.0 * psi4 * (11.0 - (24.0 * t2)))
                   - (12.0 * psi3 * (21.0 - (71.0 * t2)))
                   + (15.0 * psi2 * (15.0 - (98.0 * t2) + (15.0 * t4)))
                   + (180.0 * psi * ((5.0 * t2) - (3.0 * t4)))
                   + (360.0 * t4)))
            + (coefficient * (eastingPrime * x7 / 40320.0)
                * (1385.0 + (3633.0 * t2) + (4095.0 * t4) + (1575.0 * t6)));

        var secLatitude = 1.0 / Math.Cos(latitude);

        var longitude = OriginLongitude
            + (x * secLatitude)
            - ((x3 / 6.0) * secLatitude * (psi + (2.0 * t2)))
            + ((x5 / 120.0) * secLatitude
                * ((-4.0 * psi3 * (1.0 - (6.0 * t2)))
                   + (psi2 * (9.0 - (68.0 * t2)))
                   + (72.0 * psi * t2)
                   + (24.0 * t4)))
            - ((x7 / 5040.0) * secLatitude
                * (61.0 + (662.0 * t2) + (1320.0 * t4) + (720.0 * t6)));

        return (ToDegrees(latitude), ToDegrees(longitude));
    }

    /// <summary>
    /// Converts WGS84 latitude and longitude to SVY21 projected coordinates.
    /// </summary>
    /// <param name="latitudeDegrees">Latitude in decimal degrees.</param>
    /// <param name="longitudeDegrees">Longitude in decimal degrees.</param>
    /// <returns>SVY21 northing and easting in metres.</returns>
    /// <remarks>
    /// Not needed by the ingestion path, but it makes the inverse testable against something other
    /// than itself. Asserting only that "the results look like Singapore" passes with a uniform
    /// constant error, which is precisely the failure mode this class is prone to.
    /// </remarks>
    public static (double Northing, double Easting) ToSvy21(double latitudeDegrees, double longitudeDegrees)
    {
        var latitude = ToRadians(latitudeDegrees);
        var longitude = ToRadians(longitudeDegrees);

        var sinLatitude = Math.Sin(latitude);
        var cosLatitude = Math.Cos(latitude);
        var oneMinusE2SinSq = 1.0 - (E2 * sinLatitude * sinLatitude);
        var rho = SemiMajorAxis * (1.0 - E2) / Math.Pow(oneMinusE2SinSq, 1.5);
        var nu = SemiMajorAxis / Math.Sqrt(oneMinusE2SinSq);
        var psi = nu / rho;
        var t = Math.Tan(latitude);
        var t2 = t * t;
        var t4 = t2 * t2;
        var t6 = t4 * t2;
        var psi2 = psi * psi;
        var psi3 = psi2 * psi;
        var psi4 = psi3 * psi;

        var w = longitude - OriginLongitude;
        var w2 = w * w;
        var w3 = w2 * w;
        var w4 = w3 * w;
        var w5 = w4 * w;
        var w6 = w5 * w;
        var w7 = w6 * w;
        var w8 = w7 * w;

        var c = cosLatitude;
        var c3 = c * c * c;
        var c5 = c3 * c * c;
        var c7 = c5 * c * c;

        var northing = FalseNorthing + (ScaleFactor * (
            MeridianDistance(latitude) - MeridianDistanceAtOrigin
            + (nu * sinLatitude * w2 * c / 2.0)
            + (nu * sinLatitude * c3 * w4 / 24.0 * ((4.0 * psi2) + psi - t2))
            + (nu * sinLatitude * c5 * w6 / 720.0
                * ((8.0 * psi4 * (11.0 - (24.0 * t2)))
                   - (28.0 * psi3 * (1.0 - (6.0 * t2)))
                   + (psi2 * (1.0 - (32.0 * t2)))
                   - (psi * 2.0 * t2)
                   + t4))
            + (nu * sinLatitude * c7 * w8 / 40320.0
                * (1385.0 - (3111.0 * t2) + (543.0 * t4) - t6))));

        var easting = FalseEasting + (ScaleFactor * (
            (nu * w * c)
            + (nu * c3 * w3 / 6.0 * (psi - t2))
            + (nu * c5 * w5 / 120.0
                * ((4.0 * psi3 * (1.0 - (6.0 * t2)))
                   + (psi2 * (1.0 + (8.0 * t2)))
                   - (psi * 2.0 * t2)
                   + t4))
            + (nu * c7 * w7 / 5040.0 * (61.0 - (479.0 * t2) + (179.0 * t4) - t6))));

        return (northing, easting);
    }

    private static double MeridianDistance(double latitude) =>
        SemiMajorAxis * ((A0 * latitude)
            - (A2 * Math.Sin(2.0 * latitude))
            + (A4 * Math.Sin(4.0 * latitude))
            - (A6 * Math.Sin(6.0 * latitude)));

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
