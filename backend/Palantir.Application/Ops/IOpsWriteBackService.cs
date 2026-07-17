using Palantir.Application.Outbound;

namespace Palantir.Application.Ops;

public static class OpsWriteBackKinds
{
    public const string MaintainXComment = "maintainx.comment";
    public const string MondayUpdate = "monday.update";
}

public sealed record ProposeOpsWriteBackRequest(
    Guid OrganizationId,
    Guid UserId,
    string SourceSystem,
    string? EnvironmentName,
    string ExternalId,
    string Title,
    string Body);

public interface IOpsWriteBackService
{
    Task<ReplyDraftResult> ProposeAsync(
        ProposeOpsWriteBackRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes MaintainX/Monday write-back when draft kind matches; otherwise returns null.
    /// </summary>
    Task<ReplyDraftResult?> TryExecuteApprovedAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
