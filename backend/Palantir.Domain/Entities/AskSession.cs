namespace Palantir.Domain.Entities;

/// <summary>Persisted Ask / ops assistant chat thread (ChatGPT-style history).</summary>
public class AskSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Organization? Organization { get; set; }
    public User? User { get; set; }
    public ICollection<AskMessage> Messages { get; set; } = new List<AskMessage>();
}

public class AskMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AskSession? Session { get; set; }
}
