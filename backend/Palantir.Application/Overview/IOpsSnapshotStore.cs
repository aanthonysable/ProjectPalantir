namespace Palantir.Application.Overview;

public interface IOpsSnapshotStore
{
    public const string DefaultFocusKey = "ops-default";

    Task<OverviewSnapshotDto?> TryGetFreshAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default);

    Task UpsertReadyAsync(
        Guid organizationId,
        string focusKey,
        OverviewSnapshotDto snapshot,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);

    Task MarkRefreshingAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid organizationId,
        string focusKey,
        string error,
        CancellationToken cancellationToken = default);

    /// <summary>True when another worker already marked this row Refreshing recently.</summary>
    Task<bool> IsRefreshingAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default);
}
