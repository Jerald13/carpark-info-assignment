using CarparkInfo.Domain.Carparks;

namespace CarparkInfo.Domain.UnitTests.Carparks;

public sealed class SourceRowHasherTests
{
    private static string HashOf(
        string carParkNo = "ACB",
        string address = "BLK 270/271 ALBERT CENTRE BASEMENT CAR PARK",
        double svy21X = 30314.7936,
        double svy21Y = 31490.4942,
        string carParkTypeCode = "BASEMENT",
        string parkingSystemCode = "ELECTRONIC",
        string shortTermParkingCode = "WHOLE_DAY",
        string freeParkingCode = "NONE",
        bool hasNightParking = true,
        int deckCount = 1,
        decimal rawGantryHeight = 1.80m,
        bool hasBasement = true) =>
        SourceRowHasher.Compute(
            carParkNo, address, svy21X, svy21Y, carParkTypeCode, parkingSystemCode,
            shortTermParkingCode, freeParkingCode, hasNightParking, deckCount,
            rawGantryHeight, hasBasement);

    [Fact]
    public void The_same_row_always_produces_the_same_hash()
    {
        HashOf().Should().Be(HashOf(),
            "the hash must be stable across processes, machines and runs, or every row looks "
            + "changed on every ingestion");
    }

    [Fact]
    public void The_hash_is_a_lowercase_sha256_digest()
    {
        HashOf().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData("carParkNo")]
    [InlineData("address")]
    [InlineData("svy21X")]
    [InlineData("svy21Y")]
    [InlineData("carParkType")]
    [InlineData("parkingSystem")]
    [InlineData("shortTermParking")]
    [InlineData("freeParking")]
    [InlineData("nightParking")]
    [InlineData("deckCount")]
    [InlineData("gantryHeight")]
    [InlineData("basement")]
    public void Changing_any_single_field_changes_the_hash(string field)
    {
        var baseline = HashOf();

        var changed = field switch
        {
            "carParkNo" => HashOf(carParkNo: "ACM"),
            "address" => HashOf(address: "BLK 98A ALJUNIED CRESCENT"),
            "svy21X" => HashOf(svy21X: 30314.7937),
            "svy21Y" => HashOf(svy21Y: 31490.4943),
            "carParkType" => HashOf(carParkTypeCode: "SURFACE"),
            "parkingSystem" => HashOf(parkingSystemCode: "COUPON"),
            "shortTermParking" => HashOf(shortTermParkingCode: "T0700_1900"),
            "freeParking" => HashOf(freeParkingCode: "SUN_PH_0700_2230"),
            "nightParking" => HashOf(hasNightParking: false),
            "deckCount" => HashOf(deckCount: 2),
            "gantryHeight" => HashOf(rawGantryHeight: 2.15m),
            "basement" => HashOf(hasBasement: false),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        changed.Should().NotBe(baseline,
            $"a change to {field} must be detected, or that field silently stops being updated");
    }

    [Fact]
    public void Field_boundaries_cannot_be_confused_by_shifting_content()
    {
        // Without a delimiter, "AB" + "C" and "A" + "BC" would hash identically, so a carpark
        // renamed in a way that shifts a character across a field boundary would look unchanged.
        var left = HashOf(carParkNo: "AB", address: "C");
        var right = HashOf(carParkNo: "A", address: "BC");

        left.Should().NotBe(right);
    }

    [Fact]
    public void The_raw_gantry_height_is_hashed_rather_than_the_normalised_limit()
    {
        // 0.00 and 9.99 both normalise to "unrestricted", but they are different source values and
        // a change between them is a real change to the feed.
        HashOf(rawGantryHeight: 0.00m).Should().NotBe(HashOf(rawGantryHeight: 9.99m),
            "the audit trail must record that the source row changed, even though the "
            + "interpretation did not");
    }

    [Fact]
    public void Decimal_precision_is_preserved_in_the_hash()
    {
        HashOf(rawGantryHeight: 2.10m).Should().NotBe(HashOf(rawGantryHeight: 2.15m));
    }

    [Fact]
    public void Coordinate_precision_matches_the_source_files_four_decimal_places()
    {
        // The feed supplies four decimal places; anything beyond that is noise and must not make
        // an otherwise-unchanged row look modified.
        HashOf(svy21X: 30314.79361).Should().Be(HashOf(svy21X: 30314.79364),
            "differences below the source's precision are not changes");
    }
}
