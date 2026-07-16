using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Palantir.Api.Auth;
using Palantir.Api.Hubs;
using Palantir.Application.Tasks;

namespace Palantir.Api.Controllers;

[ApiController]
[Route("tasks")]
public sealed class TasksController : ControllerBase
{
    private readonly ITaskService _tasks;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IHubContext<NotificationsHub> _hub;

    public TasksController(
        ITaskService tasks,
        ICurrentUserAccessor currentUser,
        IHubContext<NotificationsHub> hub)
    {
        _tasks = tasks;
        _currentUser = currentUser;
        _hub = hub;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaskDto>>> List(
        [FromQuery] Guid? organizationId,
        [FromQuery] bool assignedToMe = false,
        CancellationToken cancellationToken = default)
    {
        var orgId = organizationId ?? _currentUser.OrganizationId;
        if (orgId is null)
        {
            return BadRequest("organizationId is required.");
        }

        Guid? assignee = assignedToMe ? _currentUser.UserId : null;
        var items = await _tasks.ListAsync(orgId.Value, assignee, cancellationToken);
        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<TaskDto>> Create(
        [FromBody] CreateTaskBody body,
        CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null)
        {
            return BadRequest("X-Palantir-User-Id header is required in the pilot.");
        }

        var orgId = body.OrganizationId ?? _currentUser.OrganizationId
            ?? throw new InvalidOperationException("Organization id is required.");

        var created = await _tasks.CreateAsync(
            new CreateTaskRequest(
                orgId,
                body.Title,
                _currentUser.UserId.Value,
                body.Description,
                body.AssignedToUserId,
                body.ConversationId,
                body.ProjectId,
                body.DueAt,
                body.Priority ?? "Normal"),
            cancellationToken);

        await _hub.Clients.Group($"org:{created.OrganizationId}")
            .SendAsync("task.created", created, cancellationToken);

        return Created($"/tasks/{created.Id}", created);
    }

    [HttpPost("{taskId:guid}/complete")]
    public async Task<ActionResult<TaskDto>> Complete(Guid taskId, CancellationToken cancellationToken)
    {
        var result = await _tasks.CompleteAsync(taskId, _currentUser.UserId, cancellationToken);
        await _hub.Clients.Group($"org:{result.OrganizationId}")
            .SendAsync("task.updated", result, cancellationToken);
        return Ok(result);
    }
}

public sealed class CreateTaskBody
{
    public Guid? OrganizationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? ProjectId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? Priority { get; set; }
}
