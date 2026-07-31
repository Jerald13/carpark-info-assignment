using CarparkInfo.Application.Abstractions;
using CarparkInfo.Domain.Users;

namespace CarparkInfo.Application.Auth;

/// <summary>Tokens issued after a successful authentication.</summary>
/// <param name="AccessToken">Short-lived bearer token. Hold in memory only.</param>
/// <param name="RefreshToken">Long-lived, single-use token. Store securely.</param>
/// <param name="ExpiresAt">When the access token expires.</param>
/// <param name="ExpiresInSeconds">Access-token lifetime, for clients that prefer a duration.</param>
public sealed record AuthTokens(
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

/// <summary>The outcome of an authentication attempt.</summary>
/// <param name="Succeeded">Whether tokens were issued.</param>
/// <param name="Tokens">The tokens, when successful.</param>
/// <param name="FailureReason">A deliberately generic message, when not.</param>
public sealed record AuthResult(bool Succeeded, AuthTokens? Tokens, string? FailureReason)
{
    /// <summary>A successful result.</summary>
    /// <param name="tokens">The issued tokens.</param>
    /// <returns>The result.</returns>
    public static AuthResult Success(AuthTokens tokens) => new(true, tokens, null);

    /// <summary>
    /// A failed result.
    /// </summary>
    /// <param name="reason">The message returned to the caller.</param>
    /// <returns>The result.</returns>
    /// <remarks>
    /// Callers pass the same message for every failure mode. Distinguishing "no such account" from
    /// "wrong password" turns the endpoint into an account-enumeration oracle.
    /// </remarks>
    public static AuthResult Failure(string reason) => new(false, null, reason);
}

/// <summary>Authentication options.</summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth";

    /// <summary>Minimum acceptable signing key length in bits.</summary>
    public const int MinimumSigningKeyBits = 256;

    /// <summary>Token issuer.</summary>
    public string Issuer { get; set; } = "carpark-info-api";

    /// <summary>Token audience.</summary>
    public string Audience { get; set; } = "carpark-info-clients";

    /// <summary>
    /// HMAC signing key.
    /// </summary>
    /// <remarks>
    /// Supplied by configuration, user-secrets or environment - never committed. Validated at
    /// startup for length, because a short key silently weakens every token the system issues.
    /// </remarks>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Access-token lifetime. Short, because a bearer token cannot be revoked.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Refresh-token lifetime.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Registration, sign-in and token rotation.
/// </summary>
/// <remarks>
/// <para>
/// Two properties are load-bearing here and neither is obvious from the happy path.
/// </para>
/// <para>
/// <b>Enumeration resistance.</b> Registration and sign-in return the same message and take the
/// same time whether or not the account exists. That is why registration hashes a password it is
/// about to discard, and why sign-in verifies against a dummy hash when the user is unknown -
/// skipping either turns response latency into an oracle for which email addresses have accounts.
/// </para>
/// <para>
/// <b>Refresh-token reuse detection.</b> Tokens are single-use and rotated. Presenting one twice
/// means somebody holds a copy, so the entire chain is revoked. Rotation without detection simply
/// lets a stolen token work quietly until it expires and teaches you nothing.
/// </para>
/// </remarks>
public sealed class AuthenticationService
{
    /// <summary>Returned for every failed sign-in, regardless of cause.</summary>
    public const string GenericFailureMessage = "Invalid email address or password.";

    /// <summary>Returned for every registration attempt, successful or not.</summary>
    public const string GenericRegistrationMessage =
        "If that email address can be registered, the account has been created.";

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwords;
    private readonly ITokenService _tokens;
    private readonly TimeProvider _timeProvider;
    private readonly AuthOptions _options;

    /// <summary>A hash to verify against when the account does not exist, to equalise timing.</summary>
    private readonly string _dummyHash;

    /// <summary>Creates the service.</summary>
    /// <param name="users">User and token storage.</param>
    /// <param name="passwords">Password hashing.</param>
    /// <param name="tokens">Token issuance.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="options">Authentication options.</param>
    public AuthenticationService(
        IUserRepository users,
        IPasswordHasher passwords,
        ITokenService tokens,
        TimeProvider timeProvider,
        AuthOptions options)
    {
        _users = users;
        _passwords = passwords;
        _tokens = tokens;
        _timeProvider = timeProvider;
        _options = options;
        _dummyHash = passwords.Hash("timing-equalisation-placeholder");
    }

    /// <summary>Registers an account.</summary>
    /// <param name="email">The email address.</param>
    /// <param name="password">The password.</param>
    /// <param name="displayName">A human-readable name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The same generic message whether or not the address was already registered.</returns>
    public async Task<string> RegisterAsync(
        string email, string password, string displayName, CancellationToken cancellationToken)
    {
        var existing = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        // Hashed either way. Returning early when the address is taken would make registration
        // measurably faster for existing accounts, which is an enumeration oracle.
        var hash = _passwords.Hash(password);

        if (existing is null)
        {
            var user = new User(email, hash, displayName, _timeProvider.GetUtcNow());
            await _users.AddAsync(user, cancellationToken).ConfigureAwait(false);
            await _users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return GenericRegistrationMessage;
    }

    /// <summary>Signs in.</summary>
    /// <param name="email">The email address.</param>
    /// <param name="password">The password.</param>
    /// <param name="clientIp">The caller's address, recorded on the refresh token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Tokens, or a generic failure.</returns>
    public async Task<AuthResult> SignInAsync(
        string email, string password, string? clientIp, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        // Verified against a dummy hash when the user is unknown, so an unknown address costs the
        // same ~200 ms of PBKDF2 as a known one.
        var passwordIsValid = _passwords.Verify(password, user?.PasswordHash ?? _dummyHash);

        if (user is null || !passwordIsValid)
        {
            if (user is not null)
            {
                user.RecordFailedSignIn(now);
                await _users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return AuthResult.Failure(GenericFailureMessage);
        }

        if (user.IsLockedOut(now))
        {
            // Also generic: telling an attacker they have found a real account and locked it is
            // still telling them they have found a real account.
            return AuthResult.Failure(GenericFailureMessage);
        }

        user.RecordSuccessfulSignIn();
        await _users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return AuthResult.Success(
            await IssueTokensAsync(user, clientIp, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Exchanges a refresh token for a new pair.</summary>
    /// <param name="refreshToken">The token presented by the client.</param>
    /// <param name="clientIp">The caller's address.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>New tokens, or a generic failure.</returns>
    public async Task<AuthResult> RefreshAsync(
        string refreshToken, string? clientIp, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var hash = _tokens.HashRefreshToken(refreshToken);

        var stored = await _users.FindRefreshTokenAsync(hash, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        // ---------------------------------------------------------------------------------
        // REUSE DETECTION.
        //
        // The token exists but has already been used or revoked. Since tokens are single-use,
        // a second presentation means a copy is in circulation. Revoke the entire chain: the
        // legitimate user is forced to sign in again, which is a far better outcome than an
        // attacker holding a working session for the remaining lifetime.
        // ---------------------------------------------------------------------------------
        if (!stored.IsActive(now))
        {
            await _users.RevokeAllTokensForUserAsync(stored.UserId, cancellationToken)
                .ConfigureAwait(false);

            return AuthResult.Failure(
                "This refresh token has already been used. All sessions have been revoked; "
                + "please sign in again.");
        }

        var user = await _users.FindByIdAsync(stored.UserId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        var tokens = await IssueTokensAsync(user, clientIp, cancellationToken).ConfigureAwait(false);

        stored.Revoke(now);
        await _users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return AuthResult.Success(tokens);
    }

    /// <summary>Revokes a refresh token.</summary>
    /// <param name="refreshToken">The token to revoke.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when a token was revoked.</returns>
    public async Task<bool> SignOutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = _tokens.HashRefreshToken(refreshToken);
        var stored = await _users.FindRefreshTokenAsync(hash, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return false;
        }

        stored.Revoke(_timeProvider.GetUtcNow());
        await _users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<AuthTokens> IssueTokensAsync(
        User user, string? clientIp, CancellationToken cancellationToken)
    {
        var (accessToken, expiresAt) = _tokens.IssueAccessToken(user);
        var refreshToken = _tokens.GenerateRefreshToken();

        await _users.AddRefreshTokenAsync(
            new RefreshToken(
                user.Id,
                _tokens.HashRefreshToken(refreshToken),
                _timeProvider.GetUtcNow().Add(_options.RefreshTokenLifetime),
                clientIp),
            cancellationToken).ConfigureAwait(false);

        await _users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AuthTokens(
            accessToken,
            refreshToken,
            expiresAt,
            (int)_options.AccessTokenLifetime.TotalSeconds);
    }
}
