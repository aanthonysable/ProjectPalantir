using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Palantir.Api.Auth;
using Palantir.Api.Hubs;
using Palantir.Application.Approvals;
using Palantir.Application.Outbound;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("approvals")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly IApprovalService _approvals;
    private readonly IOutboundEmailService _outbound;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IHubContext<NotificationsHub> _hub;

    public ApprovalsController(
        IApprovalService approvals,
        IOutboundEmailService outbound,
        ICurrentUserAccessor currentUser,
        IHubContext<NotificationsHub> hub)
    {
        _approvals = approvals;
        _outbound = outbound;
        _currentUser = currentUser;
        _hub = hub;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApprovalDto>>> List(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required in the pilot.");
        }

        var items = await _approvals.ListForUserAsync(_currentUser.UserId.Value, cancellationToken);
        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<ApprovalDto>> Create(
        [FromBody] CreateApprovalBody body,
        CancellationToken cancellationToken)
    {
        var organizationId = body.OrganizationId ?? _currentUser.OrganizationId
            ?? throw new InvalidOperationException("Organization id is required.");

        var created = await _approvals.CreateAsync(
            new CreateApprovalRequest(
                organizationId,
                body.RequestedForUserId,
                body.DraftId,
                body.DraftRevision,
                body.ExpiresAt),
            _currentUser.UserId,
            cancellationToken);

        await _hub.Clients.Group($"user:{created.RequestedForUserId}")
            .SendAsync("approval.created", created, cancellationToken);

        return Created($"/approvals/{created.Id}", created);
    }

    [HttpPost("{approvalId:guid}/approve")]
    public async Task<ActionResult<object>> Approve(Guid approvalId, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required in the pilot.");
        }

        try
        {
            var sent = await _outbound.ApproveAndSendAsync(approvalId, _currentUser.UserId.Value, cancellationToken);
            await _hub.Clients.Group($"user:{_currentUser.UserId}")
                .SendAsync("approval.updated", sent, cancellationToken);
            return Ok(sent);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{approvalId:guid}/reject")]
    public async Task<ActionResult<ApprovalDto>> Reject(Guid approvalId, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required in the pilot.");
        }

        var result = await _approvals.RejectAsync(approvalId, _currentUser.UserId.Value, cancellationToken);
        await _hub.Clients.Group($"user:{result.RequestedForUserId}")
            .SendAsync("approval.updated", result, cancellationToken);
        return Ok(result);
    }
}

public sealed class CreateApprovalBody
{
    public Guid? OrganizationId { get; set; }
    public Guid RequestedForUserId { get; set; }
    public Guid? DraftId { get; set; }
    public int? DraftRevision { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
