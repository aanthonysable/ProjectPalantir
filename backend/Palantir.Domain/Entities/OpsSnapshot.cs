namespace Palantir.Domain.Entities;

/// <summary>
/// Shared org-level ops fact sheet (MaintainX / Monday / EZRentOut / inventory)
/// refreshed on a schedule so Ask does not re-pull connectors per user/request.
/// </summary>
public class OpsSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>Stable key for the focus shape, e.g. ops-default.</summary>
    public string FocusKey { get; set; } = "ops-default";

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Ready | Refreshing | Failed</summary>
    public string Status { get; set; } = "Ready";

    public string? Error { get; set; }

    /// <summary>Serialized <c>OverviewSnapshotDto</c> JSON.</summary>
    public string SnapshotJson { get; set; } = "{}";

    public Organization? Organization { get; set; }
}
