using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Overview;

namespace Palantir.Application.Connectors;

public interface IWhatsAppOpsWatchService
{
    Task<IReadOnlyList<WhatsAppGapDto>> ListGapsAsync(CancellationToken cancellationToken = default);

    Task<WhatsAppConversationOpsDto?> AnalyzeConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}

public sealed record WhatsAppGapDto(
    Guid ConversationId,
    string Subject,
    DateTimeOffset UpdatedAt,
    string MatchStatus,
    string LatestSnippet,
    IReadOnlyList<string> ExtractedHints,
    IReadOnlyList<WhatsAppOpsMatchDto> Matches);

public sealed record WhatsAppOpsMatchDto(
    string SourceSystem,
    string EnvironmentName,
    string ExternalId,
    string Title,
    string MatchMethod,
    string? Url = null);

public sealed record WhatsAppConversationOpsDto(
    Guid ConversationId,
    IReadOnlyList<WhatsAppMessageOpsDto> Messages);

public sealed record WhatsAppMessageOpsDto(
    Guid MessageId,
    IReadOnlyList<string> ExtractedHints,
    IReadOnlyList<WhatsAppOpsConnectorPillDto> Connectors);

public sealed record WhatsAppOpsCandidateDto(
    string ExternalId,
    string Title,
    string? Url,
    string MatchMethod,
    int Score,
    string Confidence);

public sealed record WhatsAppOpsConnectorPillDto(
    string SourceSystem,
    /// <summary>Matched | Possible | NoMatch</summary>
    string Status,
    string Label,
    string? Url,
    IReadOnlyList<WhatsAppOpsCandidateDto> Candidates);

public sealed class WhatsAppOpsWatchService : IWhatsAppOpsWatchService
{
    private static readonly string[] ConnectorSystems = ["MaintainX", "Monday", "EZRentOut"];

    private static readonly Regex WoNumber = new(
        @"\b(?:WO|work\s*order)[#:\s-]*(\d{2,8})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuoteNumber = new(
        @"\b(?:Q|quote)[#:\s-]*([A-Z0-9-]{2,24})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PoSoNumber = new(
        @"\b(PO|SO)[#:\s-]*([A-Z0-9-]{2,20})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "have", "been", "will", "can",
        "pick", "ship", "rent", "on", "off", "all", "job", "name", "pending", "requested",
        "by", "are", "was", "were", "has", "had", "not", "out", "new", "add", "adding",
        "need", "needs", "please", "today", "tomorrow", "until", "into", "onto", "just",
        "also", "only", "more", "some", "any", "get", "got", "let", "know", "yes", "no",
        "whatsapp", "group", "message", "sent", "server", "do", "does", "did", "we", "you",
        "they", "our", "your", "their", "a", "an", "to", "of", "in", "at", "or", "as",
        "is", "it", "be", "if", "so", "up", "me", "my", "us"
    };

    /// <summary>Equipment / rental shorthand often used in shop WhatsApp.</summary>
    private static readonly string[] AssetTypeTokens =
    [
        "pcp", "pump", "controller", "analyzer", "header", "modbus", "pit", "monitor",
        "antenna", "mp", "m/p", "fm", "chemical", "water", "rental", "stand", "panel",
        "sim", "dual", "flow", "meter"
    ];

    private readonly IPalantirDbContext _db;
    private readonly IMaintainXConnector _maintainX;
    private readonly IEZRentOutConnector _ezRentOut;
    private readonly IMondayConnector _monday;
    private readonly IOpsSnapshotStore _opsSnapshots;
    private readonly MaintainXOptions _maintainXOptions;
    private readonly MondayOptions _mondayOptions;
    private readonly EZRentOutOptions _ezRentOutOptions;
    private readonly ILogger<WhatsAppOpsWatchService> _logger;

