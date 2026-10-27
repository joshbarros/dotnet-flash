using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flash.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Flash.IntegrationTests;

/// <summary>The real host, the real Postgres, the real auth pipeline. Only the brokers are off: nothing here needs Kafka or RabbitMQ.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-test-signing-key-0123456789abcdef";
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin-Passw0rd1";

    /// <summary>Cookies are sent by hand so each test controls exactly which refresh token it presents.</summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Flash", TestDatabase.ConnectionString);
        builder.UseSetting("Messaging:Enabled", "false");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Identity:SeedAdmin:Email", AdminEmail);
        builder.UseSetting("Identity:SeedAdmin:Password", AdminPassword);
        builder.UseSetting("RateLimit:AuthPerMinute", "100000");
        builder.UseSetting("Docs:Public", "true");
        builder.UseSetting("Auth:RequireConfirmedEmail", "false");
        builder.UseSetting("Mail:DevMailbox", "true");
        builder.UseSetting("Jwt:RevocationCacheSeconds", "0");
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;

public sealed record SignedIn(string AccessToken, string RefreshCookie, Guid UserId, string Email, string[] Roles);

public static class ApiHelpers
{
    public const string Password = "Passw0rd-Test1";

    public static HttpClient ClientFor(this ApiFactory factory, string? accessToken = null)
    {
        var client = factory.NewClient();
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>The newest link mailed to an address: (user id, token), read from the dev mailbox.</summary>
    public static async Task<(Guid UserId, string Token, string Link)> LatestMailLinkAsync(this WebApplicationFactory<Program> factory, string email)
    {
        var mails = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/dev/mailbox?to={Uri.EscapeDataString(email)}");
        var link = mails.EnumerateArray().First().GetProperty("link").GetString()!;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        var userId = Guid.Parse((query["confirm"] ?? query["reset"])!);
        return (userId, query["token"]!, link);
    }

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@test.local";
    /// <summary>The body of POST /api/deals. One place, so a system with a different shape changes one line.</summary>
    public static object DealBody(string code, int piecesTotal) =>
        new { code, name = "Flash Test", aisle = "Home", brand = "Acme Goods", priceCents = 5_000, piecesTotal, maxPerShopper = piecesTotal };

    public static string NewCode() => $"S{Guid.NewGuid():N}"[..14];

    public static async Task<SignedIn> RegisterAsync(this ApiFactory factory, string? email = null)
    {
        email ??= NewEmail();
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadSessionAsync(response);
    }

    public static async Task<SignedIn> LoginAsync(this ApiFactory factory, string email, string password)
    {
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadSessionAsync(response);
    }

    public static Task<SignedIn> AdminAsync(this ApiFactory factory) => factory.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    public static async Task<SignedIn> ReadSessionAsync(HttpResponseMessage response)
    {
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>());
        var user = json.GetProperty("user");
        return new SignedIn(
            json.GetProperty("accessToken").GetString()!,
            RefreshCookieFrom(response),
            user.GetProperty("id").GetGuid(),
            user.GetProperty("email").GetString()!,
            user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }

    public static string RefreshCookieFrom(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("flash_refresh="));
        return setCookie["flash_refresh=".Length..].Split(';')[0];
    }

    public static HttpRequestMessage RefreshRequest(string cookie, bool withHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"flash_refresh={cookie}");
        if (withHeader)
            request.Headers.Add("X-Requested-With", "flash-web");
        return request;
    }

    public static async Task<string> CreateDealAsync(this ApiFactory factory, SignedIn admin, int piecesTotal = 10)
    {
        var code = NewCode();
        var response = await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/deals", DealBody(code, piecesTotal));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return code;
    }

    public static async Task<Guid> BookAsync(this ApiFactory factory, SignedIn who, string dealCode, string? reference = null, int pieces = 1)
    {
        var response = await factory.ClientFor(who.AccessToken).PostAsJsonAsync("/api/orders",
            new { dealCode, reference = reference ?? $"SHP-{Guid.NewGuid():N}"[..12], pieces });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>An access token signed with the real key but sold in 2020, so it is long expired.</summary>
    public static string ExpiredToken(Guid userId)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return new TokenService(Options.Create(new JwtOptions { Key = ApiFactory.JwtKey }), clock)
            .CreateAccessToken(userId, "old@test.local", ["Customer"]).Value;
    }
}
