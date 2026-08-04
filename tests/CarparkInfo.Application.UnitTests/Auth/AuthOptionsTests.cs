using CarparkInfo.Application.Auth;

namespace CarparkInfo.Application.UnitTests.Auth;

/// <summary>
/// Pins the security-relevant defaults.
/// </summary>
/// <remarks>
/// <para>
/// These assert the <b>code's</b> defaults, deliberately without loading configuration.
/// <c>appsettings.Development.json</c> overrides the access-token lifetime to 30 days so a reviewer
/// clicking through Swagger is not logged out mid-session - which is a reasonable convenience, and
/// exactly the kind of convenience that quietly becomes the shipped behaviour when nobody is
/// asserting what the default was.
/// </para>
/// <para>
/// So the functional tests assert the Development value they actually run under, and these assert
/// what any deployment gets when nothing is configured. Both are true at once, and neither can
/// drift into the other unnoticed.
/// </para>
/// </remarks>
public sealed class AuthOptionsTests
{
    [Fact]
    public void The_default_access_token_lifetime_is_fifteen_minutes()
    {
        new AuthOptions().AccessTokenLifetime.Should().Be(TimeSpan.FromMinutes(15),
            "a bearer token cannot be revoked before it expires, so a short lifetime is the only "
            + "thing bounding the damage when one leaks. Development may override this; the "
            + "default must not follow it");
    }

    [Fact]
    public void The_default_refresh_token_lifetime_is_seven_days()
    {
        new AuthOptions().RefreshTokenLifetime.Should().Be(TimeSpan.FromDays(7),
            "long enough that a user is not re-typing a password daily, short enough that an "
            + "abandoned session does not stay usable for a month");
    }

    [Fact]
    public void There_is_no_default_signing_key()
    {
        new AuthOptions().SigningKey.Should().BeEmpty(
            "a hard-coded fallback that silently works is how signing keys reach production. "
            + "Startup generates a random key when none is configured, so tokens simply do not "
            + "survive a restart - a visible inconvenience rather than an invisible weakness");
    }

    [Fact]
    public void The_minimum_signing_key_length_is_256_bits()
    {
        AuthOptions.MinimumSigningKeyBits.Should().Be(256,
            "HMAC-SHA256 gains no strength from a key shorter than its output, and a short key "
            + "weakens every token the system will ever issue");
    }
}
