using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Connectors;

namespace Palantir.Infrastructure.Connectors;

public sealed class MaintainXConnector : IMaintainXConnector
{
    private static readonly HashSet<string> OpenStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPEN", "IN_PROGRESS", "ON_HOLD", "IN PROGRESS"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MaintainXOptions _options;
    private readonly ILogger<MaintainXConnector> _logger;
    private readonly Dictionary<string, Dictionary<long, string>> _userNameCache = new();

    public MaintainXConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<MaintainXOptions> options,
        ILogger<MaintainXConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConnectorHealthDto> CheckHealthAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(environment.Name) ? "MaintainX" : environment.Name;
        if (string.IsNullOrWhiteSpace(environment.ApiKey))
        {
            return new ConnectorHealthDto("MaintainX", name, false, false, "ApiKey not configured", DateTimeOffset.UtcNow);
        }

        try
        {
            using var response = await SendAsync(environment, HttpMethod.Get, "workorders?limit=1", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ConnectorHealthDto(
                    "MaintainX",
                    name,
                    true,
                    false,
                    $"HTTP {(int)response.StatusCode}: {Truncate(body)}",
                    DateTimeOffset.UtcNow);
            }

            return new ConnectorHealthDto("MaintainX", name, true, true, "OK", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MaintainX health check failed for {Name}", name);
            return new ConnectorHealthDto("MaintainX", name, true, false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public async Task<IReadOnlyList<ExternalWorkItemDto>> ListOpenWorkAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken = default)
    {
        var workOrders = await FetchWorkOrdersAsync(environment, limit: 80, cancellationToken);
        var users = await GetUserNamesAsync(environment, cancellationToken);
        return workOrders
            .Where(wo =>
            {
                var status = ReadString(wo, "status");
                return string.IsNullOrWhiteSpace(status) || OpenStatuses.Contains(status);
            })
            .Select(wo => MapWorkOrder(environment, wo, users, kind: "open"))
            .ToList();
    }

    public async Task<IReadOnlyList<ExternalWorkItemDto>> ListRecentlyCompletedAsync(
        MaintainXEnvironmentOptions environment,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var workOrders = await FetchWorkOrdersAsync(environment, limit: 100, cancellationToken);
        var users = await GetUserNamesAsync(environment, cancellationToken);
        return workOrders
            .Where(wo =>
            {
                var status = ReadString(wo, "status");
                if (!string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var updated = ReadDate(wo, "updatedAt") ?? ReadDate(wo, "createdAt");
                return updated is not null && updated >= sinceUtc;
            })
            .Select(wo => MapWorkOrder(environment, wo, users, kind: "completed"))
            .OrderByDescending(i =>
                DateTimeOffset.TryParse(i.Metadata?.GetValueOrDefault("updatedAt"), out var at)
                    ? at
                    : DateTimeOffset.MinValue)
            .ToList();
    }

    private async Task<List<JsonElement>> FetchWorkOrdersAsync(
        MaintainXEnvironmentOptions environment,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environment.ApiKey))
        {
            return [];
        }

        using var response = await SendAsync(
            environment,
            HttpMethod.Get,
            $"workorders?limit={limit}&expand=assignees",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"MaintainX list failed HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("workOrders", out var workOrders) &&
            !doc.RootElement.TryGetProperty("workorders", out workOrders))
        {
            return [];
        }

        return workOrders.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<Dictionary<long, string>> GetUserNamesAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken)
    {
        var cacheKey = environment.OrganizationId ?? environment.Name;
        if (_userNameCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<long, string>();
        try
        {
            using var response = await SendAsync(environment, HttpMethod.Get, "users?limit=100", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc.RootElement.TryGetProperty("users", out var users))
                {
                    foreach (var user in users.EnumerateArray())
                    {
                        if (!user.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                        {
                            continue;
                        }

                        var first = ReadString(user, "firstName");
                        var last = ReadString(user, "lastName");
                        var name = $"{first} {last}".Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            map[id] = name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MaintainX user lookup failed for {Name}", environment.Name);
        }

        _userNameCache[cacheKey] = map;
        return map;
    }

    private static ExternalWorkItemDto MapWorkOrder(
        MaintainXEnvironmentOptions environment,
        JsonElement wo,
        IReadOnlyDictionary<long, string> users,
        string kind)
    {
        var status = ReadString(wo, "status");
        var id = wo.TryGetProperty("id", out var idEl) ? idEl.ToString() : Guid.NewGuid().ToString("N");
        var title = ReadString(wo, "title") ?? "(untitled)";
        var sequential = wo.TryGetProperty("sequentialId", out var seqEl) ? seqEl.ToString() : null;
        var assignee = ResolveAssignees(wo, users);
        var due = ReadDate(wo, "dueDate");
        var updated = ReadDate(wo, "updatedAt");
        var area = InferArea(environment.Name, title);
        var url = $"https://app.getmaintainx.com/workorders/{id}";
        var description = ReadString(wo, "description") ?? "";
        if (description.Length > 400)
        {
            description = description[..400] + "…";
        }

        var categories = "";
        if (wo.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            categories = string.Join(", ", cats.EnumerateArray()
                .Select(c => c.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return new ExternalWorkItemDto(
            "MaintainX",
            environment.Name,
            id,
            title,
            DescribeMaintainXStatus(status),
            assignee,
            due,
            url,
            new Dictionary<string, string>
            {
                ["rawStatus"] = status ?? "",
                ["statusMeaning"] = MaintainXStatusMeaning(status),
                ["kind"] = kind,
                ["area"] = area,
                ["sequentialId"] = sequential ?? "",
                ["updatedAt"] = updated?.ToString("u") ?? "",
                ["completedAt"] = kind == "completed" ? (updated?.ToString("u") ?? "") : "",
                ["priority"] = ReadString(wo, "priority") ?? "",
                ["description"] = description,
                ["categories"] = categories
            });
    }

    private static string ResolveAssignees(JsonElement wo, IReadOnlyDictionary<long, string> users)
    {
        if (!wo.TryGetProperty("assignees", out var assignees) || assignees.ValueKind != JsonValueKind.Array)
        {
            return "Unassigned";
        }

        var names = new List<string>();
        foreach (var a in assignees.EnumerateArray())
        {
            var type = ReadString(a, "type");
            if (!string.Equals(type, "USER", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (a.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id) &&
                users.TryGetValue(id, out var mapped))
            {
                names.Add(mapped);
                continue;
            }

            // Prefer embedded name fields when the user directory miss happens.
            var first = ReadString(a, "firstName") ?? ReadString(a, "firstname");
            var last = ReadString(a, "lastName") ?? ReadString(a, "lastname");
            var embedded = $"{first} {last}".Trim();
            if (string.IsNullOrWhiteSpace(embedded))
            {
                embedded = ReadString(a, "name") ?? ReadString(a, "displayName");
            }

            if (!string.IsNullOrWhiteSpace(embedded))
            {
                names.Add(embedded);
            }
        }

        return names.Count == 0 ? "Unassigned" : string.Join(", ", names.Distinct());
    }

    private static string InferArea(string? environmentName, string title)
    {
        var env = environmentName ?? "";
        var region = env.Contains("Northern", StringComparison.OrdinalIgnoreCase) ? "Northern"
            : env.Contains("Permian", StringComparison.OrdinalIgnoreCase) ? "Permian"
            : env;

        if (LooksLikeShopWork(title))
        {
            return $"{region} · Shop";
        }

        return region;
    }

    private static bool LooksLikeShopWork(string title)
    {
        return title.Contains("TEST & PREP", StringComparison.OrdinalIgnoreCase)
               || title.Contains("NO CUSTOMER OWNED", StringComparison.OrdinalIgnoreCase)
               || title.Contains("SHOP", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DescribeMaintainXStatus(string? status)
    {
        var normalized = status?.Trim().ToUpperInvariant().Replace(' ', '_');
        return normalized switch
        {
            "OPEN" => "Open",
            "IN_PROGRESS" => "In progress",
            "ON_HOLD" => "On hold",
            "DONE" => "Done",
            _ => HumanizeStatus(status),
        };
    }

    private static string HumanizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "Unknown";
        var words = status
            .Trim()
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(
            ' ',
            words.Select(w =>
                w.Length == 0
                    ? w
                    : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    internal static string MaintainXStatusMeaning(string? status) =>
        status?.Trim().ToUpperInvariant().Replace(' ', '_') switch
        {
            "OPEN" => "Has not started yet",
            "IN_PROGRESS" => "Being worked on",
            "ON_HOLD" => "Physical work finished; back office can close when ready",
            "DONE" => "Back office has processed for billing; ticket fully closed",
            _ => "Unknown status"
        };

    public async Task<IReadOnlyList<string>> ListWorkOrderCommentSnippetsAsync(
        MaintainXEnvironmentOptions environment,
        string workOrderId,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment.ApiKey) || string.IsNullOrWhiteSpace(workOrderId))
        {
            return [];
        }

        var users = await GetUserNamesAsync(environment, cancellationToken);
        using var response = await SendAsync(
            environment,
            HttpMethod.Get,
            $"workorders/{workOrderId}/comments?limit=30",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("comments", out var comments))
        {
            return [];
        }

        var snippets = new List<string>();
        foreach (var comment in comments.EnumerateArray().Reverse())
        {
            var content = ReadString(comment, "content");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            content = content.Replace('\n', ' ').Trim();
            if (content.Length > 220)
            {
                content = content[..220] + "…";
            }

            var author = "Someone";
            if (comment.TryGetProperty("authorId", out var authorEl) && authorEl.TryGetInt64(out var authorId) &&
                users.TryGetValue(authorId, out var name))
            {
                author = name;
            }

            var at = ReadDate(comment, "createdAt");
            var when = at?.ToString("yyyy-MM-dd") ?? "";
            snippets.Add($"{when} {author}: {content}".Trim());
            if (snippets.Count >= limit)
            {
                break;
            }
        }

        return snippets;
    }

    public async Task<string> CreateWorkOrderCommentAsync(
        MaintainXEnvironmentOptions environment,
        string workOrderId,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment.ApiKey))
        {
            throw new InvalidOperationException("MaintainX API key is not configured for this environment.");
        }

        if (string.IsNullOrWhiteSpace(workOrderId))
        {
            throw new InvalidOperationException("Work order id is required.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Comment content is required.");
        }

        var payload = JsonSerializer.Serialize(new { content = content.Trim() });
        using var response = await SendAsync(
            environment,
            HttpMethod.Post,
            $"workorders/{workOrderId.Trim()}/comments",
            cancellationToken,
            payload);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"MaintainX comment failed HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var idEl))
            {
                return idEl.ToString();
            }

            if (doc.RootElement.TryGetProperty("comment", out var comment) &&
                comment.TryGetProperty("id", out var commentId))
            {
                return commentId.ToString();
            }
        }
        catch
        {
            // fall through
        }

        return "ok";
    }

    public async Task<IReadOnlyList<InventoryAlertDto>> ListInventoryAlertsAsync(
        MaintainXEnvironmentOptions environment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment.ApiKey))
        {
            return [];
        }

        var alerts = new List<InventoryAlertDto>();
        string? cursor = null;
        for (var page = 0; page < 8; page++)
        {
            var path = "parts?limit=100";
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var response = await SendAsync(environment, HttpMethod.Get, path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"MaintainX parts failed HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var part in parts.EnumerateArray())
            {
                var alert = TryMapInventoryAlert(environment.Name ?? "MaintainX", part);
                if (alert is not null)
                {
                    alerts.Add(alert);
                }
            }

            cursor = doc.RootElement.TryGetProperty("nextCursor", out var c) &&
                     c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(cursor) || parts.GetArrayLength() == 0)
            {
                break;
            }
        }

        return alerts
            .OrderBy(a => a.Severity == "Out" ? 0 : 1)
            .ThenBy(a => a.AvailableQuantity)
            .ThenBy(a => a.Name)
            .ToList();
    }

    private static InventoryAlertDto? TryMapInventoryAlert(string environmentName, JsonElement part)
    {
        var available = ReadNumber(part, "availableQuantity");
        if (available is null && part.TryGetProperty("inStockQuantity", out _))
        {
            available = ReadNumber(part, "inStockQuantity");
        }

        available ??= 0;
        var minimum = ReadNumber(part, "minimumQuantity") ?? 0;

        // If no minimum is configured and qty is zero, treat as untracked (common in some orgs).
        string? severity = null;
        if (available <= 0 && minimum > 0)
        {
            severity = "Out";
        }
        else if (available <= 0 && minimum <= 0)
        {
            // Still surface clearly negative / oversold stock even without a min.
            if (available < 0)
            {
                severity = "Out";
            }
            else
            {
                return null;
            }
        }
        else if (minimum > 0 && available <= minimum)
        {
            severity = "Low";
        }
        else
        {
            return null;
        }

        var id = part.TryGetProperty("id", out var idEl) ? idEl.ToString() : Guid.NewGuid().ToString("N");
        var name = ReadString(part, "name") ?? "(unnamed part)";
        var area = ReadString(part, "area");
        var types = "";
        if (part.TryGetProperty("partTypes", out var pt) && pt.ValueKind == JsonValueKind.Array)
        {
            types = string.Join(", ", pt.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return new InventoryAlertDto(
            environmentName,
            id,
            name,
            severity,
            available.Value,
            minimum,
            area,
            string.IsNullOrWhiteSpace(types) ? null : types);
    }

    private static double? ReadNumber(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String when double.TryParse(p.GetString(), out var d) => d,
            _ => null
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        MaintainXEnvironmentOptions environment,
        HttpMethod method,
        string relativeUrl,
        CancellationToken cancellationToken,
        string? jsonBody = null)
    {
        var client = _httpClientFactory.CreateClient("maintainx");
        var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", environment.ApiKey);
        if (!string.IsNullOrWhiteSpace(environment.OrganizationId))
        {
            request.Headers.TryAddWithoutValidation("X-Organization-Id", environment.OrganizationId);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static DateTimeOffset? ReadDate(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(p.GetString(), out var dto) ? dto : null;
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}

public sealed class EZRentOutConnector : IEZRentOutConnector
{
    private static readonly TimeSpan OpenWorkCacheTtl = TimeSpan.FromMinutes(3);
    private static readonly object OpenWorkCacheGate = new();
    private static IReadOnlyList<ExternalWorkItemDto>? OpenWorkCache;
    private static DateTimeOffset OpenWorkCacheExpiresAt;

    private static readonly TimeSpan OrdersCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly object OrdersCacheGate = new();
    private static IReadOnlyList<EzRentOrderDto>? OrdersCache;
    private static DateTimeOffset OrdersCacheExpiresAt;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EZRentOutOptions _options;
    private readonly ILogger<EZRentOutConnector> _logger;

    public EZRentOutConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<EZRentOutOptions> options,
        ILogger<EZRentOutConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConnectorHealthDto> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Subdomain) || string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return new ConnectorHealthDto(
                "EZRentOut",
                "EZRentOut",
                false,
                false,
                "Subdomain or ApiToken not configured",
                DateTimeOffset.UtcNow);
        }

        try
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                "assets/filter.api?status=checked_out&page=1",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ConnectorHealthDto(
                    "EZRentOut",
                    "EZRentOut",
                    true,
                    false,
                    $"HTTP {(int)response.StatusCode}: {Truncate(body)}",
                    DateTimeOffset.UtcNow);
            }

            return new ConnectorHealthDto("EZRentOut", "EZRentOut", true, true, "OK", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EZRentOut health check failed");
            return new ConnectorHealthDto("EZRentOut", "EZRentOut", true, false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public async Task<IReadOnlyList<ExternalWorkItemDto>> ListOpenWorkAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Subdomain) || string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return [];
        }

        lock (OpenWorkCacheGate)
        {
            if (OpenWorkCache is not null && OpenWorkCacheExpiresAt > DateTimeOffset.UtcNow)
            {
                return OpenWorkCache;
            }
        }

        try
        {
            var items = await FetchAllCheckedOutAsync(cancellationToken);
            var ordered = items
                .OrderBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
                .ThenBy(i => i.Assignee ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (OpenWorkCacheGate)
            {
                OpenWorkCache = ordered;
                OpenWorkCacheExpiresAt = DateTimeOffset.UtcNow.Add(OpenWorkCacheTtl);
            }

            return ordered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EZRentOut open-work list failed");
            lock (OpenWorkCacheGate)
            {
                if (OpenWorkCache is not null)
                {
                    return OpenWorkCache;
                }
            }

            return [];
        }
    }

    public async Task<IReadOnlyList<EzRentOrderDto>> ListOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Subdomain) || string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return [];
        }

        lock (OrdersCacheGate)
        {
            if (OrdersCache is not null && OrdersCacheExpiresAt > DateTimeOffset.UtcNow)
            {
                return OrdersCache;
            }
        }

        try
        {
            var lookback = DateTimeOffset.UtcNow.AddYears(-3);
            var filters = new[]
            {
                "filters[completed]=completed",
                "filters[state]=checked_out",
                "filters[state]=checkin_payment_pending",
            };

            var batches = await Task.WhenAll(
                filters.Select(f => FetchOrdersForFilterAsync(f, lookback, cancellationToken)));

            var byId = new Dictionary<string, EzRentOrderDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var order in batches.SelectMany(x => x))
            {
                byId[order.OrderId] = order;
            }

            var ordered = byId.Values
                .OrderByDescending(o => o.BillFrom ?? o.CompletedOn ?? o.CheckedOutOn ?? DateTimeOffset.MinValue)
                .ThenBy(o => o.Customer, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (OrdersCacheGate)
            {
                OrdersCache = ordered;
                OrdersCacheExpiresAt = DateTimeOffset.UtcNow.Add(OrdersCacheTtl);
            }

            _logger.LogInformation(
                "EZRentOut loaded {OrderCount} orders for revenue history (lookback {Lookback:u})",
                ordered.Count,
                lookback);
            return ordered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EZRentOut order list failed");
            lock (OrdersCacheGate)
            {
                if (OrdersCache is not null)
                {
                    return OrdersCache;
                }
            }

            return [];
        }
    }

    private async Task<List<EzRentOrderDto>> FetchOrdersForFilterAsync(
        string filterQuery,
        DateTimeOffset lookback,
        CancellationToken cancellationToken)
    {
        var items = new List<EzRentOrderDto>();
        var maxPages = 120;
        for (var page = 1; page <= maxPages; )
        {
            var batchPages = Enumerable.Range(page, Math.Min(6, maxPages - page + 1)).ToList();
            var pageResults = await Task.WhenAll(
                batchPages.Select(p => FetchOrdersPageAsync(filterQuery, p, cancellationToken)));

            var stop = false;
            foreach (var (pageNum, baskets) in batchPages.Zip(pageResults))
            {
                if (baskets.Count == 0)
                {
                    stop = true;
                    break;
                }

                items.AddRange(baskets);

                // Completed lists are newest-first; stop once a full page is older than lookback.
                var newestOnPage = baskets.Max(o =>
                    o.BillTo ?? o.BillFrom ?? o.CompletedOn ?? o.CheckedOutOn ?? DateTimeOffset.MinValue);
                if (baskets.Count < 25)
                {
                    stop = true;
                    break;
                }

                if (newestOnPage < lookback &&
                    filterQuery.Contains("completed", StringComparison.OrdinalIgnoreCase))
                {
                    stop = true;
                    break;
                }

                _ = pageNum;
            }

            if (stop)
            {
                break;
            }

            page += batchPages.Count;
        }

        return items;
    }

    private async Task<List<EzRentOrderDto>> FetchOrdersPageAsync(
        string filterQuery,
        int page,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"baskets.api?page={page}&{filterQuery}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "EZRentOut baskets page {Page} ({Filter}) failed: {Status} {Body}",
                page,
                filterQuery,
                (int)response.StatusCode,
                Truncate(body));
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("baskets", out var baskets) ||
            baskets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<EzRentOrderDto>();
        foreach (var basket in baskets.EnumerateArray())
        {
            var mapped = MapOrder(basket);
            if (mapped is not null)
            {
                items.Add(mapped);
            }
        }

        return items;
    }

    private static EzRentOrderDto? MapOrder(JsonElement basket)
    {
        var state = ReadAssetString(basket, "basket_state") ?? "";
        if (state.Equals("drafted", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("void", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = ReadAssetString(basket, "identification_number")
            ?? ReadAssetString(basket, "sequence_num")
            ?? Guid.NewGuid().ToString("N");
        var customer = "Unknown";
        if (basket.TryGetProperty("associated_customer", out var cust) &&
            cust.ValueKind == JsonValueKind.Object)
        {
            customer = ReadAssetString(cust, "name")?.Trim() is { Length: > 0 } name
                ? name
                : "Unknown";
        }

        var net = ReadAssetDecimal(basket, "net_amount") ?? 0m;
        var gross = ReadAssetDecimal(basket, "gross_amount") ?? net;

        return new EzRentOrderDto(
            id,
            customer,
            string.IsNullOrWhiteSpace(state) ? "unknown" : state,
            net,
            gross,
            ReadAssetDate(basket, "bill_from"),
            ReadAssetDate(basket, "bill_to"),
            ReadAssetDate(basket, "checked_out_on"),
            ReadAssetDate(basket, "completed_on"));
    }

    private async Task<List<ExternalWorkItemDto>> FetchAllCheckedOutAsync(CancellationToken cancellationToken)
    {
        var first = await FetchCheckedOutPageAsync(1, cancellationToken);
        var items = new List<ExternalWorkItemDto>(first.Items);
        if (first.TotalPages <= 1)
        {
            return items;
        }

        // Cap pages defensively; filter.api returns ~25 assets/page.
        var lastPage = Math.Min(first.TotalPages, 80);
        using var gate = new SemaphoreSlim(5);
        var tasks = new List<Task<(int Page, IReadOnlyList<ExternalWorkItemDto> Items)>>();
        for (var page = 2; page <= lastPage; page++)
        {
            var pageCopy = page;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var result = await FetchCheckedOutPageAsync(pageCopy, cancellationToken);
                    return (pageCopy, result.Items);
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        var pages = await Task.WhenAll(tasks);
        foreach (var page in pages.OrderBy(p => p.Page))
        {
            items.AddRange(page.Items);
        }

        return items;
    }

    private async Task<(int TotalPages, IReadOnlyList<ExternalWorkItemDto> Items)> FetchCheckedOutPageAsync(
        int page,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"assets/filter.api?status=checked_out&page={page}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "EZRentOut checked-out page {Page} failed: {Status} {Body}",
                page,
                (int)response.StatusCode,
                Truncate(body));
            return (page, []);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return (page, []);
        }

        var totalPages = doc.RootElement.TryGetProperty("total_pages", out var tp) &&
            tp.TryGetInt32(out var pages)
            ? Math.Max(pages, 1)
            : page;

        var items = new List<ExternalWorkItemDto>();
        foreach (var asset in assets.EnumerateArray())
        {
            var mapped = MapCheckedOutAsset(asset);
            if (mapped is not null)
            {
                items.Add(mapped);
            }
        }

        return (totalPages, items);
    }

    private static ExternalWorkItemDto? MapCheckedOutAsset(JsonElement asset)
    {
        var state = ReadAssetString(asset, "state");
        // filter.api already scopes to checked_out; keep a soft guard.
        if (!string.IsNullOrWhiteSpace(state) &&
            !string.Equals(state, "checked_out", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = ReadAssetString(asset, "identifier")
            ?? ReadAssetString(asset, "sequence_num")
            ?? Guid.NewGuid().ToString("N");
        var name = ReadAssetString(asset, "name") ?? "(unnamed asset)";
        var description = ReadAssetString(asset, "description");
        var title = string.IsNullOrWhiteSpace(description)
            ? name
            : $"{name} — {description}";
        var due = ReadAssetDate(asset, "checkin_due_on");
        var checkout = ReadAssetDate(asset, "checkout_on");
        var status = due.HasValue && due.Value < DateTimeOffset.UtcNow
            ? "Overdue return"
            : "Checked out";
        var customer = ReadAssetString(asset, "assigned_to_user_name")
            ?? ReadAssetString(asset, "sub_checked_out_to_full_name");
        var location = ReadAssetString(asset, "location_name");
        var daily = ReadRentalPrice(asset, "daily");
        var weekly = ReadRentalPrice(asset, "weekly");
        var monthly = ReadRentalPrice(asset, "monthly");
        var hourly = ReadRentalPrice(asset, "hourly");
        var rentCollected = ReadAssetDecimal(asset, "rent_collected");

        return new ExternalWorkItemDto(
            "EZRentOut",
            "EZRentOut",
            id,
            title,
            status,
            string.IsNullOrWhiteSpace(customer) ? null : customer.Trim(),
            due,
            null,
            new Dictionary<string, string>
            {
                ["rawState"] = state ?? "checked_out",
                ["checkoutOn"] = checkout?.ToString("u") ?? "",
                ["assetName"] = name,
                ["customer"] = string.IsNullOrWhiteSpace(customer) ? "" : customer.Trim(),
                ["location"] = location ?? "",
                ["dailyRate"] = FormatMoney(daily),
                ["weeklyRate"] = FormatMoney(weekly),
                ["monthlyRate"] = FormatMoney(monthly),
                ["hourlyRate"] = FormatMoney(hourly),
                ["dailyRateValue"] = daily?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                ["rentCollected"] = FormatMoney(rentCollected),
                ["rentCollectedValue"] = rentCollected?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
            });
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ezrentout");
        var baseUrl = _options.ResolveBaseUrl().TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativeUrl));
        request.Headers.TryAddWithoutValidation("token", _options.ApiToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static string? ReadAssetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static decimal? ReadRentalPrice(JsonElement asset, string period)
    {
        if (!asset.TryGetProperty("rental_prices", out var prices) ||
            prices.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadDecimal(prices, period);
    }

    private static decimal? ReadAssetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out _) ? ReadDecimal(el, name) : null;

    private static decimal? ReadDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetDecimal(out var n) => n,
            JsonValueKind.String when decimal.TryParse(
                prop.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }

    private static string FormatMoney(decimal? value) =>
        value is null ? "" : value.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadAssetDate(JsonElement el, string name)
    {
        var raw = ReadAssetString(el, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}

public sealed class MondayConnector : IMondayConnector
{
    /// <summary>Quote Status column indexes on Quotes board (color_mkwzq278).</summary>
    private static readonly int[] OpenQuoteStatusIndexes = [0, 4, 5, 6, 8]; // Sent, Ready to be Billed, Draft, Ready to be Closed, PENDING FINAL APPROVAL

    private static readonly int[] BilledQuoteStatusIndexes = [3]; // Billed

    private const string QuoteStatusColumnId = "color_mkwzq278";
    private const int ItemsPageSize = 100;
    private const int MaxOpenQuoteItems = 450;
    private const int MaxBilledQuoteItems = 250;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MondayOptions _options;
    private readonly ILogger<MondayConnector> _logger;

    public MondayConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<MondayOptions> options,
        ILogger<MondayConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConnectorHealthDto> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return new ConnectorHealthDto("Monday", "Monday.com", false, false, "ApiToken not configured", DateTimeOffset.UtcNow);
        }

        try
        {
            using var json = await PostGraphQlAsync("query { me { id name } }", cancellationToken);
            if (json.RootElement.TryGetProperty("errors", out var errors))
            {
                return new ConnectorHealthDto(
                    "Monday",
                    "Monday.com",
                    true,
                    false,
                    Truncate(errors.ToString()),
                    DateTimeOffset.UtcNow);
            }

            var name = json.RootElement.GetProperty("data").GetProperty("me").GetProperty("name").GetString();
            var scope = string.IsNullOrWhiteSpace(_options.WorkspaceName)
                ? "Monday.com"
                : $"Monday.com · {_options.WorkspaceName}";
            return new ConnectorHealthDto("Monday", scope, true, true, $"OK as {name}", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Monday health check failed");
            return new ConnectorHealthDto("Monday", "Monday.com", true, false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public async Task<IReadOnlyList<ExternalWorkItemDto>> ListOpenWorkAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return [];
        }

        var boardIds = await ResolveBoardIdsAsync(cancellationToken);
        if (boardIds.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var agingDays = Math.Max(1, _options.QuoteAgingDays);
        var byId = new Dictionary<string, ExternalWorkItemDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var boardId in boardIds)
        {
            var billedAtByItem = await LoadBilledAtByItemIdAsync(boardId, cancellationToken);

            foreach (var item in await FetchQuoteItemsAsync(
                         boardId,
                         OpenQuoteStatusIndexes,
                         MaxOpenQuoteItems,
                         cancellationToken))
            {
                var dto = MapQuoteItem(item, boardNameFallback: "Quotes", now, agingDays, billedAtByItem);
                if (dto is not null)
                {
                    byId[dto.ExternalId] = dto;
                }
            }

            foreach (var item in await FetchQuoteItemsAsync(
                         boardId,
                         BilledQuoteStatusIndexes,
                         MaxBilledQuoteItems,
                         cancellationToken))
            {
                var dto = MapQuoteItem(item, boardNameFallback: "Quotes", now, agingDays, billedAtByItem);
                if (dto is not null)
                {
                    byId[dto.ExternalId] = dto;
                }
            }
        }

        return byId.Values
            .OrderByDescending(q => q.Metadata?.GetValueOrDefault("bucket") == "billed")
            .ThenByDescending(q =>
                DateTimeOffset.TryParse(q.Metadata?.GetValueOrDefault("billedAt"), out var billedAt)
                    ? billedAt
                    : DateTimeOffset.MinValue)
            .ThenByDescending(q => int.TryParse(q.Metadata?.GetValueOrDefault("ageDays"), out var d) ? d : 0)
            .ToList();
    }

    private async Task<List<string>> ResolveBoardIdsAsync(CancellationToken cancellationToken)
    {
        var boardIds = _options.IncludedBoardIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToList();

        if (boardIds.Count > 0)
        {
            return boardIds;
        }

        if (!long.TryParse(_options.WorkspaceId, out var workspaceId))
        {
            throw new InvalidOperationException("Connectors:Monday:WorkspaceId must be a numeric workspace id.");
        }

        var discover = $$"""
            query {
              boards(workspace_ids: [{{workspaceId}}], limit: 50, state: active) {
                id
                name
              }
            }
            """;
        using var discovered = await PostGraphQlAsync(discover, cancellationToken);
        if (discovered.RootElement.TryGetProperty("data", out var ddata) &&
            ddata.TryGetProperty("boards", out var dboards))
        {
            foreach (var board in dboards.EnumerateArray())
            {
                var name = board.GetProperty("name").GetString() ?? "";
                if (_options.IncludedBoardNames.Any(n =>
                        string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    boardIds.Add(board.GetProperty("id").ToString());
                }
            }
        }

        return boardIds;
    }

    private async Task<List<JsonElement>> FetchQuoteItemsAsync(
        string boardId,
        IReadOnlyList<int> statusIndexes,
        int maxItems,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        var statusLiteral = string.Join(", ", statusIndexes);

        while (items.Count < maxItems)
        {
            string query;
            object? variables;
            if (cursor is null)
            {
                query = $$"""
                    query {
                      boards(ids: [{{boardId}}]) {
                        id
                        name
                        items_page(
                          limit: {{ItemsPageSize}},
                          query_params: {
                            rules: [{
                              column_id: "{{QuoteStatusColumnId}}",
                              compare_value: [{{statusLiteral}}],
                              operator: any_of
                            }]
                          }
                        ) {
                          cursor
                          items {
                            id
                            name
                            created_at
                            updated_at
                            column_values(ids: [
                              "color_mkwzq278",
                              "color_mkx11vkd",
                              "color_mkx0d9kz",
                              "multiple_person_mkwzczf7",
                              "link_mkwz6c7d",
                              "date_mkx0jjg9",
                              "text_mkwzxk51",
                              "color_mkx1xcf3",
                              "boolean_mkx14rhb",
                              "pulse_id_mkwzzpv1",
                              "long_text_mkwzm5c7",
                              "long_text_mkwzjbsb",
                              "text_mkx0w7k0",
                              "text_mkx5czc4",
                              "text_mkwz6emw",
                              "lookup_mkwz95eh",
                              "lookup_mkx4h3jj",
                              "board_relation_mkwz6m8s",
                              "board_relation_mkwztd18"
                            ]) {
                              id
                              text
                              value
                            }
                            subitems {
                              id
                              name
                              column_values(ids: [
                                "dropdown_mkwzk7cp",
                                "long_text_mkxv2f3d",
                                "numeric_mkwzdhvm",
                                "numeric_mkwzp3g",
                                "numeric_mkwz4a5m",
                                "numeric_mkx47b9n"
                              ]) {
                                id
                                text
                                value
                              }
                            }
                          }
                        }
                      }
                    }
                    """;
                variables = null;
            }
            else
            {
                query = $$"""
                    query ($cursor: String!) {
                      boards(ids: [{{boardId}}]) {
                        id
                        name
                        items_page(limit: {{ItemsPageSize}}, cursor: $cursor) {
                          cursor
                          items {
                            id
                            name
                            created_at
                            updated_at
                            column_values(ids: [
                              "color_mkwzq278",
                              "color_mkx11vkd",
                              "color_mkx0d9kz",
                              "multiple_person_mkwzczf7",
                              "link_mkwz6c7d",
                              "date_mkx0jjg9",
                              "text_mkwzxk51",
                              "color_mkx1xcf3",
                              "boolean_mkx14rhb",
                              "pulse_id_mkwzzpv1",
                              "long_text_mkwzm5c7",
                              "long_text_mkwzjbsb",
                              "text_mkx0w7k0",
                              "text_mkx5czc4",
                              "text_mkwz6emw",
                              "lookup_mkwz95eh",
                              "lookup_mkx4h3jj",
                              "board_relation_mkwz6m8s",
                              "board_relation_mkwztd18"
                            ]) {
                              id
                              text
                              value
                            }
                            subitems {
                              id
                              name
                              column_values(ids: [
                                "dropdown_mkwzk7cp",
                                "long_text_mkxv2f3d",
                                "numeric_mkwzdhvm",
                                "numeric_mkwzp3g",
                                "numeric_mkwz4a5m",
                                "numeric_mkx47b9n"
                              ]) {
                                id
                                text
                                value
                              }
                            }
                          }
                        }
                      }
                    }
                    """;
                variables = new { cursor };
            }

            using var json = await PostGraphQlAsync(query, variables, cancellationToken);
            if (!json.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("boards", out var boards) ||
                boards.GetArrayLength() == 0)
            {
                break;
            }

            var board = boards[0];
            var boardName = board.GetProperty("name").GetString() ?? "Board";
            if (boardName.StartsWith("Subitems of ", StringComparison.OrdinalIgnoreCase) ||
                IsExcludedBoard(boardName) ||
                (_options.IncludedBoardNames.Length > 0 &&
                 !_options.IncludedBoardNames.Any(n =>
                     string.Equals(n, boardName, StringComparison.OrdinalIgnoreCase))))
            {
                break;
            }

            if (!board.TryGetProperty("items_page", out var page) ||
                !page.TryGetProperty("items", out var boardItems))
            {
                break;
            }

            foreach (var item in boardItems.EnumerateArray())
            {
                // Clone into owned elements so the document can be disposed.
                items.Add(item.Clone());
                if (items.Count >= maxItems)
                {
                    break;
                }
            }

            cursor = page.TryGetProperty("cursor", out var cursorEl) &&
                     cursorEl.ValueKind == JsonValueKind.String
                ? cursorEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(cursor) || boardItems.GetArrayLength() == 0)
            {
                break;
            }
        }

        return items;
    }

    private async Task<Dictionary<string, DateTimeOffset>> LoadBilledAtByItemIdAsync(
        string boardId,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var end = DateTimeOffset.UtcNow.AddDays(1);
        var start = end.AddMonths(-14);

        // Chunk by month so activity_logs limit (1000) does not drop conversions.
        for (var cursor = StartOfUtcMonth(start); cursor < end; cursor = cursor.AddMonths(1))
        {
            var from = cursor.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var to = cursor.AddMonths(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var query = $$"""
                query {
                  boards(ids: [{{boardId}}]) {
                    activity_logs(
                      limit: 1000,
                      column_ids: ["{{QuoteStatusColumnId}}"],
                      from: "{{from}}",
                      to: "{{to}}"
                    ) {
                      data
                      created_at
                    }
                  }
                }
                """;

            using var json = await PostGraphQlAsync(query, cancellationToken);
            if (!json.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("boards", out var boards) ||
                boards.GetArrayLength() == 0 ||
                !boards[0].TryGetProperty("activity_logs", out var logs))
            {
                continue;
            }

            foreach (var log in logs.EnumerateArray())
            {
                if (!TryParseActivityCreatedAt(log, out var when))
                {
                    continue;
                }

                var rawData = log.TryGetProperty("data", out var dataEl) ? dataEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(rawData))
                {
                    continue;
                }

                try
                {
                    using var payload = JsonDocument.Parse(rawData);
                    var root = payload.RootElement;
                    if (!TryReadStatusLabel(root, "value", out var label) ||
                        !label.Equals("Billed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pulseId = root.TryGetProperty("pulse_id", out var pulseEl)
                        ? pulseEl.ToString()
                        : null;
                    if (string.IsNullOrWhiteSpace(pulseId))
                    {
                        continue;
                    }

                    // Keep the latest transition into Billed for each quote.
                    if (!map.TryGetValue(pulseId, out var existing) || when > existing)
                    {
                        map[pulseId] = when;
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed activity payloads.
                }
            }
        }

        return map;
    }

    private static DateTimeOffset StartOfUtcMonth(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static bool TryParseActivityCreatedAt(JsonElement log, out DateTimeOffset when)
    {
        when = default;
        if (!log.TryGetProperty("created_at", out var createdEl))
        {
            return false;
        }

        if (createdEl.ValueKind == JsonValueKind.String)
        {
            var text = createdEl.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microsLike) &&
                microsLike > 1_000_000_000_000)
            {
                // Monday activity created_at is unix seconds * 10_000_000.
                when = DateTimeOffset.FromUnixTimeMilliseconds(microsLike / 10_000);
                return true;
            }

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when);
        }

        if (createdEl.ValueKind == JsonValueKind.Number && createdEl.TryGetInt64(out var numeric) &&
            numeric > 1_000_000_000_000)
        {
            when = DateTimeOffset.FromUnixTimeMilliseconds(numeric / 10_000);
            return true;
        }

        return false;
    }

    private static bool TryReadStatusLabel(JsonElement root, string propertyName, out string label)
    {
        label = "";
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!value.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!labelEl.TryGetProperty("text", out var textEl))
        {
            return false;
        }

        label = textEl.GetString()?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(label);
    }

    private ExternalWorkItemDto? MapQuoteItem(
        JsonElement item,
        string boardNameFallback,
        DateTimeOffset now,
        int agingDays,
        IReadOnlyDictionary<string, DateTimeOffset> billedAtByItem)
    {
        var cols = ReadColumnTexts(item);
        var id = item.GetProperty("id").ToString();
        var title = item.GetProperty("name").GetString() ?? "(untitled)";
        var quoteStatus = cols.GetValueOrDefault("color_mkwzq278", "");
        var region = cols.GetValueOrDefault("color_mkx11vkd", "");
        var quoteType = cols.GetValueOrDefault("color_mkx0d9kz", "");
        var people = cols.GetValueOrDefault("multiple_person_mkwzczf7", "");
        var mxLink = cols.GetValueOrDefault("link_mkwz6c7d", "");
        var project = cols.GetValueOrDefault("text_mkwzxk51", "");
        var handling = cols.GetValueOrDefault("color_mkx1xcf3", "");
        var quoteNumber = cols.GetValueOrDefault("pulse_id_mkwzzpv1", "");
        var scope = cols.GetValueOrDefault("long_text_mkwzm5c7", "");
        var comments = cols.GetValueOrDefault("long_text_mkwzjbsb", "");
        var poNumber = cols.GetValueOrDefault("text_mkx0w7k0", "");
        var soNumber = cols.GetValueOrDefault("text_mkx5czc4", "");
        var sapInvoice = cols.GetValueOrDefault("text_mkwz6emw", "");
        var partsLabor = cols.GetValueOrDefault("lookup_mkwz95eh", "");
        var dayRate = cols.GetValueOrDefault("lookup_mkx4h3jj", "");
        var customer = cols.GetValueOrDefault("board_relation_mkwz6m8s", "");
        var contact = cols.GetValueOrDefault("board_relation_mkwztd18", "");
        var deadlineText = cols.GetValueOrDefault("date_mkx0jjg9", "");

        var created = DateTimeOffset.TryParse(
            item.TryGetProperty("created_at", out var cAt) ? cAt.GetString() : null,
            out var createdAt)
            ? createdAt
            : (DateTimeOffset?)null;
        var updated = DateTimeOffset.TryParse(
            item.TryGetProperty("updated_at", out var uAt) ? uAt.GetString() : null,
            out var updatedAt)
            ? updatedAt
            : (DateTimeOffset?)null;
        var ageDays = created is null ? 0 : (int)(now - created.Value).TotalDays;
        var isBilled = quoteStatus.Equals("Billed", StringComparison.OrdinalIgnoreCase);
        var aging = !isBilled &&
                    ageDays >= agingDays &&
                    (quoteStatus.Equals("Sent", StringComparison.OrdinalIgnoreCase) ||
                     quoteStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase) ||
                     string.IsNullOrWhiteSpace(quoteStatus));
        var mxWorkOrderId = ExtractMaintainXWorkOrderId(mxLink);
        var bucket = isBilled ? "billed"
            : !string.IsNullOrWhiteSpace(mxWorkOrderId) ? "linked_to_maintainx"
            : aging ? "aging"
            : quoteStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase) ? "draft_opportunity"
            : quoteStatus.Equals("Ready to be Billed", StringComparison.OrdinalIgnoreCase) ? "ready_to_bill"
            : "pipeline";

        DateTimeOffset? billedAt = null;
        if (billedAtByItem.TryGetValue(id, out var fromActivity))
        {
            billedAt = fromActivity;
        }
        else if (isBilled && updated is not null)
        {
            billedAt = updated;
        }

        var (subitemCount, subitemSummary, quoteTotal) = SummarizeQuoteSubitems(item);
        var boardName = boardNameFallback;

        var meta = new Dictionary<string, string>
        {
            ["boardName"] = boardName,
            ["quoteStatus"] = quoteStatus,
            ["region"] = region,
            ["quoteType"] = quoteType,
            ["project"] = project,
            ["handling"] = handling,
            ["ageDays"] = ageDays.ToString(),
            ["aging"] = aging ? "true" : "false",
            ["bucket"] = bucket,
            ["maintainXWorkOrderId"] = mxWorkOrderId ?? "",
            ["maintainXLink"] = mxLink,
            ["kind"] = "quote",
            ["quoteNumber"] = quoteNumber,
            ["scopeOfWork"] = Truncate(scope, 800),
            ["quoteComments"] = Truncate(comments, 500),
            ["poNumber"] = poNumber,
            ["soNumber"] = soNumber,
            ["sapInvoice"] = sapInvoice,
            ["partsLabor"] = Truncate(partsLabor, 400),
            ["dayRate"] = dayRate,
            ["customer"] = customer,
            ["contact"] = contact,
            ["deadline"] = deadlineText,
            ["updatedAt"] = updated?.ToString("o") ?? "",
            ["billedAt"] = billedAt?.ToString("o") ?? "",
            ["billedMonth"] = billedAt?.ToString("yyyy-MM") ?? "",
            ["billedDate"] = billedAt?.ToString("yyyy-MM-dd") ?? "",
            ["subitemCount"] = subitemCount.ToString(),
            ["subitemSummary"] = Truncate(subitemSummary, 2500),
            ["amount"] = quoteTotal > 0 ? quoteTotal.ToString("0.##") : "",
            ["amountText"] = quoteTotal > 0 ? $"${quoteTotal:0.##}" : ""
        };

        return new ExternalWorkItemDto(
            "Monday",
            $"{_options.WorkspaceName} · {boardName}",
            id,
            title,
            string.IsNullOrWhiteSpace(quoteStatus) ? "Quotes" : quoteStatus,
            string.IsNullOrWhiteSpace(people) ? null : people,
            created,
            string.IsNullOrWhiteSpace(mxLink) ? null : mxLink.Split(' ').LastOrDefault(),
            meta);
    }

    private static Dictionary<string, string> ReadColumnTexts(JsonElement item)
    {
        var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!item.TryGetProperty("column_values", out var columnValues))
        {
            return cols;
        }

        foreach (var cv in columnValues.EnumerateArray())
        {
            var cid = cv.GetProperty("id").GetString() ?? "";
            var text = cv.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text) &&
                cv.TryGetProperty("value", out var valueEl) &&
                valueEl.ValueKind == JsonValueKind.String)
            {
                text = valueEl.GetString() ?? "";
            }

            cols[cid] = text?.Trim() ?? "";
        }

        return cols;
    }

    private static (int Count, string Summary, decimal Total) SummarizeQuoteSubitems(JsonElement item)
    {
        if (!item.TryGetProperty("subitems", out var subitems) ||
            subitems.ValueKind != JsonValueKind.Array)
        {
            return (0, "", 0m);
        }

        var lines = new List<string>();
        decimal total = 0;
        var count = 0;
        foreach (var sub in subitems.EnumerateArray())
        {
            count++;
            var name = sub.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var cols = ReadColumnTexts(sub);
            var product = cols.GetValueOrDefault("dropdown_mkwzk7cp", "");
            var custom = cols.GetValueOrDefault("long_text_mkxv2f3d", "");
            var qtyText = cols.GetValueOrDefault("numeric_mkwzdhvm", "");
            var unitCostText = cols.GetValueOrDefault("numeric_mkwzp3g", "");
            var overrideText = cols.GetValueOrDefault("numeric_mkwz4a5m", "");
            var rentalText = cols.GetValueOrDefault("numeric_mkx47b9n", "");

            decimal.TryParse(qtyText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var qty);
            decimal.TryParse(unitCostText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var unitCost);
            decimal.TryParse(overrideText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var priceOverride);
            decimal.TryParse(rentalText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var rental);

            var unitPrice = priceOverride > 0 ? priceOverride : unitCost;
            var lineTotal = qty > 0 && unitPrice > 0 ? qty * unitPrice
                : unitPrice > 0 ? unitPrice
                : rental > 0 && qty > 0 ? qty * rental
                : rental;
            if (lineTotal > 0)
            {
                total += lineTotal;
            }

            var desc = !string.IsNullOrWhiteSpace(custom) ? custom
                : !string.IsNullOrWhiteSpace(product) ? product
                : name;
            var qtyBit = qty > 0 ? $" qty={qty.ToString("0.##")}" : "";
            var priceBit = unitPrice > 0 ? $" @{unitPrice.ToString("0.##")}" : "";
            var totalBit = lineTotal > 0 ? $" =${lineTotal.ToString("0.##")}" : "";
            var rentalBit = rental > 0 && lineTotal <= 0 ? $" rental={rental.ToString("0.##")}" : "";
            lines.Add($"{desc}{qtyBit}{priceBit}{totalBit}{rentalBit}");
        }

        return (count, string.Join(" || ", lines.Take(40)), total);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    public async Task<string> CreateItemUpdateAsync(
        string itemId,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            throw new InvalidOperationException("Monday API token is not configured.");
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InvalidOperationException("Monday item id is required.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Update body is required.");
        }

        const string mutation = """
            mutation ($itemId: ID!, $body: String!) {
              create_update (item_id: $itemId, body: $body) {
                id
              }
            }
            """;

        var variables = new { itemId = itemId.Trim(), body = body.Trim() };
        using var json = await PostGraphQlAsync(mutation, variables, cancellationToken);

        if (json.RootElement.TryGetProperty("errors", out var errors))
        {
            throw new InvalidOperationException($"Monday update failed: {Truncate(errors.ToString())}");
        }

        if (json.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("create_update", out var created) &&
            created.TryGetProperty("id", out var idEl))
        {
            return idEl.ToString();
        }

        throw new InvalidOperationException("Monday update succeeded but returned no update id.");
    }

    private static string? ExtractMaintainXWorkOrderId(string? linkText)
    {
        if (string.IsNullOrWhiteSpace(linkText))
        {
            return null;
        }

        // Examples: "258 - https://app.getmaintainx.com/workorders/105980671"
        var marker = "/workorders/";
        var idx = linkText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var rest = linkText[(idx + marker.Length)..];
        var id = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private bool IsExcludedBoard(string boardName) =>
        _options.ExcludedBoardNames.Any(excluded =>
            string.Equals(excluded, boardName, StringComparison.OrdinalIgnoreCase));

    private async Task<JsonDocument> PostGraphQlAsync(
        string query,
        CancellationToken cancellationToken) =>
        await PostGraphQlAsync(query, variables: (object?)null, cancellationToken);

    private async Task<JsonDocument> PostGraphQlAsync(
        string query,
        object? variables,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("monday");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApiUrl);
        request.Headers.TryAddWithoutValidation("Authorization", _options.ApiToken);
        request.Headers.TryAddWithoutValidation("API-Version", _options.ApiVersion);

        var payload = variables is null
            ? JsonSerializer.Serialize(new { query })
            : JsonSerializer.Serialize(new { query, variables });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Monday HTTP {(int)response.StatusCode}: {Truncate(raw)}");
        }

        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Monday returned non-JSON (HTTP {(int)response.StatusCode}): {Truncate(raw)}", ex);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}

public sealed class OpsConnectorHealthService : IOpsConnectorHealthService
{
    private readonly IMaintainXConnector _maintainX;
    private readonly IEZRentOutConnector _ezRentOut;
    private readonly IMondayConnector _monday;
    private readonly MaintainXOptions _maintainXOptions;

    public OpsConnectorHealthService(
        IMaintainXConnector maintainX,
        IEZRentOutConnector ezRentOut,
        IMondayConnector monday,
        IOptions<MaintainXOptions> maintainXOptions)
    {
        _maintainX = maintainX;
        _ezRentOut = ezRentOut;
        _monday = monday;
        _maintainXOptions = maintainXOptions.Value;
    }

    public async Task<IReadOnlyList<ConnectorHealthDto>> CheckAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<ConnectorHealthDto>();

        if (_maintainXOptions.Environments.Count == 0)
        {
            results.Add(new ConnectorHealthDto(
                "MaintainX",
                "MaintainX (no environments)",
                false,
                false,
                "No Environments configured",
                DateTimeOffset.UtcNow));
        }
        else
        {
            foreach (var env in _maintainXOptions.Environments)
            {
                results.Add(await _maintainX.CheckHealthAsync(env, cancellationToken));
            }
        }

        results.Add(await _ezRentOut.CheckHealthAsync(cancellationToken));
        results.Add(await _monday.CheckHealthAsync(cancellationToken));
        return results;
    }
}

/// <summary>Placeholder until SAP or Syteline is chosen.</summary>
public sealed class UnconfiguredAccountingConnector : IAccountingConnector
{
    public string ProviderName => "None";

    public Task<ConnectorHealthDto> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectorHealthDto(
            "Accounting",
            "SAP/Syteline",
            false,
            false,
            "Not selected — configure IAccountingConnector when ERP is chosen",
            DateTimeOffset.UtcNow));
}
