namespace Palantir.Domain.Entities;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Direction { get; set; } = "Inbound";
    public Guid? SenderUserId { get; set; }
    public Guid? ContactId { get; set; }
    public string? Body { get; set; }
    public string? Summary { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderMetadataJson { get; set; }
    public bool IsInternalNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
    public User? SenderUser { get; set; }
    public Contact? Contact { get; set; }
}
