using CarparkInfo.Application.Abstractions;
using CarparkInfo.Application.Auth;
using CarparkInfo.Domain.Users;
using CarparkInfo.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarparkInfo.Infrastructure.IntegrationTests;

/// <summary>
/// Asserts the Development administrator seed is genuinely confined to Development.
/// </summary>
/// <remarks>
/// <para>
/// The seed exists because nothing in the solution granted <see cref="UserRoles.Admin"/>, leaving
/// three documented endpoints returning 403 to every possible caller. The fix introduces an account
/// whose password is published in the README - which is only acceptable while it is impossible for
/// that account to exist anywhere but a developer's machine.
/// </para>
/// <para>
/// So the environment guard is the security control, not a convenience, and it is asserted here in
/// both directions. A regression that let this seed run in Production would be a real vulnerability
/// with a publicly known password, and it would not announce itself.
/// </para>
/// </remarks>
public sealed class DevelopmentSeederTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Development_gets_an_administrator()
    {
        var provider = Build();

        await DevelopmentSeeder.SeedAdminAsync(provider, "Development", Ct);

        var user = await FindSeededAsync(provider);

        user.Should().NotBeNull("every admin endpoint is unreachable without it");
        user!.Role.Should().Be(UserRoles.Admin);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Anything-Else")]
    [InlineData("development")]   // case matters: the comparison is deliberately ordinal
    public async Task No_other_environment_gets_one(string environmentName)
    {
        var provider = Build();

        await DevelopmentSeeder.SeedAdminAsync(provider, environmentName, Ct);

        (await FindSeededAsync(provider)).Should().BeNull(
            $"an account with a password published in the README must not exist in {environmentName}");
    }

    [Fact]
    public async Task Configuration_can_disable_it_within_Development()
    {
        var provider = Build(("Seed:Admin:Enabled", "false"));

        await DevelopmentSeeder.SeedAdminAsync(provider, "Development", Ct);

        (await FindSeededAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task Configuration_cannot_enable_it_outside_Development()
    {
        var provider = Build(("Seed:Admin:Enabled", "true"));

        await DevelopmentSeeder.SeedAdminAsync(provider, "Production", Ct);

        (await FindSeededAsync(provider)).Should().BeNull(
            "the environment check runs first and fails closed. A configuration switch may turn "
            + "the seed off inside Development, but must never be able to turn it on outside");
    }

    [Fact]
    public async Task Running_twice_neither_duplicates_nor_resets_the_account()
    {
        var provider = Build();
        const string development = "Development";

        await DevelopmentSeeder.SeedAdminAsync(provider, development, Ct);

        var first = await FindSeededAsync(provider);
        first.Should().NotBeNull();

        await DevelopmentSeeder.SeedAdminAsync(provider, development, Ct);

        var second = await FindSeededAsync(provider);

        second!.Id.Should().Be(first!.Id,
            "restarting the API must not create a second account, nor silently reset a password "
            + "a developer has changed");
        second.PasswordHash.Should().Be(first.PasswordHash);
    }

    // -------------------------------------------------------------------------------------------

    private static async Task<User?> FindSeededAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindByEmailAsync(new AdminSeedOptions().Email, Ct);
    }

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))
                .Append(new KeyValuePair<string, string?>(
                    "ConnectionStrings:CarparkDatabase",
                    $"Data Source={Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}.db")}")))
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
        services.AddInfrastructure(configuration);
        services.AddSingleton(new AuthOptions { SigningKey = new string('k', 64) });
        services.AddAuthInfrastructure();

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CarparkInfo.Infrastructure.Persistence.CarparkDbContext>()
                .Database.EnsureCreated();
        }

        return provider;
    }
}
