using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Connectors;

public sealed class WhatsAppIngestService : IWhatsAppIngestService
{
    private static readonly ConcurrentDictionary<string, object> IngestLocks = new(StringComparer.Ordinal);

    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;
    private readonly WhatsAppBridgeOptions _options;

    public WhatsAppIngestService(
        IPalantirDbContext db,
        IAuditEventWriter audit,
        IOptions<WhatsAppBridgeOptions> options)
    {
        _db = db;
        _audit = audit;
        _options = options.Value;
    }

    public WhatsAppBridgeStatusDto GetStatus()
    {
        var configured = _options.Enabled && !string.IsNullOrWhiteSpace(_options.WebhookSecret);
        var conversations = _db.Conversations.Count(c => c.Channel == "WhatsApp");
        var messages = (
            from m in _db.Messages
            join c in _db.Conversations on m.ConversationId equals c.Id
            where c.Channel == "WhatsApp"
            select m
        ).Count();

        var detail = !_options.Enabled
            ? "Disabled — set Connectors:WhatsApp:Enabled=true"
            : string.IsNullOrWhiteSpace(_options.WebhookSecret)
                ? "Enabled but WebhookSecret missing"
                : $"Listening for WAHA webhooks · {conversations} threads · {messages} messages";

        return new WhatsAppBridgeStatusDto(
            _options.Enabled,
            configured,
            _options.InstanceName,
            detail,
            conversations,
            messages);
    }

    public Task<WhatsAppIngestResult> IngestWahaEventAsync(
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(new WhatsAppIngestResult(false, "disabled"));
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
        var root = doc.RootElement;
        var eventName = root.TryGetProperty("event", out var ev) ? ev.GetString() : null;
        // Only message.any — "message" overlaps and double-fires when both are subscribed.
        if (!string.Equals(eventName, "message.any", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WhatsAppIngestResult(true, "ignored_event"));
        }

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(new WhatsAppIngestResult(true, "ignored_no_payload"));
        }

