namespace Palantir.Application.Tasks;

public sealed record TaskDto(
    Guid Id,
    Guid OrganizationId,
    Guid? ConversationId,
    Guid CreatedByUserId,
    Guid? AssignedToUserId,
    string Title,
    string? Description,
    DateTimeOffset? DueAt,
    string Status,
    string Priority,
    DateTimeOffset CreatedAt);

public sealed record CreateTaskRequest(
    Guid OrganizationId,
    string Title,
    Guid CreatedByUserId,
    string? Description = null,
    Guid? AssignedToUserId = null,
    Guid? ConversationId = null,
    Guid? ProjectId = null,
    DateTimeOffset? DueAt = null,
    string Priority = "Normal");

public interface ITaskService
{
    Task<IReadOnlyList<TaskDto>> ListAsync(
        Guid organizationId,
        Guid? assignedToUserId,
        CancellationToken cancellationToken = default);

    Task<TaskDto> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);

    Task<TaskDto> CompleteAsync(Guid taskId, Guid? actorUserId, CancellationToken cancellationToken = default);
}
