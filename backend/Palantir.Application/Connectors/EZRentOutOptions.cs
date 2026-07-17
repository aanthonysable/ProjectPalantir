namespace Palantir.Application.Connectors;

public sealed class EZRentOutOptions
{
    public const string SectionName = "Connectors:EZRentOut";

    public string Subdomain { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string BaseUrlTemplate { get; set; } = "https://{subdomain}.ezrentout.com";

    public string ResolveBaseUrl() =>
        BaseUrlTemplate.Replace("{subdomain}", Subdomain, StringComparison.OrdinalIgnoreCase);
}
