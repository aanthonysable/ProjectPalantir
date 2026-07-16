using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Connectors;

public sealed class OutlookInboxSyncService : IOutlookInboxSyncService
{
    private readonly IMicrosoftGraphConnectorService _graph;
    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;

    public OutlookInboxSyncService(
        IMicrosoftGraphConnectorService graph,
        IPalantirDbContext db,
        IAuditEventWriter audit)
    {
        _graph = graph;
        _db = db;
        _audit = audit;
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
        var conversationIds = new HashSet<Guid>();

        var existingProviderIds = _db.Messages
            .Where(m => m.ProviderMessageId != null)
            .Select(m => m.ProviderMessageId!)
            .ToList()
            .ToHashSet(StringComparer.Ordinal);

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

        foreach (var item in mail.OrderBy(m => m.ReceivedAt ?? DateTimeOffset.MinValue))
        {
            if (string.IsNullOrWhiteSpace(item.Id) || existingProviderIds.Contains(item.Id))
            {
                skipped++;
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
                    Status = ConversationStatus.Open,
                    AssignedUserId = userId,
                    CreatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow,
                    UpdatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow
                };
                _db.Add(conversation);
                await _db.SaveChangesAsync(cancellationToken);
                conversationId = conversation.Id;
                threadMap[threadKey] = conversationId;
            }

            var message = new Message
            {
                ConversationId = conversationId,
                Direction = "Inbound",
                Body = BuildBody(item),
                Summary = item.Preview,
                ProviderMessageId = item.Id,
                ProviderMetadataJson = JsonSerializer.Serialize(new
                {
                    provider = "MicrosoftGraph",
                    connectedAccountId,
                    graphConversationId = item.GraphConversationId,
                    from = item.From,
                    isRead = item.IsRead,
                    threadKey
                }),
                CreatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow
            };

            _db.Add(message);

            var conversationEntity = _db.Conversations.FirstOrDefault(c => c.Id == conversationId);
            if (conversationEntity is not null)
            {
                conversationEntity.UpdatedAt = item.ReceivedAt ?? DateTimeOffset.UtcNow;
                if (string.IsNullOrWhiteSpace(conversationEntity.Subject) || conversationEntity.Subject == "(no subject)")
                {
                    conversationEntity.Subject = string.IsNullOrWhiteSpace(item.Subject)
                        ? "(no subject)"
                        : item.Subject;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            existingProviderIds.Add(item.Id);
            conversationIds.Add(conversationId);
            imported++;
        }

        await _audit.WriteAsync(
            organizationId,
            "outlook.mail_synced",
            userId,
            nameof(ConnectedAccount),
            connectedAccountId,
            JsonSerializer.Serialize(new { fetched = mail.Count, imported, skipped }),
            cancellationToken);

        return new OutlookMailSyncResult(
            connectedAccountId,
            mail.Count,
            imported,
            skipped,
            conversationIds.ToList());
    }

    private static string BuildBody(OutlookMessageDto item)
    {
        var from = string.IsNullOrWhiteSpace(item.From) ? "unknown sender" : item.From;
        var preview = item.Preview ?? string.Empty;
        return $"From: {from}\n\n{preview}".Trim();
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
