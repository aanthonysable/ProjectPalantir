using Palantir.Domain.Enums;
using Palantir.Domain.Workflow;

namespace Palantir.Domain.Entities;

public class ApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DraftId { get; set; }
    public Guid RequestedForUserId { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? DraftRevision { get; set; }

    public Draft? Draft { get; set; }
    public User? RequestedForUser { get; set; }
    public User? CompletedByUser { get; set; }

    public void TransitionTo(ApprovalStatus next, Guid? completedByUserId = null)
    {
        Status = ApprovalWorkflow.Transition(Status, next);
        if (next is ApprovalStatus.Approved or ApprovalStatus.Rejected or ApprovalStatus.Cancelled or ApprovalStatus.Expired)
        {
            CompletedAt = DateTimeOffset.UtcNow;
            CompletedByUserId = completedByUserId;
        }
    }
}
