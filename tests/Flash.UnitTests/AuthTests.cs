using System.Security.Claims;
using Flash.Identity;
using Flash.Shared.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Flash.UnitTests;

internal static class TestJwt
{
    public const string Key = "unit-test-signing-key-0123456789-abcdef";

    public static TokenService Service(string key = Key, TimeProvider? clock = null, string audience = "flash-web") =>
        new(Options.Create(new JwtOptions { Key = key, Audience = audience }), clock ?? TimeProvider.System);
}

public class TokenServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<TokenValidationResult> ValidateAsync(TokenService verifier, string token) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, verifier.ValidationParameters());

    [Fact]
    public async Task AccessToken_CarriesTheUserEmailAndEveryRole()
    {
        var service = TestJwt.Service();
        var token = service.CreateAccessToken(UserId, "ana@example.com", [Roles.Staff, Roles.Customer]);

        var result = await ValidateAsync(service, token.Value);

        Assert.True(result.IsValid);
        var claims = result.ClaimsIdentity;
        Assert.Equal(UserId.ToString(), claims.FindFirst("sub")!.Value);
        Assert.Equal("ana@example.com", claims.FindFirst("email")!.Value);
        Assert.Equal([Roles.Customer, Roles.Staff], claims.FindAll("role").Select(c => c.Value).Order().ToArray());
        Assert.True(new ClaimsPrincipal(claims).IsInRole(Roles.Staff));
    }

    [Fact]
    public void AccessToken_ExpiresAfterTheConfiguredMinutes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var token = TestJwt.Service(clock: clock).CreateAccessToken(UserId, "a@b.co", []);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), token.ExpiresAt);
    }

    [Fact]
    public async Task AccessToken_SoldLongAgo_IsRejectedAsExpired()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var token = TestJwt.Service(clock: clock).CreateAccessToken(UserId, "a@b.co", []);
        Assert.False((await ValidateAsync(TestJwt.Service(), token.Value)).IsValid);
    }

    [Fact]
    public async Task AccessToken_SignedWithAnotherKey_IsRejected()
    {
        var forged = TestJwt.Service("another-signing-key-0123456789-abcdefgh").CreateAccessToken(UserId, "a@b.co", [Roles.Admin]);
        Assert.False((await ValidateAsync(TestJwt.Service(), forged.Value)).IsValid);
    }

    [Fact]
    public async Task AccessToken_ForAnotherAudience_IsRejected()
    {
        var token = TestJwt.Service(audience: "someone-else").CreateAccessToken(UserId, "a@b.co", []);
        Assert.False((await ValidateAsync(TestJwt.Service(), token.Value)).IsValid);
    }

    [Fact]
    public async Task AccessToken_WithAnEditedPayload_IsRejected()
    {
        var service = TestJwt.Service();
        var parts = service.CreateAccessToken(UserId, "a@b.co", [Roles.Customer]).Value.Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace("Customer", "Admin  ");
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";
        Assert.False((await ValidateAsync(service, tampered)).IsValid);
    }

    [Fact]
    public async Task AccessToken_WithNoSignature_IsRejected()
    {
        var parts = TestJwt.Service().CreateAccessToken(UserId, "a@b.co", []).Value.Split('.');
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        Assert.False((await ValidateAsync(TestJwt.Service(), $"{header}.{parts[1]}.")).IsValid);
    }

    [Fact]
    public void EachAccessToken_HasItsOwnId()
    {
        var service = TestJwt.Service();
        Assert.NotEqual(service.CreateAccessToken(UserId, "a@b.co", []).Value, service.CreateAccessToken(UserId, "a@b.co", []).Value);
    }

    [Fact]
    public void RefreshTokens_AreLongAndUnique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => TokenService.NewRefreshToken()).ToList();
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(t.Length >= 64));
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]+$", t));
    }

    [Fact]
    public void Hash_IsStable_Hex_AndNotTheToken()
    {
        var hash = TokenService.Hash("some-refresh-token");
        Assert.Equal(hash, TokenService.Hash("some-refresh-token"));
        Assert.NotEqual(hash, TokenService.Hash("some-refresh-tokeN"));
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.DoesNotContain("some-refresh-token", hash);
    }
}

