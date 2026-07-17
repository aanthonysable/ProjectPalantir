using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Ask;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("ask")]
public sealed class AskController : ControllerBase
{
    private readonly IAskHistoryService _ask;
    private readonly ICurrentUserAccessor _currentUser;

    public AskController(IAskHistoryService ask, ICurrentUserAccessor currentUser)
    {
        _ask = ask;
        _currentUser = currentUser;
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<AskSessionSummaryDto>>> List(
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        return Ok(await _ask.ListSessionsAsync(
            _currentUser.OrganizationId.Value,
            _currentUser.UserId.Value,
            cancellationToken));
    }

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<AskSessionDetailDto>> Get(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        var session = await _ask.GetSessionAsync(
            _currentUser.OrganizationId.Value,
            _currentUser.UserId.Value,
            sessionId,
            cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> Delete(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        try
        {
            await _ask.DeleteSessionAsync(
                _currentUser.OrganizationId.Value,
                _currentUser.UserId.Value,
                sessionId,
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
