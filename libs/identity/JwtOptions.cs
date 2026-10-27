using System.Text;

namespace Flash.Identity;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "flash-api";
    public string Audience { get; set; } = "flash-web";

    /// <summary>HMAC-SHA256 signing key (used when no RSA key is set). At least 32 bytes. Rotate it to sign everyone out.</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// An RSA private key in PEM form. When set, tokens are signed with RS256 and the public key is published at
    /// /.well-known/jwks.json, so other services can verify them without holding a secret. Leave empty to use Key (HS256).
    /// </summary>
    public string? RsaPrivateKeyPem { get; set; }

    public string KeyId { get; set; } = "flash-1";

    public int AccessMinutes { get; set; } = 15;
    public int RefreshDays { get; set; } = 14;

    /// <summary>
    /// How long a user's security stamp is cached when checking an access token. A revoked token stops working within this many
    /// seconds on every instance; 0 checks the database on every request.
    /// </summary>
    public int RevocationCacheSeconds { get; set; } = 5;

    /// <summary>Set true behind TLS so the refresh cookie is only ever sent over HTTPS.</summary>
    public bool SecureCookies { get; set; }

    public static bool IsValid(JwtOptions o) =>
        (Encoding.UTF8.GetByteCount(o.Key) >= 32 || !string.IsNullOrWhiteSpace(o.RsaPrivateKeyPem)) && o.AccessMinutes is >= 1 and <= 120 && o.RefreshDays is >= 1 and <= 90;
}

public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Sign-in is refused until the email address is confirmed. Switch off only for demos and tests.</summary>
    public bool RequireConfirmedEmail { get; set; } = true;

    /// <summary>The web app's address; links in emails point here.</summary>
    public string PublicUrl { get; set; } = "http://localhost:4290";

    public string AppName { get; set; } = "Flash";
}

public class MailOptions
{
    public const string Section = "Mail";

    /// <summary>SMTP host. Empty means mail is only written to the log (and the dev mailbox, if enabled).</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@flash.local";
    public bool StartTls { get; set; } = true;

    /// <summary>Keeps the last messages in memory and serves them at /api/dev/mailbox. For demos and browser tests only.</summary>
    public bool DevMailbox { get; set; }
}

public class SeedAdminOptions
{
    public const string Section = "Identity:SeedAdmin";

    public string? Email { get; set; }
    public string? Password { get; set; }
}
