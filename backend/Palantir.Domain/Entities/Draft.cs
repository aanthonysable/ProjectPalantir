namespace Palantir.Domain.Entities;

public class Draft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public bool CreatedByAi { get; set; }
    public string Body { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public string Status { get; set; } = "Draft";
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
    public User? CreatedByUser { get; set; }
    public ICollection<ApprovalRequest> ApprovalRequests { get; set; } = new List<ApprovalRequest>();
}
