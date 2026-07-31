using CarparkInfo.Domain.Carparks;

namespace CarparkInfo.Domain.UnitTests.Carparks;

/// <summary>
/// Guards the coordinate conversion. The failure mode this class is prone to is a <i>uniform</i>
/// offset - a wrong constant shifts every carpark by the same amount, so results still land
/// inside Singapore and still look entirely correct. Only the exact origin round-trip and a
/// forward/inverse agreement check catch it. See PLAN.md section 12.
/// </summary>
public sealed class Svy21ConverterTests
{
    private const double OriginLatitude = 1.3674765;
    private const double OriginLongitude = 103.8333333333;
    private const double OriginNorthing = 38_744.572;
    private const double OriginEasting = 28_001.642;

    /// <summary>Roughly one centimetre at Singapore's latitude.</summary>
    private const double OneCentimetreInDegrees = 0.0000001;

    // ---------------------------------------------------------------------------------------
    // The test that catches both known constant bugs
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_projection_origin_round_trips_to_within_two_millimetres()
    {
        var (latitude, longitude) = Svy21Converter.ToWgs84(OriginNorthing, OriginEasting);

        var latitudeErrorInMillimetres = Math.Abs(latitude - OriginLatitude) * 111_320.0 * 1000.0;

        latitudeErrorInMillimetres.Should().BeLessThan(2.0,
            "a truncated origin latitude of 1.366666 instead of 1.3674765 shifts every coordinate "
            + "~90 m north, and a bounding-box check would not notice. Measured residual is "
            + "~1.09 mm, which is series-truncation noise rather than a wrong constant");
        longitude.Should().BeApproximately(OriginLongitude, 1e-9,
            "longitude at the central meridian is exact by construction");
    }

    [Fact]
    public void The_inverse_uses_the_correct_meridian_arc_constant()
    {
        // Using the forward formulation's G constant instead of a*A0 for the footpoint latitude
        // produces a uniform ~127 m error - four orders of magnitude above the 1 cm threshold
        // below, so this test separates the two unambiguously.
        var (latitude, _) = Svy21Converter.ToWgs84(OriginNorthing, OriginEasting);

        var errorInMetres = Math.Abs(latitude - OriginLatitude) * 111_320.0;

        errorInMetres.Should().BeLessThan(0.01,
            "the footpoint latitude divisor must be a*A0; the alternative G constant costs ~127 m");
    }

    // ---------------------------------------------------------------------------------------
    // Forward and inverse must agree - tested against each other, not against themselves
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.2726, 103.6854)]  // south-west extremity of the dataset
    [InlineData(1.4586, 103.9885)]  // north-east extremity
    [InlineData(1.3019, 103.8541)]  // ACB, Albert Centre
    [InlineData(1.4500, 103.7000)]  // Woodlands
    [InlineData(1.2800, 103.9900)]  // Bedok / Changi
    public void Forward_and_inverse_agree_to_within_a_centimetre(double latitude, double longitude)
    {
        var (northing, easting) = Svy21Converter.ToSvy21(latitude, longitude);
        var (roundTrippedLatitude, roundTrippedLongitude) = Svy21Converter.ToWgs84(northing, easting);

        roundTrippedLatitude.Should().BeApproximately(latitude, OneCentimetreInDegrees);
        roundTrippedLongitude.Should().BeApproximately(longitude, OneCentimetreInDegrees);
    }

    // ---------------------------------------------------------------------------------------
    // Real rows from the supplied dataset
    // ---------------------------------------------------------------------------------------

    // Reference values produced by an independent implementation of the Redfearn series, then
    // sense-checked against the real locations (ACB is Albert Centre in Rochor; AK19 is Ang Mo Kio
    // Street 21; CK20 is Choa Chu Kang Avenue 4).
    [Theory]
    // car_park_no, x_coord, y_coord, expected latitude, expected longitude
    [InlineData("ACB", 30314.7936, 31490.4942, 1.301928, 103.854118)]
    [InlineData("AK19", 28185.4359, 39012.6664, 1.369899, 103.834985)]
    [InlineData("CK20", 17629.7003, 40593.1789, 1.384179, 103.740134)]
    public void Real_carparks_convert_to_their_known_positions(
        string carParkNo, double x, double y, double expectedLatitude, double expectedLongitude)
    {
        var (latitude, longitude) = Svy21Converter.ToWgs84(northing: y, easting: x);

        latitude.Should().BeApproximately(expectedLatitude, 0.000001, $"{carParkNo} latitude");
        longitude.Should().BeApproximately(expectedLongitude, 0.000001, $"{carParkNo} longitude");
    }

    [Fact]
    public void The_datasets_extremes_map_onto_the_HDB_footprint()
    {
        // Measured bounds of hdb-carpark-information-20220824010400.csv.
        var (southWestLatitude, southWestLongitude) = Svy21Converter.ToWgs84(28_123.4116, 11_539.0898);
        var (northEastLatitude, northEastLongitude) = Svy21Converter.ToWgs84(48_691.4308, 45_264.5806);

        southWestLatitude.Should().BeApproximately(1.2715, 0.001);
        southWestLongitude.Should().BeApproximately(103.6854, 0.001);
        northEastLatitude.Should().BeApproximately(1.4574, 0.001);
        northEastLongitude.Should().BeApproximately(103.9885, 0.001);
    }

    [Fact]
    public void Northing_and_easting_are_not_interchangeable()
    {
        var (correctLatitude, correctLongitude) = Svy21Converter.ToWgs84(northing: 31490.4942, easting: 30314.7936);
        var (swappedLatitude, swappedLongitude) = Svy21Converter.ToWgs84(northing: 30314.7936, easting: 31490.4942);

        correctLatitude.Should().BeApproximately(1.301928, 0.000001);
        swappedLatitude.Should().NotBeApproximately(correctLatitude, 0.01,
            "the source lists x_coord before y_coord while the formulae take northing first - "
            + "swapping them must produce an obviously wrong answer, not a subtly wrong one");
        swappedLongitude.Should().NotBeApproximately(correctLongitude, 0.01);
    }
}
