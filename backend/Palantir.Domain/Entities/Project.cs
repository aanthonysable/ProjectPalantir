namespace Palantir.Domain.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public Guid? OwnerUserId { get; set; }
    public string? MetadataJson { get; set; }

    public Organization? Organization { get; set; }
    public Customer? Customer { get; set; }
    public User? OwnerUser { get; set; }
}
