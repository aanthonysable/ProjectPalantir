using Palantir.Domain.Enums;

namespace Palantir.Application.Approvals;

public sealed record ApprovalDto(
    Guid Id,
    Guid? DraftId,
    Guid RequestedForUserId,
    ApprovalStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    Guid? CompletedByUserId,
    string? DraftBody = null,
    string? DraftSubject = null,
    string? DraftTo = null,
    Guid? ConversationId = null);

public sealed record CreateApprovalRequest(
    Guid OrganizationId,
    Guid RequestedForUserId,
    Guid? DraftId,
    int? DraftRevision,
    DateTimeOffset? ExpiresAt);

public interface IApprovalService
{
    Task<IReadOnlyList<ApprovalDto>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<ApprovalDto> CreateAsync(CreateApprovalRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<ApprovalDto> ApproveAsync(Guid approvalId, Guid completedByUserId, CancellationToken cancellationToken = default);
    Task<ApprovalDto> RejectAsync(Guid approvalId, Guid completedByUserId, CancellationToken cancellationToken = default);
}
