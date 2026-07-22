using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Palantir.Api.Auth;
using Palantir.Api.Hubs;
using Palantir.Application.FollowUps;
using Palantir.Application.Tasks;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("follow-ups")]
public sealed class FollowUpsController : ControllerBase
{
    private readonly IFollowUpScanService _scanner;
    private readonly ITaskService _tasks;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IHubContext<NotificationsHub> _hub;

    public FollowUpsController(
        IFollowUpScanService scanner,
        ITaskService tasks,
        ICurrentUserAccessor currentUser,
        IHubContext<NotificationsHub> hub)
    {
        _scanner = scanner;
        _tasks = tasks;
        _currentUser = currentUser;
        _hub = hub;
    }

    /// <summary>Run an on-demand follow-up scan for the current organization.</summary>
    [HttpPost("scan")]
    public async Task<ActionResult<FollowUpScanResult>> Scan(CancellationToken cancellationToken)
    {
        var orgId = _currentUser.OrganizationId;
        if (orgId is null)
        {
            return BadRequest("Organization is required.");
        }

        var before = await _tasks.ListAsync(orgId.Value, null, cancellationToken);
        var beforeIds = before.Select(t => t.Id).ToHashSet();

        var result = await _scanner.ScanOrganizationAsync(orgId.Value, cancellationToken);

        var after = await _tasks.ListAsync(orgId.Value, null, cancellationToken);
        foreach (var created in after.Where(t => !beforeIds.Contains(t.Id)))
        {
            await _hub.Clients.Group($"org:{orgId}")
                .SendAsync("task.created", created, cancellationToken);
        }

        return Ok(result);
    }
}
