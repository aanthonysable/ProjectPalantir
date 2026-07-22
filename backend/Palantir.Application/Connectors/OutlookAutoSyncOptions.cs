namespace Palantir.Application.Connectors;

public sealed class OutlookAutoSyncOptions
{
    public const string SectionName = "Connectors:MicrosoftGraph:AutoSync";

    public bool Enabled { get; set; } = true;

    /// <summary>How often connected mailboxes are pulled into the unified inbox.</summary>
    public int IntervalSeconds { get; set; } = 120;

    /// <summary>Delay before the first sync pass after process start.</summary>
    public int StartupDelaySeconds { get; set; } = 15;

    /// <summary>Max messages to fetch per mailbox per sync pass.</summary>
    public int Top { get; set; } = 25;

    /// <summary>
    /// Skip a mailbox if Graph reported a successful read more recently than this
    /// (avoids hammering Graph when interval is short or multiple workers overlap).
    /// </summary>
    public int MinSecondsBetweenSyncs { get; set; } = 60;
}
