namespace Palantir.Domain.Entities;

/// <summary>File attached to an inbox message (typically imported from Outlook/Graph).</summary>
public class MessageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Guid OrganizationId { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long ByteSize { get; set; }
    public bool IsInline { get; set; }

    /// <summary>Provider attachment id (e.g. Graph attachment id).</summary>
    public string? ProviderAttachmentId { get; set; }

    /// <summary>Blob path under the knowledge/mail container when content was stored.</summary>
    public string? BlobPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Message? Message { get; set; }
    public Organization? Organization { get; set; }
}
