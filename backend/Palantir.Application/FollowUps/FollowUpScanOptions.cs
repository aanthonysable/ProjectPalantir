namespace Palantir.Application.FollowUps;

public sealed class FollowUpScanOptions
{
    public const string SectionName = "FollowUpScan";

    public bool Enabled { get; set; } = true;

    /// <summary>How often to scan each organization.</summary>
    public int IntervalMinutes { get; set; } = 10;

    public int StartupDelaySeconds { get; set; } = 45;

    /// <summary>Only consider conversations updated within this window.</summary>
    public int LookbackHours { get; set; } = 72;

    public int MaxConversationsPerRun { get; set; } = 20;

    public int MaxMessagesPerConversation { get; set; } = 10;

    /// <summary>When true, create tasks immediately; when false, only log proposals.</summary>
    public bool AutoCreate { get; set; } = true;

    public bool IncludeOpenWork { get; set; } = true;

    /// <summary>Max AI task proposals accepted per organization per scan.</summary>
    public int MaxTasksPerRun { get; set; } = 12;
}
