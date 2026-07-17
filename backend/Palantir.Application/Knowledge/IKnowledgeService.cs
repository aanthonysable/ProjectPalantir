namespace Palantir.Application.Knowledge;

public sealed record KnowledgeDocumentDto(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long ByteSize,
    string Status,
    string? IndexError,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeExcerptDto(
    Guid DocumentId,
    string Title,
    string FileName,
    int Ordinal,
    string Text,
    double Score);

public sealed record KnowledgeUploadResult(
    KnowledgeDocumentDto Document,
    bool Indexed);

public sealed record KnowledgeUploadBatchResult(
    IReadOnlyList<KnowledgeUploadResult> Results,
    int SkippedEntries,
    IReadOnlyList<string> Notes);

public interface IKnowledgeService
{
    bool IsStorageConfigured { get; }

    Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload a single text file or a .zip of text files. Zips are expanded and each
    /// indexable entry becomes its own knowledge document.
    /// </summary>
    Task<KnowledgeUploadBatchResult> UploadAsync(
        Guid organizationId,
        Guid userId,
        string fileName,
        string contentType,
        Stream content,
        string? title = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>Background worker entry point — indexes a previously uploaded Queued document.</summary>
    Task IndexQueuedDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeExcerptDto>> SearchAsync(
        Guid organizationId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default);
}
