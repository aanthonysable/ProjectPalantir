using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Palantir.Application.Overview;
using Palantir.Domain.Entities;
using Palantir.Infrastructure.Persistence;

namespace Palantir.Infrastructure.Overview;

public sealed class OpsSnapshotStore : IOpsSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly PalantirDbContext _db;
    private readonly ILogger<OpsSnapshotStore> _logger;

    public OpsSnapshotStore(PalantirDbContext db, ILogger<OpsSnapshotStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OverviewSnapshotDto?> TryGetFreshAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.OpsSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.FocusKey == focusKey,
                cancellationToken);

        if (row is null ||
            !string.Equals(row.Status, "Ready", StringComparison.OrdinalIgnoreCase) ||
            row.ExpiresAt <= DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(row.SnapshotJson))
        {
            return null;
        }

        return Deserialize(row.SnapshotJson, organizationId, focusKey);
    }

    public async Task<OverviewSnapshotDto?> TryGetLatestReadyAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default)
    {
        // Prefer an explicit Ready row, but while a refresh is in flight the row is marked
        // Refreshing and still holds the previous SnapshotJson — keep serving that.
        var row = await _db.OpsSnapshots.AsNoTracking()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.FocusKey == focusKey &&
                (x.Status == "Ready" || x.Status == "Refreshing") &&
                x.SnapshotJson != null &&
                x.SnapshotJson != "" &&
                x.SnapshotJson != "{}")
            .OrderByDescending(x => x.GeneratedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return Deserialize(row.SnapshotJson, organizationId, focusKey);
    }

    private OverviewSnapshotDto? Deserialize(string json, Guid organizationId, string focusKey)
    {
        try
        {
            return JsonSerializer.Deserialize<OverviewSnapshotDto>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialize ops snapshot for org {OrganizationId} focus {FocusKey}",
                organizationId,
                focusKey);
            return null;
        }
    }

    public async Task UpsertReadyAsync(
        Guid organizationId,
        string focusKey,
        OverviewSnapshotDto snapshot,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var row = await _db.OpsSnapshots
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.FocusKey == focusKey,
                cancellationToken);

        if (row is null)
        {
            row = new OpsSnapshot
            {
                OrganizationId = organizationId,
                FocusKey = focusKey,
            };
            _db.OpsSnapshots.Add(row);
        }

        row.SnapshotJson = json;
        row.GeneratedAt = snapshot.GeneratedAt == default ? now : snapshot.GeneratedAt;
        row.ExpiresAt = now.Add(timeToLive);
        row.UpdatedAt = now;
        row.Status = "Ready";
        row.Error = null;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkRefreshingAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _db.OpsSnapshots
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.FocusKey == focusKey,
                cancellationToken);

        if (row is null)
        {
            row = new OpsSnapshot
            {
                OrganizationId = organizationId,
                FocusKey = focusKey,
                SnapshotJson = "{}",
                GeneratedAt = now,
                ExpiresAt = now,
            };
            _db.OpsSnapshots.Add(row);
        }

        row.Status = "Refreshing";
        row.UpdatedAt = now;
        row.Error = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid organizationId,
        string focusKey,
        string error,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _db.OpsSnapshots
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.FocusKey == focusKey,
                cancellationToken);

        if (row is null)
        {
            row = new OpsSnapshot
            {
                OrganizationId = organizationId,
                FocusKey = focusKey,
                SnapshotJson = "{}",
                GeneratedAt = now,
                ExpiresAt = now,
            };
            _db.OpsSnapshots.Add(row);
        }

        row.Status = "Failed";
        row.UpdatedAt = now;
        row.Error = Truncate(error, 500);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsRefreshingAsync(
        Guid organizationId,
        string focusKey,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.OpsSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.FocusKey == focusKey,
                cancellationToken);

        if (row is null ||
            !string.Equals(row.Status, "Refreshing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Treat stuck Refreshing (>20 min) as not locked so another pass can recover.
        return row.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-20);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? value
        : value.Length <= max ? value
        : value[..max];
}
