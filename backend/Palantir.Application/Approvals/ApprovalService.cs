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
        var items = _db.ApprovalRequests
            .Where(a => a.RequestedForUserId == userId)
            .ToList()
            .OrderByDescending(a => a.RequestedAt)
            .Select(Map)
            .ToList();

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

        return Map(approval);
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

        return Map(approval);
    }

    private static ApprovalDto Map(ApprovalRequest a) =>
        new(a.Id, a.DraftId, a.RequestedForUserId, a.Status, a.RequestedAt, a.CompletedAt, a.CompletedByUserId);
}
