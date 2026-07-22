using Palantir.Application.Outbound;

namespace Palantir.Application.Ai;

public sealed record ConversationSummaryResult(
    Guid ConversationId,
    string Summary);

public interface IAiAssistantService
{
    Task<ConversationSummaryResult> SummarizeConversationAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ReplyDraftResult> DraftReplyForApprovalAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        string? guidance = null,
        Guid? connectedAccountId = null,
        CancellationToken cancellationToken = default);
}
