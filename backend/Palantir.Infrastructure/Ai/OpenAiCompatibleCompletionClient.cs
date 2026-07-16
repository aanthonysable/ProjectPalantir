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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<string> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("AI API key is not configured.");
        }

        var client = _httpClientFactory.CreateClient("palantir-ai");
        using var request = BuildRequest(messages);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("AI completion failed: {Status} {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"AI completion failed: {(int)response.StatusCode}");
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
        var isAzure = string.Equals(_options.Provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);
        string url;
        if (isAzure)
        {
            if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.Deployment))
            {
                throw new InvalidOperationException("Azure OpenAI requires Ai:Endpoint and Ai:Deployment.");
            }

            var endpoint = _options.Endpoint.TrimEnd('/');
            url = $"{endpoint}/openai/deployments/{_options.Deployment}/chat/completions?api-version={_options.ApiVersion}";
        }
        else
        {
            url = "https://api.openai.com/v1/chat/completions";
        }

        var payload = new
        {
            model = isAzure ? null : _options.Model,
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

        if (isAzure)
        {
            request.Headers.Add("api-key", _apiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
