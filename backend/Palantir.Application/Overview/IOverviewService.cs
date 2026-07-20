using Palantir.Application.Connectors;

namespace Palantir.Application.Overview;

/// <summary>
/// Which sources to include in an overview. Defaults are inclusive;
/// clients can narrow this over time (per-user prefs).
/// </summary>
public sealed class OverviewFocus
{
    public bool IncludeInbox { get; set; } = false;
    public bool IncludeTasks { get; set; } = false;
    public bool IncludeApprovals { get; set; } = false;
    public bool IncludeMaintainX { get; set; } = true;
    public bool IncludeMaintainXInventory { get; set; } = true;
    public bool IncludeEZRentOut { get; set; } = true;
    public bool IncludeMonday { get; set; } = true;
    public bool IncludeConnectorHealth { get; set; } = true;

    /// <summary>Optional free-text: e.g. "focus on overdue field work".</summary>
    public string? CustomPrompt { get; set; }

    /// <summary>brief | standard | detailed</summary>
    public string Depth { get; set; } = "detailed";

    /// <summary>
    /// Days of MaintainX DONE history to include. 0 = auto (1 day if busy, else 7).
    /// </summary>
    public int CompletionLookbackDays { get; set; }
}

public sealed record OverviewCountsDto(
    int Conversations,
    int OpenTasks,
    int PendingApprovals,
    /// <summary>MaintainX OPEN + IN_PROGRESS (physical work still open).</summary>
    int ExternalOpenWork,
    /// <summary>MaintainX ON_HOLD — physically done; back office can close when ready.</summary>
    int OnHoldAwaitingClose,
    int RecentlyCompleted,
    int AgingQuotes,
    int QuotesWithMaintainXLink,
    int InventoryOut,
    int InventoryLow);

public sealed record OverviewSnapshotDto(
    DateTimeOffset GeneratedAt,
    string CompletionWindowLabel,
    OverviewCountsDto Counts,
    IReadOnlyList<ConnectorHealthDto> ConnectorHealth,
    IReadOnlyList<ExternalWorkItemDto> ExternalWorkSample,
    IReadOnlyList<ExternalWorkItemDto> RecentlyCompleted,
    IReadOnlyList<ExternalWorkItemDto> QuotesSample,
    IReadOnlyList<InventoryAlertDto> InventoryAlerts,
    IReadOnlyList<OverviewListItemDto> RecentConversations,
    IReadOnlyList<OverviewListItemDto> OpenTasks,
    IReadOnlyList<OverviewListItemDto> PendingApprovals,
    IReadOnlyList<string> Notes,
    IReadOnlyList<EzRentOrderDto> EzRentOrders);

public sealed record OverviewListItemDto(
    string Id,
    string Title,
    string? Subtitle,
    string? Status,
    DateTimeOffset? At);

public sealed record OverviewRecapDto(
    DateTimeOffset GeneratedAt,
    string Narrative,
    OverviewSnapshotDto Snapshot,
    OverviewFocus FocusUsed);

public sealed record OverviewChatTurnDto(string Role, string Content);

public sealed class OverviewChatRequest
{
    public OverviewFocus? Focus { get; set; }

    /// <summary>Prior turns plus the latest user question. Roles: user | assistant.</summary>
    public List<OverviewChatTurnDto> Messages { get; set; } = [];

    /// <summary>
    /// When true, rebuild live connector facts and upsert the shared DB snapshot.
    /// When false (default), prefer the shared org ops snapshot from the database
    /// so all users reuse the same pull. Background refresh keeps it warm.
    /// </summary>
    public bool RefreshFacts { get; set; } = false;

    /// <summary>Existing Ask chat session to append to. Null starts a new chat.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Ask attachment ids uploaded via POST /ask/attachments for this turn.</summary>
    public List<Guid> AttachmentIds { get; set; } = [];
}

public sealed record OverviewChatReplyDto(
    DateTimeOffset GeneratedAt,
    string Reply,
    OverviewSnapshotDto Snapshot,
    OverviewFocus FocusUsed,
    Guid SessionId);

public interface IOverviewService
{
    Task<OverviewSnapshotDto> GetSnapshotAsync(
        Guid organizationId,
        Guid userId,
        OverviewFocus? focus = null,
        CancellationToken cancellationToken = default);

    Task<OverviewRecapDto> GenerateRecapAsync(
        Guid organizationId,
        Guid userId,
        OverviewFocus? focus = null,
        CancellationToken cancellationToken = default);

    Task<OverviewChatReplyDto> ChatAsync(
        Guid organizationId,
        Guid userId,
        OverviewChatRequest request,
        CancellationToken cancellationToken = default);
}
