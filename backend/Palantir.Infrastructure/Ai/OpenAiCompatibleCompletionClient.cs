using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Ai;

namespace Palantir.Infrastructure.Ai;

public sealed class OpenAiCompatibleCompletionClient : IAiCompletionClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiOptions _options;
    private readonly ILogger<OpenAiCompatibleCompletionClient> _logger;
    private readonly string _apiKey;

    public OpenAiCompatibleCompletionClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> options,
        ILogger<OpenAiCompatibleCompletionClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _apiKey = FirstNonEmpty(_options.ApiKey, Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    public bool IsConfigured
    {
        get
        {
            if (IsOllama)
            {
                return !string.IsNullOrWhiteSpace(_options.Model);
            }

            if (IsAzure)
            {
                return !string.IsNullOrWhiteSpace(_apiKey)
                       && !string.IsNullOrWhiteSpace(_options.Endpoint)
                       && !string.IsNullOrWhiteSpace(_options.Deployment);
            }

            return !string.IsNullOrWhiteSpace(_apiKey);
        }
    }

    private bool IsOllama =>
        string.Equals(_options.Provider, "Ollama", StringComparison.OrdinalIgnoreCase);

    private bool IsAzure =>
        string.Equals(_options.Provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);

    public async Task<string> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(NotConfiguredMessage());
        }

        var client = _httpClientFactory.CreateClient("palantir-ai");
        using var request = BuildRequest(messages);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("AI completion failed: {Status} {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException(
                IsOllama
                    ? $"Ollama request failed ({(int)response.StatusCode}). Is ollama running and was the model pulled?"
                    : $"AI completion failed: {(int)response.StatusCode}");
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

        return content;
    }

    private HttpRequestMessage BuildRequest(IReadOnlyList<AiChatMessage> messages)
    {
        string url;
        string? model;

        if (IsAzure)
        {
            var endpoint = _options.Endpoint.TrimEnd('/');
            url = $"{endpoint}/openai/deployments/{_options.Deployment}/chat/completions?api-version={_options.ApiVersion}";
            model = null;
        }
        else if (IsOllama)
        {
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint)
                ? "http://127.0.0.1:11434"
                : _options.Endpoint.TrimEnd('/');
            url = $"{endpoint}/v1/chat/completions";
            model = _options.Model;
        }
        else
        {
            url = "https://api.openai.com/v1/chat/completions";
            model = _options.Model;
        }

        var payload = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature = 0.3
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (IsAzure)
        {
            request.Headers.Add("api-key", _apiKey);
        }
        else if (!IsOllama && !string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        else if (IsOllama && !string.IsNullOrWhiteSpace(_apiKey))
        {
            // Optional; local Ollama usually ignores auth.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private string NotConfiguredMessage()
    {
        if (IsOllama)
        {
            return "Ollama is not configured. Set Ai:Provider=Ollama, Ai:Model, start `ollama serve`, and pull the model.";
        }

        return "AI is not configured. Set Ai:ApiKey (user-secrets) or OPENAI_API_KEY, then restart the API.";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
