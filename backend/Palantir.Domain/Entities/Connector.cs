namespace Palantir.Domain.Entities;

public class Connector
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string ConnectorType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Offline";
    public string CapabilitiesJson { get; set; } = "[]";
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public Organization? Organization { get; set; }
}
