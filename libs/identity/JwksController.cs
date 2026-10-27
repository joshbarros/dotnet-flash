using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Flash.Identity;

[ApiController]
[AllowAnonymous]
public class JwksController(TokenService tokens) : ControllerBase
{
    /// <summary>The public key that verifies access tokens. 404 when tokens are signed with a shared secret.</summary>
    [HttpGet("/.well-known/jwks.json")]
    public IActionResult Get() => tokens.PublicKeySet() is { } set ? Ok(set) : NotFound();
}
