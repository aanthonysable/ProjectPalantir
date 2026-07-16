namespace Palantir.Application.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>OpenAI or AzureOpenAI</summary>
    public string Provider { get; set; } = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Azure OpenAI resource endpoint, e.g. https://myresource.openai.azure.com/</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Azure OpenAI deployment name</summary>
    public string Deployment { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "2024-08-01-preview";
}