    public WhatsAppOpsWatchService(
        IPalantirDbContext db,
        IMaintainXConnector maintainX,
        IEZRentOutConnector ezRentOut,
        IMondayConnector monday,
        IOpsSnapshotStore opsSnapshots,
        IOptions<MaintainXOptions> maintainXOptions,
        IOptions<MondayOptions> mondayOptions,
        IOptions<EZRentOutOptions> ezRentOutOptions,
        ILogger<WhatsAppOpsWatchService> logger)
    {
        _db = db;
        _maintainX = maintainX;
        _ezRentOut = ezRentOut;
        _monday = monday;
        _opsSnapshots = opsSnapshots;
        _maintainXOptions = maintainXOptions.Value;
        _mondayOptions = mondayOptions.Value;
        _ezRentOutOptions = ezRentOutOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WhatsAppGapDto>> ListGapsAsync(
        CancellationToken cancellationToken = default)
    {
        var orgId = _db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstOrDefault();
        var openWork = await LoadOpenWorkAsync(orgId, cancellationToken);

        var conversations = _db.Conversations
            .Where(c => c.Channel == "WhatsApp")
            .OrderByDescending(c => c.UpdatedAt)
            .Take(100)
            .ToList();

        var results = conversations
            .Select(c => BuildGap(c, openWork))
            .OrderBy(r => r.MatchStatus == "Unmatched" ? 0 : r.MatchStatus == "Partial" ? 1 : 2)
            .ThenByDescending(r => r.UpdatedAt)
            .ToList();

        return results;
    }

    public async Task<WhatsAppConversationOpsDto?> AnalyzeConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = _db.Conversations.FirstOrDefault(c =>
            c.Id == conversationId && c.Channel == "WhatsApp");
        if (conversation is null)
        {
            return null;
        }

        var openWork = await LoadOpenWorkAsync(conversation.OrganizationId, cancellationToken);
        var messages = _db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.CreatedAt)
            .Take(120)
            .ToList()
            .Where(m => !m.IsInternalNote && !IsPersistedAiSummary(m))
            .Take(80)
            .ToList();

        var perMessage = messages
            .Select(m => AnalyzeMessage(m.Id, m.Summary, m.Body, openWork))
            .ToList();

