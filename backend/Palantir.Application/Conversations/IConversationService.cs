using Palantir.Domain.Enums;

namespace Palantir.Application.Conversations;

public sealed record ConversationDto(
    Guid Id,
    Guid OrganizationId,
    string? Subject,
    string Channel,
    ConversationStatus Status,
    Guid? AssignedUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateConversationRequest(
    Guid OrganizationId,
    string Channel,
    string? Subject,
    Guid? CustomerId,
    Guid? ProjectId,
    Guid? AssignedUserId);

public sealed record AddMessageRequest(
    string Direction,
    string? Body,
    Guid? SenderUserId,
    bool IsInternalNote = false);

public interface IConversationService
{
    Task<IReadOnlyList<ConversationDto>> ListAsync(
        Guid organizationId,
        Guid? assignedUserId,
        CancellationToken cancellationToken = default);

    Task<ConversationDto?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationDto> CreateAsync(
        CreateConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task AddMessageAsync(
        Guid conversationId,
        AddMessageRequest request,
        CancellationToken cancellationToken = default);
}
