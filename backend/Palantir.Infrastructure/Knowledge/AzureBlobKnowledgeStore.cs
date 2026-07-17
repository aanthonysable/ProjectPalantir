using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Azure;

namespace Palantir.Infrastructure.Knowledge;

public sealed class AzureBlobKnowledgeStore : IBlobKnowledgeStore
{
    private readonly BlobContainerClient? _container;
    private readonly ILogger<AzureBlobKnowledgeStore> _logger;

    public AzureBlobKnowledgeStore(
        IOptions<AzureOptions> options,
        ILogger<AzureBlobKnowledgeStore> logger)
    {
        _logger = logger;
        var storage = options.Value.Storage;
        if (!storage.IsConfigured)
        {
            _container = null;
            return;
        }

        try
        {
            var service = new BlobServiceClient(storage.ConnectionString);
            _container = service.GetBlobContainerClient(storage.KnowledgeContainer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Azure Blob client for knowledge store");
            _container = null;
        }
    }

    public bool IsConfigured => _container is not null;

    public async Task UploadAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var container = RequireContainer();
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(blobPath);
        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            },
            cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var container = RequireContainer();
        var blob = container.GetBlobClient(blobPath);
        var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    public async Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var container = RequireContainer();
        await container.DeleteBlobIfExistsAsync(blobPath, cancellationToken: cancellationToken);
    }

    private BlobContainerClient RequireContainer() =>
        _container ?? throw new InvalidOperationException("Azure Blob Storage is not configured.");
}

/// <summary>Used when Azure storage secrets are absent so the app still starts.</summary>
public sealed class DisabledBlobKnowledgeStore : IBlobKnowledgeStore
{
    public bool IsConfigured => false;

    public Task UploadAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Azure Blob Storage is not configured.");

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Azure Blob Storage is not configured.");

    public Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
