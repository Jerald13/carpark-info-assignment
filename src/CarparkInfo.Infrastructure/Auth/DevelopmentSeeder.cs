using CarparkInfo.Application.Abstractions;
using CarparkInfo.Domain.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Infrastructure.Auth;

/// <summary>
/// Creates a known administrator account, in Development only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="UserRoles.Admin"/> is required by three endpoints -
/// ingestion history, the defect report and the manual trigger - but nothing granted it. Registration
/// takes the constructor default of <see cref="UserRoles.User"/>, and there was no promotion
/// endpoint, no configuration switch and no seed. Every administrator endpoint was therefore
/// documented in the README and unreachable in practice: a reviewer registers, signs in, calls one,
/// and gets 403 with no route forward. Three dead endpoints read as untested, and the R6 defect
/// report could not be demonstrated over HTTP at all.
/// </para>
/// <para>
/// <b>Why it is guarded by environment.</b> An account with a published password that reached
/// Production would be a genuine vulnerability, not a convenience. The guard fails closed: any
/// environment name that is not exactly "Development" seeds nothing.
/// <see cref="AdminSeedOptions.Enabled"/> can switch it off within Development too, but it can
/// never switch it <i>on</i> outside Development.
/// </para>
/// <para>
/// The environment arrives as a <see cref="string"/> rather than <c>IHostEnvironment</c> so that
/// Infrastructure keeps no dependency on the hosting stack - the same reason auth registration was
/// split out of <c>AddInfrastructure</c>.
/// </para>
/// <para>
/// <b>Why a seed rather than a promotion endpoint.</b> An endpoint that grants administrator rights
/// is a privilege-escalation surface that has to be defended forever. A Development-only seed has no
/// production surface at all. In a real deployment the first administrator is created by an operator
/// out of band, which is exactly what this stands in for.
/// </para>
/// </remarks>
public static class DevelopmentSeeder
{
    /// <summary>The only environment this seed will run in.</summary>
    public const string DevelopmentEnvironment = "Development";

    /// <summary>Seeds the administrator account when the environment is Development.</summary>
    /// <param name="services">The application's service provider.</param>
    /// <param name="environmentName">The host environment name. Only "Development" seeds.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// Idempotent: an existing account with the configured address is left exactly as it is, so
    /// restarting never resets a password a developer has changed, and never creates a duplicate.
    /// </remarks>
    public static async Task SeedAdminAsync(
        IServiceProvider services,
        string environmentName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!string.Equals(environmentName, DevelopmentEnvironment, StringComparison.Ordinal))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var options = provider.GetRequiredService<IConfiguration>()
            .GetSection(AdminSeedOptions.SectionName)
            .Get<AdminSeedOptions>() ?? new AdminSeedOptions();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevelopmentSeeder));

        if (!options.Enabled)
        {
            SeedLog.Disabled(logger);
            return;
        }

        var users = provider.GetRequiredService<IUserRepository>();

        if (await users.FindByEmailAsync(options.Email, cancellationToken).ConfigureAwait(false) is not null)
        {
            SeedLog.AlreadyPresent(logger, options.Email);
            return;
        }

        var hasher = provider.GetRequiredService<IPasswordHasher>();
        var clock = provider.GetRequiredService<TimeProvider>();

        var admin = new User(
            options.Email,
            hasher.Hash(options.Password),
            options.DisplayName,
            clock.GetUtcNow(),
            UserRoles.Admin);

        await users.AddAsync(admin, cancellationToken).ConfigureAwait(false);
        await users.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        SeedLog.Created(logger, options.Email);
    }
}

/// <summary>Configures the Development administrator seed.</summary>
public sealed class AdminSeedOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Seed:Admin";

    /// <summary>
    /// Whether to seed at all. Ignored outside Development, which never seeds.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The administrator's email address, which is also the sign-in identity.</summary>
    public string Email { get; set; } = "admin@carpark.local";

    /// <summary>
    /// The administrator's password.
    /// </summary>
    /// <remarks>
    /// Published in the README on purpose: a credential a reviewer cannot find is the same as an
    /// endpoint they cannot call. It is safe to publish precisely because this account cannot exist
    /// outside Development.
    /// </remarks>
    public string Password { get; set; } = "Admin!ChangeMe123";

    /// <summary>The administrator's display name.</summary>
    public string DisplayName { get; set; } = "Development Administrator";
}

/// <summary>Source-generated log messages for seeding.</summary>
internal static partial class SeedLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "Development administrator '{Email}' created. This account is seeded in the "
                + "Development environment only and its password is published in the README.")]
    public static partial void Created(ILogger logger, string email);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Debug,
        Message = "Development administrator '{Email}' already exists; leaving it untouched.")]
    public static partial void AlreadyPresent(ILogger logger, string email);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information,
        Message = "Development administrator seeding is disabled by configuration.")]
    public static partial void Disabled(ILogger logger);
}
