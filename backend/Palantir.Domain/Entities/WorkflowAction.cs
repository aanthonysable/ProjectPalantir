using Palantir.Domain.Enums;

namespace Palantir.Domain.Entities;

public class WorkflowAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public ActionStatus Status { get; set; } = ActionStatus.Pending;
    public Guid? RequestedByUserId { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public Guid? ClaimedByConnectorId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Organization? Organization { get; set; }
    public User? RequestedByUser { get; set; }
    public ApprovalRequest? ApprovalRequest { get; set; }
}
