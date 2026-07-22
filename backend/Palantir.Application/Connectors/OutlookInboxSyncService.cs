using System.Text.Json;
using Microsoft.Extensions.Logging;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Connectors;

public sealed class OutlookInboxSyncService : IOutlookInboxSyncService
{
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;

    private readonly IMicrosoftGraphConnectorService _graph;
    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;
    private readonly IBlobKnowledgeStore _blobs;
    private readonly ILogger<OutlookInboxSyncService> _logger;

    public OutlookInboxSyncService(
        IMicrosoftGraphConnectorService graph,
        IPalantirDbContext db,
        IAuditEventWriter audit,
        IBlobKnowledgeStore blobs,
        ILogger<OutlookInboxSyncService> logger)
    {
        _graph = graph;
        _db = db;
        _audit = audit;
        _blobs = blobs;
        _logger = logger;
    }

    public async Task<OutlookMailSyncResult> SyncAsync(
        Guid connectedAccountId,
        Guid userId,
        Guid organizationId,
        int top = 25,
        CancellationToken cancellationToken = default)
    {
        var mail = await _graph.ListMailAsync(connectedAccountId, userId, top, cancellationToken);
        var imported = 0;
        var skipped = 0;
        var updated = 0;
        var conversationIds = new HashSet<Guid>();

        var existingByProviderId = _db.Messages
            .Where(m => m.ProviderMessageId != null)
            .ToList()
            .GroupBy(m => m.ProviderMessageId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Map Graph conversationId -> Palantir conversation for this sync pass + DB lookup
        var threadMap = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var existing in _db.Messages.Where(m => m.ProviderMetadataJson != null).ToList())
        {
            var threadKey = TryReadThreadKey(existing.ProviderMetadataJson);
            if (threadKey is not null && !threadMap.ContainsKey(threadKey))
            {
                threadMap[threadKey] = existing.ConversationId;
            }
        }

        var account = _db.ConnectedAccounts.FirstOrDefault(a => a.Id == connectedAccountId);
        var mailboxKind = account?.MailboxKind ?? "Work";

        foreach (var item in mail.OrderBy(m => m.ReceivedAt ?? DateTimeOffset.MinValue))
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                skipped++;
                continue;
            }

            var body = BuildBody(item);

            if (existingByProviderId.TryGetValue(item.Id, out var existingMessage))
            {
                // Refresh truncated preview-only bodies with full Graph content.
                if (!string.IsNullOrWhiteSpace(body) &&
                    (string.IsNullOrWhiteSpace(existingMessage.Body) ||
                     body.Length > existingMessage.Body.Length + 20))
                {
                    existingMessage.Body = body;
                    existingMessage.Summary = item.Preview;
                    existingMessage.ProviderMetadataJson = MergeMetadata(
                        existingMessage.ProviderMetadataJson,
                        item,
                        connectedAccountId);
                    await _db.SaveChangesAsync(cancellationToken);
                    conversationIds.Add(existingMessage.ConversationId);
                    updated++;
                }
                else
                {
                    skipped++;
                }

                var existingConversation = _db.Conversations.FirstOrDefault(c => c.Id == existingMessage.ConversationId);
                if (existingConversation is not null)
                {
                    existingConversation.SourceConnectedAccountId ??= connectedAccountId;
                    existingConversation.SourceMailboxKind ??= mailboxKind;
                    await _db.SaveChangesAsync(cancellationToken);
                }

                if (item.HasAttachments)
                {
                    await ImportAttachmentsAsync(
                        existingMessage,
                        connectedAccountId,
                        userId,
                        organizationId,
                        item.Id,
                        cancellationToken);
                }

                continue;
            }

            var threadKey = !string.IsNullOrWhiteSpace(item.GraphConversationId)
                ? $"graph:{item.GraphConversationId}"
                : $"single:{item.Id}";

