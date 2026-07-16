using Palantir.Domain.Enums;

namespace Palantir.Domain.Workflow;

public static class ApprovalWorkflow
{
    private static readonly HashSet<(ApprovalStatus From, ApprovalStatus To)> Allowed =
    [
        (ApprovalStatus.Pending, ApprovalStatus.Approved),
        (ApprovalStatus.Pending, ApprovalStatus.Rejected),
        (ApprovalStatus.Pending, ApprovalStatus.Expired),
        (ApprovalStatus.Pending, ApprovalStatus.Cancelled)
    ];

    public static bool CanTransition(ApprovalStatus from, ApprovalStatus to) =>
        Allowed.Contains((from, to));

    public static ApprovalStatus Transition(ApprovalStatus from, ApprovalStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Cannot transition approval from '{from}' to '{to}'.");
        }

        return to;
    }
}
