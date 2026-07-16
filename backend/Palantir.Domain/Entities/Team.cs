namespace Palantir.Domain.Entities;

public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;

    public Organization? Organization { get; set; }
}
