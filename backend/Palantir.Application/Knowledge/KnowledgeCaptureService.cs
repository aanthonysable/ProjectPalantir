using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Palantir.Application.Abstractions;
using Palantir.Application.Approvals;
using Palantir.Application.Audit;
using Palantir.Application.Outbound;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Knowledge;

public sealed class KnowledgeCaptureService : IKnowledgeCaptureService
{
    private readonly IPalantirDbContext _db;
    private readonly IApprovalService _approvals;
    private readonly IKnowledgeService _knowledge;
    private readonly IAuditEventWriter _audit;

    public KnowledgeCaptureService(
        IPalantirDbContext db,
        IApprovalService approvals,
        IKnowledgeService knowledge,
        IAuditEventWriter audit)
    {
        _db = db;
        _approvals = approvals;
        _knowledge = knowledge;
        _audit = audit;
    }

    public async Task<ReplyDraftResult> ProposeAsync(
        ProposeKnowledgeCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_knowledge.IsStorageConfigured)
        {
            throw new InvalidOperationException(
                "Azure Blob Storage is not configured. Set Azure:Storage:ConnectionString before saving knowledge.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new InvalidOperationException("Knowledge body is required.");
        }

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? "Captured ops knowledge"
            : request.Title.Trim();
        if (title.Length > 120)
        {
            title = title[..120].Trim();
        }

        var body = request.Body.Trim();
        if (body.Length > 20_000)
        {
            throw new InvalidOperationException("Knowledge body exceeds the 20 KB pilot limit.");
        }

        var conversationId = await EnsureKnowledgeConversationAsync(request.OrganizationId, cancellationToken);
        var toLabel = "Knowledge · org memory";
        var subject = $"Save to knowledge: {title}";

        var draft = new Draft
        {
            ConversationId = conversationId,
            CreatedByUserId = request.UserId,
            CreatedByAi = request.CreatedByAi,
            Body = body,
            Revision = 1,
            Status = "PendingApproval",
            MetadataJson = JsonSerializer.Serialize(new
            {
                kind = KnowledgeCaptureKinds.Save,
                to = toLabel,
                subject,
                title,
                sourceQuestion = string.IsNullOrWhiteSpace(request.SourceQuestion)
                    ? null
                    : request.SourceQuestion.Trim(),
                createdByAi = request.CreatedByAi
            })
        };

        _db.Add(draft);
        await _db.SaveChangesAsync(cancellationToken);

        var approval = await _approvals.CreateAsync(
            new CreateApprovalRequest(
                request.OrganizationId,
                request.UserId,
                draft.Id,
                draft.Revision,
                DateTimeOffset.UtcNow.AddDays(7)),
            request.UserId,
            cancellationToken);

        await _audit.WriteAsync(
            request.OrganizationId,
            "knowledge.capture_draft_created",
            request.UserId,
            nameof(Draft),
            draft.Id,
            JsonSerializer.Serialize(new { approval.Id, title, createdByAi = request.CreatedByAi }),
            cancellationToken);

        return new ReplyDraftResult(
            draft.Id,
            approval.Id,
            conversationId,
            toLabel,
            subject,
            draft.Body,
            approval.Status);
    }

    public async Task<ReplyDraftResult?> TryExecuteApprovedAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var approval = _db.ApprovalRequests.FirstOrDefault(a => a.Id == approvalId)
            ?? throw new InvalidOperationException($"Approval '{approvalId}' was not found.");

        if (approval.DraftId is null)
        {
            return null;
        }

        var draft = _db.Drafts.FirstOrDefault(d => d.Id == approval.DraftId.Value)
            ?? throw new InvalidOperationException("Draft was not found.");

