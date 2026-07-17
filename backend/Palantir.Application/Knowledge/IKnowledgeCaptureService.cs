using Palantir.Application.Outbound;

namespace Palantir.Application.Knowledge;

public static class KnowledgeCaptureKinds
{
    public const string Save = "knowledge.save";
}

public sealed record ProposeKnowledgeCaptureRequest(
    Guid OrganizationId,
    Guid UserId,
    string Title,
    string Body,
    string? SourceQuestion = null,
    bool CreatedByAi = true);

public interface IKnowledgeCaptureService
{
    Task<ReplyDraftResult> ProposeAsync(
        ProposeKnowledgeCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<ReplyDraftResult?> TryExecuteApprovedAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
