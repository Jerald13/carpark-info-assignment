using System.Text;
using System.Threading.RateLimiting;
using CarparkInfo.Application.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace CarparkInfo.Api;

/// <summary>Authentication, authorisation, rate limiting and security headers.</summary>
public static class ApiSecurity
{
    /// <summary>Policy name for endpoints restricted to administrators.</summary>
    public const string AdminPolicy = "RequireAdmin";

    /// <summary>Registers the security services.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The signing key is missing or too short.</exception>
    public static IServiceCollection AddApiSecurity(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AuthOptions();
        configuration.GetSection(AuthOptions.SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            // Development convenience only. A generated key means tokens do not survive a restart,
            // which is correct: a hard-coded fallback that silently works in production is how
            // signing keys end up in source control.
            options.SigningKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        }

        var keyBits = Encoding.UTF8.GetByteCount(options.SigningKey) * 8;

        if (keyBits < AuthOptions.MinimumSigningKeyBits)
        {
            // Fails at STARTUP rather than on the first token. A short key weakens every token the
            // system will ever issue, and discovering that in production is too late.
            throw new InvalidOperationException(
                $"Auth:SigningKey must be at least {AuthOptions.MinimumSigningKeyBits} bits "
                + $"({AuthOptions.MinimumSigningKeyBits / 8} characters); it is {keyBits} bits.");
        }

        services.AddSingleton(options);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(options.SigningKey)),

                    // The default is FIVE MINUTES, which silently extends every token's life by
                    // a third of its intended 15-minute span. Zero is the only defensible value
                    // when the whole point of a short lifetime is bounding a stolen token.
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorizationBuilder()
            // FAIL CLOSED. Every endpoint requires authentication unless it explicitly opts out
            // with [AllowAnonymous]. The inverse default - public unless annotated - is how
            // endpoints get shipped unprotected: forgetting an attribute becomes a security hole
            // rather than a compile-time-visible decision.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(Domain.Users.UserRoles.Admin));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Authenticated callers are partitioned by identity, anonymous ones by address, so
                // one noisy client cannot exhaust the budget for everyone behind the same NAT.
                var partitionKey = context.User.Identity?.IsAuthenticated == true
                    ? $"user:{context.User.FindFirst("sub")?.Value}"
                    : $"ip:{context.Connection.RemoteIpAddress}";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }

    /// <summary>
    /// Adds response headers that cost nothing and remove whole categories of attack.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The application, for chaining.</returns>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Stops a browser second-guessing Content-Type, which is how a JSON response gets
            // executed as script.
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";

            // The API returns JSON only, so nothing legitimate needs to load.
            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            await next().ConfigureAwait(false);
        });
    }
}
