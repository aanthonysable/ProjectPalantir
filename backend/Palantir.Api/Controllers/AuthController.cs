using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Auth;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IPilotAuthService _auth;

    public AuthController(IPilotAuthService auth)
    {
        _auth = auth;
    }

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
