using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Overview;

namespace Palantir.Api.Controllers;

[ApiController]
[Authorize]
[Route("overview")]
public sealed class OverviewController : ControllerBase
{
    private readonly IOverviewService _overview;
    private readonly ICurrentUserAccessor _currentUser;

    public OverviewController(IOverviewService overview, ICurrentUserAccessor currentUser)
    {
        _overview = overview;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<OverviewSnapshotDto>> Snapshot(
        [FromQuery] bool includeInbox = false,
        [FromQuery] bool includeTasks = false,
        [FromQuery] bool includeApprovals = false,
        [FromQuery] bool includeMaintainX = true,
        [FromQuery] bool includeMaintainXInventory = true,
        [FromQuery] bool includeEZRentOut = true,
        [FromQuery] bool includeMonday = true,
        [FromQuery] bool includeConnectorHealth = true,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        var focus = new OverviewFocus
        {
            IncludeInbox = includeInbox,
            IncludeTasks = includeTasks,
            IncludeApprovals = includeApprovals,
            IncludeMaintainX = includeMaintainX,
            IncludeMaintainXInventory = includeMaintainXInventory,
            IncludeEZRentOut = includeEZRentOut,
            IncludeMonday = includeMonday,
            IncludeConnectorHealth = includeConnectorHealth
        };

        return Ok(await _overview.GetSnapshotAsync(
            _currentUser.OrganizationId.Value,
            _currentUser.UserId.Value,
            focus,
            cancellationToken));
    }

    [HttpPost("recap")]
    public async Task<ActionResult<OverviewRecapDto>> Recap(
        [FromBody] OverviewFocus? focus,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        return Ok(await _overview.GenerateRecapAsync(
            _currentUser.OrganizationId.Value,
            _currentUser.UserId.Value,
            focus ?? new OverviewFocus(),
            cancellationToken));
    }

    [HttpPost("chat")]
    public async Task<ActionResult<OverviewChatReplyDto>> Chat(
        [FromBody] OverviewChatRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.OrganizationId is null || _currentUser.UserId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _overview.ChatAsync(
                _currentUser.OrganizationId.Value,
                _currentUser.UserId.Value,
                request ?? new OverviewChatRequest(),
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
