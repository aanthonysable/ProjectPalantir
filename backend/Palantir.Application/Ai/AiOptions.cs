namespace Palantir.Application.Ai;

public enum AiTaskKind
{
    Default = 0,
    Recap = 1,
    Chat = 2,
    Summarize = 3,
    DraftReply = 4,
    DescribeImage = 5,
}

public sealed class AiProviderOptions
{
    /// <summary>Ollama, Gemini, OpenAI, or AzureOpenAI</summary>
    public string Provider { get; set; } = "Ollama";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Base URL. Examples:
    /// Ollama: http://127.0.0.1:11434
    /// Gemini OpenAI-compat: https://generativelanguage.googleapis.com/v1beta/openai
    /// Azure: https://{resource}.openai.azure.com/
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-08-01-preview";
    public double? Temperature { get; set; }
}

public sealed class AiTaskRoutingOptions
{
    /// <summary>Named entry under Ai:Providers (or "default").</summary>
    public string Recap { get; set; } = "default";
    public string Chat { get; set; } = "default";
    public string Summarize { get; set; } = "default";
    public string DraftReply { get; set; } = "default";
    public string DescribeImage { get; set; } = "gemini";
}

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Legacy default provider when Providers/default is absent.</summary>
    public string Provider { get; set; } = "Ollama";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "llama3.2";

    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string Deployment { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-08-01-preview";
    public double Temperature { get; set; } = 0.1;

    /// <summary>Named provider profiles (ollama, gemini, openai, …).</summary>
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which named provider handles each AI job.</summary>
    public AiTaskRoutingOptions Tasks { get; set; } = new();

    public string ResolveProviderName(AiTaskKind task) =>
        task switch
        {
            AiTaskKind.Recap => FirstNonEmpty(Tasks.Recap, "default"),
            AiTaskKind.Chat => FirstNonEmpty(Tasks.Chat, "default"),
            AiTaskKind.Summarize => FirstNonEmpty(Tasks.Summarize, "default"),
            AiTaskKind.DraftReply => FirstNonEmpty(Tasks.DraftReply, "default"),
            AiTaskKind.DescribeImage => FirstNonEmpty(Tasks.DescribeImage, "gemini", "default"),
            _ => "default",
        };

    public AiProviderOptions ResolveProfile(AiTaskKind task)
    {
        var name = ResolveProviderName(task);
        if (Providers.TryGetValue(name, out var named) && named is not null)
        {
            return MergeWithDefaults(named);
        }

        if (!string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)
            && Providers.TryGetValue("default", out var fallback)
            && fallback is not null)
        {
            return MergeWithDefaults(fallback);
        }

        return LegacyAsProfile();
    }

    public IReadOnlyList<(string Name, AiProviderOptions Profile)> ListProfiles()
    {
        var list = new List<(string, AiProviderOptions)>();
        if (Providers.Count == 0)
        {
            list.Add(("default", LegacyAsProfile()));
            return list;
        }

        foreach (var (name, profile) in Providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            list.Add((name, MergeWithDefaults(profile)));
        }

        if (!Providers.ContainsKey("default"))
        {
            list.Insert(0, ("default", LegacyAsProfile()));
        }

        return list;
    }

    private AiProviderOptions LegacyAsProfile() =>
        new()
        {
            Provider = Provider,
            ApiKey = ApiKey,
            Model = Model,
            Endpoint = Endpoint,
            Deployment = Deployment,
            ApiVersion = ApiVersion,
            Temperature = Temperature,
        };

    private AiProviderOptions MergeWithDefaults(AiProviderOptions named) =>
        new()
        {
            Provider = FirstNonEmpty(named.Provider, Provider),
            ApiKey = FirstNonEmpty(named.ApiKey, ApiKey),
            Model = FirstNonEmpty(named.Model, Model),
            Endpoint = FirstNonEmpty(named.Endpoint, Endpoint),
            Deployment = FirstNonEmpty(named.Deployment, Deployment),
            ApiVersion = FirstNonEmpty(named.ApiVersion, ApiVersion),
            Temperature = named.Temperature ?? Temperature,
        };

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
