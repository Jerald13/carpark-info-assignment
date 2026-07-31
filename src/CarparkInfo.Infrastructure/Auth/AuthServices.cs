using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Auth;
using CarparkInfo.Domain.Users;
using CarparkInfo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CarparkInfo.Infrastructure.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing.
/// </summary>
/// <remarks>
/// <para>
/// 210,000 iterations, the OWASP 2023 recommendation for PBKDF2-HMAC-SHA256, with a 128-bit
/// per-user salt. The salt, iteration count and algorithm are encoded into the stored value, so
/// the parameters can be raised later without invalidating existing hashes.
/// </para>
/// <para>
/// Verification uses <c>CryptographicOperations.FixedTimeEquals</c>. A byte-by-byte compare
/// leaks how much of the hash matched through timing, which is enough to attack it offline.
/// </para>
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;
    private const string Prefix = "pbkdf2-sha256";

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashBytes);

        return string.Join('$',
            Prefix,
            Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <inheritdoc />
    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var parts = hash.Split('$');

        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);

            // Constant-time: a short-circuiting compare leaks the matching prefix length.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Issues JWT access tokens and random refresh tokens.</summary>
public sealed class JwtTokenService : ITokenService
{
    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="options">Authentication options.</param>
    /// <param name="timeProvider">Clock.</param>
    public JwtTokenService(AuthOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(_options.AccessTokenLifetime);

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims:
            [
                // 'sub' is the ONLY place a user id enters the application. See
                // ClaimsPrincipalExtensions.GetUserId and FavouritesController.
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <inheritdoc />
    public string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <inheritdoc />
    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}

/// <summary>EF Core implementation of user and refresh-token storage.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly CarparkDbContext _db;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The database context.</param>
    public UserRepository(CarparkDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();

        return await _db.Users
            .FirstOrDefaultAsync(u => u.Email == normalised, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> FindByIdAsync(int userId, CancellationToken cancellationToken) =>
        await _db.Users.FindAsync([userId], cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(User user, CancellationToken cancellationToken)
    {
        await _db.Users.AddAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        await _db.RefreshTokens.AddAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RefreshToken?> FindRefreshTokenAsync(
        string tokenHash, CancellationToken cancellationToken) =>
        await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> RevokeAllTokensForUserAsync(int userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var active = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in active)
        {
            token.Revoke(now);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return active.Count;
    }
}
