using CarparkInfo.Domain.Carparks;

namespace CarparkInfo.Domain.UnitTests.Carparks;

public sealed class LocationTests
{
    // ACB - BLK 270/271 ALBERT CENTRE, a real row from the supplied dataset.
    private const double AlbertCentreX = 30314.7936;
    private const double AlbertCentreY = 31490.4942;

    [Fact]
    public void FromSvy21_retains_the_source_coordinates_and_derives_wgs84()
    {
        var location = Location.FromSvy21(AlbertCentreX, AlbertCentreY);

        location.Svy21X.Should().Be(AlbertCentreX, "the source values are the record of truth");
        location.Svy21Y.Should().Be(AlbertCentreY);
        location.Latitude.Should().BeApproximately(1.301928, 0.000001);
        location.Longitude.Should().BeApproximately(103.854118, 0.000001);
    }

    [Fact]
    public void FromSvy21_rejects_swapped_axes_when_the_result_leaves_singapore()
    {
        // The dataset's western extreme: x is far smaller than y, so transposing them puts the
        // northing well south of Singapore.
        var act = () => Location.FromSvy21(svy21X: 48_691.4308, svy21Y: 11_539.0898);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_bounds_check_cannot_catch_every_transposition()
    {
        // Honest limitation, recorded so nobody later mistakes the bounds check for a guarantee.
        // ACB's easting (30,315) and northing (31,490) are close enough that swapping them yields
        // 1.2913, 103.8647 - still inside Singapore, roughly 1.5 km from the true position.
        var correct = Location.FromSvy21(AlbertCentreX, AlbertCentreY);
        var transposed = Location.FromSvy21(AlbertCentreY, AlbertCentreX);

        transposed.Latitude.Should().NotBeApproximately(correct.Latitude, 0.001,
            "the transposed position is wrong...");
        correct.DistanceInKilometresTo(transposed.Latitude, transposed.Longitude)
            .Should().BeGreaterThan(1.0,
                "...by well over a kilometre, yet it still passes the bounds check. This is why "
                + "the converter's parameters are named northing/easting explicitly rather than "
                + "relying on a range check to catch the mistake");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-30000, -30000)]
    [InlineData(1_000_000, 1_000_000)]
    public void TryFromSvy21_reports_failure_for_positions_outside_singapore(double x, double y)
    {
        Location.TryFromSvy21(x, y, out _).Should().BeFalse();
    }

    [Fact]
    public void TryFromSvy21_succeeds_for_a_real_carpark()
    {
        Location.TryFromSvy21(AlbertCentreX, AlbertCentreY, out var location).Should().BeTrue();
        location.Latitude.Should().BeApproximately(1.301928, 0.000001);
    }

    [Fact]
    public void Distance_to_the_same_point_is_zero()
    {
        var location = Location.FromSvy21(AlbertCentreX, AlbertCentreY);

        location.DistanceInKilometresTo(location.Latitude, location.Longitude)
            .Should().BeApproximately(0.0, 0.000001);
    }

    [Fact]
    public void Distance_matches_a_known_separation()
    {
        var albertCentre = Location.FromSvy21(AlbertCentreX, AlbertCentreY);

        // One degree of latitude is ~111.19 km on a sphere of radius 6371.0088 km.
        var oneDegreeNorth = albertCentre.DistanceInKilometresTo(
            albertCentre.Latitude + 1.0, albertCentre.Longitude);

        oneDegreeNorth.Should().BeApproximately(111.19, 0.1);
    }

    [Fact]
    public void Distance_is_symmetric()
    {
        var a = Location.FromSvy21(AlbertCentreX, AlbertCentreY);
        var b = Location.FromSvy21(28185.4359, 39012.6664);  // AK19, Ang Mo Kio

        var thereAndBack = b.DistanceInKilometresTo(a.Latitude, a.Longitude);
        var straightThere = a.DistanceInKilometresTo(b.Latitude, b.Longitude);

        thereAndBack.Should().BeApproximately(straightThere, 0.000001);
    }

    [Fact]
    public void Distance_between_two_real_carparks_is_plausible()
    {
        var albertCentre = Location.FromSvy21(AlbertCentreX, AlbertCentreY);   // Rochor
        var angMoKio = Location.FromSvy21(28185.4359, 39012.6664);             // Ang Mo Kio

        var distance = albertCentre.DistanceInKilometresTo(angMoKio.Latitude, angMoKio.Longitude);

        distance.Should().BeInRange(7.0, 9.0,
            "Rochor to Ang Mo Kio is roughly 8 km as the crow flies");
    }

    [Fact]
    public void Two_locations_from_the_same_source_coordinates_are_equal()
    {
        Location.FromSvy21(AlbertCentreX, AlbertCentreY)
            .Should().Be(Location.FromSvy21(AlbertCentreX, AlbertCentreY),
                "this is a value object with no identity of its own");
    }

    [Fact]
    public void ToString_reports_the_wgs84_position()
    {
        Location.FromSvy21(AlbertCentreX, AlbertCentreY).ToString()
            .Should().Be("1.301928, 103.854118");
    }
}
