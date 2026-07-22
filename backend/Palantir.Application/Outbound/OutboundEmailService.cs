using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Approvals;
using Palantir.Application.Audit;
using Palantir.Application.Connectors;
using Palantir.Application.Knowledge;
using Palantir.Application.Ops;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Outbound;

public sealed class OutboundEmailService : IOutboundEmailService
{
    private readonly IPalantirDbContext _db;
    private readonly IApprovalService _approvals;
    private readonly IMicrosoftGraphConnectorService _graph;
    private readonly IOpsWriteBackService _opsWriteBack;
    private readonly IKnowledgeCaptureService _knowledgeCapture;
    private readonly IAuditEventWriter _audit;

    public OutboundEmailService(
        IPalantirDbContext db,
        IApprovalService approvals,
        IMicrosoftGraphConnectorService graph,
        IOpsWriteBackService opsWriteBack,
        IKnowledgeCaptureService knowledgeCapture,
        IAuditEventWriter audit)
    {
        _db = db;
        _approvals = approvals;
        _graph = graph;
        _opsWriteBack = opsWriteBack;
        _knowledgeCapture = knowledgeCapture;
        _audit = audit;
    }

    public async Task<ReplyDraftResult> CreateReplyForApprovalAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        string body,
        Guid? connectedAccountId = null,
        bool createdByAi = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Reply body is required.");
        }

        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

        if (!string.Equals(conversation.Channel, "Email", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Approval-gated Outlook send is only supported for Email conversations.");
        }

        var account = ResolveConnectedAccount(userId, connectedAccountId);
        var toAddress = ResolveReplyToAddress(conversationId, account)
            ?? throw new InvalidOperationException(
                "Could not determine a recipient from this thread. Sync email first.");

        var subject = conversation.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true
            ? conversation.Subject!
            : $"Re: {conversation.Subject ?? "(no subject)"}";

        var draft = new Draft
        {
            ConversationId = conversationId,
            CreatedByUserId = userId,
            CreatedByAi = createdByAi,
            Body = body.Trim(),
            Revision = 1,
            Status = "PendingApproval",
            MetadataJson = JsonSerializer.Serialize(new
            {
                kind = "outlook.reply",
                to = toAddress,
                subject,
                connectedAccountId = account.Id,
                createdByAi
            })
        };

        _db.Add(draft);
        await _db.SaveChangesAsync(cancellationToken);

        var approval = await _approvals.CreateAsync(
            new CreateApprovalRequest(
                organizationId,
                userId,
                draft.Id,
                draft.Revision,
                DateTimeOffset.UtcNow.AddDays(2)),
            userId,
            cancellationToken);

        await _audit.WriteAsync(
            organizationId,
            "outlook.reply_draft_created",
            userId,
            nameof(Draft),
            draft.Id,
            JsonSerializer.Serialize(new { toAddress, subject, approval.Id }),
            cancellationToken);

        return new ReplyDraftResult(
            draft.Id,
            approval.Id,
            conversationId,
            toAddress,
            subject,
            draft.Body,
            approval.Status);
    }

    public async Task<ReplyDraftResult> ApproveAndSendAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var approval = _db.ApprovalRequests.FirstOrDefault(a => a.Id == approvalId)
            ?? throw new InvalidOperationException($"Approval '{approvalId}' was not found.");

        if (approval.DraftId is null)
        {
            throw new InvalidOperationException("This approval is not linked to a draft.");
        }

        var draft = _db.Drafts.FirstOrDefault(d => d.Id == approval.DraftId.Value)
            ?? throw new InvalidOperationException("Draft was not found.");

        var meta = ParseDraftMeta(draft.MetadataJson);
        if (!string.Equals(meta.Kind, "outlook.reply", StringComparison.OrdinalIgnoreCase))
        {
            var opsResult = await _opsWriteBack.TryExecuteApprovedAsync(approvalId, userId, cancellationToken);
            if (opsResult is not null)
            {
                return opsResult;
            }

            var knowledgeResult = await _knowledgeCapture.TryExecuteApprovedAsync(
                approvalId,
                userId,
                cancellationToken);
            if (knowledgeResult is not null)
            {
                return knowledgeResult;
            }

            // Unknown non-email approvals: just approve.
            var plain = await _approvals.ApproveAsync(approvalId, userId, cancellationToken);
            return new ReplyDraftResult(
                draft.Id,
                plain.Id,
                draft.ConversationId,
                meta.To ?? string.Empty,
                meta.Subject ?? string.Empty,
                draft.Body,
                plain.Status);
        }

        if (meta.ConnectedAccountId is null || string.IsNullOrWhiteSpace(meta.To))
        {
            throw new InvalidOperationException("Draft metadata is missing recipient or connected account.");
        }

        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == draft.ConversationId)
            ?? throw new InvalidOperationException("Conversation was not found.");

        var approved = await _approvals.ApproveAsync(approvalId, userId, cancellationToken);

        var idempotencyKey = $"outlook-send:{approvalId}:{draft.Revision}";
        var existingAction = _db.WorkflowActions
            .Where(a => a.IdempotencyKey == idempotencyKey)
            .ToList()
            .FirstOrDefault();

        if (existingAction is { Status: ActionStatus.Completed })
        {
            return new ReplyDraftResult(
                draft.Id,
                approved.Id,
                draft.ConversationId,
                meta.To!,
                meta.Subject ?? string.Empty,
                draft.Body,
                approved.Status);
        }

        WorkflowAction action;
        if (existingAction is null)
        {
            action = new WorkflowAction
            {
                OrganizationId = conversation.OrganizationId,
                ActionType = "SendEmail",
                Status = ActionStatus.Running,
                RequestedByUserId = userId,
                ApprovalRequestId = approvalId,
                IdempotencyKey = idempotencyKey,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    draftId = draft.Id,
                    to = meta.To,
                    subject = meta.Subject,
                    connectedAccountId = meta.ConnectedAccountId
                }),
                StartedAt = DateTimeOffset.UtcNow
            };
            _db.Add(action);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            action = existingAction;
            action.Status = ActionStatus.Running;
            action.StartedAt = DateTimeOffset.UtcNow;
            action.CompletedAt = null;
            action.ResultJson = null;
            await _db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await _graph.SendMailAsync(
                meta.ConnectedAccountId.Value,
                userId,
                meta.To!,
                meta.Subject ?? $"Re: {conversation.Subject}",
                draft.Body,
                cancellationToken);

            action.Status = ActionStatus.Completed;
            action.CompletedAt = DateTimeOffset.UtcNow;
            action.ResultJson = JsonSerializer.Serialize(new { sent = true });
            draft.Status = "Sent";

            _db.Add(new Message
            {
                ConversationId = draft.ConversationId,
                Direction = "Outbound",
                SenderUserId = userId,
                Body = draft.Body,
                ProviderMetadataJson = JsonSerializer.Serialize(new
                {
                    provider = "MicrosoftGraph",
                    kind = "sent_via_approval",
                    approvalId,
                    to = meta.To,
                    subject = meta.Subject
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });

            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                conversation.OrganizationId,
                "outlook.reply_sent",
                userId,
                nameof(Draft),
                draft.Id,
                JsonSerializer.Serialize(new { meta.To, meta.Subject, approvalId }),
                cancellationToken);
        }
        catch
        {
            action.Status = ActionStatus.Failed;
            action.CompletedAt = DateTimeOffset.UtcNow;
            action.ResultJson = JsonSerializer.Serialize(new { sent = false });
            draft.Status = "SendFailed";
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }

        return new ReplyDraftResult(
            draft.Id,
            approved.Id,
            draft.ConversationId,
            meta.To!,
            meta.Subject ?? string.Empty,
            draft.Body,
            approved.Status);
    }

    private ConnectedAccount ResolveConnectedAccount(Guid userId, Guid? connectedAccountId)
    {
        if (connectedAccountId.HasValue)
        {
            return _db.ConnectedAccounts.FirstOrDefault(a =>
                       a.Id == connectedAccountId.Value &&
                       a.UserId == userId &&
                       a.ConnectionStatus == ConnectionStatus.Connected)
                   ?? throw new InvalidOperationException("Connected Outlook account was not found.");
        }

        return _db.ConnectedAccounts
                   .Where(a => a.UserId == userId &&
                               a.Provider == "MicrosoftGraph" &&
                               a.ConnectionStatus == ConnectionStatus.Connected)
                   .ToList()
                   .OrderByDescending(a => a.UpdatedAt)
                   .FirstOrDefault()
               ?? throw new InvalidOperationException("Connect an email account before sending.");
    }

    private string? ResolveReplyToAddress(Guid conversationId, ConnectedAccount account)
    {
        var self = account.PrimaryAddress;
        var messages = _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToList()
            .OrderByDescending(m => m.CreatedAt)
            .ToList();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.ProviderMetadataJson))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(message.ProviderMetadataJson);
                if (doc.RootElement.TryGetProperty("from", out var from) &&
                    from.GetString() is { Length: > 0 } address &&
                    !SameEmail(address, self))
                {
                    return address;
                }
            }
            catch
            {
                // continue
            }
        }

        foreach (var message in messages)
        {
            if (message.Body is null)
            {
                continue;
            }

            const string prefix = "From: ";
            var line = message.Body.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (line is null)
            {
                continue;
            }

            var address = line[prefix.Length..].Trim();
            if (!SameEmail(address, self))
            {
                return address;
            }
        }

        return null;
    }

    private static bool SameEmail(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static DraftMeta ParseDraftMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DraftMeta(null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Guid? accountId = null;
            if (root.TryGetProperty("connectedAccountId", out var account) &&
                account.ValueKind == JsonValueKind.String &&
                Guid.TryParse(account.GetString(), out var parsed))
            {
                accountId = parsed;
            }

            return new DraftMeta(
                root.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                root.TryGetProperty("to", out var to) ? to.GetString() : null,
                root.TryGetProperty("subject", out var subject) ? subject.GetString() : null,
                accountId);
        }
        catch
        {
            return new DraftMeta(null, null, null, null);
        }
    }

    private sealed record DraftMeta(string? Kind, string? To, string? Subject, Guid? ConnectedAccountId);
}
