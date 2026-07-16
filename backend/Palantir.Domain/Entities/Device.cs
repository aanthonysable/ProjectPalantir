namespace Palantir.Domain.Entities;

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? PushToken { get; set; }
    public bool IsActive { get; set; } = true;

    public User? User { get; set; }
}
