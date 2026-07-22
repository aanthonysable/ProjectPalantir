using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;
using Palantir.Domain.Workflow;

namespace Palantir.Application.Conversations;

public sealed class ConversationService : IConversationService
{
    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;

    public ConversationService(IPalantirDbContext db, IAuditEventWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<IReadOnlyList<ConversationDto>> ListAsync(
        Guid organizationId,
        Guid? assignedUserId,
        bool? unassignedOnly,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Conversations.Where(c => c.OrganizationId == organizationId);
        if (assignedUserId.HasValue)
        {
            query = query.Where(c => c.AssignedUserId == assignedUserId);
        }

        if (unassignedOnly == true)
        {
            query = query.Where(c => c.AssignedUserId == null);
        }

        var items = query
            .ToList()
            .OrderByDescending(c => c.UpdatedAt)
            .Select(Map)
            .ToList();

        return Task.FromResult<IReadOnlyList<ConversationDto>>(items);
    }

    public Task<ConversationDto?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId);
        return Task.FromResult(conversation is null ? null : Map(conversation));
    }

    public async Task<ConversationDto> CreateAsync(
        CreateConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var conversation = new Conversation
        {
            OrganizationId = request.OrganizationId,
            Channel = request.Channel,
            Subject = request.Subject,
            CustomerId = request.CustomerId,
            ProjectId = request.ProjectId,
            AssignedUserId = request.AssignedUserId,
            Status = ConversationStatus.Open
        };

        _db.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            request.OrganizationId,
            "conversation.created",
            actorUserId,
            nameof(Conversation),
            conversation.Id,
            JsonSerializer.Serialize(new { conversation.Channel, conversation.Subject }),
            cancellationToken);

        return Map(conversation);
    }

    public async Task<IReadOnlyList<MessageDto>> ListMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var items = _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToList()
            .Where(m => !IsPersistedAiSummary(m))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        var messageIds = items.Select(m => m.Id).ToList();
        var attachments = _db.MessageAttachments
            .Where(a => messageIds.Contains(a.MessageId))
            .ToList()
            .GroupBy(a => a.MessageId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MessageAttachmentDto>)g
                .OrderBy(a => a.IsInline)
                .ThenBy(a => a.FileName)
                .Select(MapAttachment)
                .ToList());

        var mapped = items
            .Select(m => MapMessage(m, attachments.GetValueOrDefault(m.Id, Array.Empty<MessageAttachmentDto>())))
            .ToList();

        // Opening the thread marks it read in the unified inbox.
        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (conversation is { IsUnread: true })
        {
            conversation.IsUnread = false;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return mapped;
    }

    public async Task<MessageDto> AddMessageAsync(
        Guid conversationId,
        AddMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

        var message = new Message
        {
            ConversationId = conversationId,
            Direction = request.Direction,
            Body = request.Body,
            SenderUserId = request.SenderUserId,
            IsInternalNote = request.IsInternalNote
        };

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        if (!request.IsInternalNote &&
            !string.Equals(request.Direction, "Outbound", StringComparison.OrdinalIgnoreCase))
        {
            conversation.IsUnread = true;
        }

        _db.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            request.IsInternalNote ? "conversation.internal_note_added" : "conversation.message_added",
            request.SenderUserId,
            nameof(Message),
            message.Id,
            cancellationToken: cancellationToken);

        return MapMessage(message, Array.Empty<MessageAttachmentDto>());
    }

    public async Task<ConversationDto> ClaimAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        ConversationAssignment.Claim(conversation, userId);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            "conversation.claimed",
            userId,
            nameof(Conversation),
            conversation.Id,
            cancellationToken: cancellationToken);

        return Map(conversation);
    }

    public async Task<ConversationDto> AssignAsync(
        Guid conversationId,
        AssignConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        ConversationAssignment.Assign(conversation, request.UserId, request.TeamId);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            "conversation.assigned",
            actorUserId,
            nameof(Conversation),
            conversation.Id,
            JsonSerializer.Serialize(new { request.UserId, request.TeamId }),
            cancellationToken);

        return Map(conversation);
    }

    public async Task<ConversationDto> ReleaseAsync(
        Guid conversationId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        ConversationAssignment.Release(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            "conversation.released",
            actorUserId,
            nameof(Conversation),
            conversation.Id,
            cancellationToken: cancellationToken);

        return Map(conversation);
    }

    public async Task<ConversationDto> MarkReadAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        if (conversation.IsUnread)
        {
            conversation.IsUnread = false;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Map(conversation);
    }

    private Conversation RequireConversation(Guid conversationId) =>
        _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
        ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

    private static ConversationDto Map(Conversation c) =>
        new(c.Id, c.OrganizationId, c.Subject, c.Channel, c.Status, c.AssignedUserId, c.AssignedTeamId, c.CreatedAt, c.UpdatedAt,
            c.SourceConnectedAccountId, c.SourceMailboxKind, c.IsUnread);

    private static MessageDto MapMessage(Message m, IReadOnlyList<MessageAttachmentDto> attachments) =>
        new(m.Id, m.ConversationId, m.Direction, m.Body, m.Summary, m.SenderUserId, m.IsInternalNote, m.CreatedAt,
            attachments, TryReadFromDisplay(m));

    private static MessageAttachmentDto MapAttachment(MessageAttachment a) =>
        new(a.Id, a.MessageId, a.FileName, a.ContentType, a.ByteSize, a.IsInline, !string.IsNullOrWhiteSpace(a.BlobPath));

    private static string? TryReadFromDisplay(Message m)
    {
        if (!string.IsNullOrWhiteSpace(m.ProviderMetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.ProviderMetadataJson);
                if (doc.RootElement.TryGetProperty("fromMe", out var fromMe) &&
                    fromMe.ValueKind is JsonValueKind.True)
                {
                    return "You";
                }

                if (doc.RootElement.TryGetProperty("notifyName", out var name) &&
                    name.GetString() is { Length: > 0 } display)
                {
                    var cleaned = CleanWhatsAppDisplay(display);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        return cleaned;
                    }
                }

                if (doc.RootElement.TryGetProperty("participant", out var participant) &&
                    participant.GetString() is { Length: > 0 } participantId)
                {
                    var cleaned = CleanWhatsAppDisplay(participantId);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        return cleaned;
                    }
                }

                if (doc.RootElement.TryGetProperty("from", out var from) &&
                    from.GetString() is { Length: > 0 } address)
                {
                    var cleaned = CleanWhatsAppDisplay(address);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        return cleaned;
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        if (!string.IsNullOrWhiteSpace(m.Summary) &&
            m.Summary.Contains(':') &&
            !string.Equals(m.Summary, "AI summary", StringComparison.Ordinal))
        {
            var prefix = m.Summary.Split(':', 2)[0].Trim();
            var cleaned = CleanWhatsAppDisplay(prefix);
            if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length < 80)
            {
                return cleaned;
            }
        }

        if (!string.IsNullOrWhiteSpace(m.Body) &&
            m.Body.StartsWith("From: ", StringComparison.OrdinalIgnoreCase))
        {
            var line = m.Body.Split('\n')[0];
            return CleanWhatsAppDisplay(line["From: ".Length..].Trim());
        }

        return null;
    }

    private static string? CleanWhatsAppDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var at = trimmed.IndexOf('@');
        var local = at >= 0 ? trimmed[..at] : trimmed;
        var domain = at >= 0 ? trimmed[(at + 1)..] : string.Empty;

        // Friendly push / notify names (no JID domain).
        if (at < 0 &&
            !local.All(ch => char.IsDigit(ch) || ch is '+' or '-' or ' ') &&
            !trimmed.StartsWith("WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // Real phone JIDs only — WhatsApp LIDs are also long digit strings.
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

        if (local.Length > 4 && local.All(char.IsDigit))
        {
            return $"WhatsApp …{local[^4..]}";
        }

        return string.IsNullOrWhiteSpace(local) ? null : local;
    }

    private static bool IsPersistedAiSummary(Message message)
    {
        if (string.Equals(message.Summary, "AI summary", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message.ProviderMetadataJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(message.ProviderMetadataJson);
            return doc.RootElement.TryGetProperty("kind", out var kind) &&
                   string.Equals(kind.GetString(), "ai.summary", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
