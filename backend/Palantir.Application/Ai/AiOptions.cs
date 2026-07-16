namespace Palantir.Application.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>OpenAI, AzureOpenAI, or Ollama</summary>
    public string Provider { get; set; } = "Ollama";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "llama3.2";

    /// <summary>
    /// Base URL for OpenAI-compatible APIs.
    /// Ollama default: http://127.0.0.1:11434
    /// Azure OpenAI: https://{resource}.openai.azure.com/
    /// </summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Azure OpenAI deployment name</summary>
    public string Deployment { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "2024-08-01-preview";
}
