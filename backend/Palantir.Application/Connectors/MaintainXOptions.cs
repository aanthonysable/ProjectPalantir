namespace Palantir.Application.Connectors;

public sealed class MaintainXOptions
{
    public const string SectionName = "Connectors:MaintainX";

    public string BaseUrl { get; set; } = "https://api.getmaintainx.com/v1";
    public List<MaintainXEnvironmentOptions> Environments { get; set; } = [];
}

public sealed class MaintainXEnvironmentOptions
{
    /// <summary>Human label, e.g. MaintainX-A / MaintainX-B.</summary>
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
}
