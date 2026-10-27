using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Flash.IntegrationTests;

[Collection("api")]
public class AccountSecurityApiTests(ApiFactory factory)
{
    private const string NewPassword = "Another-Passw0rd9";

    private static HttpRequestMessage Refresh(string cookie) => ApiHelpers.RefreshRequest(cookie);

    private async Task<HttpStatusCode> MeAsync(string accessToken) =>
        (await factory.ClientFor(accessToken).GetAsync("/api/auth/me")).StatusCode;

    [Fact]
    public async Task WhenConfirmationIsRequired_RegisterGivesNoSession_UntilTheLinkIsOpened()
    {
        await using var strict = factory.WithWebHostBuilder(b => b.UseSetting("Auth:RequireConfirmedEmail", "true"));
        var email = ApiHelpers.NewEmail();

        var registered = await strict.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = ApiHelpers.Password });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.DoesNotContain(registered.Headers, h => h.Key == "Set-Cookie");

        var refused = await strict.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = ApiHelpers.Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var (userId, token, _) = await strict.LatestMailLinkAsync(email);
        var confirmed = await strict.CreateClient().PostAsJsonAsync("/api/auth/confirm-email", new { userId, token });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        var ok = await strict.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = ApiHelpers.Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_WithAWrongToken_IsRefused()
    {
        var user = await factory.RegisterAsync();
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/confirm-email", new { userId = user.UserId, token = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_LooksTheSameForKnownAndUnknownAddresses()
    {
        var known = await factory.RegisterAsync();
        var a = await factory.NewClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = known.Email });
        var b = await factory.NewClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = ApiHelpers.NewEmail() });

        Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, b.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetPassword_SetsANewPassword_EndsEverySession_AndTheLinkWorksOnce()
    {
        var user = await factory.RegisterAsync();
        await factory.NewClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = user.Email });
        var (userId, token, _) = await factory.LatestMailLinkAsync(user.Email);

        var reset = await factory.NewClient().PostAsJsonAsync("/api/auth/reset-password", new { userId, token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = NewPassword })).StatusCode);
        // The sessions that existed before the reset are gone, access token and refresh token alike.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(user.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().SendAsync(Refresh(user.RefreshCookie))).StatusCode);

        var again = await factory.NewClient().PostAsJsonAsync("/api/auth/reset-password", new { userId, token, newPassword = "Third-Passw0rd77" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_StillEnforcesThePasswordPolicy()
    {
        var user = await factory.RegisterAsync();
        await factory.NewClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = user.Email });
        var (userId, token, _) = await factory.LatestMailLinkAsync(user.Email);

        var weak = await factory.NewClient().PostAsJsonAsync("/api/auth/reset-password", new { userId, token, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NeedsTheCurrentOne_AndEndsTheOtherSessions()
    {
        var user = await factory.RegisterAsync();
        var client = factory.ClientFor(user.AccessToken);

        var wrong = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Wrong-Passw0rd", newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var changed = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = ApiHelpers.Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var fresh = await ApiHelpers.ReadSessionAsync(changed);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(user.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().SendAsync(Refresh(user.RefreshCookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(fresh.AccessToken));
    }

    [Fact]
    public async Task LogoutEverywhere_EndsAccessTokensAndRefreshTokensOnEveryDevice()
    {
        var user = await factory.RegisterAsync(); // one device
        var other = await factory.LoginAsync(user.Email, ApiHelpers.Password); // another device
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other.AccessToken));

        var response = await factory.ClientFor(user.AccessToken).PostAsync("/api/auth/logout-everywhere", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(user.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(other.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().SendAsync(Refresh(other.RefreshCookie))).StatusCode);
    }

    [Fact]
    public async Task ATokenWithoutTheSessionVersion_IsRejected()
    {
        var user = await factory.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(ApiHelpers.ExpiredToken(user.UserId)));
    }

    [Fact]
    public async Task Mfa_FullLifecycle_SetupEnableSignInRecoveryDisable()
    {
        var user = await factory.RegisterAsync();
        var client = factory.ClientFor(user.AccessToken);

        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/auth/mfa")).GetProperty("enabled").GetBoolean());
        var setup = await (await client.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<JsonElement>();
        var key = setup.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("otpAuthUri").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = "000000" })).StatusCode);
        var enabled = await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var recovery = (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(8, recovery.Length);

        // Sign-in now needs a code.
        var missing = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("A verification code is required.", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        var ok = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password, code = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // A recovery code works once.
        var viaRecovery = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password, code = recovery[0] });
        Assert.Equal(HttpStatusCode.OK, viaRecovery.StatusCode);
        var reused = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password, code = recovery[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        // Turning it off needs the password and a code, and ends the sessions.
        var session = await ApiHelpers.ReadSessionAsync(ok);
        var denied = await factory.ClientFor(session.AccessToken).PostAsJsonAsync("/api/auth/mfa/disable", new { password = "Wrong-Passw0rd", code = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var off = await factory.ClientFor(session.AccessToken).PostAsJsonAsync("/api/auth/mfa/disable", new { password = ApiHelpers.Password, code = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password })).StatusCode);
    }

    [Fact]
    public async Task AWrongMfaCode_CountsTowardsTheLockout()
    {
        var user = await factory.RegisterAsync();
        var client = factory.ClientFor(user.AccessToken);
        var key = (await (await client.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = Totp.Code(key) });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password, code = "000000" })).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task TheDevMailbox_IsNotFound_WhenSwitchedOff()
    {
        await using var closed = factory.WithWebHostBuilder(b => b.UseSetting("Mail:DevMailbox", "false"));
        var response = await closed.CreateClient().GetAsync("/api/dev/mailbox?to=anyone@test.local");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithAnRsaKey_TokensAreRs256_AndTheKeySetIsPublic()
    {
        var pem = System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem();
        await using var rsa = factory.WithWebHostBuilder(b => b.UseSetting("Jwt:RsaPrivateKeyPem", pem));
        var client = rsa.CreateClient();

        var keys = await client.GetFromJsonAsync<JsonElement>("/.well-known/jwks.json");
        Assert.Equal("RS256", keys.GetProperty("keys")[0].GetProperty("alg").GetString());

        var registered = await client.PostAsJsonAsync("/api/auth/register", new { email = ApiHelpers.NewEmail(), password = ApiHelpers.Password });
        var token = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        Assert.Contains("\"alg\":\"RS256\"", System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(token.Split('.')[0])));
        var authed = rsa.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task WithASharedSecret_TheKeySetIsNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await factory.NewClient().GetAsync("/.well-known/jwks.json")).StatusCode);
}
