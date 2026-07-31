namespace CarparkInfo.Domain.Users;

/// <summary>Authorisation roles.</summary>
public static class UserRoles
{
    /// <summary>A normal user: may search carparks and manage their own favourites.</summary>
    public const string User = "User";

    /// <summary>An administrator: may additionally trigger ingestion and read job history.</summary>
    public const string Admin = "Admin";
}

/// <summary>
/// An account that can hold favourites.
/// </summary>
/// <remarks>
/// The password hash is stored, never the password. Failed-attempt tracking and lockout live here
/// rather than in the API so they survive a restart and apply regardless of which host authenticates.
/// </remarks>
public sealed class User
{
    /// <summary>Failed attempts tolerated before the account locks.</summary>
    public const int MaximumFailedAttempts = 5;

    /// <summary>How long an account stays locked.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly List<Favourite> _favourites = [];

    private User() { }   // EF Core materialisation

    /// <summary>Creates a user account.</summary>
    /// <param name="email">The login address; stored lowercase and unique.</param>
    /// <param name="passwordHash">A PBKDF2 hash of the password. Never the password itself.</param>
    /// <param name="displayName">A human-readable name.</param>
    /// <param name="createdAt">When the account was created.</param>
    /// <param name="role">The authorisation role.</param>
    public User(string email, string passwordHash, string displayName, DateTimeOffset createdAt,
        string role = UserRoles.User)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Email : displayName.Trim();
        CreatedAt = createdAt;
        Role = role;
    }

    /// <summary>Surrogate key. Never accepted as API input - see the note on favourites.</summary>
    public int Id { get; private set; }

    /// <summary>The login address, lowercase and unique.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>PBKDF2-HMAC-SHA256 hash of the password.</summary>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>A human-readable name.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Authorisation role. See <see cref="UserRoles"/>.</summary>
    public string Role { get; private set; } = UserRoles.User;

    /// <summary>Consecutive failed sign-in attempts.</summary>
    public int FailedLoginCount { get; private set; }

    /// <summary>When the current lockout expires, if any.</summary>
    public DateTimeOffset? LockoutEndsAt { get; private set; }

    /// <summary>When the account was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The carparks this user has favourited.</summary>
    public IReadOnlyCollection<Favourite> Favourites => _favourites;

    /// <summary>Whether sign-in is currently blocked.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> while the account is locked out.</returns>
    public bool IsLockedOut(DateTimeOffset now) => LockoutEndsAt is { } until && until > now;

    /// <summary>Records a failed sign-in, locking the account once the threshold is reached.</summary>
    /// <param name="now">The current time.</param>
    public void RecordFailedSignIn(DateTimeOffset now)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= MaximumFailedAttempts)
        {
            LockoutEndsAt = now.Add(LockoutDuration);
            FailedLoginCount = 0;
        }
    }

    /// <summary>Clears failed-attempt state after a successful sign-in.</summary>
    public void RecordSuccessfulSignIn()
    {
        FailedLoginCount = 0;
        LockoutEndsAt = null;
    }
}

/// <summary>
/// A user's favourite carpark. The junction of the many-to-many relationship.
/// </summary>
/// <remarks>
/// The primary key is the pair <c>(UserId, CarparkId)</c>, which makes a duplicate favourite
/// structurally impossible. The idempotent <c>PUT</c> is therefore guaranteed by the schema rather
/// than by remembering to check first - even a direct SQL insert cannot create one.
/// </remarks>
public sealed class Favourite
{
    private Favourite() { }   // EF Core materialisation

    /// <summary>Creates a favourite.</summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="carparkId">The favourited carpark.</param>
    /// <param name="createdAt">When it was favourited.</param>
    public Favourite(int userId, int carparkId, DateTimeOffset createdAt)
    {
        UserId = userId;
        CarparkId = carparkId;
        CreatedAt = createdAt;
    }

    /// <summary>The owning user. Half of the composite primary key.</summary>
    public int UserId { get; private set; }

    /// <summary>The favourited carpark. Half of the composite primary key.</summary>
    public int CarparkId { get; private set; }

    /// <summary>When the carpark was favourited.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Navigation to the owning user.</summary>
    public User? User { get; private set; }

    /// <summary>Navigation to the favourited carpark.</summary>
    public Carparks.Carpark? Carpark { get; private set; }
}

/// <summary>
/// A refresh token, stored as a hash.
/// </summary>
/// <remarks>
/// Tokens are single-use and rotated. <see cref="ReplacedById"/> forms the rotation chain, which is
/// what makes reuse detection possible: a single-use token presented twice is proof that someone
/// holds a copy, so the entire chain is revoked. Rotation without reuse detection just lets a
/// stolen token work quietly until it expires.
/// </remarks>
public sealed class RefreshToken
{
    private RefreshToken() { }   // EF Core materialisation

    /// <summary>Creates a refresh token record.</summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="tokenHash">SHA-256 of the token. The raw token is never stored.</param>
    /// <param name="expiresAt">When the token expires.</param>
    /// <param name="createdByIp">The client address that requested it.</param>
    public RefreshToken(int userId, string tokenHash, DateTimeOffset expiresAt, string? createdByIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedByIp = createdByIp;
    }

    /// <summary>Surrogate key.</summary>
    public int Id { get; private set; }

    /// <summary>The owning user.</summary>
    public int UserId { get; private set; }

    /// <summary>SHA-256 of the token. A database leak yields no usable credential.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>When the token expires.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the token was revoked, if it has been.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The token that replaced this one, forming the rotation chain.</summary>
    public int? ReplacedById { get; private set; }

    /// <summary>The client address that requested the token.</summary>
    public string? CreatedByIp { get; private set; }

    /// <summary>Navigation to the owning user.</summary>
    public User? User { get; private set; }

    /// <summary>Whether the token can still be exchanged.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when the token is neither revoked nor expired.</returns>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    /// <summary>Revokes the token.</summary>
    /// <param name="now">When the revocation happened.</param>
    /// <param name="replacedById">The token that superseded this one, when rotating.</param>
    public void Revoke(DateTimeOffset now, int? replacedById = null)
    {
        RevokedAt ??= now;
        ReplacedById ??= replacedById;
    }
}
