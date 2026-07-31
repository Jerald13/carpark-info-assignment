using CarparkInfo.Domain.Users;

namespace CarparkInfo.Application.Abstractions;

/// <summary>Hashes and verifies passwords.</summary>
/// <remarks>
/// A port rather than a direct call so the algorithm can be replaced - Argon2id being the obvious
/// candidate - without touching a single use case.
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Hashes a password.</summary>
    /// <param name="password">The plain-text password. Never stored.</param>
    /// <returns>An encoded hash including its salt and parameters.</returns>
    string Hash(string password);

    /// <summary>Verifies a password against a stored hash.</summary>
    /// <param name="password">The candidate password.</param>
    /// <param name="hash">The stored hash.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    bool Verify(string password, string hash);
}

/// <summary>Issues and validates tokens.</summary>
public interface ITokenService
{
    /// <summary>Issues a short-lived access token for a user.</summary>
    /// <param name="user">The authenticated user.</param>
    /// <returns>The signed token and when it expires.</returns>
    (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(User user);

    /// <summary>Generates a cryptographically random refresh token.</summary>
    /// <returns>The raw token, which is returned to the client exactly once.</returns>
    string GenerateRefreshToken();

    /// <summary>Hashes a refresh token for storage.</summary>
    /// <param name="refreshToken">The raw token.</param>
    /// <returns>A SHA-256 digest. The raw token is never stored.</returns>
    string HashRefreshToken(string refreshToken);
}

/// <summary>Reads and writes user accounts and their refresh tokens.</summary>
public interface IUserRepository
{
    /// <summary>Finds a user by email.</summary>
    /// <param name="email">The email address, case-insensitive.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Finds a user by id.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByIdAsync(int userId, CancellationToken cancellationToken);

    /// <summary>Adds a user.</summary>
    /// <param name="user">The new user.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task AddAsync(User user, CancellationToken cancellationToken);

    /// <summary>Persists pending changes.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Stores a refresh token.</summary>
    /// <param name="token">The token record, holding a hash rather than the token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task AddRefreshTokenAsync(RefreshToken token, CancellationToken cancellationToken);

    /// <summary>Finds a refresh token by its hash.</summary>
    /// <param name="tokenHash">SHA-256 of the presented token.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The token record, or null.</returns>
    Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every token in a rotation chain.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many tokens were revoked.</returns>
    /// <remarks>
    /// Called when a single-use token is presented twice, which means somebody holds a copy.
    /// Revoking the whole chain turns a stolen credential into a detected incident rather than a
    /// silent seven-day session.
    /// </remarks>
    Task<int> RevokeAllTokensForUserAsync(int userId, CancellationToken cancellationToken);
}
