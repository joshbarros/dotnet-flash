using System.Net.Mail;
using Flash.Shared;
using Flash.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;

namespace Flash.Identity;

public record UserInfo(Guid Id, string Email, string[] Roles);
public record AuthResult(AccessToken Access, string RefreshToken, DateTimeOffset RefreshExpiresAt, UserInfo User);

public sealed class AuthService(
    UserManager<ApplicationUser> users,
    IdentityModuleDbContext db,
    TokenService tokens,
    IOptions<JwtOptions> options,
    IOptions<AuthOptions> authOptions,
    IAuthMailer mailer,
    TimeProvider clock)
{
    public const string MfaRequired = "A verification code is required.";

    /// <summary>Hashing cost grows with the input, so an absurdly long password is refused before anything is hashed.</summary>
    public const int MaxPasswordLength = 128;

    private readonly JwtOptions _o = options.Value;
    private readonly AuthOptions _auth = authOptions.Value;

    /// <summary>Creates a customer and mails a confirmation link. Returns the session, or null when the address must be confirmed first.</summary>
    public async Task<AuthResult?> RegisterAsync(string? email, string? password, CancellationToken ct)
    {
        if (!IsEmail(email))
            throw new DomainException(400, "A valid email address is required.");
        if (string.IsNullOrEmpty(password))
            throw new DomainException(400, "A password is required.");
        RequireReasonableLength(password);

        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new DomainException(created.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail") ? 409 : 400,
                string.Join(" ", created.Errors.Select(e => e.Description)));

        await users.AddToRoleAsync(user, Roles.Customer);
        await SendConfirmationAsync(user, ct);
        return _auth.RequireConfirmedEmail ? null : await IssueAsync(user, Guid.CreateVersion7(), ct);
    }

    public Task<AuthResult> LoginAsync(string? email, string? password, CancellationToken ct) => LoginAsync(email, password, null, ct);

    public async Task<AuthResult> LoginAsync(string? email, string? password, string? code, CancellationToken ct)
    {
        var invalid = new DomainException(401, "Invalid email or password.");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
            throw invalid;

        var user = await users.FindByEmailAsync(email);
        if (user is null)
            throw invalid;
        if (await users.IsLockedOutAsync(user))
            throw new DomainException(429, "Too many failed attempts. Try again in 15 minutes.");
        if (!await users.CheckPasswordAsync(user, password))
            throw await FailAsync(user, invalid);

        if (_auth.RequireConfirmedEmail && !await users.IsEmailConfirmedAsync(user))
            throw new DomainException(403, "Confirm your email address first. Check your inbox.");

        if (await users.GetTwoFactorEnabledAsync(user))
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new DomainException(401, MfaRequired);
            if (!await VerifyCodeAsync(user, code))
                throw await FailAsync(user, new DomainException(401, "Invalid verification code."));
        }

        await users.ResetAccessFailedCountAsync(user);
        return await IssueAsync(user, Guid.CreateVersion7(), ct);
    }

    private async Task<DomainException> FailAsync(ApplicationUser user, DomainException invalid)
    {
        await users.AccessFailedAsync(user);
        return await users.IsLockedOutAsync(user) ? new DomainException(429, "Too many failed attempts. Try again in 15 minutes.") : invalid;
    }

    /// <summary>An authenticator code, or one of the one-time recovery codes.</summary>
    private async Task<bool> VerifyCodeAsync(ApplicationUser user, string code)
    {
        var clean = code.Replace(" ", "").Replace("-", "");
        return await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, clean)
            || (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim())).Succeeded;
    }

    public async Task ConfirmEmailAsync(Guid userId, string? token, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        var result = user is null || string.IsNullOrEmpty(token) ? IdentityResult.Failed() : await users.ConfirmEmailAsync(user, Decode(token));
        if (!result.Succeeded)
            throw new DomainException(400, "This confirmation link is invalid or has expired.");
    }

    /// <summary>Always looks the same from outside, so it cannot be used to find out who has an account.</summary>
    public async Task ResendConfirmationAsync(string? email, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email);
        if (user is not null && !await users.IsEmailConfirmedAsync(user))
            await SendConfirmationAsync(user, ct);
    }

    public async Task ForgotPasswordAsync(string? email, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email);
        if (user is null)
            return;
        var token = Encode(await users.GeneratePasswordResetTokenAsync(user));
        var link = $"{_auth.PublicUrl.TrimEnd('/')}/?reset={user.Id}&token={token}";
        await mailer.SendAsync(new MailMessageData(user.Email!, $"Reset your {_auth.AppName} password",
            $"Open this link to choose a new password. It works once and expires soon.\n\n{link}\n\nIf you did not ask for this, ignore this email.", link), ct);
    }

    public async Task ResetPasswordAsync(Guid userId, string? token, string? newPassword, CancellationToken ct)
    {
        var invalid = new DomainException(400, "This reset link is invalid or has expired.");
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(newPassword))
            throw invalid;
        RequireReasonableLength(newPassword);
        var result = await users.ResetPasswordAsync(user, Decode(token), newPassword);
        if (!result.Succeeded)
            throw result.Errors.Any(e => e.Code == "InvalidToken") ? invalid : new DomainException(400, string.Join(" ", result.Errors.Select(e => e.Description)));
        await users.ResetAccessFailedCountAsync(user);
        await RevokeAllAsync(user.Id, ct);
    }

    /// <summary>Changes the password, ends every other session, and signs this one back in on a fresh token.</summary>
    public async Task<AuthResult> ChangePasswordAsync(Guid userId, string? current, string? next, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new DomainException(401, "Sign in again.");
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(next))
            throw new DomainException(400, "The current and the new password are required.");
        RequireReasonableLength(next);
        if (current.Length > MaxPasswordLength)
            throw new DomainException(400, "The current password is not correct.");
        var result = await users.ChangePasswordAsync(user, current, next);
        if (!result.Succeeded)
            throw new DomainException(400, string.Join(" ", result.Errors.Select(e => e.Description)));
        await RevokeAllAsync(user.Id, ct);
        await db.Entry(user).ReloadAsync(ct); // pick up the new session version before issuing under it
        return await IssueAsync(user, Guid.CreateVersion7(), ct);
    }

    /// <summary>Revokes every refresh token and, through the session version, every access token sold so far.</summary>
    public async Task LogoutEverywhereAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not null)
            await RevokeAllAsync(user.Id, ct);
    }

    public sealed record MfaSetup(string SharedKey, string OtpAuthUri);

    public async Task<MfaSetup> SetupMfaAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new DomainException(401, "Sign in again.");
        if (await users.GetTwoFactorEnabledAsync(user))
            throw new DomainException(409, "Two-factor authentication is already on.");
        await users.ResetAuthenticatorKeyAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var issuer = Uri.EscapeDataString(_auth.AppName);
        return new MfaSetup(key, $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer={issuer}&digits=6");
    }

    /// <summary>Turns two-factor on once a code from the authenticator proves the key was scanned. Returns the one-time recovery codes.</summary>
    public async Task<string[]> EnableMfaAsync(Guid userId, string? code, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new DomainException(401, "Sign in again.");
        if (string.IsNullOrWhiteSpace(code)
            || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code.Replace(" ", "")))
            throw new DomainException(400, "That code is not valid. Check the time on your phone and try again.");
        await users.SetTwoFactorEnabledAsync(user, true);
        return (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8))!.ToArray();
    }

    public async Task DisableMfaAsync(Guid userId, string? password, string? code, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new DomainException(401, "Sign in again.");
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength || !await users.CheckPasswordAsync(user, password))
            throw new DomainException(400, "The password is not correct.");
        if (await users.GetTwoFactorEnabledAsync(user) && (string.IsNullOrWhiteSpace(code) || !await VerifyCodeAsync(user, code)))
            throw new DomainException(400, "That code is not valid.");
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await RevokeAllAsync(user.Id, ct);
    }

    public async Task<bool> MfaEnabledAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is not null && await users.GetTwoFactorEnabledAsync(user);
    }

    private async Task SendConfirmationAsync(ApplicationUser user, CancellationToken ct)
    {
        var token = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = $"{_auth.PublicUrl.TrimEnd('/')}/?confirm={user.Id}&token={token}";
        await mailer.SendAsync(new MailMessageData(user.Email!, $"Confirm your {_auth.AppName} email address",
            $"Welcome. Open this link to confirm your email address.\n\n{link}", link), ct);
    }

    private async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        // One atomic increment: every access token sold under the old version stops working.
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SessionVersion, u => u.SessionVersion + 1), ct);
    }

    private static void RequireReasonableLength(string password)
    {
        if (password.Length > MaxPasswordLength)
            throw new DomainException(400, $"A password is at most {MaxPasswordLength} characters.");
    }

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static string Decode(string token)
    {
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair. The old token is revoked with one conditional UPDATE, so two requests
    /// presenting the same token cannot both succeed. A token that was already used means it leaked: revoke the whole family.
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string? rawToken, CancellationToken ct)
    {
        var unauthorised = new DomainException(401, "The session has expired. Sign in again.");
        if (string.IsNullOrEmpty(rawToken))
            throw unauthorised;

        var hash = TokenService.Hash(rawToken);
        var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw unauthorised;
        var now = clock.GetUtcNow();

        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw unauthorised;
        }
        if (stored.ExpiresAt <= now)
            throw unauthorised;

        var user = await users.FindByIdAsync(stored.UserId.ToString()) ?? throw unauthorised;
        if (await users.IsLockedOutAsync(user))
            throw unauthorised;

        var next = TokenService.NewRefreshToken();
        var nextHash = TokenService.Hash(next);
        var revoked = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedByHash, nextHash), ct);
        if (revoked != 1)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw unauthorised;
        }

        return await IssueAsync(user, stored.FamilyId, ct, next);
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
            return;
        var hash = TokenService.Hash(rawToken);
        var familyId = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (familyId is { } id)
            await RevokeFamilyAsync(id, ct);
    }

    private async Task<AuthResult> IssueAsync(ApplicationUser user, Guid familyId, CancellationToken ct, string? refreshToken = null)
    {
        var roles = (await users.GetRolesAsync(user)).ToArray();
        var access = tokens.CreateAccessToken(user.Id, user.Email!, roles, user.SessionVersion);

        var raw = refreshToken ?? TokenService.NewRefreshToken();
        var now = clock.GetUtcNow();
        var expires = now.AddDays(_o.RefreshDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = TokenService.Hash(raw),
            CreatedAt = now,
            ExpiresAt = expires,
        });
        await db.SaveChangesAsync(ct);
        return new AuthResult(access, raw, expires, new UserInfo(user.Id, user.Email!, roles));
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && MailAddress.TryCreate(value, out var m) && m.Address == value;
}
