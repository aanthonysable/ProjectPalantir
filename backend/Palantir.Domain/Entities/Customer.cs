namespace Palantir.Domain.Entities;

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }

    public Organization? Organization { get; set; }
}
