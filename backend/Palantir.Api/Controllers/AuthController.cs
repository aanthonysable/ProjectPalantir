using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Palantir.Api.Auth;
using Palantir.Application.Auth;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IPilotAuthService _auth;
    private readonly IEntraExternalIdAuthService _entra;

    public AuthController(IPilotAuthService auth, IEntraExternalIdAuthService entra)
    {
        _auth = auth;
        _entra = entra;
    }

    [AllowAnonymous]
    [HttpGet("providers")]
    public ActionResult<AuthProvidersDto> Providers() => Ok(_entra.GetProviders());

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<PilotLoginResult>> Login(
        [FromBody] LoginBody body,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _auth.LoginAsync(
                new PilotLoginRequest(body.Email ?? string.Empty, body.Password ?? string.Empty),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<PilotLoginResult>> Register(
        [FromBody] RegisterBody body,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _auth.RegisterAsync(
                new PilotRegisterRequest(
                    body.Email ?? string.Empty,
                    body.Password ?? string.Empty,
                    body.DisplayName ?? string.Empty,
                    body.OrganizationId),
                cancellationToken);
            return Created("/me", result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Exchange an Entra External ID (or workforce) access/ID token for a Palantir pilot JWT.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("entra/exchange")]
    public async Task<ActionResult<PilotLoginResult>> ExchangeEntra(
        [FromBody] EntraExchangeBody? body,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = body?.IdToken
                        ?? body?.AccessToken
                        ?? Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);

            var result = await _entra.ExchangeAsync(token ?? string.Empty, cancellationToken);
            return Ok(result);
        }
        catch (SecurityTokenException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

[ApiController]
[Route("me")]
public sealed class MeController : ControllerBase
{
    private readonly IPilotAuthService _auth;
    private readonly ICurrentUserAccessor _currentUser;

    public MeController(IPilotAuthService auth, ICurrentUserAccessor currentUser)
    {
        _auth = auth;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<MeResult>> Get(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return Unauthorized(new { error = "Sign in required." });
        }

        var me = await _auth.GetMeAsync(_currentUser.UserId.Value, cancellationToken);
        return me is null ? NotFound() : Ok(me);
    }
}

public sealed class LoginBody
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

public sealed class RegisterBody
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? DisplayName { get; set; }
    public Guid? OrganizationId { get; set; }
}

public sealed class EntraExchangeBody
{
    public string? IdToken { get; set; }
    public string? AccessToken { get; set; }
}
