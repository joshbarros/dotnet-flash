using Flash.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Flash.Identity;

public record CredentialsRequest(string? Email, string? Password, string? Code = null);
public record EmailRequest(string? Email);
public record ConfirmEmailRequest(Guid UserId, string? Token);
public record ResetPasswordRequest(Guid UserId, string? Token, string? NewPassword);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public record MfaCodeRequest(string? Code);
public record MfaDisableRequest(string? Password, string? Code);
public record MessageResponse(string Message);
public record MfaStatus(bool Enabled);
public record RecoveryCodes(string[] Codes);

public record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, UserInfo User);

[ApiController]
[Route("api/auth")]
public class AuthController(AuthService auth, IOptions<JwtOptions> options) : ControllerBase
{
    public const string RefreshCookie = "flash_refresh";
    public const string RequestedWithHeader = "X-Requested-With";
    public const string RequestedWithValue = "flash-web";

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(CredentialsRequest request, CancellationToken ct)
    {
        var result = await auth.RegisterAsync(request.Email, request.Password, ct);
        if (result is null)
            return StatusCode(StatusCodes.Status202Accepted, new MessageResponse("Check your inbox to confirm your email address, then sign in."));
        SetCookie(result);
        return StatusCode(StatusCodes.Status201Created, ToResponse(result));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login(CredentialsRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, request.Code, ct);
        SetCookie(result);
        return ToResponse(result);
    }

    [AllowAnonymous]
    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken ct)
    {
        await auth.ConfirmEmailAsync(request.UserId, request.Token, ct);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation(EmailRequest request, CancellationToken ct)
    {
        await auth.ResendConfirmationAsync(request.Email, ct);
        return StatusCode(StatusCodes.Status202Accepted, new MessageResponse("If that address needs confirming, a new link is on its way."));
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(request.Email, ct);
        return StatusCode(StatusCodes.Status202Accepted, new MessageResponse("If that address has an account, a reset link is on its way."));
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(request.UserId, request.Token, request.NewPassword, ct);
        return NoContent();
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<TokenResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var result = await auth.ChangePasswordAsync(CallerId(), request.CurrentPassword, request.NewPassword, ct);
        SetCookie(result);
        return ToResponse(result);
    }

    /// <summary>Ends every session of the caller on every device, including access tokens already sold.</summary>
    [Authorize]
    [HttpPost("logout-everywhere")]
    public async Task<IActionResult> LogoutEverywhere(CancellationToken ct)
    {
        await auth.LogoutEverywhereAsync(CallerId(), ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(null));
        return NoContent();
    }

    [Authorize]
    [HttpGet("mfa")]
    public async Task<MfaStatus> Mfa() => new(await auth.MfaEnabledAsync(CallerId()));

    [Authorize]
    [HttpPost("mfa/setup")]
    public async Task<AuthService.MfaSetup> MfaSetup(CancellationToken ct) => await auth.SetupMfaAsync(CallerId(), ct);

    [Authorize]
    [HttpPost("mfa/enable")]
    public async Task<RecoveryCodes> MfaEnable(MfaCodeRequest request, CancellationToken ct) =>
        new(await auth.EnableMfaAsync(CallerId(), request.Code, ct));

    [Authorize]
    [HttpPost("mfa/disable")]
    public async Task<IActionResult> MfaDisable(MfaDisableRequest request, CancellationToken ct)
    {
        await auth.DisableMfaAsync(CallerId(), request.Password, request.Code, ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(null));
        return NoContent();
    }

    /// <summary>The refresh token travels in an HttpOnly cookie. A custom header is required so another site cannot make the browser call this.</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<TokenResponse>> Refresh(CancellationToken ct)
    {
        RequireSameSectionHeader();
        var result = await auth.RefreshAsync(Request.Cookies[RefreshCookie], ct);
        SetCookie(result);
        return ToResponse(result);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        RequireSameSectionHeader();
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(null));
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public ActionResult<UserInfo> Me() => new UserInfo(
        Guid.Parse(User.FindFirst("sub")!.Value),
        User.FindFirst("email")?.Value ?? "",
        User.FindAll("role").Select(c => c.Value).ToArray());

    private Guid CallerId() => Guid.Parse(User.FindFirst("sub")!.Value);

    private void RequireSameSectionHeader()
    {
        if (Request.Headers[RequestedWithHeader] != RequestedWithValue)
            throw new DomainException(400, $"The {RequestedWithHeader} header is required.");
    }

    private void SetCookie(AuthResult result) =>
        Response.Cookies.Append(RefreshCookie, result.RefreshToken, CookieOptions(result.RefreshExpiresAt));

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = options.Value.SecureCookies || Request.IsHttps,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };

    private static TokenResponse ToResponse(AuthResult r) => new(r.Access.Value, r.Access.ExpiresAt, r.User);
}
