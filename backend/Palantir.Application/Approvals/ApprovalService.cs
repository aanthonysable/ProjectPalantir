using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.Approvals;

public sealed class ApprovalService : IApprovalService
{
    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;

    public ApprovalService(IPalantirDbContext db, IAuditEventWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<IReadOnlyList<ApprovalDto>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var approvals = _db.ApprovalRequests
            .Where(a => a.RequestedForUserId == userId)
            .ToList()
            .OrderByDescending(a => a.RequestedAt)
            .ToList();

        var draftIds = approvals.Where(a => a.DraftId.HasValue).Select(a => a.DraftId!.Value).ToHashSet();
        var drafts = _db.Drafts.Where(d => draftIds.Contains(d.Id)).ToList()
            .ToDictionary(d => d.Id);

        var items = approvals.Select(a => Map(a, a.DraftId is Guid id && drafts.TryGetValue(id, out var draft) ? draft : null)).ToList();
        return Task.FromResult<IReadOnlyList<ApprovalDto>>(items);
    }

    public async Task<ApprovalDto> CreateAsync(
        CreateApprovalRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var approval = new ApprovalRequest
        {
            DraftId = request.DraftId,
            RequestedForUserId = request.RequestedForUserId,
            DraftRevision = request.DraftRevision,
            ExpiresAt = request.ExpiresAt,
            Status = ApprovalStatus.Pending
        };

        _db.Add(approval);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            request.OrganizationId,
            "approval.created",
            actorUserId,
            nameof(ApprovalRequest),
            approval.Id,
            JsonSerializer.Serialize(new { approval.DraftId, approval.RequestedForUserId }),
            cancellationToken);

        var draft = request.DraftId is Guid draftId
            ? _db.Drafts.FirstOrDefault(d => d.Id == draftId)
            : null;

        return Map(approval, draft);
    }

    public Task<ApprovalDto> ApproveAsync(Guid approvalId, Guid completedByUserId, CancellationToken cancellationToken = default) =>
        CompleteAsync(approvalId, ApprovalStatus.Approved, completedByUserId, cancellationToken);

    public Task<ApprovalDto> RejectAsync(Guid approvalId, Guid completedByUserId, CancellationToken cancellationToken = default) =>
        CompleteAsync(approvalId, ApprovalStatus.Rejected, completedByUserId, cancellationToken);

    private async Task<ApprovalDto> CompleteAsync(
        Guid approvalId,
        ApprovalStatus next,
        Guid completedByUserId,
        CancellationToken cancellationToken)
    {
        var approval = _db.ApprovalRequests.FirstOrDefault(a => a.Id == approvalId)
            ?? throw new InvalidOperationException($"Approval '{approvalId}' was not found.");

        approval.TransitionTo(next, completedByUserId);
        await _db.SaveChangesAsync(cancellationToken);

        var organizationId = _db.Users
            .Where(u => u.Id == approval.RequestedForUserId)
            .Select(u => u.OrganizationId)
            .FirstOrDefault();

        await _audit.WriteAsync(
            organizationId,
            next == ApprovalStatus.Approved ? "approval.approved" : "approval.rejected",
            completedByUserId,
            nameof(ApprovalRequest),
            approval.Id,
            cancellationToken: cancellationToken);

        var draft = approval.DraftId is Guid draftId
            ? _db.Drafts.FirstOrDefault(d => d.Id == draftId)
            : null;

        return Map(approval, draft);
    }

    private static ApprovalDto Map(ApprovalRequest a, Draft? draft)
    {
        string? subject = null;
        string? to = null;
        if (draft?.MetadataJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(draft.MetadataJson);
                if (doc.RootElement.TryGetProperty("subject", out var s))
                {
                    subject = s.GetString();
                }

                if (doc.RootElement.TryGetProperty("to", out var t))
                {
                    to = t.GetString();
                }
            }
            catch
            {
                // ignore malformed metadata
            }
        }

        return new ApprovalDto(
            a.Id,
            a.DraftId,
            a.RequestedForUserId,
            a.Status,
            a.RequestedAt,
            a.CompletedAt,
            a.CompletedByUserId,
            draft?.Body,
            subject,
            to,
            draft?.ConversationId);
    }
}
