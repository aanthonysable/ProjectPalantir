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
    private readonly IWahaDirectoryClient _waha;
    private readonly WhatsAppBridgeOptions _options;

    public WhatsAppIngestService(
        IPalantirDbContext db,
        IAuditEventWriter audit,
        IWahaDirectoryClient waha,
        IOptions<WhatsAppBridgeOptions> options)
    {
        _db = db;
        _audit = audit;
        _waha = waha;
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
                : $"Listening for WAHA webhooks · {conversations} threads · {messages} messages" +
                  (_waha.IsConfigured ? " · group titles via WAHA API" : " · set BaseUrl+ApiKey for group titles");

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
        var notifyName = await ResolveSenderDisplayNameAsync(payload, fromMe, participant, cancellationToken);

        var sentAt = ReadTimestamp(payload) ?? DateTimeOffset.UtcNow;
        var threadKey = $"waha:{chatId}";
        var organizationId = ResolveOrganizationId();

        var conversationId = FindConversationByThreadKey(threadKey);
        var subject = await ResolveSubjectAsync(chatId, payload, root, notifyName, cancellationToken);
        if (conversationId is null)
        {
            var conversation = new Conversation
            {
                OrganizationId = organizationId,
                Subject = subject,
                Channel = "WhatsApp",
                Status = ConversationStatus.Open,
                IsUnread = !fromMe,
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

            if (ShouldUpgradeSubject(conversation.Subject, subject))
            {
                conversation.Subject = subject;
            }

            if (!fromMe)
            {
                conversation.IsUnread = true;
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
                session = ReadString(root, "session") ?? _options.Session,
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
            detailsJson: JsonSerializer.Serialize(new { providerMessageId, chatId, fromMe, notifyName }),
            cancellationToken: cancellationToken);

        return new WhatsAppIngestResult(true, "imported", conversationId, message.Id);
    }

    public async Task<int> RefreshChatTitlesAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _waha.ListGroupSubjectsAsync(cancellationToken);
        if (groups.Count == 0)
        {
            return 0;
        }

        var conversations = _db.Conversations.Where(c => c.Channel == "WhatsApp").ToList();
        var updated = 0;
        foreach (var conversation in conversations)
        {
            var chatId = FindChatIdForConversation(conversation.Id)
                         ?? MatchGroupIdFromSubjectTail(conversation.Subject, groups.Keys)
                         ?? MatchGroupIdFromSubjectName(conversation.Subject, groups);
            if (string.IsNullOrWhiteSpace(chatId) || !groups.TryGetValue(chatId, out var subject))
            {
                continue;
            }

            var next = $"WhatsApp · {subject}";
            if (ShouldUpgradeSubject(conversation.Subject, next) ||
                !string.Equals(conversation.Subject, next, StringComparison.Ordinal))
            {
                if (string.Equals(conversation.Subject, next, StringComparison.Ordinal))
                {
                    continue;
                }

                conversation.Subject = next;
                conversation.UpdatedAt = DateTimeOffset.UtcNow;
                updated++;
            }
        }

        // Drop empty placeholder duplicates once a titled thread exists for the same group.
        var removed = 0;
        var byChat = new Dictionary<string, List<Domain.Entities.Conversation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conversation in conversations)
        {
            var chatId = FindChatIdForConversation(conversation.Id)
                         ?? MatchGroupIdFromSubjectTail(conversation.Subject, groups.Keys)
                         ?? MatchGroupIdFromSubjectName(conversation.Subject, groups);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                continue;
            }

            if (!byChat.TryGetValue(chatId, out var list))
            {
                list = [];
                byChat[chatId] = list;
            }

            list.Add(conversation);
        }

        foreach (var list in byChat.Values.Where(g => g.Count > 1))
        {
            var keep = list
                .OrderByDescending(c => _db.Messages.Count(m => m.ConversationId == c.Id))
                .ThenByDescending(c => c.UpdatedAt)
                .First();
            foreach (var dup in list.Where(c => c.Id != keep.Id))
            {
                if (_db.Messages.Any(m => m.ConversationId == dup.Id))
                {
                    continue;
                }

                _db.Remove(dup);
                removed++;
            }
        }

        if (updated > 0 || removed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        var labelsFixed = await BackfillSenderLabelsAsync(cancellationToken);
        return updated + removed + labelsFixed;
    }

    private async Task<int> BackfillSenderLabelsAsync(CancellationToken cancellationToken)
    {
        var messages = _db.Messages
            .Where(m => m.ProviderMetadataJson != null && m.ProviderMetadataJson.Contains("\"provider\":\"WAHA\""))
            .OrderByDescending(m => m.CreatedAt)
            .Take(500)
            .ToList();

        var fixedCount = 0;
        foreach (var message in messages)
        {
            try
            {
                using var doc = JsonDocument.Parse(message.ProviderMetadataJson!);
                var root = doc.RootElement;
                var fromMe = root.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True;
                if (fromMe)
                {
                    continue;
                }

                var notifyName = root.TryGetProperty("notifyName", out var nn) ? nn.GetString() : null;
                var participant = root.TryGetProperty("participant", out var p) ? p.GetString() : null;
                if (!string.IsNullOrWhiteSpace(notifyName) &&
                    !LooksLikeJid(notifyName) &&
                    !notifyName.StartsWith("WhatsApp …", StringComparison.Ordinal))
                {
                    continue;
                }

                var label = await _waha.ResolveParticipantLabelAsync(
                    participant ?? notifyName ?? string.Empty,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(label) ||
                    string.Equals(label, notifyName, StringComparison.Ordinal))
                {
                    continue;
                }

                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message.ProviderMetadataJson!)
                           ?? new Dictionary<string, JsonElement>();
                var rebuilt = new Dictionary<string, object?>();
                foreach (var kv in dict)
                {
                    rebuilt[kv.Key] = kv.Value.ValueKind switch
                    {
                        JsonValueKind.String => kv.Value.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => kv.Value.TryGetInt64(out var l) ? l : kv.Value.GetDouble(),
                        JsonValueKind.Null => null,
                        _ => kv.Value.GetRawText()
                    };
                }

                rebuilt["notifyName"] = label;
                message.ProviderMetadataJson = JsonSerializer.Serialize(rebuilt);

                if (!string.IsNullOrWhiteSpace(message.Summary) &&
                    message.Summary.Contains(':') &&
                    LooksLikeJid(message.Summary.Split(':', 2)[0].Trim()))
                {
                    var bodyPart = message.Summary.Split(':', 2).ElementAtOrDefault(1)?.TrimStart() ?? message.Body;
                    message.Summary = $"{label}: {(bodyPart?.Length > 160 ? bodyPart[..160] + "…" : bodyPart)}";
                }

                fixedCount++;
            }
            catch
            {
                // skip malformed metadata
            }
        }

        if (fixedCount > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return fixedCount;
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

    private async Task<string> ResolveSubjectAsync(
        string chatId,
        JsonElement payload,
        JsonElement root,
        string notifyName,
        CancellationToken cancellationToken)
    {
        var isGroup = chatId.Contains("@g.us", StringComparison.Ordinal);
        if (isGroup)
        {
            var fromPayload = ReadString(payload, "chatName")
                ?? ReadString(root, "chatName")
                ?? ReadNestedString(payload, "_data", "subject");
            if (!string.IsNullOrWhiteSpace(fromPayload))
            {
                return $"WhatsApp · {fromPayload}";
            }

            var fromWaha = await _waha.GetChatSubjectAsync(chatId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fromWaha))
            {
                return $"WhatsApp · {fromWaha}";
            }

            var shortId = chatId.Split('@')[0];
            var tail = shortId.Length > 8 ? shortId[^8..] : shortId;
            return $"WhatsApp group · …{tail}";
        }

        var peer = !LooksLikeJid(notifyName) ? notifyName : chatId.Split('@')[0];
        return $"WhatsApp · {peer}";
    }

    private string? FindChatIdForConversation(Guid conversationId)
    {
        var meta = _db.Messages
            .Where(m => m.ConversationId == conversationId && m.ProviderMetadataJson != null)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ProviderMetadataJson)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(meta))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(meta);
            if (doc.RootElement.TryGetProperty("chatId", out var chatId) &&
                chatId.GetString() is { Length: > 0 } id)
            {
                return id;
            }
        }
        catch
        {
            // ignore
        }

        return null;
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

    private async Task<string> ResolveSenderDisplayNameAsync(
        JsonElement payload,
        bool fromMe,
        string? participant,
        CancellationToken cancellationToken)
    {
        if (fromMe)
        {
            return "You";
        }

        var name = ReadString(payload, "notifyName")
            ?? ReadNestedString(payload, "_data", "pushName")
            ?? ReadNestedString(payload, "_data", "notifyName");
        if (!string.IsNullOrWhiteSpace(name) && !LooksLikeJid(name))
        {
            return name.Trim();
        }

        var fromDirectory = await _waha.ResolveParticipantLabelAsync(
            participant ?? name ?? string.Empty,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromDirectory))
        {
            return fromDirectory;
        }

        return HumanizeWhatsAppId(participant ?? name) ?? "Unknown";
    }

    private static bool LooksLikeJid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains('@') ||
         value.EndsWith("lid", StringComparison.OrdinalIgnoreCase) ||
         value.All(ch => char.IsDigit(ch) || ch is '+' or '-' or ' '));

    private static string? HumanizeWhatsAppId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var at = value.IndexOf('@');
        var local = at >= 0 ? value[..at] : value;
        var domain = at >= 0 ? value[(at + 1)..] : string.Empty;
        var isPhoneJid = domain.StartsWith("s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                         (at < 0 && local.All(char.IsDigit) && local.Length is 10 or 11);

        if (isPhoneJid && local.All(char.IsDigit))
        {
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

        if (local.Length > 4)
        {
            return $"WhatsApp …{local[^4..]}";
        }

        return local;
    }

    private static string? MatchGroupIdFromSubjectTail(string? subject, IEnumerable<string> groupIds)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var idx = subject.LastIndexOf('…');
        if (idx < 0 || idx >= subject.Length - 1)
        {
            idx = subject.LastIndexOf("...", StringComparison.Ordinal);
            if (idx < 0 || idx + 3 >= subject.Length)
            {
                return null;
            }

            var asciiTail = subject[(idx + 3)..].Trim();
            return groupIds.FirstOrDefault(id => id.Split('@')[0].EndsWith(asciiTail, StringComparison.Ordinal));
        }

        var tail = subject[(idx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        return groupIds.FirstOrDefault(id => id.Split('@')[0].EndsWith(tail, StringComparison.Ordinal));
    }

    private static string? MatchGroupIdFromSubjectName(
        string? subject,
        IReadOnlyDictionary<string, string> groups)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var name = subject;
        foreach (var prefix in new[] { "WhatsApp · ", "WhatsApp group · " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('…') || name.StartsWith("..."))
        {
            return null;
        }

        return groups
            .FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
            .Key;
    }

    private static bool ShouldUpgradeSubject(string? current, string next)
    {
        if (string.IsNullOrWhiteSpace(next))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        var isPlaceholder = current.Contains("group · …", StringComparison.OrdinalIgnoreCase) ||
                            current.StartsWith("WhatsApp group", StringComparison.OrdinalIgnoreCase);
        var nextIsFriendly = next.StartsWith("WhatsApp · ", StringComparison.OrdinalIgnoreCase) &&
                             !next.Contains("group · …", StringComparison.OrdinalIgnoreCase);
        return isPlaceholder && nextIsFriendly &&
               !string.Equals(current, next, StringComparison.Ordinal);
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
