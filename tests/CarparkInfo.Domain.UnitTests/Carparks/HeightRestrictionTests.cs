using CarparkInfo.Domain.Carparks;

namespace CarparkInfo.Domain.UnitTests.Carparks;

/// <summary>
/// Guards the single most consequential rule in the system: HDB encodes "no height limit" as a
/// number, and reading it literally hides 477 of 2,181 carparks. See PLAN.md section 2, ADR-006.
/// </summary>
public sealed class HeightRestrictionTests
{
    // ---------------------------------------------------------------------------------------
    // The sentinels
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void FromSource_treats_zero_as_no_gantry_rather_than_zero_clearance()
    {
        var restriction = HeightRestriction.FromSource(0.00m);

        restriction.IsRestricted.Should().BeFalse(
            "0.00 occurs on 477 rows and every one is a SURFACE CAR PARK - it means the carpark "
            + "has no gantry, not that nothing fits");
        restriction.MaximumVehicleHeightMetres.Should().BeNull();
    }

    [Fact]
    public void FromSource_treats_the_999_sentinel_as_unlimited()
    {
        var restriction = HeightRestriction.FromSource(9.99m);

        restriction.IsRestricted.Should().BeFalse(
            "9.99 occurs on 67 rows, all surface carparks - it is the source's 'unlimited' sentinel, "
            + "not a 9.99 m measurement");
        restriction.MaximumVehicleHeightMetres.Should().BeNull();
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(9.99)]
    public void FromSource_retains_the_raw_value_of_a_sentinel_for_audit(double raw)
    {
        var restriction = HeightRestriction.FromSource((decimal)raw);

        restriction.RawSourceValue.Should().Be((decimal)raw,
            "the source value must survive normalisation so the interpretation can be revisited "
            + "with a migration rather than a re-ingest");
    }

    // ---------------------------------------------------------------------------------------
    // Genuine measurements
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.70)]  // lowest observed in the dataset
    [InlineData(1.80)]
    [InlineData(2.00)]
    [InlineData(2.15)]  // most common: 807 rows
    [InlineData(4.50)]  // second most common: 437 rows
    [InlineData(5.40)]  // highest genuine measurement observed
    public void FromSource_keeps_real_measurements_intact(double raw)
    {
        var restriction = HeightRestriction.FromSource((decimal)raw);

        restriction.IsRestricted.Should().BeTrue();
        restriction.MaximumVehicleHeightMetres.Should().Be((decimal)raw);
        restriction.RawSourceValue.Should().Be((decimal)raw);
    }

    [Fact]
    public void FromSource_does_not_lose_decimal_precision()
    {
        var restriction = HeightRestriction.FromSource(2.15m);

        restriction.MaximumVehicleHeightMetres.Should().Be(2.15m,
            "heights are compared for equality; a REAL round-trip would make 2.15 into 2.1499999");
    }

    // ---------------------------------------------------------------------------------------
    // Accommodates - this method IS the user story
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(4.5)]
    [InlineData(100.0)]
    public void Unrestricted_carparks_accommodate_any_vehicle(double vehicleHeight)
    {
        HeightRestriction.FromSource(0.00m)
            .Accommodates((decimal)vehicleHeight).Should().BeTrue(
                "an open-air carpark with no gantry admits anything that can drive to it");
    }

    [Theory]
    [InlineData(2.15, 2.00, true)]   // comfortably under
    [InlineData(2.15, 2.15, true)]   // exactly at the limit - inclusive
    [InlineData(2.15, 2.16, false)]  // one centimetre over
    [InlineData(1.80, 2.00, false)]
    [InlineData(4.50, 4.50, true)]
    public void Restricted_carparks_admit_vehicles_up_to_and_including_the_limit(
        double gantry, double vehicle, bool expected)
    {
        HeightRestriction.FromSource((decimal)gantry)
            .Accommodates((decimal)vehicle).Should().Be(expected);
    }

    // ---------------------------------------------------------------------------------------
    // The regression this whole type exists to prevent
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_two_metre_vehicle_fits_an_unrestricted_carpark_that_a_naive_filter_would_hide()
    {
        var unrestricted = HeightRestriction.FromSource(0.00m);

        // The naive implementation - `gantry_height >= vehicleHeight` against the raw value.
        var naiveResult = unrestricted.RawSourceValue >= 2.0m;
        naiveResult.Should().BeFalse("this is exactly the bug: 0.00 >= 2.0 is false");

        // The correct implementation.
        unrestricted.Accommodates(2.0m).Should().BeTrue(
            "477 carparks - 23% of the dataset - hang on this distinction");
    }

    // ---------------------------------------------------------------------------------------
    // Rejecting impossible values
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.5)]    // below the plausible floor but not the no-gantry sentinel
    [InlineData(25.0)]   // taller than any road vehicle
    public void FromSource_rejects_values_that_are_neither_sentinel_nor_plausible(double raw)
    {
        var act = () => HeightRestriction.FromSource((decimal)raw);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(25.0)]
    public void TryFromSource_reports_failure_without_throwing(double raw)
    {
        HeightRestriction.TryFromSource((decimal)raw, out _).Should().BeFalse(
            "ingestion collects every defect in a file before aborting, so it cannot afford to "
            + "throw on the first bad row");
    }

    [Fact]
    public void TryFromSource_succeeds_for_valid_input()
    {
        HeightRestriction.TryFromSource(2.15m, out var restriction).Should().BeTrue();
        restriction.MaximumVehicleHeightMetres.Should().Be(2.15m);
    }

    // ---------------------------------------------------------------------------------------
    // Value semantics
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Two_restrictions_from_the_same_source_value_are_equal()
    {
        HeightRestriction.FromSource(2.15m).Should().Be(HeightRestriction.FromSource(2.15m),
            "this is a value object; it has no identity of its own");
    }

    [Fact]
    public void The_two_unrestricted_sentinels_are_distinguishable_by_raw_value()
    {
        var noGantry = HeightRestriction.FromSource(0.00m);
        var unlimited = HeightRestriction.FromSource(9.99m);

        noGantry.IsRestricted.Should().Be(unlimited.IsRestricted, "both admit any vehicle");
        noGantry.Should().NotBe(unlimited, "but the source distinguished them, and so do we");
    }

    [Fact]
    public void Unrestricted_factory_matches_a_zero_source_row()
    {
        HeightRestriction.Unrestricted.Should().Be(HeightRestriction.FromSource(0.00m));
    }

    [Theory]
    [InlineData(0.00, "unrestricted")]
    [InlineData(9.99, "unrestricted")]
    [InlineData(2.15, "2.15 m")]
    public void ToString_describes_the_restriction_readably(double raw, string expected)
    {
        HeightRestriction.FromSource((decimal)raw).ToString().Should().Be(expected);
    }
}
