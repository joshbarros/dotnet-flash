using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Flash.IntegrationTests;

[Collection("api")]
public class AuthApiTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_SignsTheCustomerIn_WithAnAccessTokenAndAHardenedRefreshCookie()
    {
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/register", new { email = ApiHelpers.NewEmail(), password = ApiHelpers.Password });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await ApiHelpers.ReadSessionAsync(response);
        Assert.Equal(["Customer"], session.Roles);
        Assert.False(string.IsNullOrEmpty(session.AccessToken));

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("flash_refresh="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.AccessToken, cookie);
    }

    [Fact]
    public async Task Register_AnExistingEmail_Conflicts()
    {
        var email = ApiHelpers.NewEmail();
        await factory.RegisterAsync(email);
        var again = await factory.NewClient().PostAsJsonAsync("/api/auth/register", new { email, password = ApiHelpers.Password });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "Passw0rd-Test1")]
    [InlineData("a@test.local", "short")]
    [InlineData("a@test.local", "alllowercase12345")]
    [InlineData("", "")]
    public async Task Register_RefusesBadInput(string email, string password)
    {
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_WithAWrongPassword_IsUnauthorised_AndSaysNothingAboutWhetherTheAccountExists()
    {
        var known = await factory.RegisterAsync();
        var wrong = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = known.Email, password = "Wrong-Passw0rd" });
        var unknown = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email = ApiHelpers.NewEmail(), password = ApiHelpers.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync().ContinueWith(t => Title(t.Result)), Title(await unknown.Content.ReadAsStringAsync()));
    }

    private static string? Title(string body) => JsonDocument.Parse(body).RootElement.GetProperty("title").GetString();

    [Fact]
    public async Task Login_LocksTheAccountAfterFiveWrongPasswords()
    {
        var user = await factory.RegisterAsync();
        var client = factory.NewClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Wrong-Passw0rd" })).StatusCode);
        var withTheRightPassword = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = ApiHelpers.Password });

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, withTheRightPassword.StatusCode);
    }

    [Fact]
    public async Task TheSeededAdministrator_CanSignIn_AndIsAnAdmin()
    {
        var admin = await factory.AdminAsync();
        Assert.Contains("Admin", admin.Roles);
    }

    [Fact]
    public async Task Me_NeedsAToken_AndReportsTheCallersRoles()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync("/api/auth/me")).StatusCode);

        var user = await factory.RegisterAsync();
        var me = await factory.ClientFor(user.AccessToken).GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(user.Email, me.GetProperty("email").GetString());
        Assert.Equal(user.UserId, me.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnExpiredToken_IsRejected()
    {
        var user = await factory.RegisterAsync();
        var response = await factory.ClientFor(ApiHelpers.ExpiredToken(user.UserId)).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenWithAnEditedSignature_IsRejected()
    {
        var user = await factory.RegisterAsync();
        var token = user.AccessToken;
        var forged = token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1];
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(forged).GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesTheToken_AndAReplayedOldTokenRevokesTheWholeFamily()
    {
        var session = await factory.RegisterAsync();
        var client = factory.NewClient();

        var first = await client.SendAsync(ApiHelpers.RefreshRequest(session.RefreshCookie));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = ApiHelpers.RefreshCookieFrom(first);
        Assert.NotEqual(session.RefreshCookie, rotated);

        // The old token was already used: this is a replay, so it fails and takes the new token down with it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ApiHelpers.RefreshRequest(session.RefreshCookie))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ApiHelpers.RefreshRequest(rotated))).StatusCode);
    }

    [Fact]
    public async Task Refresh_WithTheSameTokenTwiceAtOnce_SucceedsOnlyOnce()
    {
        var session = await factory.RegisterAsync();
        var client = factory.NewClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => client.SendAsync(ApiHelpers.RefreshRequest(session.RefreshCookie))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithoutTheSameSectionHeader_IsRefused_SoAnotherSiteCannotTriggerIt()
    {
        var session = await factory.RegisterAsync();
        var response = await factory.NewClient().SendAsync(ApiHelpers.RefreshRequest(session.RefreshCookie, withHeader: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithNoCookieOrAGarbageCookie_IsUnauthorised()
    {
        var client = factory.NewClient();
        var noCookie = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        noCookie.Headers.Add("X-Requested-With", "flash-web");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(noCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ApiHelpers.RefreshRequest("not-a-real-token"))).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken_AndClearsTheCookie()
    {
        var session = await factory.RegisterAsync();
        var client = factory.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"flash_refresh={session.RefreshCookie}");
        request.Headers.Add("X-Requested-With", "flash-web");

        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("flash_refresh=;") || c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ApiHelpers.RefreshRequest(session.RefreshCookie))).StatusCode);
    }

    [Fact]
    public async Task AuthEndpoints_AreRateLimitedPerClient_AcrossInstances()
    {
        // Two API instances against one database share one count per client, so splitting traffic between them does not help.
        var client = $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder b) => b
            .UseSetting("RateLimit:AuthPerMinute", "3").UseSetting("ForwardedHeaders:Enabled", "true");
        await using var first = factory.WithWebHostBuilder(Configure);
        await using var second = factory.WithWebHostBuilder(Configure);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var instance = i % 2 == 0 ? first : second;
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "nobody@test.local", password = "Wrong-Passw0rd" }),
            };
            request.Headers.Add("X-Forwarded-For", client);
            statuses.Add((await instance.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(request)).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}
