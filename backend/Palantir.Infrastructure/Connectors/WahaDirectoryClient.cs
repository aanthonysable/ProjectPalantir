using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Connectors;

namespace Palantir.Infrastructure.Connectors;

public sealed class WahaDirectoryClient : IWahaDirectoryClient
{
    private static readonly ConcurrentDictionary<string, string> SubjectCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ParticipantLabelCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _groupsFetchedAt = DateTimeOffset.MinValue;
    private static readonly object GroupsLock = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WhatsAppBridgeOptions _options;
    private readonly ILogger<WahaDirectoryClient> _logger;

    public WahaDirectoryClient(
        IHttpClientFactory httpClientFactory,
        IOptions<WhatsAppBridgeOptions> options,
        ILogger<WahaDirectoryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<string?> GetChatSubjectAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return null;
        }

        if (SubjectCache.TryGetValue(chatId, out var cached))
        {
            return cached;
        }

        var all = await ListGroupSubjectsAsync(cancellationToken);
        return all.TryGetValue(chatId, out var subject) ? subject : null;
    }

    public async Task<string?> ResolveParticipantLabelAsync(
        string participantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(participantId))
        {
            return null;
        }

        await ListGroupSubjectsAsync(cancellationToken);

        if (ParticipantLabelCache.TryGetValue(participantId, out var label))
        {
            return label;
        }

        var local = participantId.Split('@')[0];
        if (ParticipantLabelCache.TryGetValue(local, out label))
        {
            return label;
        }

        return FormatPhoneJid(participantId);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListGroupSubjectsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return SubjectCache;
        }

        lock (GroupsLock)
        {
            if (DateTimeOffset.UtcNow - _groupsFetchedAt < TimeSpan.FromMinutes(5) && SubjectCache.Count > 0)
            {
                return new Dictionary<string, string>(SubjectCache, StringComparer.OrdinalIgnoreCase);
            }
        }

        try
        {
            var client = _httpClientFactory.CreateClient("waha");
            var session = string.IsNullOrWhiteSpace(_options.Session) ? "default" : _options.Session.Trim();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_options.BaseUrl.TrimEnd('/')}/api/{Uri.EscapeDataString(session)}/groups");
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("WAHA groups lookup failed: {Status} {Body}", (int)response.StatusCode, body);
                return new Dictionary<string, string>(SubjectCache, StringComparer.OrdinalIgnoreCase);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(SubjectCache, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var id = prop.Name;
                var subject = prop.Value.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(subject))
                {
                    SubjectCache[id] = subject!;
                }

                if (prop.Value.TryGetProperty("participants", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in parts.EnumerateArray())
                    {
                        CacheParticipant(p);
                    }
                }
            }

            lock (GroupsLock)
            {
                _groupsFetchedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAHA groups lookup threw");
        }

        return new Dictionary<string, string>(SubjectCache, StringComparer.OrdinalIgnoreCase);
    }

    private static void CacheParticipant(JsonElement p)
    {
        var id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var phone = p.TryGetProperty("phoneNumber", out var phoneEl) ? phoneEl.GetString() : null;
        var label = FormatPhoneJid(phone) ?? FormatPhoneJid(id);
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            ParticipantLabelCache[id] = label;
            ParticipantLabelCache[id.Split('@')[0]] = label;
        }

        if (!string.IsNullOrWhiteSpace(phone))
        {
            ParticipantLabelCache[phone] = label;
            ParticipantLabelCache[phone.Split('@')[0]] = label;
        }
    }

    private static string? FormatPhoneJid(string? jidOrPhone)
    {
        if (string.IsNullOrWhiteSpace(jidOrPhone))
        {
            return null;
        }

        var at = jidOrPhone.IndexOf('@');
        var local = at >= 0 ? jidOrPhone[..at] : jidOrPhone;
        var domain = at >= 0 ? jidOrPhone[(at + 1)..] : string.Empty;
        if (string.IsNullOrWhiteSpace(local) || !local.All(char.IsDigit))
        {
            return null;
        }

        var isPhone = domain.StartsWith("s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                      (at < 0 && local.Length is 10 or 11);
        if (!isPhone)
        {
            return null;
        }

        if (local.Length == 11 && local.StartsWith('1'))
        {
            return $"+1 {local[1..4]}-{local[4..7]}-{local[7..]}";
        }

        if (local.Length == 10)
        {
            return $"+1 {local[..3]}-{local[3..6]}-{local[6..]}";
        }

        return $"+{local}";
    }
}
