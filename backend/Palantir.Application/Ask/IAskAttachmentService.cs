using Palantir.Application.Knowledge;

namespace Palantir.Application.Ask;

public sealed record AskAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long ByteSize,
    string ExtractStatus,
    int ExtractedChars,
    Guid? SessionId,
    Guid? KnowledgeDocumentId,
    DateTimeOffset CreatedAt);

public sealed record AskAttachmentPromoteResult(
    AskAttachmentDto Attachment,
    KnowledgeUploadResult? Knowledge);

public interface IAskAttachmentService
{
    Task<IReadOnlyList<AskAttachmentDto>> UploadAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        IReadOnlyList<(string FileName, string ContentType, Stream Content, long Length)> files,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AskAttachmentDto>> GetAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait until Queued/Extracting attachments finish (or timeout), then return prompt text.
    /// </summary>
    Task<IReadOnlyList<(AskAttachmentDto Meta, string Text)>> GetExtractedForPromptAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    Task BindToSessionAsync(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>Background worker — extract text after blob is stored.</summary>
    Task ExtractQueuedAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    Task<AskAttachmentPromoteResult> PromoteToKnowledgeAsync(
        Guid organizationId,
        Guid userId,
        Guid attachmentId,
        string? title = null,
        CancellationToken cancellationToken = default);
}
