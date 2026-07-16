using Microsoft.AspNetCore.Mvc;
using Palantir.Api.Auth;
using Palantir.Application.Conversations;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("organizations/{organizationId:guid}/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversations;
    private readonly ICurrentUserAccessor _currentUser;

    public ConversationsController(IConversationService conversations, ICurrentUserAccessor currentUser)
    {
        _conversations = conversations;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationDto>>> List(
        Guid organizationId,
        [FromQuery] bool assignedToMe = false,
        CancellationToken cancellationToken = default)
    {
        Guid? assignedUserId = assignedToMe ? _currentUser.UserId : null;
        var items = await _conversations.ListAsync(organizationId, assignedUserId, cancellationToken);
        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<ConversationDto>> Create(
        Guid organizationId,
        [FromBody] CreateConversationBody body,
        CancellationToken cancellationToken = default)
    {
        var created = await _conversations.CreateAsync(
            new CreateConversationRequest(
                organizationId,
                body.Channel,
                body.Subject,
                body.CustomerId,
                body.ProjectId,
                body.AssignedUserId ?? _currentUser.UserId),
            _currentUser.UserId,
            cancellationToken);

        return Created($"/conversations/{created.Id}", created);
    }
}

public sealed class CreateConversationBody
{
    public string Channel { get; set; } = "Internal";
    public string? Subject { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? AssignedUserId { get; set; }
}
