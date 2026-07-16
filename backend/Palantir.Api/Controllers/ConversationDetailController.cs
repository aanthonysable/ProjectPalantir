using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Conversations;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("conversations/{conversationId:guid}")]
[ApiExplorerSettings(GroupName = "ConversationDetail")]
public sealed class ConversationDetailController : ControllerBase
{
    private readonly IConversationService _conversations;
    private readonly ICurrentUserAccessor _currentUser;

    public ConversationDetailController(IConversationService conversations, ICurrentUserAccessor currentUser)
    {
        _conversations = conversations;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<ConversationDto>> Get(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken);
        return conversation is null ? NotFound() : Ok(conversation);
    }

    [HttpPost("messages")]
    public async Task<IActionResult> AddMessage(
        Guid conversationId,
        [FromBody] AddMessageBody body,
        CancellationToken cancellationToken)
    {
        await _conversations.AddMessageAsync(
            conversationId,
            new AddMessageRequest(
                body.Direction,
                body.Body,
                body.SenderUserId ?? _currentUser.UserId,
                body.IsInternalNote),
            cancellationToken);

        return Created($"conversations/{conversationId}/messages", null);
    }
}

public sealed class AddMessageBody
{
    public string Direction { get; set; } = "Outbound";
    public string? Body { get; set; }
    public Guid? SenderUserId { get; set; }
    public bool IsInternalNote { get; set; }
}
