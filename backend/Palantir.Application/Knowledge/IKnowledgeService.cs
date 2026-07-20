namespace Palantir.Application.Knowledge;

public sealed record KnowledgeDocumentDto(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long ByteSize,
    string Status,
    string? IndexError,
    string? Tags,
    string Collection,
    string? FolderPath,
    string? ContentHash,
    Guid? DuplicateOfDocumentId,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeLibraryDto(
    IReadOnlyList<KnowledgeCollectionDto> Collections,
    IReadOnlyList<KnowledgeDocumentDto> Documents);

public sealed record KnowledgeCollectionDto(
    string Name,
    int DocumentCount,
    IReadOnlyList<string> Folders);

public sealed record KnowledgeExcerptDto(
    Guid DocumentId,
    string Title,
    string FileName,
    int Ordinal,
    string Text,
    double Score,
    string? Tags = null);

public sealed record KnowledgeUploadResult(
    KnowledgeDocumentDto Document,
    bool Indexed);

public sealed record KnowledgeUploadBatchResult(
    IReadOnlyList<KnowledgeUploadResult> Results,
    int SkippedEntries,
    IReadOnlyList<string> Notes);

public sealed record KnowledgeDownloadDto(
    Stream Content,
    string FileName,
    string ContentType,
    string Title);

public sealed record KnowledgeDedupResult(
    int HashesComputed,
    int DuplicatesMarked);

public interface IKnowledgeService
{
    bool IsStorageConfigured { get; }

    Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>Browsable library grouped by auto-classified Collection / FolderPath.</summary>
    Task<KnowledgeLibraryDto> GetLibraryAsync(
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

    /// <summary>Stream the original uploaded blob for download (Ask / Admin).</summary>
    Task<KnowledgeDownloadDto> OpenDownloadAsync(
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>Background worker entry point — indexes a previously uploaded Queued document.</summary>
    Task IndexQueuedDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Derive/refresh Tags and browse Collection/FolderPath for docs without re-reading blobs.
    /// </summary>
    Task<int> BackfillSearchTagsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hash blobs missing ContentHash and mark newer same-hash uploads as Duplicate
    /// (keeps the oldest doc per org). Safe to run periodically.
    /// </summary>
    Task<KnowledgeDedupResult> ScanAndMarkDuplicatesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeExcerptDto>> SearchAsync(
        Guid organizationId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default);

    /// <summary>Indexed document titles for Ask catalog hints (filename leaf preferred).</summary>
    Task<IReadOnlyList<(Guid Id, string Title, string FileName, string? Tags)>> ListIndexedCatalogAsync(
        Guid organizationId,
        int limit = 40,
        CancellationToken cancellationToken = default);
}
