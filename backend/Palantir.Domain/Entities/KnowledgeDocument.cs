namespace Palantir.Domain.Entities;

public class KnowledgeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string BlobPath { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public string Status { get; set; } = "Uploaded";
    public string? IndexError { get; set; }
    public Guid? UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Organization? Organization { get; set; }
    public User? UploadedByUser { get; set; }
    public ICollection<KnowledgeChunk> Chunks { get; set; } = new List<KnowledgeChunk>();
}

public class KnowledgeChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public string Text { get; set; } = string.Empty;

    public KnowledgeDocument? Document { get; set; }
}