public class JwtOptionsTests
{
    [Fact]
    public void ARealKey_IsValid() => Assert.True(JwtOptions.IsValid(new JwtOptions { Key = new string('k', 32) }));

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void AShortOrMissingKey_IsInvalid(string key) => Assert.False(JwtOptions.IsValid(new JwtOptions { Key = key }));

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void UnreasonableAccessLifetimes_AreInvalid(int minutes) =>
        Assert.False(JwtOptions.IsValid(new JwtOptions { Key = new string('k', 32), AccessMinutes = minutes }));

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void UnreasonableRefreshLifetimes_AreInvalid(int days) =>
        Assert.False(JwtOptions.IsValid(new JwtOptions { Key = new string('k', 32), RefreshDays = days }));
}

public class OwnerAccessTests
{
    private sealed class FakeUser(bool authenticated, Guid? id, bool staff) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;
        public Guid? Id => id;
        public bool IsStaff => staff;
    }

    private static readonly Guid Owner = Guid.NewGuid();

    [Fact]
    public void TheOwner_CanAccess() => Assert.True(OwnerAccess.CanAccess(new FakeUser(true, Owner, false), Owner));

    [Fact]
    public void Staff_CanAccessAnyone() => Assert.True(OwnerAccess.CanAccess(new FakeUser(true, Guid.NewGuid(), true), Owner));

    [Fact]
    public void AnotherCustomer_CannotAccess() => Assert.False(OwnerAccess.CanAccess(new FakeUser(true, Guid.NewGuid(), false), Owner));

    [Fact]
    public void AnAnonymousCaller_CannotAccess_EvenWithAMatchingId() =>
        Assert.False(OwnerAccess.CanAccess(new FakeUser(false, Owner, false), Owner));

    [Fact]
    public void AnAnonymousCaller_CannotAccess_EvenIfStaffIsClaimed() =>
        Assert.False(OwnerAccess.CanAccess(new FakeUser(false, null, true), Owner));
}

public class HttpCurrentUserTests
{
    private static HttpCurrentUser For(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role")),
        };
        return new HttpCurrentUser(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void ReadsTheSubjectAsTheId()
    {
        var id = Guid.NewGuid();
        var user = For(new Claim("sub", id.ToString()), new Claim("role", Roles.Customer));
        Assert.True(user.IsAuthenticated);
        Assert.Equal(id, user.Id);
        Assert.False(user.IsStaff);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Staff)]
    public void AdminAndStaffRolesAreStaff(string role) =>
        Assert.True(For(new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", role)).IsStaff);

    [Fact]
    public void ANonGuidSubject_GivesNoId() => Assert.Null(For(new Claim("sub", "not-a-guid")).Id);

    [Fact]
    public void NoHttpContext_MeansNobodyIsSignedIn()
    {
        var user = new HttpCurrentUser(new HttpContextAccessor());
        Assert.False(user.IsAuthenticated);
        Assert.Null(user.Id);
        Assert.False(user.IsStaff);
    }
}

public class AsymmetricSigningTests
{
    private static string NewPem() => System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    [Fact]
    public async Task Rs256Tokens_VerifyWithOnlyThePublicKey()
    {
        var options = new JwtOptions { RsaPrivateKeyPem = NewPem(), Key = "" };
        var service = new TokenService(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
        var token = service.CreateAccessToken(Guid.NewGuid(), "a@b.c", ["Customer"], 0).Value;

        var jwks = System.Text.Json.JsonSerializer.SerializeToElement(service.PublicKeySet());
        var key = jwks.GetProperty("keys")[0];
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.False(key.TryGetProperty("d", out _)); // never the private part

        var verifier = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            ValidAlgorithms = ["RS256"],
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.JsonWebKey(key.GetRawText()),
        };
        var result = await new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ValidateTokenAsync(token, verifier);
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public void WithASharedSecret_NoKeySetIsPublished()
    {
        var service = new TokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions { Key = TestJwt.Key }), TimeProvider.System);
        Assert.Null(service.PublicKeySet());
    }

    [Fact]
    public void Options_AcceptEitherKind_ButNeitherAlone()
    {
        Assert.True(JwtOptions.IsValid(new JwtOptions { RsaPrivateKeyPem = NewPem() }));
        Assert.True(JwtOptions.IsValid(new JwtOptions { Key = TestJwt.Key }));
        Assert.False(JwtOptions.IsValid(new JwtOptions()));
    }
}
