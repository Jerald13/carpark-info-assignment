using System.Globalization;
using System.Text;

namespace CarparkInfo.BatchJob;

/// <summary>
/// Synthesises a large CSV from the real dataset's distribution.
/// </summary>
/// <remarks>
/// <para>
/// The supplied file has 2,181 rows, which proves nothing about the "dataset has the potential to
/// be large" challenge. This generates an arbitrarily large file so the claim can be measured
/// rather than asserted.
/// </para>
/// <para>
/// It samples the <b>real</b> distribution, which matters more than the row count. In particular
/// it preserves the ~22% share of rows carrying <c>gantry_height = 0.00</c>, so a load test
/// genuinely exercises the unrestricted-carpark path rather than a uniform synthetic one that
/// would never hit it.
/// </para>
/// </remarks>
public static class LoadTestGenerator
{
    private const string Header =
        "\"car_park_no\",\"address\",\"x_coord\",\"y_coord\",\"car_park_type\","
        + "\"type_of_parking_system\",\"short_term_parking\",\"free_parking\",\"night_parking\","
        + "\"car_park_decks\",\"gantry_height\",\"car_park_basement\"";

    /// <summary>Carpark types with their observed frequencies, out of 2,181.</summary>
    private static readonly (string Value, int Weight)[] CarParkTypes =
    [
        ("SURFACE CAR PARK", 1087),
        ("MULTI-STOREY CAR PARK", 1033),
        ("BASEMENT CAR PARK", 38),
        ("SURFACE/MULTI-STOREY CAR PARK", 12),
        ("COVERED CAR PARK", 8),
        ("MECHANISED AND SURFACE CAR PARK", 2),
        ("MECHANISED CAR PARK", 1),
    ];

    private static readonly (string Value, int Weight)[] ParkingSystems =
    [
        ("ELECTRONIC PARKING", 1998),
        ("COUPON PARKING", 183),
    ];

    private static readonly (string Value, int Weight)[] ShortTermParking =
    [
        ("WHOLE DAY", 1758),
        ("7AM-10.30PM", 261),
        ("NO", 119),
        ("7AM-7PM", 43),
    ];

    private static readonly (string Value, int Weight)[] FreeParking =
    [
        ("SUN & PH FR 7AM-10.30PM", 1594),
        ("NO", 576),
        ("SUN & PH FR 1PM-10.30PM", 11),
    ];

    /// <summary>
    /// Gantry heights with their observed frequencies.
    /// </summary>
    /// <remarks>
    /// 0.00 appears 477 times and 9.99 sixty-seven, together 25% of the file. Preserving that share
    /// is the reason this generator samples the real distribution instead of picking values at
    /// random: a synthetic file without them would never exercise the code path that 477 real
    /// carparks depend on.
    /// </remarks>
    private static readonly (string Value, int Weight)[] GantryHeights =
    [
        ("2.15", 807), ("0.00", 477), ("4.50", 437), ("2.00", 138), ("1.90", 99),
        ("9.99", 67), ("5.40", 40), ("2.10", 23), ("3.80", 17), ("1.80", 16),
        ("4.30", 6), ("2.40", 5), ("2.50", 4), ("3.50", 4), ("3.00", 4),
        ("1.70", 1), ("5.00", 1),
    ];

    private static readonly (string Value, int Weight)[] NightParking =
    [
        ("YES", 1795),
        ("NO", 386),
    ];

    private static readonly (string Value, int Weight)[] Basement =
    [
        ("N", 2143),
        ("Y", 38),
    ];

    private static readonly string[] StreetNames =
    [
        "ANG MO KIO AVENUE", "BEDOK NORTH ROAD", "BISHAN STREET", "CLEMENTI AVENUE",
        "HOUGANG AVENUE", "JURONG WEST STREET", "TAMPINES STREET", "TOA PAYOH LORONG",
        "WOODLANDS DRIVE", "YISHUN RING ROAD",
    ];

    /// <summary>
    /// Writes a synthetic CSV.
    /// </summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="rowCount">How many rows.</param>
    /// <param name="seed">Random seed, so a run is reproducible.</param>
    /// <returns>The number of bytes written.</returns>
    public static long Generate(string path, int rowCount, int seed = 20220824)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);

        var random = new Random(seed);

        using var stream = new FileStream(path, System.IO.FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 1 << 20);
        using var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 1 << 20);

        writer.WriteLine(Header);

        for (var i = 0; i < rowCount; i++)
        {
            // SVY21 coordinates inside Singapore's real extent, so every row converts successfully
            // and the load test exercises the coordinate path too.
            var x = 12_000 + (random.NextDouble() * 33_000);
            var y = 28_500 + (random.NextDouble() * 20_000);

            // One in twelve addresses carries commas, matching the real file's rate. This is what
            // makes the load test exercise the RFC 4180 parser rather than a happy-path splitter.
            var address = i % 12 == 0
                ? $"BLK {random.Next(1, 900)}-{random.Next(1, 900)},{random.Next(1, 900)} "
                  + $"{StreetNames[random.Next(StreetNames.Length)]} {random.Next(1, 12)}"
                : $"BLK {random.Next(1, 999)} {StreetNames[random.Next(StreetNames.Length)]} "
                  + $"{random.Next(1, 12)}";

            writer.Write('"');
            writer.Write($"LT{i:D8}");
            writer.Write("\",\"");
            writer.Write(address);
            writer.Write("\",\"");
            writer.Write(x.ToString("F4", CultureInfo.InvariantCulture));
            writer.Write("\",\"");
            writer.Write(y.ToString("F4", CultureInfo.InvariantCulture));
            writer.Write("\",\"");
            writer.Write(Pick(CarParkTypes, random));
            writer.Write("\",\"");
            writer.Write(Pick(ParkingSystems, random));
            writer.Write("\",\"");
            writer.Write(Pick(ShortTermParking, random));
            writer.Write("\",\"");
            writer.Write(Pick(FreeParking, random));
            writer.Write("\",\"");
            writer.Write(Pick(NightParking, random));
            writer.Write("\",\"");
            writer.Write(random.Next(0, 22).ToString(CultureInfo.InvariantCulture));
            writer.Write("\",\"");
            writer.Write(Pick(GantryHeights, random));
            writer.Write("\",\"");
            writer.Write(Pick(Basement, random));
            writer.WriteLine('"');
        }

        writer.Flush();

        return stream.Length;
    }

    private static string Pick((string Value, int Weight)[] distribution, Random random)
    {
        var total = 0;
        foreach (var (_, weight) in distribution)
        {
            total += weight;
        }

        var roll = random.Next(total);

        foreach (var (value, weight) in distribution)
        {
            roll -= weight;
            if (roll < 0)
            {
                return value;
            }
        }

        return distribution[^1].Value;
    }
}
