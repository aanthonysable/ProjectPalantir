using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palantir.Application.Knowledge;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Infrastructure.Knowledge;

/// <summary>
/// Indexes knowledge documents after upload so the HTTP request can return as soon as the blob is stored.
/// </summary>
public sealed class KnowledgeIndexBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKnowledgeIndexQueue _queue;
    private readonly ILogger<KnowledgeIndexBackgroundService> _logger;

    public KnowledgeIndexBackgroundService(
        IServiceScopeFactory scopeFactory,
        IKnowledgeIndexQueue queue,
        ILogger<KnowledgeIndexBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingAsync(stoppingToken);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var knowledge = scope.ServiceProvider.GetRequiredService<IKnowledgeService>();
            var updated = await knowledge.BackfillSearchTagsAsync(stoppingToken);
            if (updated > 0)
            {
                _logger.LogInformation(
                    "Backfilled search tags / browse classification on {Count} knowledge document(s)",
                    updated);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not backfill knowledge search tags on startup");
        }

        await foreach (var job in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var knowledge = scope.ServiceProvider.GetRequiredService<IKnowledgeService>();
                await knowledge.IndexQueuedDocumentAsync(job.DocumentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Background knowledge indexing failed for document {DocumentId}",
                    job.DocumentId);
            }
        }
    }

    private async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PalantirDbContext>();
            var pending = await db.KnowledgeDocuments
                .AsNoTracking()
                .Where(d => d.Status == "Queued" || d.Status == "Indexing")
                .Select(d => new { d.OrganizationId, d.Id })
                .ToListAsync(cancellationToken);

            foreach (var doc in pending)
            {
                await _queue.EnqueueAsync(new KnowledgeIndexJob(doc.OrganizationId, doc.Id), cancellationToken);
            }

            if (pending.Count > 0)
            {
                _logger.LogInformation(
                    "Re-queued {Count} knowledge document(s) for background indexing",
                    pending.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recover pending knowledge indexing jobs on startup");
        }
    }
}
