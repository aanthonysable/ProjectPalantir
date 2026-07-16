using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;

namespace Palantir.Application.Tasks;

public sealed class TaskService : ITaskService
{
    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;

    public TaskService(IPalantirDbContext db, IAuditEventWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<IReadOnlyList<TaskDto>> ListAsync(
        Guid organizationId,
        Guid? assignedToUserId,
        CancellationToken cancellationToken = default)
    {
        var query = _db.TaskItems.Where(t => t.OrganizationId == organizationId);
        if (assignedToUserId.HasValue)
        {
            query = query.Where(t => t.AssignedToUserId == assignedToUserId);
        }

        var items = query
            .ToList()
            .OrderByDescending(t => t.CreatedAt)
            .Select(Map)
            .ToList();

        return Task.FromResult<IReadOnlyList<TaskDto>>(items);
    }

    public async Task<TaskDto> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = new TaskItem
        {
            OrganizationId = request.OrganizationId,
            Title = request.Title,
            Description = request.Description,
            CreatedByUserId = request.CreatedByUserId,
            AssignedToUserId = request.AssignedToUserId ?? request.CreatedByUserId,
            ConversationId = request.ConversationId,
            ProjectId = request.ProjectId,
            DueAt = request.DueAt,
            Priority = request.Priority,
            Status = "Open"
        };

        _db.Add(task);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            request.OrganizationId,
            "task.created",
            request.CreatedByUserId,
            nameof(TaskItem),
            task.Id,
            JsonSerializer.Serialize(new { task.Title, task.AssignedToUserId, task.DueAt }),
            cancellationToken);

        return Map(task);
    }

    public async Task<TaskDto> CompleteAsync(Guid taskId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var task = _db.TaskItems.FirstOrDefault(t => t.Id == taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' was not found.");

        if (task.Status == "Completed")
        {
            return Map(task);
        }

        task.Status = "Completed";
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            task.OrganizationId,
            "task.completed",
            actorUserId,
            nameof(TaskItem),
            task.Id,
            cancellationToken: cancellationToken);

        return Map(task);
    }

    private static TaskDto Map(TaskItem t) =>
        new(t.Id, t.OrganizationId, t.ConversationId, t.CreatedByUserId, t.AssignedToUserId,
            t.Title, t.Description, t.DueAt, t.Status, t.Priority, t.CreatedAt);
}
