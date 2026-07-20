using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palantir.Application.Knowledge;

namespace Palantir.Infrastructure.Knowledge;

/// <summary>
/// Periodically hashes knowledge blobs and marks duplicate uploads (same content, any filename).
/// </summary>
public sealed class KnowledgeDuplicateScanBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KnowledgeDuplicateScanBackgroundService> _logger;

    public KnowledgeDuplicateScanBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<KnowledgeDuplicateScanBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var knowledge = scope.ServiceProvider.GetRequiredService<IKnowledgeService>();
                var result = await knowledge.ScanAndMarkDuplicatesAsync(stoppingToken);
                if (result.HashesComputed > 0 || result.DuplicatesMarked > 0)
                {
                    _logger.LogInformation(
                        "Knowledge duplicate scan: hashed {Hashed}, marked {Dupes} duplicate(s)",
                        result.HashesComputed,
                        result.DuplicatesMarked);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Knowledge duplicate scan failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
