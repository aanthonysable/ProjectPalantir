using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palantir.Application.Ask;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Infrastructure.Ask;

/// <summary>
/// Extracts Ask attachment text after the HTTP upload returns (same pattern as knowledge indexing).
/// </summary>
public sealed class AskAttachmentExtractBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AskAttachmentExtractQueue _queue;
    private readonly ILogger<AskAttachmentExtractBackgroundService> _logger;

    public AskAttachmentExtractBackgroundService(
        IServiceScopeFactory scopeFactory,
        AskAttachmentExtractQueue queue,
        ILogger<AskAttachmentExtractBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingAsync(stoppingToken);

        await foreach (var job in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var attachments = scope.ServiceProvider.GetRequiredService<IAskAttachmentService>();
                await attachments.ExtractQueuedAsync(job.AttachmentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Background Ask attachment extract failed for {AttachmentId}",
                    job.AttachmentId);
            }
        }
    }

    private async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PalantirDbContext>();
            var pending = await db.AskAttachments
                .AsNoTracking()
                .Where(a => a.ExtractStatus == "Queued" || a.ExtractStatus == "Extracting")
                .Select(a => new { a.OrganizationId, a.Id })
                .ToListAsync(cancellationToken);

            foreach (var row in pending)
            {
                await _queue.EnqueueAsync(
                    new AskAttachmentExtractJob(row.OrganizationId, row.Id),
                    cancellationToken);
            }

            if (pending.Count > 0)
            {
                _logger.LogInformation(
                    "Re-queued {Count} Ask attachment(s) for background extract",
                    pending.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recover pending Ask attachment extracts on startup");
        }
    }
}
