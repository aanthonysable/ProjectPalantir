using Palantir.Application.Approvals;
using Palantir.Domain.Enums;

namespace Palantir.Application.Outbound;

public sealed record ReplyDraftResult(
    Guid DraftId,
    Guid ApprovalId,
    Guid ConversationId,
    string ToAddress,
    string Subject,
    string Body,
    ApprovalStatus ApprovalStatus);

public interface IOutboundEmailService
{
    Task<ReplyDraftResult> CreateReplyForApprovalAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        string body,
        Guid? connectedAccountId = null,
        CancellationToken cancellationToken = default);

    Task<ReplyDraftResult> ApproveAndSendAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
