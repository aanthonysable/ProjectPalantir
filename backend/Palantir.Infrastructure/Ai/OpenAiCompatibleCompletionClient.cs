using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Ai;

namespace Palantir.Infrastructure.Ai;

/// <summary>
/// OpenAI-compatible chat completions for Ollama, Gemini, OpenAI, and Azure OpenAI.
/// Task routing picks a named provider profile (e.g. Gemini for recap, Ollama for local draft).
/// Transient Gemini failures (429 quota / 503 demand) fall back to the next configured provider.
/// </summary>
public sealed class OpenAiCompatibleCompletionClient : IAiCompletionClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiOptions _options;
    private readonly ILogger<OpenAiCompatibleCompletionClient> _logger;

    public OpenAiCompatibleCompletionClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> options,
        ILogger<OpenAiCompatibleCompletionClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.ListProfiles().Any(p => IsProfileConfigured(p.Profile));

    public bool IsConfiguredFor(AiTaskKind task) =>
        IsProfileConfigured(_options.ResolveProfile(task)) || IsConfigured;

    public AiStatusDto GetStatus()
    {
        var providers = _options.ListProfiles()
            .Select(p =>
            {
                var configured = IsProfileConfigured(p.Profile);
                return new AiProviderStatusDto(
                    p.Name,
                    p.Profile.Provider,
                    p.Profile.Model,
                    configured,
                    configured ? "Ready" : DescribeMissing(p.Profile));
            })
            .ToList();

        var tasks = new[]
            {
                AiTaskKind.Recap,
                AiTaskKind.Chat,
                AiTaskKind.Summarize,
                AiTaskKind.DraftReply,
                AiTaskKind.DescribeImage,
                AiTaskKind.FollowUp,
            }
            .Select(task =>
            {
                var name = _options.ResolveProviderName(task);
                var profile = _options.ResolveProfile(task);
                return new AiRoutingStatusDto(
                    task.ToString(),
                    name,
                    profile.Provider,
                    profile.Model,
                    IsProfileConfigured(profile));
            })
            .ToList();

        return new AiStatusDto(providers.Any(p => p.Configured), providers, tasks);
    }

    public Task<string> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(AiTaskKind.Default, messages, cancellationToken);

    public async Task<string> CompleteAsync(
        AiTaskKind task,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var candidates = BuildCandidateProfiles(task);
        Exception? lastError = null;

        foreach (var profile in candidates)
        {
            try
            {
                return await CompleteWithProfileAsync(profile, task, messages, cancellationToken);
            }
            catch (InvalidOperationException ex) when (IsTransientProviderFailure(ex))
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "AI provider {Provider}/{Model} failed transiently for {Task}; trying next configured provider",
                    profile.Provider,
                    profile.Model,
                    task);
            }
        }

        throw lastError
              ?? new InvalidOperationException("No configured AI provider could complete the request.");
    }

    private async Task<string> CompleteWithProfileAsync(
        AiProviderOptions profile,
        AiTaskKind task,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("palantir-ai");
        using var request = BuildRequest(profile, messages);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AI completion failed ({Provider}/{Model}): {Status} {Body}",
                profile.Provider,
                profile.Model,
                status,
                body);
            throw new InvalidOperationException(FailureMessage(profile, status, body));
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("AI returned an empty response.");
        }

        _logger.LogInformation(
            "AI completion ok via {Provider} model {Model} for task {Task}",
            profile.Provider,
            profile.Model,
            task);
        return content;
    }

    private List<AiProviderOptions> BuildCandidateProfiles(AiTaskKind task)
    {
        var list = new List<AiProviderOptions>();
        var preferred = _options.ResolveProfile(task);
        if (IsProfileConfigured(preferred))
        {
            list.Add(preferred);
        }

        foreach (var (_, profile) in _options.ListProfiles())
        {
            if (!IsProfileConfigured(profile) || list.Any(p => SameProfile(p, profile)))
            {
                continue;
            }

            list.Add(profile);
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException(DescribeMissing(preferred));
        }

        // Keep preferred first; among fallbacks, try Ollama before other clouds —
        // except vision tasks, where local Ollama is often text-only.
        if (task == AiTaskKind.DescribeImage)
        {
            return list
                .Select((p, i) => (p, i))
                .OrderBy(x => x.i == 0 ? 0 : IsOllama(x.p.Provider) ? 2 : 1)
                .ThenBy(x => x.i)
                .Select(x => x.p)
                .ToList();
        }

        return list
            .Select((p, i) => (p, i))
            .OrderBy(x => x.i == 0 ? 0 : IsOllama(x.p.Provider) ? 1 : 2)
            .ThenBy(x => x.i)
            .Select(x => x.p)
            .ToList();
    }

    private static bool SameProfile(AiProviderOptions a, AiProviderOptions b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Model, b.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Endpoint ?? "", b.Endpoint ?? "", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientProviderFailure(InvalidOperationException ex)
    {
        var msg = ex.Message;
        return msg.Contains("(429)", StringComparison.Ordinal)
               || msg.Contains("(503)", StringComparison.Ordinal)
               || msg.Contains("(404)", StringComparison.Ordinal)
               || msg.Contains("quota", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("high demand", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProfileConfigured(AiProviderOptions profile)
    {
        var provider = profile.Provider?.Trim() ?? "";
        var apiKey = ResolveApiKey(profile);

        if (IsOllama(provider))
        {
            return !string.IsNullOrWhiteSpace(profile.Model);
        }

        if (IsAzure(provider))
        {
            return !string.IsNullOrWhiteSpace(apiKey)
                   && !string.IsNullOrWhiteSpace(profile.Endpoint)
                   && !string.IsNullOrWhiteSpace(profile.Deployment);
        }

        if (IsGemini(provider))
        {
            return !string.IsNullOrWhiteSpace(apiKey)
                   && !string.IsNullOrWhiteSpace(profile.Model);
        }

        return !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(profile.Model);
    }

    private HttpRequestMessage BuildRequest(
        AiProviderOptions profile,
        IReadOnlyList<AiChatMessage> messages)
    {
        var provider = profile.Provider?.Trim() ?? "";
        var apiKey = ResolveApiKey(profile);
        string url;
        string? model;
        var maxTokens = IsOllama(provider) ? 700 : 1800;

        if (IsAzure(provider))
        {
            var endpoint = profile.Endpoint.TrimEnd('/');
            url = $"{endpoint}/openai/deployments/{profile.Deployment}/chat/completions?api-version={profile.ApiVersion}";
            model = null;
        }
        else if (IsOllama(provider))
        {
            var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint)
                ? "http://127.0.0.1:11434"
                : profile.Endpoint.TrimEnd('/');
            url = $"{endpoint}/v1/chat/completions";
            model = profile.Model;
        }
        else if (IsGemini(provider))
        {
            var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint)
                ? "https://generativelanguage.googleapis.com/v1beta/openai"
                : profile.Endpoint.TrimEnd('/');
            url = $"{endpoint}/chat/completions";
            model = profile.Model;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(profile.Endpoint)
                && !profile.Endpoint.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
            {
                url = $"{profile.Endpoint.TrimEnd('/')}/chat/completions";
            }
            else
            {
                url = "https://api.openai.com/v1/chat/completions";
            }

            model = profile.Model;
        }

        var temperature = Math.Clamp(profile.Temperature ?? _options.Temperature, 0, 1.5);
        var maxTokensForTask = maxTokens;
        object[] serializedMessages = messages.Select(m =>
        {
            if (m.Parts is { Count: > 0 })
            {
                var parts = m.Parts.Select(p =>
                {
                    if (string.Equals(p.Type, "image_url", StringComparison.OrdinalIgnoreCase))
                    {
                        return (object)new
                        {
                            type = "image_url",
                            image_url = new { url = p.ImageUrl }
                        };
                    }

                    return new
                    {
                        type = "text",
                        text = p.Text ?? string.Empty
                    };
                }).ToArray();

                return new { role = m.Role, content = (object)parts };
            }

            return new { role = m.Role, content = (object)m.Content };
        }).ToArray();

        var payload = new
        {
            model,
            messages = serializedMessages,
            temperature,
            max_tokens = maxTokensForTask,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (IsAzure(provider))
        {
            request.Headers.Add("api-key", apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    private static string ResolveApiKey(AiProviderOptions profile)
    {
        var provider = profile.Provider?.Trim() ?? "";
        if (IsGemini(provider))
        {
            return FirstNonEmpty(
                profile.ApiKey,
                Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
                Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        }

        if (IsOllama(provider))
        {
            return profile.ApiKey?.Trim() ?? string.Empty;
        }

        return FirstNonEmpty(
            profile.ApiKey,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    private static string DescribeMissing(AiProviderOptions profile)
    {
        var provider = profile.Provider?.Trim() ?? "AI";
        if (IsOllama(provider))
        {
            return "Ollama is not configured. Set Ai:Providers:ollama:Model (or Ai:Model), start `ollama serve`, and pull the model.";
        }

        if (IsGemini(provider))
        {
            return "Gemini is not configured. Set Ai:Providers:gemini:ApiKey (or GEMINI_API_KEY) and a model such as gemini-flash-lite-latest.";
        }

        if (IsAzure(provider))
        {
            return "Azure OpenAI is not configured. Set Endpoint, Deployment, and ApiKey.";
        }

        return $"{provider} is not configured. Set ApiKey (user-secrets) and Model, then restart the API.";
    }

    private static string FailureMessage(AiProviderOptions profile, int status, string body)
    {
        if (IsGemini(profile.Provider) && status == 429)
        {
            return $"Gemini request failed (429): free-tier quota exceeded for model '{profile.Model}'. " +
                   "Try Ai:Providers:gemini:Model=gemini-flash-lite-latest, wait for quota reset, or enable billing. " +
                   "Palantir will fall back to another provider when available.";
        }

        if (IsOllama(profile.Provider))
        {
            return $"Ollama request failed ({status}). Is ollama running and was the model pulled?";
        }

        if (IsGemini(profile.Provider))
        {
            var snippet = body.Length <= 180 ? body : body[..180] + "…";
            return $"Gemini request failed ({status}). Model '{profile.Model}'. {snippet}";
        }

        return $"AI completion failed ({profile.Provider}): {status}";
    }

    private static bool IsOllama(string? provider) =>
        string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase);

    private static bool IsAzure(string? provider) =>
        string.Equals(provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);

    private static bool IsGemini(string? provider) =>
        string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
