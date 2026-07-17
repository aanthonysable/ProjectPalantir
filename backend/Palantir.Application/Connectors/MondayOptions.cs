namespace Palantir.Application.Connectors;

public sealed class MondayOptions
{
    public const string SectionName = "Connectors:Monday";

    public string ApiToken { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = "https://api.monday.com/v2";
    public string ApiVersion { get; set; } = "2024-10";

    /// <summary>Only this Monday workspace is active for Palantir (others deprecated).</summary>
    public string WorkspaceId { get; set; } = "11721129";

    public string WorkspaceName { get; set; } = "Sable Operations";

    /// <summary>If set, only these boards are included (preferred over scanning the whole workspace).</summary>
    public string[] IncludedBoardIds { get; set; } = ["18242475298"];

    public string[] IncludedBoardNames { get; set; } = ["Quotes"];

    /// <summary>
    /// Boards that are reference sheets, not actionable work (excluded from open-work / overview).
    /// </summary>
    public string[] ExcludedBoardNames { get; set; } =
    [
        "Truck Inventory"
    ];

    /// <summary>Quotes older than this (in Sent/Draft) are flagged as aging.</summary>
    public int QuoteAgingDays { get; set; } = 14;
}
