using CarparkInfo.Domain.Carparks;

namespace CarparkInfo.Domain.UnitTests.Carparks;

public sealed class CarparkTests
{
    private static readonly DateTimeOffset Day1 = new(2022, 8, 24, 1, 4, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);

    private static Carpark Build(
        string carParkNo = "ACB",
        string address = "BLK 270/271 ALBERT CENTRE BASEMENT CAR PARK",
        decimal gantryHeight = 1.80m,
        bool hasNightParking = true,
        int deckCount = 1,
        string hash = "hash-v1",
        DateTimeOffset? observedAt = null) =>
        new(carParkNo,
            address,
            Location.FromSvy21(30314.7936, 31490.4942),
            carParkTypeId: 1,
            parkingSystemTypeId: 1,
            shortTermParkingTypeId: 1,
            freeParkingTypeId: 1,
            hasNightParking: hasNightParking,
            deckCount: deckCount,
            heightRestriction: HeightRestriction.FromSource(gantryHeight),
            hasBasement: true,
            sourceRowHash: hash,
            observedAt: observedAt ?? Day1);

    // ---------------------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_new_carpark_is_active_and_stamped_with_the_observation_time()
    {
        var carpark = Build();

        carpark.IsActive.Should().BeTrue();
        carpark.FirstSeenAt.Should().Be(Day1);
        carpark.LastSeenAt.Should().Be(Day1);
        carpark.LastModifiedAt.Should().Be(Day1);
    }

    [Fact]
    public void The_business_key_is_normalised_to_uppercase()
    {
        Build(carParkNo: " acb ").CarParkNo.Should().Be("ACB",
            "the key is matched on during ingestion, so casing and padding must not create a "
            + "second carpark for the same site");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_carpark_cannot_be_created_without_a_business_key(string carParkNo)
    {
        var act = () => Build(carParkNo: carParkNo);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_carpark_cannot_have_a_negative_deck_count()
    {
        var act = () => Build(deckCount: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------------------------------------------------------------------------------------
    // The height rule, reachable from the aggregate
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_open_air_carpark_accommodates_any_vehicle()
    {
        Build(gantryHeight: 0.00m).Accommodates(2.0m).Should().BeTrue(
            "477 surface carparks carry 0.00, meaning no gantry - not zero clearance");
    }

    [Theory]
    [InlineData(2.15, 2.00, true)]
    [InlineData(2.15, 2.15, true)]
    [InlineData(2.15, 2.50, false)]
    public void A_gantry_admits_vehicles_up_to_its_limit(double gantry, double vehicle, bool expected)
    {
        Build(gantryHeight: (decimal)gantry).Accommodates((decimal)vehicle).Should().Be(expected);
    }

    // ---------------------------------------------------------------------------------------
    // Delta application - the scaling property
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_unchanged_row_reports_no_change_and_leaves_the_modified_stamp_alone()
    {
        var existing = Build(hash: "hash-v1");
        var incoming = Build(hash: "hash-v1");

        var changed = existing.ApplyUpdate(incoming, Day2);

        changed.Should().BeFalse(
            "on a real daily delta most rows are unchanged; skipping the write is what keeps "
            + "ingestion cost proportional to change rather than to catalogue size");
        existing.LastModifiedAt.Should().Be(Day1, "nothing actually changed");
        existing.LastSeenAt.Should().Be(Day2, "but the row was present in the feed");
    }

    [Fact]
    public void A_changed_row_is_applied_and_restamped()
    {
        var existing = Build(address: "OLD ADDRESS", gantryHeight: 1.80m, hash: "hash-v1");
        var incoming = Build(address: "NEW ADDRESS", gantryHeight: 2.15m, hash: "hash-v2");

        var changed = existing.ApplyUpdate(incoming, Day2);

        changed.Should().BeTrue();
        existing.Address.Should().Be("NEW ADDRESS");
        existing.HeightRestriction.MaximumVehicleHeightMetres.Should().Be(2.15m);
        existing.SourceRowHash.Should().Be("hash-v2");
        existing.LastModifiedAt.Should().Be(Day2);
        existing.LastSeenAt.Should().Be(Day2);
    }

    [Fact]
    public void An_update_never_moves_the_first_seen_stamp()
    {
        var existing = Build(hash: "hash-v1");

        existing.ApplyUpdate(Build(hash: "hash-v2"), Day2);

        existing.FirstSeenAt.Should().Be(Day1,
            "first_seen_at answers when a carpark entered the catalogue and must not drift");
    }

    [Fact]
    public void An_update_records_the_run_that_wrote_it()
    {
        var existing = Build(hash: "hash-v1");

        existing.ApplyUpdate(Build(hash: "hash-v2"), Day2, jobRunId: 42);

        existing.LastJobRunId.Should().Be(42,
            "every row traces back to the file and run that produced it");
    }

    // ---------------------------------------------------------------------------------------
    // Soft delete
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Deactivation_marks_the_carpark_inactive_without_removing_it()
    {
        var carpark = Build();

        carpark.Deactivate(Day2);

        carpark.IsActive.Should().BeFalse();
        carpark.LastModifiedAt.Should().Be(Day2);
        carpark.CarParkNo.Should().Be("ACB", "the record survives; users may have favourited it");
    }

    [Fact]
    public void Deactivating_an_already_inactive_carpark_does_nothing()
    {
        var carpark = Build();
        carpark.Deactivate(Day2);

        carpark.Deactivate(Day2.AddDays(1));

        carpark.LastModifiedAt.Should().Be(Day2, "the state did not change, so nor did the stamp");
    }

    [Fact]
    public void A_carpark_that_returns_to_the_feed_is_reactivated_even_if_its_data_is_identical()
    {
        var carpark = Build(hash: "hash-v1");
        carpark.Deactivate(Day2);

        var changed = carpark.ApplyUpdate(Build(hash: "hash-v1"), Day2.AddDays(1));

        changed.Should().BeTrue(
            "the row hash is unchanged, but reactivation is itself a change that must be written");
        carpark.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ToString_identifies_the_carpark_readably()
    {
        Build().ToString().Should().Be("ACB - BLK 270/271 ALBERT CENTRE BASEMENT CAR PARK");
    }
}
