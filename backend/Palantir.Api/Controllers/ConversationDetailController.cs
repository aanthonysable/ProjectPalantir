using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Palantir.Api.Auth;
using Palantir.Api.Hubs;
using Palantir.Application.Conversations;
using Palantir.Application.Outbound;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("conversations/{conversationId:guid}")]
public sealed class ConversationDetailController : ControllerBase
{
    private readonly IConversationService _conversations;
    private readonly IOutboundEmailService _outbound;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IHubContext<NotificationsHub> _hub;

    public ConversationDetailController(
        IConversationService conversations,
        IOutboundEmailService outbound,
        ICurrentUserAccessor currentUser,
        IHubContext<NotificationsHub> hub)
    {
        _conversations = conversations;
        _outbound = outbound;
        _currentUser = currentUser;
        _hub = hub;
    }

    [HttpGet]
    public async Task<ActionResult<ConversationDto>> Get(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken);
        return conversation is null ? NotFound() : Ok(conversation);
    }

    [HttpGet("messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> ListMessages(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var messages = await _conversations.ListMessagesAsync(conversationId, cancellationToken);
        return Ok(messages);
    }

    [HttpPost("messages")]
    public async Task<ActionResult<MessageDto>> AddMessage(
        Guid conversationId,
        [FromBody] AddMessageBody body,
        CancellationToken cancellationToken)
    {
        var message = await _conversations.AddMessageAsync(
            conversationId,
            new AddMessageRequest(
                body.Direction,
                body.Body,
                body.SenderUserId ?? _currentUser.UserId,
                body.IsInternalNote),
            cancellationToken);

        var conversation = await _conversations.GetAsync(conversationId, cancellationToken);
        if (conversation is not null)
        {
            await _hub.Clients.Group($"org:{conversation.OrganizationId}")
                .SendAsync("conversation.message_added", new { conversationId, message }, cancellationToken);
        }

        return Created($"/conversations/{conversationId}/messages/{message.Id}", message);
    }

    [HttpPost("claim")]
    public async Task<ActionResult<ConversationDto>> Claim(Guid conversationId, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required in the pilot.");
        }

        try
        {
            var result = await _conversations.ClaimAsync(conversationId, _currentUser.UserId.Value, cancellationToken);
            await _hub.Clients.Group($"org:{result.OrganizationId}")
                .SendAsync("conversation.updated", result, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("assign")]
    public async Task<ActionResult<ConversationDto>> Assign(
        Guid conversationId,
        [FromBody] AssignBody body,
        CancellationToken cancellationToken)
    {
        var result = await _conversations.AssignAsync(
            conversationId,
            new AssignConversationRequest(body.UserId, body.TeamId),
            _currentUser.UserId,
            cancellationToken);

        await _hub.Clients.Group($"org:{result.OrganizationId}")
            .SendAsync("conversation.updated", result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("release")]
    public async Task<ActionResult<ConversationDto>> Release(Guid conversationId, CancellationToken cancellationToken)
    {
        var result = await _conversations.ReleaseAsync(conversationId, _currentUser.UserId, cancellationToken);
        await _hub.Clients.Group($"org:{result.OrganizationId}")
            .SendAsync("conversation.updated", result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("reply-for-approval")]
    public async Task<ActionResult<ReplyDraftResult>> ReplyForApproval(
        Guid conversationId,
        [FromBody] ReplyForApprovalBody body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null || _currentUser.OrganizationId is null)
        {
            return BadRequest("X-Palantir-User-Id and X-Palantir-Organization-Id headers are required.");
        }

        try
        {
            var result = await _outbound.CreateReplyForApprovalAsync(
                conversationId,
                _currentUser.OrganizationId.Value,
                _currentUser.UserId.Value,
                body.Body ?? string.Empty,
                body.ConnectedAccountId,
                cancellationToken);

            await _hub.Clients.Group($"user:{_currentUser.UserId}")
                .SendAsync("approval.created", result, cancellationToken);

            return Created($"/approvals/{result.ApprovalId}", result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class AddMessageBody
{
    public string Direction { get; set; } = "Outbound";
    public string? Body { get; set; }
    public Guid? SenderUserId { get; set; }
    public bool IsInternalNote { get; set; }
}

public sealed class AssignBody
{
    public Guid? UserId { get; set; }
    public Guid? TeamId { get; set; }
}

public sealed class ReplyForApprovalBody
{
    public string? Body { get; set; }
    public Guid? ConnectedAccountId { get; set; }
}
