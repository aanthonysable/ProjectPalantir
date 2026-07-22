using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Connectors;
using Palantir.Domain.Enums;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Infrastructure.Connectors;

/// <summary>
/// Periodically syncs every Connected Microsoft Graph mailbox into the unified inbox
/// so users do not need to click Sync Outlook.
/// </summary>
public sealed class OutlookInboxAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OutlookAutoSyncOptions> _options;
    private readonly ILogger<OutlookInboxAutoSyncBackgroundService> _logger;

    public OutlookInboxAutoSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<OutlookAutoSyncOptions> options,
        ILogger<OutlookInboxAutoSyncBackgroundService> logger)
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
            _logger.LogInformation("Outlook inbox auto-sync is disabled");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Clamp(opts.StartupDelaySeconds, 0, 300));
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
            "Outlook inbox auto-sync started (every {IntervalSeconds}s, top={Top})",
            Math.Clamp(opts.IntervalSeconds, 30, 3600),
            Math.Clamp(opts.Top, 1, 50));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllConnectedMailboxesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outlook inbox auto-sync loop failed");
            }

            var interval = TimeSpan.FromSeconds(Math.Clamp(opts.IntervalSeconds, 30, 3600));
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

    private async Task SyncAllConnectedMailboxesAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var minAge = TimeSpan.FromSeconds(Math.Clamp(opts.MinSecondsBetweenSyncs, 0, 3600));
        var top = Math.Clamp(opts.Top, 1, 50);
        var cutoff = DateTimeOffset.UtcNow - minAge;

        await using var listScope = _scopeFactory.CreateAsyncScope();
        var db = listScope.ServiceProvider.GetRequiredService<PalantirDbContext>();

        var targets = await (
            from account in db.ConnectedAccounts.AsNoTracking()
            join user in db.Users.AsNoTracking() on account.UserId equals user.Id
            where account.Provider == "MicrosoftGraph"
                  && account.ConnectionStatus == ConnectionStatus.Connected
                  && user.IsActive
                  && (account.LastSuccessfulSyncAt == null || account.LastSuccessfulSyncAt < cutoff)
            orderby account.LastSuccessfulSyncAt ?? DateTimeOffset.MinValue
            select new
            {
                account.Id,
                account.UserId,
                user.OrganizationId,
                account.PrimaryAddress
            }).ToListAsync(cancellationToken);

        if (targets.Count == 0)
        {
            _logger.LogDebug("Outlook auto-sync: no mailboxes due");
            return;
        }

        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<IOutlookInboxSyncService>();
                var result = await sync.SyncAsync(
                    target.Id,
                    target.UserId,
                    target.OrganizationId,
                    top,
                    cancellationToken);

                if (result.Imported > 0 || result.Skipped > 0)
                {
                    _logger.LogInformation(
                        "Outlook auto-sync {Address}: fetched={Fetched} imported={Imported} skipped={Skipped}",
                        target.PrimaryAddress ?? target.Id.ToString(),
                        result.Fetched,
                        result.Imported,
                        result.Skipped);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Outlook auto-sync failed for {Address} ({AccountId})",
                    target.PrimaryAddress ?? "(unknown)",
                    target.Id);
            }
        }
    }
}
