using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace CarparkInfo.Application.Carparks;

/// <summary>
/// Encodes and decodes the opaque paging cursor.
/// </summary>
/// <remarks>
/// <para>
/// Base64<b>Url</b> rather than the raw key, so the cursor reads as a token rather than an
/// invitation to hand-craft one. It is obfuscation, not security - the value it carries is a public
/// identifier and the query is bounded regardless of what a caller puts here.
/// </para>
/// <para>
/// The URL-safe alphabet is not optional: standard Base64 emits <c>+</c> and <c>/</c>, which a
/// client pasting the cursor straight back into a query string will mangle. The bug appears only on
/// the subset of keys whose encoding happens to contain those characters, which is exactly the kind
/// of intermittent paging failure that is miserable to diagnose later.
/// </para>
/// <para>
/// <b>This lives in the Application layer rather than beside the repository</b> because two callers
/// need it and they are in different layers: Infrastructure decodes a cursor to build the query,
/// and the API validates one before the request is accepted. It belongs with
/// <see cref="PageRequest"/>, which is the thing that carries it.
/// </para>
/// </remarks>
public static class PageCursor
{
    /// <summary>Encodes a key as a cursor.</summary>
    /// <param name="key">The last key on the page.</param>
    /// <returns>An opaque, URL-safe cursor.</returns>
    public static string Encode(string key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CursorPayload(key))));

    /// <summary>
    /// Attempts to decode a cursor back to a key.
    /// </summary>
    /// <param name="cursor">The cursor supplied by the caller.</param>
    /// <param name="key">The decoded key, or empty when the cursor is unreadable.</param>
    /// <returns><see langword="true"/> when the cursor is a well-formed cursor this API issued.</returns>
    /// <remarks>
    /// <para>
    /// <b>This used to be a Decode that swallowed every failure and returned an empty string</b>,
    /// which the repository then compared as <c>car_park_no &gt; ''</c> - matching everything. So a
    /// truncated, corrupted or invented cursor silently returned <i>page one</i> with a 200. A client
    /// whose cursor was mangled in transit would page forever without ever being told, and
    /// <c>?cursor=100</c> looked like it worked.
    /// </para>
    /// <para>
    /// The original reasoning was sound as far as it went - a malformed cursor must never become a
    /// 500, and it parses untrusted input, so the broad catch stays. But "not a 500" does not mean
    /// "pretend it was fine". The caller now gets a 400 naming the parameter, which is the honest
    /// answer: the request was malformed, and no page of results is the right response to it.
    /// </para>
    /// </remarks>
    public static bool TryDecode(string? cursor, out string key)
    {
        key = string.Empty;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var payload = JsonSerializer.Deserialize<CursorPayload>(json);

            if (string.IsNullOrEmpty(payload?.K))
            {
                return false;
            }

            key = payload.K;
            return true;
        }
        catch (Exception)
        {
            // Deliberately broad: this parses untrusted client input, and the failure modes across
            // Base64Url and JSON are several and version-dependent. The caller decides what to do
            // with false; nothing here throws at a user.
            return false;
        }
    }

    private sealed record CursorPayload(string K);
}
