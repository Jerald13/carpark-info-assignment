using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CarparkInfo.Domain.Carparks;

/// <summary>
/// Produces a stable fingerprint of a source row, so an unchanged row can be skipped entirely.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important scaling property in the ingestion path. Without it, every
/// nightly run rewrites every row - WAL churn, index maintenance and replication traffic all
/// proportional to the size of the <i>catalogue</i>. With it, they are proportional to the size of
/// the <i>change</i>. A real delta touching five rows writes five rows, whether the catalogue holds
/// 2,181 carparks or 2,000,000.
/// </para>
/// <para>
/// Determinism matters more than speed here. The hash must be identical across processes, machines
/// and runs, so every component is written with the invariant culture and a fixed field order, and
/// fields are separated by a delimiter that cannot occur in the data. See PLAN.md section 10.3.
/// </para>
/// </remarks>
public static class SourceRowHasher
{
    /// <summary>
    /// Unit separator (U+001F). Chosen because it cannot appear in the source CSV, so no
    /// combination of field values can produce a collision by shifting a delimiter.
    /// </summary>
    private const char FieldSeparator = '';

    /// <summary>
    /// Computes the fingerprint of a source row from its normalised field values.
    /// </summary>
    /// <param name="carParkNo">The business key.</param>
    /// <param name="address">The address as supplied.</param>
    /// <param name="svy21X">SVY21 easting.</param>
    /// <param name="svy21Y">SVY21 northing.</param>
    /// <param name="carParkTypeCode">Carpark type code.</param>
    /// <param name="parkingSystemCode">Parking system code.</param>
    /// <param name="shortTermParkingCode">Short-term parking code.</param>
    /// <param name="freeParkingCode">Free parking code.</param>
    /// <param name="hasNightParking">Whether night parking is offered.</param>
    /// <param name="deckCount">Number of decks.</param>
    /// <param name="rawGantryHeight">The <b>raw</b> gantry height from the source.</param>
    /// <param name="hasBasement">Whether the carpark has a basement.</param>
    /// <returns>A lowercase hexadecimal SHA-256 digest.</returns>
    /// <remarks>
    /// The <paramref name="rawGantryHeight"/> is hashed rather than the normalised limit. If HDB
    /// changed a carpark from 0.00 to 9.99 both would normalise to "unrestricted", but the source
    /// row genuinely changed and the audit trail must record that it did.
    /// </remarks>
    public static string Compute(
        string carParkNo,
        string address,
        double svy21X,
        double svy21Y,
        string carParkTypeCode,
        string parkingSystemCode,
        string shortTermParkingCode,
        string freeParkingCode,
        bool hasNightParking,
        int deckCount,
        decimal rawGantryHeight,
        bool hasBasement)
    {
        var builder = new StringBuilder(256);

        Append(builder, carParkNo);
        Append(builder, address);
        Append(builder, svy21X.ToString("F4", CultureInfo.InvariantCulture));
        Append(builder, svy21Y.ToString("F4", CultureInfo.InvariantCulture));
        Append(builder, carParkTypeCode);
        Append(builder, parkingSystemCode);
        Append(builder, shortTermParkingCode);
        Append(builder, freeParkingCode);
        Append(builder, hasNightParking ? "1" : "0");
        Append(builder, deckCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, rawGantryHeight.ToString("F2", CultureInfo.InvariantCulture));
        Append(builder, hasBasement ? "1" : "0");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value);
        builder.Append(FieldSeparator);
    }
}
