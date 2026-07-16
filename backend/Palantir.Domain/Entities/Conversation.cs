using Palantir.Domain.Enums;

namespace Palantir.Domain.Entities;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? ProjectId { get; set; }
    public string? Subject { get; set; }
    public string Channel { get; set; } = "Internal";
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public Guid? AssignedUserId { get; set; }
    public Guid? AssignedTeamId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Organization? Organization { get; set; }
    public Customer? Customer { get; set; }
    public Project? Project { get; set; }
    public User? AssignedUser { get; set; }
    public Team? AssignedTeam { get; set; }
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Draft> Drafts { get; set; } = new List<Draft>();
}
