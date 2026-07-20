namespace Palantir.Application.Overview;

public static class OpsSnapshotFocus
{
    /// <summary>Canonical focus used for shared DB snapshots and Ask reuse.</summary>
    public static OverviewFocus CreateDefault() => new()
    {
        IncludeInbox = false,
        IncludeTasks = false,
        IncludeApprovals = false,
        IncludeMaintainX = true,
        IncludeMaintainXInventory = true,
        IncludeEZRentOut = true,
        IncludeMonday = true,
        IncludeConnectorHealth = true,
        CompletionLookbackDays = 0,
        Depth = "detailed",
    };

    public static bool IsCompatibleWithDefault(OverviewFocus focus) =>
        focus.IncludeMaintainX &&
        focus.IncludeMaintainXInventory &&
        focus.IncludeEZRentOut &&
        focus.IncludeMonday &&
        !focus.IncludeInbox &&
        !focus.IncludeTasks &&
        !focus.IncludeApprovals;
}
