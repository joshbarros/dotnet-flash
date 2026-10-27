using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flash.Identity;

public record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Creates and hashes tokens. It knows nothing about the database, so it is a plain unit-test target.</summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public const string VersionClaim = "ver";

    private readonly JwtOptions _o = options.Value;

    private readonly RsaSecurityKey? _rsa = LoadRsa(options.Value);

    /// <summary>True when tokens are signed with RS256 and the public key is published.</summary>
    public bool UsesRsa => _rsa is not null;

    public SecurityKey SigningKey => _rsa is not null ? _rsa : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key));

    private string Algorithm => UsesRsa ? SecurityAlgorithms.RsaSha256 : SecurityAlgorithms.HmacSha256;

    private static RsaSecurityKey? LoadRsa(JwtOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.RsaPrivateKeyPem))
            return null;
        var rsa = RSA.Create();
        rsa.ImportFromPem(o.RsaPrivateKeyPem);
        return new RsaSecurityKey(rsa) { KeyId = o.KeyId };
    }

    /// <summary>The public half of the signing key as a JWK set, or null when tokens are signed with a shared secret.</summary>
    public object? PublicKeySet()
    {
        if (_rsa is null)
            return null;
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.Rsa.ExportParameters(false)) { KeyId = _o.KeyId });
        return new { keys = new[] { new { kty = jwk.Kty, use = "sig", alg = SecurityAlgorithms.RsaSha256, kid = jwk.Kid, n = jwk.N, e = jwk.E } } };
    }

    public AccessToken CreateAccessToken(Guid userId, string email, IEnumerable<string> roles, int? sessionVersion = null)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_o.AccessMinutes);
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("email", email),
            new("jti", Guid.NewGuid().ToString("N")),
        };
        if (sessionVersion is { } version)
            claims.Add(new Claim(VersionClaim, version.ToString()));
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, Algorithm),
        };
        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>48 random bytes, URL-safe. The raw value goes to the client once; only its hash is stored.</summary>
    public static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));

    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _o.Issuer,
        ValidateAudience = true,
        ValidAudience = _o.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        RequireExpirationTime = true,
        NameClaimType = "sub",
        RoleClaimType = "role",
        ValidAlgorithms = [Algorithm],
    };
}
