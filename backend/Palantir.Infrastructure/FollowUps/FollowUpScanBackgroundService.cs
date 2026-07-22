using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Palantir.Application.FollowUps;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Infrastructure.FollowUps;

/// <summary>
/// Periodically scans inbox / WhatsApp / open work and creates follow-up tasks.
/// </summary>
public sealed class FollowUpScanBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<FollowUpScanOptions> _options;
    private readonly ILogger<FollowUpScanBackgroundService> _logger;

    public FollowUpScanBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<FollowUpScanOptions> options,
        ILogger<FollowUpScanBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation("Follow-up scan is disabled");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Clamp(opts.StartupDelaySeconds, 0, 600));
        if (startupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(startupDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogInformation(
            "Follow-up scan started (every {IntervalMinutes} min, lookback={LookbackHours}h)",
            Math.Clamp(opts.IntervalMinutes, 2, 180),
            Math.Clamp(opts.LookbackHours, 1, 24 * 14));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAllOrganizationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Follow-up scan loop failed");
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(opts.IntervalMinutes, 2, 180));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ScanAllOrganizationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PalantirDbContext>();
        var orgs = await db.Organizations.AsNoTracking()
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        foreach (var orgId in orgs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await using var orgScope = _scopeFactory.CreateAsyncScope();
                var scanner = orgScope.ServiceProvider.GetRequiredService<IFollowUpScanService>();
                var result = await scanner.ScanOrganizationAsync(orgId, cancellationToken);
                if (result.TasksCreated > 0 || result.Proposals > 0)
                {
                    _logger.LogInformation(
                        "Follow-up scan org {OrganizationId}: reviewed={Reviewed}, proposals={Proposals}, created={Created}",
                        orgId,
                        result.ConversationsReviewed,
                        result.Proposals,
                        result.TasksCreated);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Follow-up scan failed for org {OrganizationId}", orgId);
            }
        }
    }
}
