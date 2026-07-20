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
    /// <summary>Comma-separated lookup tags (filename path, title, headings) for Ask retrieval.</summary>
    public string? Tags { get; set; }
    /// <summary>Browsable pack/collection (often zip root or first path segment).</summary>
    public string Collection { get; set; } = "General";
    /// <summary>Folder path under Collection (no leaf filename). Null/empty = pack root.</summary>
    public string? FolderPath { get; set; }
    /// <summary>SHA-256 hex of blob bytes — used to detect duplicate uploads regardless of filename.</summary>
    public string? ContentHash { get; set; }
    /// <summary>When Status is Duplicate, points at the kept canonical document.</summary>
    public Guid? DuplicateOfDocumentId { get; set; }
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
