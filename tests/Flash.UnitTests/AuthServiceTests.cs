using Flash.Identity;
using Flash.Shared;
using Flash.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flash.UnitTests;

/// <summary>Runs the real Identity stack over EF's in-memory provider. Refresh rotation uses a conditional UPDATE, so it is covered against Postgres in the integration tests.</summary>
public class AuthServiceTests
{
    private const string Strong = "Passw0rd-Test1";

    internal static ServiceProvider Build(string? seedEmail = null, string? seedPassword = null, bool requireConfirmedEmail = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(TimeProvider.System);
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<IdentityModuleDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IdentityModuleDbContext>()
            .AddDefaultTokenProviders();
        services.AddSingleton(Options.Create(new JwtOptions { Key = TestJwt.Key }));
        services.AddSingleton(Options.Create(new AuthOptions { RequireConfirmedEmail = requireConfirmedEmail }));
        services.AddSingleton<CapturingMailer>();
        services.AddSingleton<IAuthMailer>(sp => sp.GetRequiredService<CapturingMailer>());
        services.AddSingleton<TokenService>();
        services.AddScoped<AuthService>();
        services.AddSingleton(Options.Create(new SeedAdminOptions { Email = seedEmail, Password = seedPassword }));
        services.AddSingleton<IdentitySeeder>();
        return services.BuildServiceProvider();
    }

    internal static async Task SeedRolesAsync(IServiceProvider sp)
    {
        await sp.GetRequiredService<IdentitySeeder>().MigrateAsync(sp, default);
    }

    [Fact]
    public async Task Register_CreatesACustomer_AndSignsThemIn()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);

        var result = await sp.GetRequiredService<AuthService>().RegisterAsync("ana@example.com", Strong, default);

        Assert.Equal("ana@example.com", result!.User.Email);
        Assert.Equal([Roles.Customer], result!.User.Roles);
        Assert.False(string.IsNullOrEmpty(result.Access.Value));
        Assert.True(result.RefreshExpiresAt > DateTimeOffset.UtcNow.AddDays(13));
    }

    [Fact]
    public async Task Register_StoresOnlyTheHashOfTheRefreshToken()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);

        var result = await sp.GetRequiredService<AuthService>().RegisterAsync("ana@example.com", Strong, default);

        var stored = await sp.GetRequiredService<IdentityModuleDbContext>().RefreshTokens.SingleAsync();
        Assert.Equal(TokenService.Hash(result!.RefreshToken), stored.TokenHash);
        Assert.NotEqual(result.RefreshToken, stored.TokenHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("two@@example.com")]
    [InlineData("Ana <ana@example.com>")]
    public async Task Register_RefusesAnInvalidEmail(string? email)
    {
        using var sp = Build();
        var ex = await Assert.ThrowsAsync<DomainException>(() => sp.GetRequiredService<AuthService>().RegisterAsync(email, Strong, default));
        Assert.Equal(400, ex.Status);
    }

    [Theory]
    [InlineData("short1A")]
    [InlineData("alllowercase1234")]
    [InlineData("ALLUPPERCASE1234")]
    [InlineData("NoDigitsInHere")]
    public async Task Register_RefusesAWeakPassword(string password)
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        var ex = await Assert.ThrowsAsync<DomainException>(() => sp.GetRequiredService<AuthService>().RegisterAsync("ana@example.com", password, default));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Register_RefusesADuplicateEmail_EvenInADifferentCase()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);

        var ex = await Assert.ThrowsAsync<DomainException>(() => auth.RegisterAsync("ANA@example.com", Strong, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Login_WithTheRightPassword_Succeeds()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);

        var result = await auth.LoginAsync("ana@example.com", Strong, default);
        Assert.Equal("ana@example.com", result!.User.Email);
    }

    [Fact]
    public async Task Login_GivesTheSameAnswerForAWrongPasswordAndAnUnknownUser()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);

        var wrongPassword = await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", "Wrong-Passw0rd", default));
        var unknownUser = await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("nobody@example.com", Strong, default));

        Assert.Equal(401, wrongPassword.Status);
        Assert.Equal(wrongPassword.Status, unknownUser.Status);
        Assert.Equal(wrongPassword.Message, unknownUser.Message);
    }

    [Fact]
    public async Task Login_LocksTheAccountAfterFiveFailures_EvenForTheRightPassword()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);

        for (var i = 0; i < 4; i++)
            Assert.Equal(401, (await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", "Wrong-Passw0rd", default))).Status);
        Assert.Equal(429, (await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", "Wrong-Passw0rd", default))).Status);
        Assert.Equal(429, (await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", Strong, default))).Status);
    }

    [Fact]
    public async Task Login_ASuccessResetsTheFailureCount()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
                await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", "Wrong-Passw0rd", default));
            await auth.LoginAsync("ana@example.com", Strong, default);
        }
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("ana@example.com", null)]
    [InlineData("", "")]
    public async Task Login_WithMissingFields_IsUnauthorised(string? email, string? password)
    {
        using var sp = Build();
        var ex = await Assert.ThrowsAsync<DomainException>(() => sp.GetRequiredService<AuthService>().LoginAsync(email, password, default));
        Assert.Equal(401, ex.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Refresh_WithoutAToken_IsUnauthorised(string? token)
    {
        using var sp = Build();
        var ex = await Assert.ThrowsAsync<DomainException>(() => sp.GetRequiredService<AuthService>().RefreshAsync(token, default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Refresh_WithAnUnknownToken_IsUnauthorised()
    {
        using var sp = Build();
        var ex = await Assert.ThrowsAsync<DomainException>(() => sp.GetRequiredService<AuthService>().RefreshAsync(TokenService.NewRefreshToken(), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Seeder_CreatesTheRoles_AndTheAdministratorOnce()
    {
        using var sp = Build("admin@example.com", "Admin-Passw0rd1");
        await SeedRolesAsync(sp);
        await SeedRolesAsync(sp);

        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Assert.True(await roles.RoleExistsAsync(Roles.Admin));
        Assert.True(await roles.RoleExistsAsync(Roles.Staff));
        Assert.True(await roles.RoleExistsAsync(Roles.Customer));

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        Assert.Equal("admin@example.com", Assert.Single(admins).Email);
    }

    [Fact]
    public async Task Seeder_WithoutAConfiguredAdministrator_CreatesNobody()
    {
        using var sp = Build();
        await SeedRolesAsync(sp);
        Assert.Empty(await sp.GetRequiredService<UserManager<ApplicationUser>>().GetUsersInRoleAsync(Roles.Admin));
    }

    [Fact]
    public async Task Seeder_RefusesAWeakAdministratorPassword()
    {
        using var sp = Build("admin@example.com", "weak");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SeedRolesAsync(sp));
    }
}

public sealed class CapturingMailer : IAuthMailer
{
    public List<MailMessageData> Sent { get; } = [];

    public Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}