            if (!threadMap.TryGetValue(threadKey, out var conversationId))
            {
                var conversation = new Conversation
                {
                    OrganizationId = organizationId,
                    Subject = string.IsNullOrWhiteSpace(item.Subject) ? "(no subject)" : item.Subject,
                    Channel = "Email",
                    SourceConnectedAccountId = connectedAccountId,
                    SourceMailboxKind = mailboxKind,
                    Status = ConversationStatus.Open,
                    AssignedUserId = userId,
                    IsUnread = item.IsRead != true,
                    CreatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow,
                    UpdatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow
                };
                _db.Add(conversation);
                await _db.SaveChangesAsync(cancellationToken);
                conversationId = conversation.Id;
                threadMap[threadKey] = conversationId;
            }
            else
            {
                var existingConversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId);
                if (existingConversation is not null)
                {
                    existingConversation.SourceConnectedAccountId ??= connectedAccountId;
                    existingConversation.SourceMailboxKind ??= mailboxKind;
                }
            }

            var message = new Message
            {
                ConversationId = conversationId,
                Direction = "Inbound",
                Body = body,
                Summary = item.Preview,
                ProviderMessageId = item.Id,
                ProviderMetadataJson = BuildMetadataJson(item, connectedAccountId, threadKey),
                CreatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow
            };

            _db.Add(message);

            var conversationEntity = _db.Conversations.FirstOrDefault(c => c.Id == conversationId);
            if (conversationEntity is not null)
            {
                conversationEntity.UpdatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow;
                // Graph unread or any newly imported inbound mail lights up the thread.
                if (item.IsRead != true)
                {
                    conversationEntity.IsUnread = true;
                }

                if (string.IsNullOrWhiteSpace(conversationEntity.Subject) || conversationEntity.Subject == "(no subject)")
                {
                    conversationEntity.Subject = string.IsNullOrWhiteSpace(item.Subject)
                        ? "(no subject)"
                        : item.Subject;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            existingByProviderId[item.Id] = message;
            conversationIds.Add(conversationId);
            imported++;

            if (item.HasAttachments)
            {
                await ImportAttachmentsAsync(
                    message,
                    connectedAccountId,
                    userId,
                    organizationId,
                    item.Id,
                    cancellationToken);
            }
        }

        await _audit.WriteAsync(
            organizationId,
            "outlook.mail_synced",
            userId,
            nameof(ConnectedAccount),
            connectedAccountId,
            JsonSerializer.Serialize(new { fetched = mail.Count, imported, skipped, updated }),
            cancellationToken);

        return new OutlookMailSyncResult(
            connectedAccountId,
            mail.Count,
            imported,
            skipped,
            conversationIds.ToList());
    }

    private async Task ImportAttachmentsAsync(
        Message message,
        Guid connectedAccountId,
        Guid userId,
        Guid organizationId,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OutlookMailAttachmentDto> attachments;
        try
        {
            attachments = await _graph.ListMailAttachmentsAsync(
                connectedAccountId,
                userId,
                providerMessageId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed listing Graph attachments for message {ProviderMessageId}",
                providerMessageId);
            return;
        }

        if (attachments.Count == 0)
        {
            return;
        }

        var existing = _db.MessageAttachments
            .Where(a => a.MessageId == message.Id)
            .ToList();
        var existingIds = existing
            .Select(a => a.ProviderAttachmentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var attachment in attachments)
        {
            if (existingIds.Contains(attachment.Id))
            {
                continue;
            }

            string? blobPath = null;
            if (attachment.ContentBytes is { Length: > 0 } bytes &&
                bytes.LongLength <= MaxAttachmentBytes &&
                _blobs.IsConfigured)
            {
                blobPath =
                    $"mail-attachments/{organizationId:N}/{message.Id:N}/{attachment.Id}/{SanitizeFileName(attachment.Name)}";
                try
                {
                    await using var stream = new MemoryStream(bytes);
                    await _blobs.UploadAsync(blobPath, stream, attachment.ContentType, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed uploading mail attachment {FileName} for message {MessageId}",
                        attachment.Name,
                        message.Id);
                    blobPath = null;
                }
            }

            _db.Add(new MessageAttachment
            {
                MessageId = message.Id,
                OrganizationId = organizationId,
                FileName = attachment.Name,
                ContentType = attachment.ContentType,
                ByteSize = attachment.Size,
                IsInline = attachment.IsInline,
                ProviderAttachmentId = attachment.Id,
                BlobPath = blobPath,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildMetadataJson(
        OutlookMessageDto item,
        Guid connectedAccountId,
        string threadKey) =>
        JsonSerializer.Serialize(new
        {
            provider = "MicrosoftGraph",
            connectedAccountId,
            graphConversationId = item.GraphConversationId,
            from = item.From,
            to = item.ToAddresses,
            cc = item.CcAddresses,
            hasAttachments = item.HasAttachments,
            isRead = item.IsRead,
            threadKey,
            bodySource = string.IsNullOrWhiteSpace(item.BodyText) ? "preview" : "full"
        });

    private static string MergeMetadata(
        string? existingJson,
        OutlookMessageDto item,
        Guid connectedAccountId)
    {
        var threadKey = TryReadThreadKey(existingJson)
            ?? (!string.IsNullOrWhiteSpace(item.GraphConversationId)
                ? $"graph:{item.GraphConversationId}"
                : $"single:{item.Id}");
        return BuildMetadataJson(item, connectedAccountId, threadKey);
    }

    private static string BuildBody(OutlookMessageDto item)
    {
        var from = string.IsNullOrWhiteSpace(item.From) ? "unknown sender" : item.From;
        var content = !string.IsNullOrWhiteSpace(item.BodyText)
            ? item.BodyText
            : item.Preview ?? string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"From: {from}");
        if (item.ToAddresses is { Count: > 0 })
        {
            sb.AppendLine($"To: {string.Join(", ", item.ToAddresses)}");
        }

        if (item.CcAddresses is { Count: > 0 })
        {
            sb.AppendLine($"Cc: {string.Join(", ", item.CcAddresses)}");
        }

        sb.AppendLine();
        sb.Append(content);
        return sb.ToString().Trim();
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment.bin" : cleaned;
    }

    private static string? TryReadThreadKey(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("threadKey", out var threadKey) &&
                threadKey.GetString() is { Length: > 0 } key)
            {
                return key;
            }

            if (doc.RootElement.TryGetProperty("graphConversationId", out var graphId) &&
                graphId.GetString() is { Length: > 0 } id)
            {
                return $"graph:{id}";
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
