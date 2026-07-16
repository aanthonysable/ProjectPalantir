using Palantir.Domain.Enums;

namespace Palantir.Application.Conversations;

public sealed record ConversationDto(
    Guid Id,
    Guid OrganizationId,
    string? Subject,
    string Channel,
    ConversationStatus Status,
    Guid? AssignedUserId,
    Guid? AssignedTeamId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    string Direction,
    string? Body,
    string? Summary,
    Guid? SenderUserId,
    bool IsInternalNote,
    DateTimeOffset CreatedAt);

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

public sealed record AssignConversationRequest(Guid? UserId, Guid? TeamId);

public interface IConversationService
{
    Task<IReadOnlyList<ConversationDto>> ListAsync(
        Guid organizationId,
        Guid? assignedUserId,
        bool? unassignedOnly,
        CancellationToken cancellationToken = default);

    Task<ConversationDto?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationDto> CreateAsync(
        CreateConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MessageDto>> ListMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<MessageDto> AddMessageAsync(
        Guid conversationId,
        AddMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<ConversationDto> ClaimAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ConversationDto> AssignAsync(
        Guid conversationId,
        AssignConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<ConversationDto> ReleaseAsync(
        Guid conversationId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}
