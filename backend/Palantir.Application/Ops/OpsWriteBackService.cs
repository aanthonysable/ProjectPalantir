using System.Text.Json;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Approvals;
using Palantir.Application.Audit;
using Palantir.Application.Connectors;
using Palantir.Application.Outbound;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Ops;

public sealed class OpsWriteBackService : IOpsWriteBackService
{
    private readonly IPalantirDbContext _db;
    private readonly IApprovalService _approvals;
    private readonly IMaintainXConnector _maintainX;
    private readonly IMondayConnector _monday;
    private readonly MaintainXOptions _maintainXOptions;
    private readonly IAuditEventWriter _audit;

    public OpsWriteBackService(
        IPalantirDbContext db,
        IApprovalService approvals,
        IMaintainXConnector maintainX,
        IMondayConnector monday,
        IOptions<MaintainXOptions> maintainXOptions,
        IAuditEventWriter audit)
    {
        _db = db;
        _approvals = approvals;
        _maintainX = maintainX;
        _monday = monday;
        _maintainXOptions = maintainXOptions.Value;
        _audit = audit;
    }

    public async Task<ReplyDraftResult> ProposeAsync(
        ProposeOpsWriteBackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new InvalidOperationException("Write-back body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ExternalId))
        {
            throw new InvalidOperationException("External item id is required.");
        }

        var source = request.SourceSystem.Trim();
        var kind = ResolveKind(source);
        var environmentName = string.IsNullOrWhiteSpace(request.EnvironmentName)
            ? null
            : request.EnvironmentName.Trim();

        string? maintainXOrganizationId = null;
        if (kind == OpsWriteBackKinds.MaintainXComment)
        {
            var environment = ResolveMaintainXEnvironment(environmentName, organizationId: null);
            environmentName = environment.Name;
            maintainXOrganizationId = environment.OrganizationId;
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? $"(#{request.ExternalId})" : request.Title.Trim();
        var toLabel = kind == OpsWriteBackKinds.MaintainXComment
            ? $"MaintainX · {environmentName ?? "org"} · WO {request.ExternalId}"
            : $"Monday · {environmentName ?? "board"} · #{request.ExternalId}";
        var subject = kind == OpsWriteBackKinds.MaintainXComment
            ? $"Comment on: {title}"
            : $"Update on: {title}";

        var conversationId = await EnsureOpsConversationAsync(request.OrganizationId, cancellationToken);

        var draft = new Draft
        {
            ConversationId = conversationId,
            CreatedByUserId = request.UserId,
            CreatedByAi = false,
            Body = request.Body.Trim(),
            Revision = 1,
            Status = "PendingApproval",
            MetadataJson = JsonSerializer.Serialize(new
            {
                kind,
                to = toLabel,
                subject,
                sourceSystem = source,
                environmentName,
                organizationId = maintainXOrganizationId,
                externalId = request.ExternalId.Trim(),
                title
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
                DateTimeOffset.UtcNow.AddDays(2)),
            request.UserId,
            cancellationToken);

        await _audit.WriteAsync(
            request.OrganizationId,
            kind == OpsWriteBackKinds.MaintainXComment
                ? "maintainx.comment_draft_created"
                : "monday.update_draft_created",
            request.UserId,
            nameof(Draft),
            draft.Id,
            JsonSerializer.Serialize(new
            {
                approvalId = approval.Id,
                draftId = draft.Id,
                kind,
                environmentName,
                externalId = request.ExternalId
            }),
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
        if (meta.Kind is not (OpsWriteBackKinds.MaintainXComment or OpsWriteBackKinds.MondayUpdate))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(meta.ExternalId) || string.IsNullOrWhiteSpace(draft.Body))
        {
            throw new InvalidOperationException("Ops write-back draft is missing target id or body.");
        }

        var organizationId = ResolveOrganizationId(userId, draft.ConversationId);
        var approved = await _approvals.ApproveAsync(approvalId, userId, cancellationToken);

        var actionType = meta.Kind == OpsWriteBackKinds.MaintainXComment
            ? "MaintainXComment"
            : "MondayUpdate";
        var idempotencyKey = $"ops-write:{meta.Kind}:{approvalId}:{draft.Revision}";
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
                ActionType = actionType,
                Status = ActionStatus.Running,
                RequestedByUserId = userId,
                ApprovalRequestId = approvalId,
                IdempotencyKey = idempotencyKey,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    draftId = draft.Id,
                    kind = meta.Kind,
                    environmentName = meta.EnvironmentName,
                    externalId = meta.ExternalId
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
            string remoteId;
            if (meta.Kind == OpsWriteBackKinds.MaintainXComment)
            {
                var environment = ResolveMaintainXEnvironment(meta.EnvironmentName, meta.OrganizationId);
                remoteId = await _maintainX.CreateWorkOrderCommentAsync(
                    environment,
                    meta.ExternalId!,
                    draft.Body,
                    cancellationToken);
            }
            else
            {
                remoteId = await _monday.CreateItemUpdateAsync(
                    meta.ExternalId!,
                    draft.Body,
                    cancellationToken);
            }

            action.Status = ActionStatus.Completed;
            action.CompletedAt = DateTimeOffset.UtcNow;
            action.ResultJson = JsonSerializer.Serialize(new { posted = true, remoteId });
            draft.Status = "Posted";
            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                organizationId,
                meta.Kind == OpsWriteBackKinds.MaintainXComment
                    ? "maintainx.comment_posted"
                    : "monday.update_created",
                userId,
                nameof(Draft),
                draft.Id,
                JsonSerializer.Serialize(new
                {
                    approvalId,
                    remoteId,
                    meta.ExternalId,
                    meta.EnvironmentName
                }),
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

    private async Task<Guid> EnsureOpsConversationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var existing = _db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.Channel == "Ops")
            .ToList()
            .FirstOrDefault(c =>
                string.Equals(c.Subject, "Ops write-back", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing.Id;
        }

        var conversation = new Conversation
        {
            OrganizationId = organizationId,
            Channel = "Ops",
            Subject = "Ops write-back",
            Status = ConversationStatus.Open
        };
        _db.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);
        return conversation.Id;
    }

    private MaintainXEnvironmentOptions ResolveMaintainXEnvironment(
        string? environmentName,
        string? organizationId)
    {
        var configured = _maintainXOptions.Environments
            .Where(e => !string.IsNullOrWhiteSpace(e.ApiKey))
            .ToList();

        if (configured.Count == 0)
        {
            throw new InvalidOperationException("No MaintainX environments are configured.");
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            var byOrg = configured.FirstOrDefault(e =>
                string.Equals(e.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase));
            if (byOrg is not null)
            {
                return byOrg;
            }
        }

        if (string.IsNullOrWhiteSpace(environmentName))
        {
            if (configured.Count == 1)
            {
                return configured[0];
            }

            throw new InvalidOperationException(
                "MaintainX environment name is required when multiple orgs are configured.");
        }

        var match = configured.FirstOrDefault(e =>
            string.Equals(e.Name, environmentName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        // Open-work rows use the human Name from options; tolerate partial matches.
        match = configured.FirstOrDefault(e =>
            environmentName.Contains(e.Name, StringComparison.OrdinalIgnoreCase) ||
            e.Name.Contains(environmentName, StringComparison.OrdinalIgnoreCase));

        return match
               ?? throw new InvalidOperationException(
                   $"MaintainX environment '{environmentName}' was not found.");
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

    private static string ResolveKind(string sourceSystem) =>
        sourceSystem.Equals("MaintainX", StringComparison.OrdinalIgnoreCase)
            ? OpsWriteBackKinds.MaintainXComment
            : sourceSystem.Equals("Monday", StringComparison.OrdinalIgnoreCase)
                ? OpsWriteBackKinds.MondayUpdate
                : throw new InvalidOperationException(
                    $"Write-back is not supported for '{sourceSystem}'. Use MaintainX or Monday.");

    private static OpsDraftMeta ParseMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new OpsDraftMeta(null, null, null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new OpsDraftMeta(
                root.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                root.TryGetProperty("to", out var to) ? to.GetString() : null,
                root.TryGetProperty("subject", out var subject) ? subject.GetString() : null,
                root.TryGetProperty("environmentName", out var env) ? env.GetString() : null,
                root.TryGetProperty("organizationId", out var org) ? org.GetString() : null,
                root.TryGetProperty("externalId", out var id) ? id.GetString() : null);
        }
        catch
        {
            return new OpsDraftMeta(null, null, null, null, null, null);
        }
    }

    private sealed record OpsDraftMeta(
        string? Kind,
        string? To,
        string? Subject,
        string? EnvironmentName,
        string? OrganizationId,
        string? ExternalId);
}
