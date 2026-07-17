namespace Palantir.Application.Abstractions;

public interface IBlobKnowledgeStore
{
    bool IsConfigured { get; }

    Task UploadAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string blobPath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default);
}