        var meta = ParseMeta(draft.MetadataJson);
        if (!string.Equals(meta.Kind, KnowledgeCaptureKinds.Save, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(draft.Body))
        {
            throw new InvalidOperationException("Knowledge capture draft is missing a body.");
        }

        var organizationId = ResolveOrganizationId(userId, draft.ConversationId);
        var approved = await _approvals.ApproveAsync(approvalId, userId, cancellationToken);

        var idempotencyKey = $"knowledge-save:{approvalId}:{draft.Revision}";
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
                meta.To ?? string.Empty,
                meta.Subject ?? string.Empty,
                draft.Body,
                approved.Status);
        }

        WorkflowAction action;
        if (existingAction is null)
        {
            action = new WorkflowAction
            {
                OrganizationId = organizationId,
                ActionType = "KnowledgeSave",
                Status = ActionStatus.Running,
                RequestedByUserId = userId,
                ApprovalRequestId = approvalId,
                IdempotencyKey = idempotencyKey,
                PayloadJson = JsonSerializer.Serialize(new { draftId = draft.Id, title = meta.Title }),
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
            var title = string.IsNullOrWhiteSpace(meta.Title) ? "Captured ops knowledge" : meta.Title!;
            var markdown = BuildMarkdown(title, meta.SourceQuestion, draft.Body);
            var fileName = $"{Slugify(title)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.md";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
            var uploaded = await _knowledge.UploadAsync(
                organizationId,
                userId,
                fileName,
                "text/markdown",
                stream,
                title,
                cancellationToken);
            var first = uploaded.Results.FirstOrDefault()
                        ?? throw new InvalidOperationException("Knowledge upload returned no documents.");

            action.Status = ActionStatus.Completed;
            action.CompletedAt = DateTimeOffset.UtcNow;
            action.ResultJson = JsonSerializer.Serialize(new
            {
                posted = true,
                documentId = first.Document.Id,
                indexed = first.Indexed
            });
            draft.Status = "Posted";
            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                organizationId,
                "knowledge.capture_saved",
                userId,
                nameof(KnowledgeDocument),
                first.Document.Id,
                JsonSerializer.Serialize(new { approvalId, draft.Id, first.Indexed }),
                cancellationToken);

            return new ReplyDraftResult(
                draft.Id,
                approved.Id,
                draft.ConversationId,
                meta.To ?? string.Empty,
                meta.Subject ?? string.Empty,
                draft.Body,
                approved.Status);
        }
        catch
        {
            action.Status = ActionStatus.Failed;
            action.CompletedAt = DateTimeOffset.UtcNow;
            action.ResultJson = JsonSerializer.Serialize(new { posted = false });
            draft.Status = "PostFailed";
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<Guid> EnsureKnowledgeConversationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var existing = _db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.Channel == "Knowledge")
            .ToList()
            .FirstOrDefault(c =>
                string.Equals(c.Subject, "Knowledge capture", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing.Id;
        }

        var conversation = new Conversation
        {
            OrganizationId = organizationId,
            Channel = "Knowledge",
            Subject = "Knowledge capture",
            Status = ConversationStatus.Open
        };
        _db.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);
        return conversation.Id;
    }

    private Guid ResolveOrganizationId(Guid userId, Guid conversationId)
    {
        var fromUser = _db.Users.Where(u => u.Id == userId).Select(u => u.OrganizationId).FirstOrDefault();
        if (fromUser != Guid.Empty)
        {
            return fromUser;
        }

        return _db.Conversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.OrganizationId)
            .FirstOrDefault();
    }

    private static string BuildMarkdown(string title, string? question, string answer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"Captured: {DateTimeOffset.UtcNow:yyyy-MM-dd} UTC");
        sb.AppendLine("Source: Overview Ask (AI capture, human-approved)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(question))
        {
            sb.AppendLine("## Question");
            sb.AppendLine(question.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## Answer");
        sb.AppendLine(answer.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    private static string Slugify(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "knowledge";
        }

        return slug.Length <= 48 ? slug : slug[..48].Trim('-');
    }

    private static CaptureMeta ParseMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CaptureMeta(null, null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new CaptureMeta(
                root.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                root.TryGetProperty("to", out var to) ? to.GetString() : null,
                root.TryGetProperty("subject", out var subject) ? subject.GetString() : null,
                root.TryGetProperty("title", out var title) ? title.GetString() : null,
                root.TryGetProperty("sourceQuestion", out var q) ? q.GetString() : null);
        }
        catch
        {
            return new CaptureMeta(null, null, null, null, null);
        }
    }

    private sealed record CaptureMeta(
        string? Kind,
        string? To,
        string? Subject,
        string? Title,
        string? SourceQuestion);
}
