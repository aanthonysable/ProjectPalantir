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
    /// <summary>Connected mailbox that sourced this thread (Outlook), when known.</summary>
    public Guid? SourceConnectedAccountId { get; set; }
    /// <summary>Work or Personal — copied from the source mailbox for inbox folders.</summary>
    public string? SourceMailboxKind { get; set; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public Guid? AssignedUserId { get; set; }
    public Guid? AssignedTeamId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when the thread has inbound activity not yet opened in Palantir.</summary>
    public bool IsUnread { get; set; }

    public Organization? Organization { get; set; }
    public Customer? Customer { get; set; }
    public Project? Project { get; set; }
    public User? AssignedUser { get; set; }
    public Team? AssignedTeam { get; set; }
    public ConnectedAccount? SourceConnectedAccount { get; set; }
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Draft> Drafts { get; set; } = new List<Draft>();
}
