using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Customers;
using Palantir.Application.Overview;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Infrastructure.Overview;

/// <summary>
/// Periodically rebuilds shared org ops snapshots so Ask answers from the database
/// instead of every client waiting on live MaintainX / Monday / EZRentOut pulls.
/// </summary>
public sealed class OpsSnapshotRefreshBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OpsSnapshotOptions> _options;
    private readonly ILogger<OpsSnapshotRefreshBackgroundService> _logger;

    public OpsSnapshotRefreshBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<OpsSnapshotOptions> options,
        ILogger<OpsSnapshotRefreshBackgroundService> logger)
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
            _logger.LogInformation("Ops snapshot background refresh is disabled");
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAllOrganizationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ops snapshot refresh loop failed");
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(opts.RefreshIntervalMinutes, 1, 120));
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

    private async Task RefreshAllOrganizationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PalantirDbContext>();
        var orgs = await db.Organizations.AsNoTracking()
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        if (orgs.Count == 0)
        {
            return;
        }

        foreach (var orgId in orgs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RefreshOrganizationAsync(orgId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ops snapshot refresh failed for org {OrganizationId}", orgId);
            }
        }
    }

    private async Task RefreshOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOpsSnapshotStore>();
        var overview = scope.ServiceProvider.GetRequiredService<IOverviewService>();
        var db = scope.ServiceProvider.GetRequiredService<PalantirDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OpsSnapshotOptions>>().Value;

        if (await store.IsRefreshingAsync(organizationId, IOpsSnapshotStore.DefaultFocusKey, cancellationToken))
        {
            _logger.LogDebug(
                "Skipping ops snapshot refresh for org {OrganizationId} — already refreshing",
                organizationId);
            return;
        }

        var userId = await db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == organizationId)
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == Guid.Empty)
        {
            _logger.LogDebug(
                "No users for org {OrganizationId}; skipping ops snapshot refresh",
                organizationId);
            return;
        }

        await store.MarkRefreshingAsync(organizationId, IOpsSnapshotStore.DefaultFocusKey, cancellationToken);

        try
        {
            var focus = OpsSnapshotFocus.CreateDefault();
            var snapshot = await overview.GetSnapshotAsync(organizationId, userId, focus, cancellationToken);
            var notes = snapshot.Notes.ToList();
            notes.Insert(
                0,
                $"Shared DB ops snapshot generated {snapshot.GeneratedAt:u} " +
                $"(MaintainX / Monday / EZRentOut / inventory; reused across users until next refresh).");
            snapshot = snapshot with { Notes = notes };

            var ttl = TimeSpan.FromMinutes(Math.Clamp(options.TimeToLiveMinutes, 2, 240));
            await store.UpsertReadyAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                snapshot,
                ttl,
                cancellationToken);

            _logger.LogInformation(
                "Ops snapshot refreshed for org {OrganizationId} at {GeneratedAt:u} " +
                "(physicalOpen={Physical}, quotes={Quotes}, ezOrders={Orders})",
                organizationId,
                snapshot.GeneratedAt,
                snapshot.Counts.ExternalOpenWork,
                snapshot.QuotesSample.Count,
                snapshot.EzRentOrders.Count);

            // Persist customer CRM activity into SQL for all users (shared cache).
            try
            {
                var customers = scope.ServiceProvider.GetRequiredService<ICustomerService>();
                var warmed = await customers.WarmFromSnapshotAsync(organizationId, cancellationToken);
                if (warmed > 0)
                {
                    _logger.LogInformation(
                        "Persisted CRM activity for {Count} customer(s) in org {OrganizationId}",
                        warmed,
                        organizationId);
                }
            }
            catch (Exception warmEx)
            {
                _logger.LogWarning(
                    warmEx,
                    "Customer CRM warm from snapshot failed for org {OrganizationId}",
                    organizationId);
            }
        }
        catch (Exception ex)
        {
            await store.MarkFailedAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                ex.Message,
                cancellationToken);
            throw;
        }
    }
}
