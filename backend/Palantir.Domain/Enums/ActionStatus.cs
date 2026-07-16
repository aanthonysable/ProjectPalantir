namespace Palantir.Domain.Enums;

public enum ActionStatus
{
    Pending = 0,
    Claimed = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Expired = 5,
    Cancelled = 6,
    Retried = 7,
    DeadLettered = 8
}
