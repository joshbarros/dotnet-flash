using Flash.Identity;
using Flash.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flash.UnitTests;

/// <summary>Email confirmation, forgotten passwords and two-factor sign-in on the real Identity stack. Flows that revoke tokens use a conditional UPDATE and run against Postgres in the integration tests.</summary>
public class AccountFlowTests
{
    private const string Strong = "Passw0rd-Test1";

    private static string TokenFrom(MailMessageData mail) => Uri.UnescapeDataString(mail.Link!.Split("token=")[1]);

    private static Guid UserFrom(MailMessageData mail, string name) =>
        Guid.Parse(mail.Link!.Split($"{name}=")[1].Split('&')[0]);

    [Fact]
    public async Task Register_WhenConfirmationIsRequired_SendsALinkAndGivesNoSession()
    {
        using var sp = AuthServiceTests.Build(requireConfirmedEmail: true);
        await AuthServiceTests.SeedRolesAsync(sp);

        var session = await sp.GetRequiredService<AuthService>().RegisterAsync("ana@example.com", Strong, default);

        Assert.Null(session);
        var mail = Assert.Single(sp.GetRequiredService<CapturingMailer>().Sent);
        Assert.Equal("ana@example.com", mail.To);
        Assert.Contains("confirm=", mail.Link);
    }

    [Fact]
    public async Task SignIn_IsRefusedUntilTheAddressIsConfirmed_ThenWorks()
    {
        using var sp = AuthServiceTests.Build(requireConfirmedEmail: true);
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);

        var refused = await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", Strong, default));
        Assert.Equal(403, refused.Status);

        var mail = sp.GetRequiredService<CapturingMailer>().Sent.Single();
        await auth.ConfirmEmailAsync(UserFrom(mail, "confirm"), TokenFrom(mail), default);

        var session = await auth.LoginAsync("ana@example.com", Strong, default);
        Assert.Equal("ana@example.com", session.User.Email);
    }

    [Fact]
    public async Task ConfirmEmail_WithAGarbageToken_IsRefused()
    {
        using var sp = AuthServiceTests.Build(requireConfirmedEmail: true);
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);
        var mail = sp.GetRequiredService<CapturingMailer>().Sent.Single();

        var error = await Assert.ThrowsAsync<DomainException>(() => auth.ConfirmEmailAsync(UserFrom(mail, "confirm"), "not-a-token", default));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task ForgotPassword_MailsAKnownAddress_AndSaysNothingAboutAnUnknownOne()
    {
        using var sp = AuthServiceTests.Build();
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        await auth.RegisterAsync("ana@example.com", Strong, default);
        var mailer = sp.GetRequiredService<CapturingMailer>();
        mailer.Sent.Clear();

        await auth.ForgotPasswordAsync("nobody@example.com", default);
        Assert.Empty(mailer.Sent);

        await auth.ForgotPasswordAsync("ana@example.com", default);
        Assert.Contains("reset=", Assert.Single(mailer.Sent).Link);
    }

    [Fact]
    public async Task ResetPassword_WithAGarbageToken_IsRefused()
    {
        using var sp = AuthServiceTests.Build();
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        var session = await auth.RegisterAsync("ana@example.com", Strong, default);

        var error = await Assert.ThrowsAsync<DomainException>(() => auth.ResetPasswordAsync(session!.User.Id, "bad", "Another-Passw0rd9", default));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task Mfa_RequiresACode_AcceptsTheAuthenticatorCode_AndEachRecoveryCodeOnce()
    {
        using var sp = AuthServiceTests.Build();
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        var session = await auth.RegisterAsync("ana@example.com", Strong, default);
        var setup = await auth.SetupMfaAsync(session!.User.Id, default);
        var recovery = await auth.EnableMfaAsync(session.User.Id, Totp.Code(setup.SharedKey), default);

        var missing = await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", Strong, default));
        Assert.Equal(AuthService.MfaRequired, missing.Message);
        var wrong = await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", Strong, "000000", default));
        Assert.Equal(401, wrong.Status);

        Assert.NotNull(await auth.LoginAsync("ana@example.com", Strong, Totp.Code(setup.SharedKey), default));
        Assert.Equal(8, recovery.Length);
        Assert.NotNull(await auth.LoginAsync("ana@example.com", Strong, recovery[0], default));
        await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", Strong, recovery[0], default));
    }

    [Fact]
    public async Task EnablingMfa_WithTheWrongCode_ChangesNothing()
    {
        using var sp = AuthServiceTests.Build();
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        var session = await auth.RegisterAsync("ana@example.com", Strong, default);
        await auth.SetupMfaAsync(session!.User.Id, default);

        await Assert.ThrowsAsync<DomainException>(() => auth.EnableMfaAsync(session.User.Id, "123456", default));

        Assert.False(await auth.MfaEnabledAsync(session.User.Id));
        Assert.NotNull(await auth.LoginAsync("ana@example.com", Strong, default));
    }

    [Fact]
    public void AccessTokens_CarryTheSessionVersion()
    {
        var tokens = new TokenService(Options.Create(new JwtOptions { Key = TestJwt.Key }), TimeProvider.System);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(tokens.CreateAccessToken(Guid.NewGuid(), "a@b.c", [], 3).Value);
        Assert.Equal("3", jwt.Claims.Single(c => c.Type == TokenService.VersionClaim).Value);
    }

    [Fact]
    public async Task PasswordsLongerThanTheLimit_AreRefusedBeforeAnythingIsHashed()
    {
        using var sp = AuthServiceTests.Build();
        await AuthServiceTests.SeedRolesAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();
        var tooLong = "Aa1" + new string('x', AuthService.MaxPasswordLength);

        var register = await Assert.ThrowsAsync<DomainException>(() => auth.RegisterAsync("ana@example.com", tooLong, default));
        Assert.Equal(400, register.Status);
        var login = await Assert.ThrowsAsync<DomainException>(() => auth.LoginAsync("ana@example.com", new string('x', 1_000_000), default));
        Assert.Equal(401, login.Status);

        var exactly = "Aa1" + new string('y', AuthService.MaxPasswordLength - 3);
        Assert.NotNull(await auth.RegisterAsync("bo@example.com", exactly, default));
    }
}
