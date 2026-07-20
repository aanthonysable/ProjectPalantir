namespace Palantir.Domain.Entities;

/// <summary>File attached to an Ask chat for review (optional promote to org knowledge).</summary>
public class AskAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long ByteSize { get; set; }

    /// <summary>Optional blob path under knowledge store (ask-attachments/…).</summary>
    public string? BlobPath { get; set; }

    /// <summary>Extracted text for the model (may be truncated).</summary>
    public string ExtractedText { get; set; } = string.Empty;

    /// <summary>Ready | Empty | Unsupported</summary>
    public string ExtractStatus { get; set; } = "Empty";

    public Guid? KnowledgeDocumentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Organization? Organization { get; set; }
    public User? User { get; set; }
    public AskSession? Session { get; set; }
}