        return new WhatsAppConversationOpsDto(conversation.Id, perMessage);
    }

    private WhatsAppMessageOpsDto AnalyzeMessage(
        Guid messageId,
        string? summary,
        string? body,
        IReadOnlyList<ExternalWorkItemDto> openWork)
    {
        // Prefer body text for context; summary often starts with sender id noise.
        var haystack = string.IsNullOrWhiteSpace(body) ? summary ?? "" : body;
        var hints = ExtractHints(haystack, subject: null);
        var scored = ScoreOpenWork(hints, haystack, openWork);

        var connectors = ConnectorSystems
            .Select(system => BuildConnectorPill(system, scored))
            .ToList();

        var contextHints = ExtractContextHints(haystack);
        var allHints = hints.Concat(contextHints).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();

        return new WhatsAppMessageOpsDto(messageId, allHints, connectors);
    }

    private WhatsAppOpsConnectorPillDto BuildConnectorPill(
        string system,
        IReadOnlyList<(ExternalWorkItemDto Item, int Score, string Methods, string Confidence)> scored)
    {
        var candidates = scored
            .Where(s => string.Equals(s.Item.SourceSystem, system, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(s => new WhatsAppOpsCandidateDto(
                s.Item.ExternalId,
                s.Item.Title ?? "(untitled)",
                ResolveItemUrl(s.Item),
                s.Methods,
                s.Score,
                s.Confidence))
            .ToList();

        if (candidates.Count == 0)
        {
            return new WhatsAppOpsConnectorPillDto(
                system,
                "NoMatch",
                "No match",
                HomeUrl(system),
                []);
        }

        if (candidates.Count == 1 &&
            string.Equals(candidates[0].Confidence, "Exact", StringComparison.OrdinalIgnoreCase))
        {
            return new WhatsAppOpsConnectorPillDto(
                system,
                "Matched",
                ShortLabel(candidates[0].Title, candidates[0].ExternalId),
                candidates[0].Url ?? HomeUrl(system),
                candidates);
        }

        if (candidates.Count == 1)
        {
            var only = candidates[0];
            var unsure = string.Equals(only.Confidence, "Possible", StringComparison.OrdinalIgnoreCase);
            return new WhatsAppOpsConnectorPillDto(
                system,
                unsure ? "Possible" : "Matched",
                unsure
                    ? $"Possible: {ShortLabel(only.Title, only.ExternalId)}"
                    : ShortLabel(only.Title, only.ExternalId),
                only.Url ?? HomeUrl(system),
                candidates);
        }

        return new WhatsAppOpsConnectorPillDto(
            system,
            "Possible",
            $"{candidates.Count} possible — review",
            HomeUrl(system),
            candidates);
    }

    private async Task<List<ExternalWorkItemDto>> LoadOpenWorkAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var fromSnapshot = await TryLoadFromOpsSnapshotAsync(organizationId, cancellationToken);
        if (fromSnapshot is { Count: > 0 })
        {
            return fromSnapshot;
        }

        _logger.LogDebug(
            "WhatsApp ops matching: no fresh ops snapshot for org {OrganizationId}; falling back to live connector pulls",
            organizationId);

        var openWork = new List<ExternalWorkItemDto>();
        foreach (var env in _maintainXOptions.Environments.Where(e => !string.IsNullOrWhiteSpace(e.ApiKey)))
        {
            openWork.AddRange(await _maintainX.ListOpenWorkAsync(env, cancellationToken));
        }

        openWork.AddRange(await _ezRentOut.ListOpenWorkAsync(cancellationToken));
        openWork.AddRange(await _monday.ListOpenWorkAsync(cancellationToken));
        return openWork;
    }

    private async Task<List<ExternalWorkItemDto>?> TryLoadFromOpsSnapshotAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _opsSnapshots.TryGetFreshAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                cancellationToken);
            if (snapshot is null)
            {
                return null;
            }

            var items = new List<ExternalWorkItemDto>();
            items.AddRange(snapshot.ExternalWorkSample ?? []);
            items.AddRange(snapshot.QuotesSample ?? []);
            items.AddRange(snapshot.RecentlyCompleted ?? []);

            // Dedupe by system + environment + id.
            var deduped = items
                .GroupBy(
                    i => $"{i.SourceSystem}|{i.EnvironmentName}|{i.ExternalId}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return deduped.Count == 0 ? null : deduped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WhatsApp ops matching could not read ops snapshot");
            return null;
        }
    }

    private WhatsAppGapDto BuildGap(
        Domain.Entities.Conversation conversation,
        IReadOnlyList<ExternalWorkItemDto> openWork)
    {
        var messages = _db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.CreatedAt)
            .Take(120)
            .ToList()
            .Where(m => !m.IsInternalNote && !IsPersistedAiSummary(m))
            .Take(80)
            .ToList();

        var haystack = string.Join(
            '\n',
            messages.Select(m => $"{m.Summary}\n{m.Body}").Where(b => !string.IsNullOrWhiteSpace(b)));
        var hints = ExtractHints(haystack, conversation.Subject);
        var scored = ScoreOpenWork(hints, haystack, openWork);
        var matches = scored
            .Take(12)
            .Select(s => new WhatsAppOpsMatchDto(
                s.Item.SourceSystem,
                s.Item.EnvironmentName ?? s.Item.SourceSystem,
                s.Item.ExternalId,
                s.Item.Title ?? "(untitled)",
                s.Methods,
                ResolveItemUrl(s.Item)))
            .ToList();

        var status = matches.Count > 0
            ? "Linked"
            : hints.Count > 0
                ? "Partial"
                : "Unmatched";

        var latest = messages.FirstOrDefault();
        return new WhatsAppGapDto(
            conversation.Id,
            conversation.Subject ?? "(no subject)",
            conversation.UpdatedAt,
            status,
            latest?.Summary ?? latest?.Body ?? "",
            hints,
            matches);
    }

    private List<(ExternalWorkItemDto Item, int Score, string Methods, string Confidence)> ScoreOpenWork(
        IReadOnlyList<string> hints,
        string haystack,
        IReadOnlyList<ExternalWorkItemDto> openWork)
    {
        var compactHay = Compact(haystack);
        var messageTokens = Tokenize(haystack);
        var results = new List<(ExternalWorkItemDto Item, int Score, string Methods, string Confidence)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in openWork)
        {
            var key = $"{item.SourceSystem}:{item.EnvironmentName}:{item.ExternalId}";
            if (!seen.Add(key))
            {
                continue;
            }

            var (score, methods) = ScoreItem(item, hints, haystack, compactHay, messageTokens);
            if (score < 10)
            {
                continue;
            }

            var confidence = score >= 40 || methods.Any(m =>
                    m is "WorkOrderNumber" or "QuoteNumber" or "PoSoNumber" or "ExternalId" or "SequentialId")
                ? "Exact"
                : score >= 22
                    ? "Likely"
                    : "Possible";

            results.Add((item, score, string.Join("+", methods.Distinct()), confidence));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (int Score, List<string> Methods) ScoreItem(
        ExternalWorkItemDto item,
        IReadOnlyList<string> hints,
        string haystack,
        string compactHay,
        HashSet<string> messageTokens)
    {
        var methods = new List<string>();
        var score = 0;
        var meta = item.Metadata ?? new Dictionary<string, string>();

        foreach (var hint in hints)
        {
            if (hint.StartsWith("WO:", StringComparison.OrdinalIgnoreCase))
            {
                var num = hint[3..];
                if (string.Equals(item.ExternalId, num, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(meta.GetValueOrDefault("sequentialId"), num, StringComparison.OrdinalIgnoreCase) ||
                    (item.Title?.Contains(num, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    methods.Add("WorkOrderNumber");
                    score += 50;
                }
            }
            else if (hint.StartsWith("Quote:", StringComparison.OrdinalIgnoreCase))
            {
                var num = hint[6..];
                if (string.Equals(meta.GetValueOrDefault("quoteNumber"), num, StringComparison.OrdinalIgnoreCase) ||
                    (item.Title?.Contains(num, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    methods.Add("QuoteNumber");
                    score += 45;
                }
            }
            else if (hint.Contains(':'))
            {
                var value = hint[(hint.IndexOf(':') + 1)..];
                if (string.Equals(meta.GetValueOrDefault("poNumber"), value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(meta.GetValueOrDefault("soNumber"), value, StringComparison.OrdinalIgnoreCase))
                {
                    methods.Add("PoSoNumber");
                    score += 45;
                }
            }
        }

        var compactExternalId = Compact(item.ExternalId);
        if (compactExternalId.Length >= 6 &&
            compactHay.Contains(compactExternalId, StringComparison.Ordinal))
        {
            methods.Add("ExternalId");
            score += 40;
        }

        var sequential = meta.GetValueOrDefault("sequentialId");
        if (!string.IsNullOrWhiteSpace(sequential) &&
            Compact(sequential).Length >= 3 &&
            compactHay.Contains(Compact(sequential), StringComparison.Ordinal))
        {
            methods.Add("SequentialId");
            score += 35;
        }

        // Customer / assignee / contact parties mentioned in chat.
        foreach (var partyKey in new[] { "customer", "contact" })
        {
            var party = meta.GetValueOrDefault(partyKey);
            if (string.IsNullOrWhiteSpace(party))
            {
                continue;
            }

            foreach (var chunk in SplitPartyNames(party))
            {
                var compactParty = Compact(chunk);
                if (compactParty.Length < 4)
                {
                    continue;
                }

                if (compactHay.Contains(compactParty, StringComparison.Ordinal))
                {
                    methods.Add("CustomerName");
                    score += compactParty.Length >= 8 ? 28 : 18;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Assignee))
        {
            foreach (var chunk in SplitPartyNames(item.Assignee))
            {
                var compactParty = Compact(chunk);
                if (compactParty.Length >= 5 &&
                    compactHay.Contains(compactParty, StringComparison.Ordinal) &&
                    !methods.Contains("CustomerName"))
                {
                    methods.Add("CustomerName");
                    score += 16;
                }
            }
        }

        // Job / project / location context.
        foreach (var field in new[] { "project", "location", "area" })
        {
            var value = meta.GetValueOrDefault(field);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var compactValue = Compact(value);
            if (compactValue.Length >= 5 && compactHay.Contains(compactValue, StringComparison.Ordinal))
            {
                methods.Add(field == "project" ? "JobName" : "Location");
                score += field == "project" ? 24 : 14;
            }
            else
            {
                // Partial token overlap for multi-word job names ("triple crown").
                var overlap = Tokenize(value).Count(t => messageTokens.Contains(t) && t.Length >= 4);
                if (overlap >= 2)
                {
                    methods.Add(field == "project" ? "JobName" : "Location");
                    score += 12 + (overlap * 3);
                }
            }
        }

        var assetName = meta.GetValueOrDefault("assetName");
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            var compactAsset = Compact(assetName);
            if (compactAsset.Length >= 4 && compactHay.Contains(compactAsset, StringComparison.Ordinal))
            {
                methods.Add("AssetName");
                score += 22;
            }
        }

        // Title / description keyword overlap (asset types, equipment wording).
        var itemText = string.Join(
            ' ',
            new[]
            {
                item.Title,
                meta.GetValueOrDefault("description"),
                meta.GetValueOrDefault("scopeOfWork"),
                meta.GetValueOrDefault("assetName"),
                meta.GetValueOrDefault("categories")
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var itemTokens = Tokenize(itemText);
        var shared = messageTokens.Intersect(itemTokens, StringComparer.OrdinalIgnoreCase).ToList();
        if (shared.Count > 0)
        {
            var assetHits = shared.Count(t => AssetTypeTokens.Any(a =>
                string.Equals(Compact(a), t, StringComparison.OrdinalIgnoreCase) ||
                t.Contains(Compact(a), StringComparison.OrdinalIgnoreCase)));
            if (assetHits > 0)
            {
                methods.Add("AssetType");
                score += 10 + (assetHits * 6);
            }

            var meaningful = shared.Count(t => t.Length >= 5);
            if (meaningful >= 2)
            {
                methods.Add("ContextKeywords");
                score += Math.Min(18, meaningful * 4);
            }
            else if (meaningful == 1 && score < 10)
            {
                methods.Add("ContextKeywords");
                score += 8;
            }
        }

        // Quantity + type phrases like "10 pcp" / "12\" fm" boost EZRentOut/asset items.
        if (Regex.IsMatch(haystack, @"\b\d+\s*(pcp|m/?p|fm|antenna|analy[sz]er)\b", RegexOptions.IgnoreCase) &&
            itemTokens.Overlaps(messageTokens.Where(t => AssetTypeTokens.Select(Compact).Contains(t))))
        {
            methods.Add("QtyAssetType");
            score += 10;
        }

        return (score, methods);
    }

    private static IEnumerable<string> SplitPartyNames(string party)
    {
        foreach (var part in party.Split([',', ';', '/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length >= 3)
            {
                yield return part;
            }
        }
    }

    private static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0)
            {
                return;
            }

            var token = sb.ToString().ToLowerInvariant();
            sb.Clear();
            if (token.Length < 3 || StopWords.Contains(token))
            {
                return;
            }

            set.Add(token);
            var compact = Compact(token);
            if (compact.Length >= 3)
            {
                set.Add(compact);
            }
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is '/' or '-')
            {
                sb.Append(ch);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return set;
    }

    private static List<string> ExtractContextHints(string haystack)
    {
        var hints = new List<string>();
        var tokens = Tokenize(haystack)
            .Where(t => t.Length >= 4 && !t.All(char.IsDigit))
            .Take(12);
        foreach (var t in tokens)
        {
            hints.Add($"ctx:{t}");
        }

        foreach (var asset in AssetTypeTokens)
        {
            if (Compact(haystack).Contains(Compact(asset), StringComparison.Ordinal))
            {
                hints.Add($"asset:{asset}");
            }
        }

        return hints.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
    }

    private string? ResolveItemUrl(ExternalWorkItemDto item)
    {
        if (string.Equals(item.SourceSystem, "MaintainX", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(item.Url)
                ? item.Url
                : $"https://app.getmaintainx.com/workorders/{item.ExternalId}";
        }

        if (string.Equals(item.SourceSystem, "Monday", StringComparison.OrdinalIgnoreCase))
        {
            var boardId = item.Metadata?.GetValueOrDefault("boardId");
            if (string.IsNullOrWhiteSpace(boardId))
            {
                boardId = _mondayOptions.IncludedBoardIds.FirstOrDefault();
            }

            if (!string.IsNullOrWhiteSpace(boardId) && !string.IsNullOrWhiteSpace(item.ExternalId))
            {
                return $"https://view.monday.com/boards/{boardId}/pulses/{item.ExternalId}";
            }

            return HomeUrl("Monday");
        }

        if (string.Equals(item.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = _ezRentOutOptions.ResolveBaseUrl().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(item.ExternalId))
            {
                return $"{baseUrl}/#/assets/{Uri.EscapeDataString(item.ExternalId)}";
            }

            return baseUrl;
        }

        return item.Url ?? HomeUrl(item.SourceSystem);
    }

    private string HomeUrl(string sourceSystem) =>
        sourceSystem.ToLowerInvariant() switch
        {
            "maintainx" => "https://app.getmaintainx.com/",
            "monday" => _mondayOptions.IncludedBoardIds.FirstOrDefault() is { Length: > 0 } board
                ? $"https://view.monday.com/boards/{board}"
                : "https://monday.com/",
            "ezrentout" => _ezRentOutOptions.ResolveBaseUrl().TrimEnd('/'),
            _ => "about:blank"
        };

    private static List<string> ExtractHints(string haystack, string? subject)
    {
        var text = string.IsNullOrWhiteSpace(subject) ? haystack : $"{subject}\n{haystack}";
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in WoNumber.Matches(text))
        {
            hints.Add($"WO:{m.Groups[1].Value}");
        }

        foreach (Match m in QuoteNumber.Matches(text))
        {
            hints.Add($"Quote:{m.Groups[1].Value}");
        }

        foreach (Match m in PoSoNumber.Matches(text))
        {
            hints.Add($"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value}");
        }

        return hints.Take(20).ToList();
    }

    private static string ShortLabel(string? title, string externalId)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length > 36)
        {
            t = t[..33] + "…";
        }

        return string.IsNullOrWhiteSpace(t) ? externalId : t;
    }

    private static bool IsPersistedAiSummary(Domain.Entities.Message message)
    {
        if (string.Equals(message.Summary, "AI summary", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message.ProviderMetadataJson))
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message.ProviderMetadataJson);
            return doc.RootElement.TryGetProperty("kind", out var kind) &&
                   string.Equals(kind.GetString(), "ai.summary", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }
}