        var providerMessageId = NormalizeProviderMessageId(ReadString(payload, "id"));
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
            return Task.FromResult(new WhatsAppIngestResult(true, "ignored_no_id"));
        }

        var gate = IngestLocks.GetOrAdd(providerMessageId, _ => new object());
        lock (gate)
        {
            var result = IngestLockedAsync(root, payload, eventName!, providerMessageId, cancellationToken)
                .GetAwaiter()
                .GetResult();
            return Task.FromResult(result);
        }
    }

    private async Task<WhatsAppIngestResult> IngestLockedAsync(
        JsonElement root,
        JsonElement payload,
        string eventName,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        var existing = _db.Messages.FirstOrDefault(m => m.ProviderMessageId == providerMessageId);
        if (existing is not null)
        {
            return new WhatsAppIngestResult(true, "duplicate", existing.ConversationId, existing.Id);
        }

        var chatId = ResolveChatId(payload);
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return new WhatsAppIngestResult(true, "ignored_no_chat");
        }

        var body = ReadString(payload, "body")?.Trim();
        var hasMedia = payload.TryGetProperty("hasMedia", out var hm) && hm.ValueKind == JsonValueKind.True;
        if (string.IsNullOrWhiteSpace(body) && hasMedia)
        {
            body = "[media]";
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new WhatsAppIngestResult(true, "ignored_empty");
        }

        var fromMe = payload.TryGetProperty("fromMe", out var fm) &&
                     (fm.ValueKind == JsonValueKind.True ||
                      (fm.ValueKind == JsonValueKind.String &&
                       bool.TryParse(fm.GetString(), out var fmb) &&
                       fmb));

        var participant = ReadString(payload, "participant")
            ?? (fromMe ? "me" : ReadString(payload, "from"));
        var notifyName = ReadString(payload, "notifyName")
            ?? ReadNestedString(payload, "_data", "notifyName")
            ?? participant;

        var sentAt = ReadTimestamp(payload) ?? DateTimeOffset.UtcNow;
        var threadKey = $"waha:{chatId}";
        var organizationId = ResolveOrganizationId();

        var conversationId = FindConversationByThreadKey(threadKey);
        if (conversationId is null)
        {
            var subject = BuildSubject(chatId, payload, root);
            var conversation = new Conversation
            {
                OrganizationId = organizationId,
                Subject = subject,
                Channel = "WhatsApp",
                Status = ConversationStatus.Open,
                CreatedAt = sentAt,
                UpdatedAt = sentAt
            };
            _db.Add(conversation);
            await _db.SaveChangesAsync(cancellationToken);
            conversationId = conversation.Id;
        }
        else
        {
            var conversation = _db.Conversations.First(c => c.Id == conversationId.Value);
            conversation.UpdatedAt = sentAt > conversation.UpdatedAt ? sentAt : DateTimeOffset.UtcNow;
            if (conversation.Status == ConversationStatus.Closed)
            {
                conversation.Status = ConversationStatus.Open;
            }
        }

        var summary = $"{notifyName}: {(body.Length > 160 ? body[..160] + "…" : body)}";
        var message = new Message
        {
            ConversationId = conversationId.Value,
            Direction = fromMe ? "Outbound" : "Inbound",
            Body = body,
            Summary = summary,
            ProviderMessageId = providerMessageId,
            IsInternalNote = false,
            CreatedAt = sentAt,
            ProviderMetadataJson = JsonSerializer.Serialize(new
            {
                provider = "WAHA",
                threadKey,
                chatId,
                participant,
                notifyName,
                fromMe,
                hasMedia,
                session = ReadString(root, "session"),
                eventName
            })
        };
        _db.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            organizationId,
            "whatsapp.message.ingested",
            actorUserId: null,
            entityType: "Conversation",
            entityId: conversationId.Value,
            detailsJson: JsonSerializer.Serialize(new { providerMessageId, chatId, fromMe }),
            cancellationToken: cancellationToken);

        return new WhatsAppIngestResult(true, "imported", conversationId, message.Id);
    }

    public async Task<int> DedupeStoredMessagesAsync(CancellationToken cancellationToken = default)
    {
        var whatsAppConversationIds = _db.Conversations
            .Where(c => c.Channel == "WhatsApp")
            .Select(c => c.Id)
            .ToList();
        if (whatsAppConversationIds.Count == 0)
        {
            return 0;
        }

        var messages = _db.Messages
            .Where(m => whatsAppConversationIds.Contains(m.ConversationId))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToList();

        var remove = new HashSet<Guid>();

        foreach (var group in messages
                     .Where(m => !string.IsNullOrWhiteSpace(m.ProviderMessageId))
                     .GroupBy(m => NormalizeProviderMessageId(m.ProviderMessageId)!, StringComparer.Ordinal))
        {
            foreach (var dup in group.Skip(1))
            {
                remove.Add(dup.Id);
            }
        }

        foreach (var group in messages
                     .Where(m => !remove.Contains(m.Id))
                     .GroupBy(m => new
                     {
                         m.ConversationId,
                         Body = (m.Body ?? string.Empty).Trim(),
                         Bucket = m.CreatedAt.ToUnixTimeSeconds()
                     }))
        {
            if (string.IsNullOrWhiteSpace(group.Key.Body))
            {
                continue;
            }

            foreach (var dup in group.Skip(1))
            {
                remove.Add(dup.Id);
            }
        }

        if (remove.Count == 0)
        {
            return 0;
        }

        foreach (var id in remove)
        {
            var row = messages.First(m => m.Id == id);
            _db.Remove(row);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return remove.Count;
    }

    private Guid ResolveOrganizationId()
    {
        if (!string.IsNullOrWhiteSpace(_options.OrganizationId) &&
            Guid.TryParse(_options.OrganizationId, out var configured) &&
            configured != Guid.Empty)
        {
            return configured;
        }

        var first = _db.Organizations
            .OrderBy(o => o.CreatedAt)
            .Select(o => o.Id)
            .FirstOrDefault();
        if (first == Guid.Empty)
        {
            throw new InvalidOperationException("No organization available for WhatsApp ingest.");
        }

        return first;
    }

    private Guid? FindConversationByThreadKey(string threadKey)
    {
        var needle = $"\"threadKey\":\"{threadKey}\"";
        return (
            from m in _db.Messages
            join c in _db.Conversations on m.ConversationId equals c.Id
            where c.Channel == "WhatsApp" &&
                  m.ProviderMetadataJson != null &&
                  m.ProviderMetadataJson.Contains(needle)
            select (Guid?)m.ConversationId
        ).FirstOrDefault();
    }

    private static string BuildSubject(string chatId, JsonElement payload, JsonElement root)
    {
        var groupName = ReadString(payload, "chatName")
            ?? ReadNestedString(payload, "_data", "notifyName")
            ?? ReadString(root, "chatName");
        if (!string.IsNullOrWhiteSpace(groupName) && chatId.Contains("@g.us", StringComparison.Ordinal))
        {
            return $"WhatsApp · {groupName}";
        }

        if (chatId.Contains("@g.us", StringComparison.Ordinal))
        {
            var shortId = chatId.Split('@')[0];
            var tail = shortId.Length > 8 ? shortId[^8..] : shortId;
            return $"WhatsApp group · …{tail}";
        }

        var peer = ReadString(payload, "notifyName") ?? chatId.Split('@')[0];
        return $"WhatsApp · {peer}";
    }

    private static string? ResolveChatId(JsonElement payload)
    {
        var from = ReadString(payload, "from");
        var to = ReadString(payload, "to");
        var fromMe = payload.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True;

        if (!string.IsNullOrWhiteSpace(from) && from.Contains("@g.us", StringComparison.Ordinal))
        {
            return from;
        }

        if (!string.IsNullOrWhiteSpace(to) && to.Contains("@g.us", StringComparison.Ordinal))
        {
            return to;
        }

        if (fromMe && !string.IsNullOrWhiteSpace(to))
        {
            return to;
        }

        return from ?? to;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement payload)
    {
        if (!payload.TryGetProperty("timestamp", out var ts))
        {
            return null;
        }

        if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var unix))
        {
            if (unix > 10_000_000_000L)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unix);
            }

            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return null;
    }

    private static string? NormalizeProviderMessageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // WAHA ids look like true_<chatId>_<hash> / false_<chatId>_<hash>.
        // Normalize so message / message.any variants collapse to one key.
        if (id.StartsWith("true_", StringComparison.Ordinal) ||
            id.StartsWith("false_", StringComparison.Ordinal))
        {
            var second = id.IndexOf('_', 5);
            if (second > 0 && second < id.Length - 1)
            {
                return id[(second + 1)..];
            }
        }

        return id.Trim();
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? ReadNestedString(JsonElement el, string parent, string child)
    {
        if (!el.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(p, child);
    }
}
