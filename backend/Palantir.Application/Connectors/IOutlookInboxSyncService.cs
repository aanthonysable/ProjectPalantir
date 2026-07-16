using Palantir.Domain.Enums;

namespace Palantir.Application.Connectors;

public sealed record OutlookMailSyncResult(
    Guid ConnectedAccountId,
    int Fetched,
    int Imported,
    int Skipped,
    IReadOnlyList<Guid> ConversationIds);

public interface IOutlookInboxSyncService
{
    Task<OutlookMailSyncResult> SyncAsync(
        Guid connectedAccountId,
        Guid userId,
        Guid organizationId,
        int top = 25,
        CancellationToken cancellationToken = default);
}
