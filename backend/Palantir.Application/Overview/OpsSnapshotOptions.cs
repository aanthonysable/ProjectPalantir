namespace Palantir.Application.Overview;

public sealed class OpsSnapshotOptions
{
    public const string SectionName = "OpsSnapshots";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the background worker rebuilds shared snapshots.</summary>
    public int RefreshIntervalMinutes { get; set; } = 5;

    /// <summary>How long Ask may serve a stored snapshot before forcing a live rebuild.</summary>
    public int TimeToLiveMinutes { get; set; } = 15;

    /// <summary>Delay before the first background refresh after process start.</summary>
    public int StartupDelaySeconds { get; set; } = 20;
}
