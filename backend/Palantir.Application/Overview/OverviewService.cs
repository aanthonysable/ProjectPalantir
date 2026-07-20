using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Ai;
using Palantir.Application.Ask;
using Palantir.Application.Audit;
using Palantir.Application.Connectors;
using Palantir.Application.Knowledge;
using Palantir.Domain.Enums;

namespace Palantir.Application.Overview;

public sealed class OverviewService : IOverviewService
{
    private const int SampleLimit = 12;
    private static readonly TimeSpan SnapshotCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (OverviewSnapshotDto Snapshot, DateTimeOffset ExpiresAt)> SnapshotCache = new();

    private readonly IPalantirDbContext _db;
    private readonly IOpsConnectorHealthService _opsHealth;
    private readonly IMaintainXConnector _maintainX;
    private readonly IEZRentOutConnector _ezRentOut;
    private readonly IMondayConnector _monday;
    private readonly MaintainXOptions _maintainXOptions;
    private readonly IAiCompletionClient _ai;
    private readonly IKnowledgeService _knowledge;
    private readonly IAskHistoryService _askHistory;
    private readonly IAskAttachmentService _askAttachments;
    private readonly IAuditEventWriter _audit;
    private readonly IOpsSnapshotStore _opsSnapshots;
    private readonly OpsSnapshotOptions _opsSnapshotOptions;

    public OverviewService(
        IPalantirDbContext db,
        IOpsConnectorHealthService opsHealth,
        IMaintainXConnector maintainX,
        IEZRentOutConnector ezRentOut,
        IMondayConnector monday,
        IOptions<MaintainXOptions> maintainXOptions,
        IAiCompletionClient ai,
        IKnowledgeService knowledge,
        IAskHistoryService askHistory,
        IAskAttachmentService askAttachments,
        IAuditEventWriter audit,
        IOpsSnapshotStore opsSnapshots,
        IOptions<OpsSnapshotOptions> opsSnapshotOptions)
    {
        _db = db;
        _opsHealth = opsHealth;
        _maintainX = maintainX;
        _ezRentOut = ezRentOut;
        _monday = monday;
        _maintainXOptions = maintainXOptions.Value;
        _ai = ai;
        _knowledge = knowledge;
        _askHistory = askHistory;
        _askAttachments = askAttachments;
        _audit = audit;
        _opsSnapshots = opsSnapshots;
        _opsSnapshotOptions = opsSnapshotOptions.Value;
    }

    public async Task<OverviewSnapshotDto> GetSnapshotAsync(
        Guid organizationId,
        Guid userId,
        OverviewFocus? focus = null,
        CancellationToken cancellationToken = default)
    {
        focus ??= new OverviewFocus();
        var notes = new List<string>();

        IReadOnlyList<ConnectorHealthDto> health = [];
        if (focus.IncludeConnectorHealth)
        {
            health = await _opsHealth.CheckAllAsync(cancellationToken);
        }

        var maintainXOpen = new List<ExternalWorkItemDto>();
        var ezRentOutOpen = new List<ExternalWorkItemDto>();
        var ezRentOrders = Array.Empty<EzRentOrderDto>();
        var recentlyCompleted = new List<ExternalWorkItemDto>();
        var quotes = new List<ExternalWorkItemDto>();
        var inventoryAlerts = new List<InventoryAlertDto>();

        // Auto lookback: prefer last day when there is meaningful volume, else week.
        var lookbackDays = focus.CompletionLookbackDays;
        if (lookbackDays <= 0)
        {
            lookbackDays = 7;
        }

        var sinceWeek = DateTimeOffset.UtcNow.AddDays(-7);
        var sinceDay = DateTimeOffset.UtcNow.AddDays(-1);

        if (focus.IncludeMaintainX || focus.IncludeMaintainXInventory)
        {
            foreach (var env in _maintainXOptions.Environments.Where(e => !string.IsNullOrWhiteSpace(e.ApiKey)))
            {
                try
                {
                    if (focus.IncludeMaintainX)
                    {
                        maintainXOpen.AddRange(await _maintainX.ListOpenWorkAsync(env, cancellationToken));
                        recentlyCompleted.AddRange(
                            await _maintainX.ListRecentlyCompletedAsync(env, sinceWeek, cancellationToken));
                    }

                    if (focus.IncludeMaintainXInventory)
                    {
                        inventoryAlerts.AddRange(
                            await _maintainX.ListInventoryAlertsAsync(env, cancellationToken));
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"MaintainX ({env.Name}): {ex.Message}");
                }
            }
        }

        if (focus.CompletionLookbackDays <= 0)
        {
            var dayCount = recentlyCompleted.Count(i =>
                DateTimeOffset.TryParse(i.Metadata?.GetValueOrDefault("updatedAt"), out var at) && at >= sinceDay);
            lookbackDays = dayCount >= 5 ? 1 : 7;
        }

        var since = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        recentlyCompleted = recentlyCompleted
            .Where(i =>
                DateTimeOffset.TryParse(i.Metadata?.GetValueOrDefault("updatedAt"), out var at) && at >= since)
            .ToList();
        var completionWindowLabel = lookbackDays <= 1 ? "last 24 hours" : $"last {lookbackDays} days";

        if (focus.IncludeEZRentOut)
        {
            try
            {
                ezRentOutOpen.AddRange(await _ezRentOut.ListOpenWorkAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                notes.Add($"EZRentOut assets: {ex.Message}");
            }

            try
            {
                ezRentOrders = (await _ezRentOut.ListOrdersAsync(cancellationToken)).ToArray();
            }
            catch (Exception ex)
            {
                notes.Add($"EZRentOut orders: {ex.Message}");
            }
        }

        if (focus.IncludeMonday)
        {
            try
            {
                quotes = (await _monday.ListOpenWorkAsync(cancellationToken)).ToList();
            }
            catch (Exception ex)
            {
                notes.Add($"Monday: {ex.Message}");
            }
        }

        // Cross-reference quotes ↔ MaintainX with friendly WO# / titles (not raw DB ids).
        var allMx = maintainXOpen
            .Concat(recentlyCompleted)
            .Where(i => i.SourceSystem == "MaintainX")
            .ToList();
        var mxByExternalId = allMx
            .GroupBy(i => i.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var physicalMxIds = maintainXOpen
            .Where(i => i.SourceSystem == "MaintainX" && IsPhysicalActive(i))
            .Select(i => i.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onHoldMxIds = maintainXOpen
            .Where(i => i.SourceSystem == "MaintainX" && IsOnHold(i))
            .Select(i => i.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        quotes = quotes.Select(quote =>
        {
            var mxId = quote.Metadata?.GetValueOrDefault("maintainXWorkOrderId");
            if (string.IsNullOrWhiteSpace(mxId))
            {
                return quote;
            }

            var meta = new Dictionary<string, string>(quote.Metadata ?? new Dictionary<string, string>());
            if (mxByExternalId.TryGetValue(mxId, out var wo))
            {
                var seq = wo.Metadata?.GetValueOrDefault("sequentialId");
                if (!string.IsNullOrWhiteSpace(seq))
                {
                    meta["maintainXWoNumber"] = seq;
                }

                meta["maintainXTitle"] = wo.Title;
                meta["maintainXAssignee"] = wo.Assignee ?? "Unassigned";
                meta["maintainXStatus"] = wo.Status ?? "";
                meta["maintainXRawStatus"] = wo.Metadata?.GetValueOrDefault("rawStatus") ?? "";
            }

            if (physicalMxIds.Contains(mxId))
            {
                meta["crossRefPhysicalMaintainX"] = "true";
                meta["crossRefOpenMaintainX"] = "true";
            }
            else if (onHoldMxIds.Contains(mxId))
            {
                meta["crossRefOnHoldMaintainX"] = "true";
            }

            return quote with { Metadata = meta };
        }).ToList();

        var quotesByMxId = quotes
            .Where(q => !string.IsNullOrWhiteSpace(q.Metadata?.GetValueOrDefault("maintainXWorkOrderId")))
            .GroupBy(q => q.Metadata!["maintainXWorkOrderId"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => string.Join(
                    " · ",
                    g.Select(x => x.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Take(3)),
                StringComparer.OrdinalIgnoreCase);

        maintainXOpen = maintainXOpen.Select(item =>
        {
            if (item.SourceSystem != "MaintainX" ||
                !quotesByMxId.TryGetValue(item.ExternalId, out var quoteTitles) ||
                string.IsNullOrWhiteSpace(quoteTitles))
            {
                return item;
            }

            var meta = new Dictionary<string, string>(item.Metadata ?? new Dictionary<string, string>())
            {
                ["linkedQuotes"] = quoteTitles
            };
            return item with { Metadata = meta };
        }).ToList();

        var conversations = focus.IncludeInbox
            ? _db.Conversations
                .Where(c => c.OrganizationId == organizationId)
                .ToList()
                .OrderByDescending(c => c.UpdatedAt)
                .Take(SampleLimit)
                .Select(c => new OverviewListItemDto(
                    c.Id.ToString(),
                    c.Subject ?? "(no subject)",
                    c.Channel,
                    c.Status.ToString(),
                    c.UpdatedAt))
                .ToList()
            : [];

        var openTaskEntities = focus.IncludeTasks
            ? _db.TaskItems
                .Where(t => t.OrganizationId == organizationId && t.Status != "Done" && t.Status != "Completed")
                .ToList()
            : [];

        var openTasks = openTaskEntities
            .OrderByDescending(t => t.CreatedAt)
            .Take(SampleLimit)
            .Select(t => new OverviewListItemDto(
                t.Id.ToString(),
                t.Title,
                t.Priority,
                t.Status,
                t.DueAt))
            .ToList();

        var orgUserIds = _db.Users
            .Where(u => u.OrganizationId == organizationId)
            .Select(u => u.Id)
            .ToHashSet();

        var pendingApprovalsRaw = focus.IncludeApprovals
            ? _db.ApprovalRequests
                .Where(a => a.Status == ApprovalStatus.Pending && orgUserIds.Contains(a.RequestedForUserId))
                .ToList()
                .OrderByDescending(a => a.RequestedAt)
                .Take(SampleLimit)
                .ToList()
            : [];

        var draftIds = pendingApprovalsRaw
            .Where(a => a.DraftId.HasValue)
            .Select(a => a.DraftId!.Value)
            .ToHashSet();
        var drafts = _db.Drafts.Where(d => draftIds.Contains(d.Id)).ToList()
            .ToDictionary(d => d.Id);

        var pendingApprovals = pendingApprovalsRaw.Select(a =>
        {
            drafts.TryGetValue(a.DraftId ?? Guid.Empty, out var draft);
            var preview = draft is null
                ? "Pending approval"
                : (draft.Body.Length <= 80 ? draft.Body : draft.Body[..80] + "…");
            return new OverviewListItemDto(
                a.Id.ToString(),
                preview,
                draft is null ? null : $"Conversation {draft.ConversationId.ToString()[..8]}",
                a.Status.ToString(),
                a.RequestedAt);
        }).ToList();

        var conversationCount = focus.IncludeInbox
            ? _db.Conversations.Count(c => c.OrganizationId == organizationId)
            : 0;
        var openTaskCount = focus.IncludeTasks
            ? openTaskEntities.Count
            : 0;
        var pendingApprovalCount = focus.IncludeApprovals
            ? _db.ApprovalRequests.Count(a =>
                a.Status == ApprovalStatus.Pending && orgUserIds.Contains(a.RequestedForUserId))
            : 0;

        var mxAll = maintainXOpen.Where(i => i.SourceSystem == "MaintainX").ToList();
        var mxPhysical = mxAll.Where(IsPhysicalActive).ToList();
        var mxOnHold = mxAll.Where(IsOnHold).ToList();

        // Keep nearly all physical open work so Ask can match specific people / WO#s / titles.
        // Comment enrichment is limited inside EnrichMaintainXCommentsAsync (top candidates only).
        var mxSample = mxPhysical
            .Concat(
                mxOnHold
                    .Where(i => !string.IsNullOrWhiteSpace(i.Metadata?.GetValueOrDefault("linkedQuotes")))
                    .Take(24))
            .GroupBy(i => i.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(250)
            .ToList();

        if (focus.IncludeMaintainX && mxSample.Count > 0)
        {
            mxSample = await EnrichMaintainXCommentsAsync(mxSample, notes, cancellationToken);
        }

        // Also pull comments for recently completed items that lack narrative context.
        if (focus.IncludeMaintainX && recentlyCompleted.Count > 0)
        {
            var completedSample = recentlyCompleted.Take(20).ToList();
            completedSample = await EnrichMaintainXCommentsAsync(completedSample, notes, cancellationToken);
            var enrichedIds = completedSample.ToDictionary(i => i.ExternalId);
            recentlyCompleted = recentlyCompleted
                .Select(i => enrichedIds.TryGetValue(i.ExternalId, out var e) ? e : i)
                .ToList();
        }

        var agingQuotes = quotes.Count(q => q.Metadata?.GetValueOrDefault("aging") == "true");
        var linkedQuotes = quotes.Count(q =>
            !string.IsNullOrWhiteSpace(q.Metadata?.GetValueOrDefault("maintainXWorkOrderId")));
        var billedQuotes = quotes
            .Where(q => string.Equals(q.Status, "Billed", StringComparison.OrdinalIgnoreCase) ||
                        q.Metadata?.GetValueOrDefault("bucket") == "billed")
            .OrderByDescending(q =>
                DateTimeOffset.TryParse(q.Metadata?.GetValueOrDefault("billedAt"), out var billedAt)
                    ? billedAt
                    : DateTimeOffset.MinValue)
            .ToList();
        if (billedQuotes.Count > 0)
        {
            var withDates = billedQuotes.Count(q =>
                !string.IsNullOrWhiteSpace(q.Metadata?.GetValueOrDefault("billedAt")));
            notes.Add(
                $"Monday Quotes: {quotes.Count} open/pipeline + billed loaded; " +
                $"{billedQuotes.Count} currently Billed ({withDates} with status-change dates). " +
                "Quote Status \"Billed\" = converted to billed order (not EZRentOut rental revenue).");
        }

        var inventoryOut = inventoryAlerts.Count(a => a.Severity == "Out");
        var inventoryLow = inventoryAlerts.Count(a => a.Severity == "Low");
        var inventorySample = inventoryAlerts
            .OrderBy(a => a.Severity == "Out" ? 0 : 1)
            .ThenBy(a => a.AvailableQuantity)
            .Take(120)
            .ToList();

        // Keep all checked-out rentals so Ask can aggregate customer $/day accurately.
        var ezSample = ezRentOutOpen
            .OrderBy(i => i.Assignee ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ezSample.Count > 0 || ezRentOrders.Length > 0)
        {
            var overdue = ezSample.Count(i =>
                string.Equals(i.Status, "Overdue return", StringComparison.OrdinalIgnoreCase));
            var dailyTotal = ezSample.Sum(ReadDailyRate);
            var now = DateTimeOffset.UtcNow;
            var mtd = SumOrderRevenue(ezRentOrders, StartOfMonth(now), StartOfMonth(now).AddMonths(1));
            var ytd = SumOrderRevenue(ezRentOrders, StartOfYear(now), StartOfYear(now).AddYears(1));
            notes.Add(
                $"EZRentOut: {ezSample.Count} assets checked out ({overdue} overdue); " +
                $"current daily run-rate ${dailyTotal:0.##}/day (not historical). " +
                $"Order history: {ezRentOrders.Length} orders; MTD ${mtd:0.##}; YTD ${ytd:0.##} " +
                "(net amounts prorated across each order's bill_from→bill_to).");
        }

        var externalSample = mxSample.Concat(ezSample).ToList();
        var quoteSample = billedQuotes
            .Concat(
                quotes
                    .Where(q => billedQuotes.All(b =>
                        !string.Equals(b.ExternalId, q.ExternalId, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(q =>
                        int.TryParse(q.Metadata?.GetValueOrDefault("ageDays"), out var d) ? d : 0)
                    .Take(140))
            .Take(220)
            .ToList();

        return new OverviewSnapshotDto(
            DateTimeOffset.UtcNow,
            completionWindowLabel,
            new OverviewCountsDto(
                conversationCount,
                openTaskCount,
                pendingApprovalCount,
                mxPhysical.Count,
                mxOnHold.Count,
                recentlyCompleted.Count,
                agingQuotes,
                linkedQuotes,
                inventoryOut,
                inventoryLow),
            health,
            externalSample,
            recentlyCompleted.Take(80).ToList(),
            quoteSample,
            inventorySample,
            conversations,
            openTasks,
            pendingApprovals,
            notes,
            ezRentOrders);
    }

    private async Task<List<ExternalWorkItemDto>> EnrichMaintainXCommentsAsync(
        List<ExternalWorkItemDto> items,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        // Prefer physical work (in progress / open) for narrative context; ON_HOLD is low priority.
        var candidates = items
            .OrderByDescending(i =>
            {
                var raw = i.Metadata?.GetValueOrDefault("rawStatus") ?? "";
                var score = raw.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)
                            || raw.Equals("IN PROGRESS", StringComparison.OrdinalIgnoreCase)
                    ? 4
                    : raw.Equals("OPEN", StringComparison.OrdinalIgnoreCase) ? 3
                    : raw.Equals("ON_HOLD", StringComparison.OrdinalIgnoreCase)
                      || raw.Equals("ON HOLD", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1;
                var desc = i.Metadata?.GetValueOrDefault("description") ?? "";
                if (desc.Length < 40)
                {
                    score += 1;
                }

                if (!string.IsNullOrWhiteSpace(i.Metadata?.GetValueOrDefault("linkedQuotes")))
                {
                    score += 1;
                }

                return score;
            })
            .Take(18)
            .ToList();

        var envByName = _maintainXOptions.Environments
            .Where(e => !string.IsNullOrWhiteSpace(e.ApiKey))
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        var gate = new SemaphoreSlim(4);
        var tasks = candidates.Select(async item =>
        {
            if (string.IsNullOrWhiteSpace(item.EnvironmentName) ||
                !envByName.TryGetValue(item.EnvironmentName, out var env))
            {
                return item;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var snippets = await _maintainX.ListWorkOrderCommentSnippetsAsync(
                    env,
                    item.ExternalId,
                    limit: 5,
                    cancellationToken);
                if (snippets.Count == 0)
                {
                    return item;
                }

                var meta = new Dictionary<string, string>(item.Metadata ?? new Dictionary<string, string>())
                {
                    ["comments"] = string.Join(" | ", snippets)
                };
                return item with { Metadata = meta };
            }
            catch (Exception ex)
            {
                notes.Add($"MaintainX comments ({item.ExternalId}): {ex.Message}");
                return item;
            }
            finally
            {
                gate.Release();
            }
        });

        var enriched = await Task.WhenAll(tasks);
        var byId = enriched.ToDictionary(i => i.ExternalId);
        return items.Select(i => byId.TryGetValue(i.ExternalId, out var e) ? e : i).ToList();
    }

    public async Task<OverviewRecapDto> GenerateRecapAsync(
        Guid organizationId,
        Guid userId,
        OverviewFocus? focus = null,
        CancellationToken cancellationToken = default)
    {
        focus ??= new OverviewFocus();
        var snapshot = await GetSnapshotAsync(organizationId, userId, focus, cancellationToken);

        if (!_ai.IsConfiguredFor(AiTaskKind.Recap) && !_ai.IsConfigured)
        {
            var fallback = BuildFallbackNarrative(snapshot, focus);
            return new OverviewRecapDto(DateTimeOffset.UtcNow, fallback, snapshot, focus);
        }

        var depthInstruction = focus.Depth.ToLowerInvariant() switch
        {
            "brief" => "Keep the whole briefing under ~250 words.",
            "detailed" => "Write a full briefing (~400-700 words) with clear section headings.",
            _ => "Write a concise briefing (~300-450 words) with short sections."
        };

        var custom = string.IsNullOrWhiteSpace(focus.CustomPrompt)
            ? "No special focus — prioritize physical field/shop work (OPEN / IN_PROGRESS), aging quotes tied to that work, and inventory that blocks jobs. Treat ON_HOLD as physically finished."
            : $"Extra focus from the reader: {focus.CustomPrompt.Trim()}";

        var factSheet = BuildFactSheet(snapshot);
        var knowledgeQuery = string.IsNullOrWhiteSpace(focus.CustomPrompt)
            ? "procedures policy safety inventory maintenance checklist"
            : focus.CustomPrompt!;
        var (knowledgeBlock, _) = await BuildKnowledgeBlockAsync(organizationId, knowledgeQuery, cancellationToken);

        var narrative = (await _ai.CompleteAsync(
            AiTaskKind.Recap,
            [
                new AiChatMessage(
                    "system",
                    """
                    You write a daily ops executive brief for Sable people who already know the business,
                    the areas (Northern / Permian / Shop), MaintainX, EZRentOut, and the Monday Quotes board.
                    This is NOT an outsider overview, onboarding doc, or company explainer.

                    Voice:
                    - Insider to insider. Assume shared context; never define Sable, MaintainX, Monday, EZRentOut, or status codes.
                    - Lead with physical work that still needs wrenches: OPEN and IN_PROGRESS.
                    - Prefer concrete names, WO ids, rental assets, quote ages, and part shortages over generalities.
                    - Skip healthy/routine noise unless it changes the picture.
                    - Do not open with "Sable Automation…" or "This briefing covers…".

                    Status policy (critical):
                    - OPEN / IN_PROGRESS = physical work still open — this is the main story.
                    - ON_HOLD = physical work is effectively finished; back office can close whenever. Do NOT treat ON_HOLD as a blocker or backlog to chase unless the reader explicitly asks.
                    - DONE = closed.

                    Structure (use these headings; omit a section only if that data is absent):
                    1) Executive snapshot — 3–6 sentences on physical work needing attention now
                    2) Who is working what — by person; OPEN / IN_PROGRESS; unassigned and stuck physical work
                    3) Area view — Northern, Permian, Shop (TEST & PREP / shop-style titles) for physical work
                    4) Completions — what closed in the stated window (who/area), only noteworthy closures
                    5) Quotes — aging Sent/Draft; call out quotes linked to physical MX work; ON_HOLD links are FYI only
                    6) Inventory — outs and lows that risk active jobs; group by org; name the worst parts
                    7) Watch list — 3–5 concrete asks with owners when known (physical work / quotes / parts — not ON_HOLD cleanup)

                    Fact rules:
                    - Use ONLY the FACT SHEET and KNOWLEDGE EXCERPTS. Do not invent people, tickets, quotes, comments, or quantities.
                    - Use comment/description snippets only when present, to say what is actually happening.
                    - Aging quotes (ageDays) are commercial risk — say who/what needs chase.
                    - Inventory: Out vs Low per fact sheet; do not lecture on the rules.
                    - Ignore connector health unless UNHEALTHY.
                    - When policy/procedure questions arise, cite knowledge excerpts by document title.
                    """),
                new AiChatMessage(
                    "user",
                    $"""
                    Length guidance: {depthInstruction}
                    {custom}
                    Completion window for closed work: {snapshot.CompletionWindowLabel}

                    FACT SHEET:
                    {factSheet}
                    {knowledgeBlock}
                    """)
            ],
            cancellationToken)).Trim();


        await _audit.WriteAsync(
            organizationId,
            "overview.recap_generated",
            userId,
            "Overview",
            null,
            JsonSerializer.Serialize(new
            {
                depth = focus.Depth,
                hasCustomPrompt = !string.IsNullOrWhiteSpace(focus.CustomPrompt),
                counts = snapshot.Counts
            }),
            cancellationToken);

        return new OverviewRecapDto(DateTimeOffset.UtcNow, narrative, snapshot, focus);
    }

    public async Task<OverviewChatReplyDto> ChatAsync(
        Guid organizationId,
        Guid userId,
        OverviewChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var focus = request.Focus ?? new OverviewFocus();
        var turns = NormalizeChatTurns(request.Messages);
        if (turns.Count == 0)
        {
            throw new InvalidOperationException("Ask a question about current ops facts.");
        }

        // Prefer shared DB ops snapshot (background-refreshed for all users).
        // Pass refreshFacts: true only when the client forces a live rebuild.
        var snapshot = await ResolveSnapshotAsync(
            organizationId,
            userId,
            focus,
            refreshFacts: request.RefreshFacts,
            cancellationToken);
        var question = turns[^1].Content;
        var attachmentIds = (request.AttachmentIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(5)
            .ToList();
        var attachedFiles = attachmentIds.Count == 0
            ? []
            : await _askAttachments.GetExtractedForPromptAsync(
                organizationId, userId, attachmentIds, cancellationToken);
        var attachmentBlock = BuildAskAttachmentBlock(attachedFiles);
        var wantsPromoteAttachments = attachedFiles.Count > 0 && WantsPromoteAttachments(question);

        // Pull comments for WOs that best match this question (beyond the generic snapshot enrich).
        snapshot = await EnrichSnapshotForQuestionAsync(snapshot, question, cancellationToken);

        // File review / promote needs the model (or promote path) — skip ops deterministic shortcuts.
        // Also skip deterministic when the question looks like a knowledge/PDF lookup or retrieval already
        // found strong document matches — otherwise ops shortcuts ignore indexed manuals.
        var knowledgeQuery = BuildKnowledgeSearchQuery(question, turns);
        var (knowledgeBlock, knowledgeSources) = await BuildKnowledgeBlockAsync(
            organizationId, knowledgeQuery, cancellationToken);
        var knowledgeStrong = knowledgeSources.Count > 0;
        var knowledgeQuestion = LooksLikeKnowledgeQuestion(question);
        var wantsSourceDoc = WantsKnowledgeSourceDocument(question);

        var deterministic = attachedFiles.Count > 0 || knowledgeStrong || knowledgeQuestion || wantsSourceDoc
            ? null
            : TryBuildDeterministicChatReply(snapshot, question);
        if (deterministic is not null)
        {
            await _audit.WriteAsync(
                organizationId,
                "overview.chat",
                userId,
                "Overview",
                null,
                JsonSerializer.Serialize(new
                {
                    turnCount = turns.Count,
                    refreshFacts = request.RefreshFacts,
                    mode = "deterministic",
                    questionChars = question.Length,
                    counts = snapshot.Counts
                }),
                cancellationToken);
            return await PersistAndReplyAsync(
                organizationId, userId, request.SessionId, question, deterministic, snapshot, focus,
                attachmentIds, knowledgeSources: null, cancellationToken);
        }

        // Explicit "send me / download the source" — attach files even before the model answers.
        if (wantsSourceDoc && knowledgeSources.Count > 0 && !knowledgeQuestion &&
            LooksLikeSourceOnlyRequest(question))
        {
            var sourceReply = AppendKnowledgeSourceMarkers(
                BuildKnowledgeSourceReply(knowledgeSources),
                knowledgeSources);
            return await PersistAndReplyAsync(
                organizationId, userId, request.SessionId, question, sourceReply, snapshot, focus,
                attachmentIds, knowledgeSources, cancellationToken);
        }

        var factSheet = BuildChatFactSheet(snapshot, question);

        if (!_ai.IsConfiguredFor(AiTaskKind.Chat) && !_ai.IsConfigured)
        {
            var fallback = attachedFiles.Count > 0
                ? "AI is not configured, so I can't review the attached file(s). " +
                  "Configure AI under Admin, or promote the file to knowledge from Ask."
                : "AI is not configured. Live counts from the fact sheet:\n" +
                  $"mxOpen={snapshot.Counts.ExternalOpenWork}, completed={snapshot.Counts.RecentlyCompleted}, " +
                  $"agingQuotes={snapshot.Counts.AgingQuotes}, inventoryOut={snapshot.Counts.InventoryOut}, " +
                  $"inventoryLow={snapshot.Counts.InventoryLow}.";
            if (wantsPromoteAttachments)
            {
                fallback += "\n\n" + await PromoteAttachmentsAndSummarizeAsync(
                    organizationId, userId, attachmentIds, cancellationToken);
            }

            return await PersistAndReplyAsync(
                organizationId, userId, request.SessionId, question, fallback, snapshot, focus,
                attachmentIds, knowledgeSources, cancellationToken);
        }

        var custom = string.IsNullOrWhiteSpace(focus.CustomPrompt)
            ? null
            : $"Standing reader focus: {focus.CustomPrompt.Trim()}";

        var messages = new List<AiChatMessage>
        {
            new(
                "system",
                """
                You are an internal Sable ops assistant. Readers already know the shop.

                The FACT SHEET was just retrieved from live MaintainX / EZRentOut / Monday / inventory and ranked for THIS question.
                KNOWLEDGE EXCERPTS were searched against indexed org documents (PDF manuals, tutorials, wiring packs, captured notes) using title/tags/body.
                PRIOR ASK HISTORY was searched against prior chats.
                ATTACHED FILES (if present) were uploaded for THIS turn — review them when the user asks about the file/document.
                Answer ONLY from those blocks. Prefer live FACT SHEET over prior Ask history when they conflict. Copy names, WO#, and asset ids exactly as written.
                Rules:
                - Default style: a short human answer (1–3 sentences, or a few tight bullets). Lead with the number / name they asked for.
                - Do NOT dump line items, full WO lists, asset lists, quote tables, or long rollups unless the user asks for details / breakdown / list / line items / "show me" / "which ones".
                - If they only asked for a total or "who has the most", give that answer and stop. Offer that you can break it down if useful is optional and brief — do not attach the breakdown unprompted.
                - Prefer "## Matches for this question" when you need a fact, but still summarize — don't paste the whole section.
                - For rentals, separate CURRENT daily run-rate (checked-out asset list rates) from HISTORICAL billed revenue (order net amounts).
                - MTD / YTD / past months / past years for RENTALS MUST come from EZRentOut order history rollups — never multiply current daily rates.
                - Monday Quote Status "Billed" means that quote was converted to a billed order. Use "## Monday quotes converted to Billed" and billedAt/billedDate — this is NOT EZRentOut rental revenue.
                - If the user names a customer/company (e.g. Elevate), ONLY use quotes that match that name in title/customer. Never list other customers' quotes for a named-party question.
                - Current daily on-rent is a point-in-time run-rate only; say so briefly when quoting it.
                - Never invent people, WO numbers, rental assets, parts, quotes, or quantities.
                - Never cite long MaintainX database ids.
                - No company intro. No full recap unless asked.
                - If the answer is not in the FACT SHEET, KNOWLEDGE EXCERPTS, or ATTACHED FILES, say what is missing — do not invent.
                - ON_HOLD = physically finished; back office can close later. Only discuss ON_HOLD if the user asks.
                - For how-to / procedure / wiring / datasheet / tutorial / PDF questions: prefer KNOWLEDGE EXCERPTS over the live fact sheet. Cite the document title. Quote concrete steps from excerpts when present.
                - When knowledge documents are matched, name the document title. The app will offer a Preview of the original file (with Download from there) — say the source is available to preview when the user asked for the file/PDF/source.
                - Do not invent download URLs. Do not claim a file is attached unless SOURCE DOCUMENTS are listed in the user message.
                - When ATTACHED FILES are present, prioritize reviewing their extracted text for file questions. If extract status is Empty/Unsupported, say so. Zips may be Ready or Partial (some entries skipped); review each --- path --- section.
                - Do not claim you saved a file to knowledge unless the user message or a system note says it was promoted.
                """),
            new(
                "user",
                knowledgeStrong || knowledgeQuestion || wantsSourceDoc
                    ? $"""
                    This question looks like an org-knowledge / document lookup. Prefer KNOWLEDGE EXCERPTS; use the FACT SHEET only for live ops counts if needed.
                    Completion window = {snapshot.CompletionWindowLabel}
                    {(knowledgeSources.Count > 0
                        ? "SOURCE DOCUMENTS (app will offer Preview → Download):\n" +
                          string.Join("\n", knowledgeSources.Select(s => $"- {s.Title} ({s.FileName}) id={s.DocumentId:N}"))
                        : "SOURCE DOCUMENTS: (none matched)")}

                    {knowledgeBlock}

                    LIVE FACT SHEET (secondary for this question):
                    {factSheet}
                    {attachmentBlock}
                    {(custom is null ? "" : "\n" + custom)}
                    """
                    : $"""
                    LIVE FACT SHEET for question (completion window = {snapshot.CompletionWindowLabel}):
                    {factSheet}
                    {knowledgeBlock}
                    {attachmentBlock}
                    {(custom is null ? "" : "\n" + custom)}
                    """)
        };

        foreach (var turn in turns)
        {
            messages.Add(new AiChatMessage(turn.Role, turn.Content));
        }

        messages.Add(new AiChatMessage(
            "user",
            attachedFiles.Count > 0
                ? "Answer the latest user question now from the LIVE FACT SHEET, KNOWLEDGE EXCERPTS, and ATTACHED FILES. Keep it short and human. Do not invent."
                : "Answer the latest user question now from the LIVE FACT SHEET and KNOWLEDGE EXCERPTS. Keep it short and human. Do not invent. Only include line-item detail if they asked for a breakdown/list/details."));

        var reply = (await _ai.CompleteAsync(AiTaskKind.Chat, messages, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(reply))
        {
            reply = "No answer returned — try rephrasing with a WO#, person name, part, or quote title.";
        }

        if (wantsPromoteAttachments)
        {
            var promoteNote = await PromoteAttachmentsAndSummarizeAsync(
                organizationId, userId, attachmentIds, cancellationToken);
            reply = string.IsNullOrWhiteSpace(promoteNote) ? reply : $"{reply.TrimEnd()}\n\n{promoteNote}";
        }

        var sourcesToAttach = knowledgeStrong || wantsSourceDoc ? knowledgeSources : null;
        if (sourcesToAttach is { Count: > 0 })
        {
            reply = AppendKnowledgeSourceMarkers(reply, sourcesToAttach);
        }

        await _audit.WriteAsync(
            organizationId,
            "overview.chat",
            userId,
            "Overview",
            null,
            JsonSerializer.Serialize(new
            {
                turnCount = turns.Count,
                refreshFacts = request.RefreshFacts,
                attachmentCount = attachedFiles.Count,
                promoted = wantsPromoteAttachments,
                knowledgeSourceCount = sourcesToAttach?.Count ?? 0,
                questionChars = turns[^1].Content.Length,
                counts = snapshot.Counts
            }),
            cancellationToken);

        return await PersistAndReplyAsync(
            organizationId, userId, request.SessionId, question, reply, snapshot, focus,
            attachmentIds, sourcesToAttach, cancellationToken);
    }


    private async Task<OverviewChatReplyDto> PersistAndReplyAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        string question,
        string reply,
        OverviewSnapshotDto snapshot,
        OverviewFocus focus,
        IReadOnlyList<Guid> attachmentIds,
        IReadOnlyList<KnowledgeSourceDto>? knowledgeSources,
        CancellationToken cancellationToken)
    {
        var (resolvedSessionId, _) = await _askHistory.AppendTurnAsync(
            organizationId,
            userId,
            sessionId,
            question,
            reply,
            cancellationToken);

        if (attachmentIds.Count > 0)
        {
            try
            {
                await _askAttachments.BindToSessionAsync(
                    organizationId, userId, resolvedSessionId, attachmentIds, cancellationToken);
            }
            catch
            {
                // Chat reply already persisted; binding is best-effort.
            }
        }

        return new OverviewChatReplyDto(
            DateTimeOffset.UtcNow,
            reply,
            snapshot,
            focus,
            resolvedSessionId,
            knowledgeSources);
    }

    private static string BuildAskAttachmentBlock(
        IReadOnlyList<(AskAttachmentDto Meta, string Text)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("ATTACHED FILES:");
        if (files.Count == 0)
        {
            sb.AppendLine("(none)");
            return sb.ToString();
        }

        foreach (var (meta, text) in files)
        {
            sb.AppendLine(
                $"--- {meta.FileName} ({meta.ContentType}, {meta.ByteSize} bytes, extract={meta.ExtractStatus}) ---");
            if (string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine("(no extractable text — scanned PDF or unsupported type)");
            }
            else
            {
                sb.AppendLine(text);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static bool WantsPromoteAttachments(string question)
    {
        var q = question.Trim().ToLowerInvariant();
        return q.Contains("add to knowledge") ||
               q.Contains("save to knowledge") ||
               q.Contains("save this to knowledge") ||
               q.Contains("save these to knowledge") ||
               q.Contains("put in knowledge") ||
               q.Contains("index this") ||
               q.Contains("index these") ||
               q.Contains("add this file to knowledge") ||
               q.Contains("add these files to knowledge") ||
               q.Contains("promote to knowledge");
    }

    private async Task<string> PromoteAttachmentsAndSummarizeAsync(
        Guid organizationId,
        Guid userId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (var id in attachmentIds)
        {
            try
            {
                var promoted = await _askAttachments.PromoteToKnowledgeAsync(
                    organizationId, userId, id, title: null, cancellationToken);
                var doc = promoted.Knowledge?.Document;
                lines.Add(doc is null
                    ? $"- {promoted.Attachment.FileName}: already in knowledge (or promote skipped)."
                    : $"- {promoted.Attachment.FileName} → knowledge \"{doc.Title}\" ({doc.Status}).");
            }
            catch (Exception ex)
            {
                lines.Add($"- Attachment {id}: promote failed — {ex.Message}");
            }
        }

        return lines.Count == 0
            ? string.Empty
            : "Added to knowledge:\n" + string.Join("\n", lines);
    }

    // Compatibility overload used by older call sites.
    private Task<OverviewChatReplyDto> PersistAndReplyAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        string question,
        string reply,
        OverviewSnapshotDto snapshot,
        OverviewFocus focus,
        CancellationToken cancellationToken) =>
        PersistAndReplyAsync(
            organizationId, userId, sessionId, question, reply, snapshot, focus, [], null, cancellationToken);

    private Task<OverviewChatReplyDto> PersistAndReplyAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        string question,
        string reply,
        OverviewSnapshotDto snapshot,
        OverviewFocus focus,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken) =>
        PersistAndReplyAsync(
            organizationId, userId, sessionId, question, reply, snapshot, focus, attachmentIds, null, cancellationToken);

    /// <summary>
    /// For common factual questions, answer from structured data so local Llama cannot invent names/WO#s.
    /// Returns null when the question needs free-form narrative.
    /// </summary>
    private static string? TryBuildDeterministicChatReply(OverviewSnapshotDto snapshot, string question)
    {
        var q = question.Trim().ToLowerInvariant();
        var mxOpen = snapshot.ExternalWorkSample
            .Where(i => i.SourceSystem == "MaintainX" && IsPhysicalActive(i))
            .ToList();

        var asksWorkload = (q.Contains("most open") || q.Contains("most work") || q.Contains("who has") ||
                            q.Contains("workload") || q.Contains("busiest")) &&
                           (q.Contains("who") || q.Contains("person") || q.Contains("people") ||
                            q.Contains("assignee") || q.Contains("tech") || q.Contains("open"));
        if (asksWorkload && mxOpen.Count > 0)
        {
            var areaFilter = q.Contains("permi") ? "Permian"
                : q.Contains("north") ? "Northern"
                : q.Contains("shop") ? "Shop"
                : null;
            var scoped = areaFilter is null
                ? mxOpen
                : mxOpen.Where(i =>
                    (i.Metadata?.GetValueOrDefault("area") ?? "").Contains(areaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (scoped.Count == 0)
            {
                return $"No physical (OPEN/IN_PROGRESS) WOs for {areaFilter} in the current sample.";
            }

            var top = scoped
                .GroupBy(i => string.IsNullOrWhiteSpace(i.Assignee) ? "Unassigned" : i.Assignee!)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First();
            var scopeLabel = areaFilter is null
                ? "physical open work"
                : $"physical open {areaFilter} work";
            var summary = $"{top.Key} has the most {scopeLabel} right now ({top.Count()}).";
            if (!WantsDetails(q))
            {
                return summary;
            }

            var sb = new StringBuilder();
            sb.AppendLine(summary);
            sb.AppendLine();
            foreach (var item in top.Take(8))
            {
                var area = item.Metadata?.GetValueOrDefault("area") ?? "";
                sb.AppendLine($"- {FormatWoLabel(item)} | {area} | {item.Status} | {item.Title}");
            }

            return sb.ToString().Trim();
        }

        var quoteParty = FindQuotePartyFilter(q, snapshot.QuotesSample);
        var asksQuoteConversion =
            (q.Contains("quote") || q.Contains("monday")) &&
            (q.Contains("converted") || q.Contains("conversion") || q.Contains("moved to billed") ||
             q.Contains("moved to bill") ||
             (q.Contains("billed") && !q.Contains("rent") && !q.Contains("ezrent") &&
              !q.Contains("on-rent") && !q.Contains("on rent") && !q.Contains("asset")) ||
             (quoteParty is not null &&
              (q.Contains("detail") || q.Contains("those") || q.Contains("more about"))));
        if (asksQuoteConversion && snapshot.QuotesSample.Count > 0)
        {
            var period = DetectEzHistoryPeriod(q);
            var billedRows = snapshot.QuotesSample
                .Where(x => string.Equals(x.Status, "Billed", StringComparison.OrdinalIgnoreCase) ||
                            x.Metadata?.GetValueOrDefault("bucket") == "billed")
                .Select(x =>
                {
                    DateTimeOffset? when = null;
                    if (DateTimeOffset.TryParse(x.Metadata?.GetValueOrDefault("billedAt"), out var billedAt))
                    {
                        when = billedAt;
                    }

                    return (Quote: x, When: when);
                })
                .Where(x => x.When is not null)
                .Where(x => quoteParty is null || QuoteMatchesParty(x.Quote, quoteParty))
                .ToList();

            string periodLabel;
            List<(ExternalWorkItemDto Quote, DateTimeOffset? When)> billedInPeriod;
            if (period is not null)
            {
                periodLabel = period.Label;
                billedInPeriod = billedRows
                    .Where(x => x.When >= period.Start && x.When < period.EndExclusive)
                    .OrderBy(x => x.When)
                    .ToList();
            }
            else if (quoteParty is not null &&
                     (q.Contains("detail") || q.Contains("those") || q.Contains("more about")))
            {
                // Follow-up without a month: show that party's recent billed conversions (not everyone else's).
                periodLabel = "recent periods";
                billedInPeriod = billedRows.OrderByDescending(x => x.When).Take(40).OrderBy(x => x.When).ToList();
            }
            else
            {
                period = DefaultHistoryPeriod();
                periodLabel = period.Label;
                billedInPeriod = billedRows
                    .Where(x => x.When >= period.Start && x.When < period.EndExclusive)
                    .OrderBy(x => x.When)
                    .ToList();
            }

            var partyBit = quoteParty is null ? "" : $" for {quoteParty}";
            var totalAmount = billedInPeriod.Sum(x =>
                decimal.TryParse(x.Quote.Metadata?.GetValueOrDefault("amount"), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var amt)
                    ? amt
                    : 0m);
            var amountBit = totalAmount > 0 ? $" (~${totalAmount:0.##} quote $)" : "";
            var summary = billedInPeriod.Count == 0
                ? $"No Monday quotes{partyBit} moved to Billed in {periodLabel}."
                : $"{billedInPeriod.Count} Monday quote(s){partyBit} converted to Billed in {periodLabel}{amountBit}.";
            if (!WantsDetails(q) && !q.Contains("which") && !q.Contains("list") && !q.Contains("show") &&
                !q.Contains("those") && !q.Contains("more about"))
            {
                return summary;
            }

            var sb = new StringBuilder();
            sb.AppendLine(summary);
            if (billedInPeriod.Count == 0)
            {
                return sb.ToString().Trim();
            }

            sb.AppendLine();
            foreach (var row in billedInPeriod.Take(40))
            {
                sb.AppendLine($"- {FormatBilledQuoteLine(row.Quote)}");
            }

            return sb.ToString().Trim();
        }

        var asksRentals = q.Contains("ezrent") || q.Contains("ez rent") || q.Contains("rental") ||
                          q.Contains("checked out") || q.Contains("check out") || q.Contains("overdue return") ||
                          q.Contains("on rent") || q.Contains("on-rent") ||
                          q.Contains("mtd") || q.Contains("ytd") || q.Contains("month to date") ||
                          q.Contains("year to date") ||
                          ((q.Contains("daily") || q.Contains("monthly") || q.Contains("yearly") ||
                            q.Contains("annual") || q.Contains("weekly") || q.Contains("dollar") ||
                            q.Contains("$") || q.Contains("total") || q.Contains("revenue") ||
                            q.Contains("billed") || q.Contains("billing")) &&
                           (q.Contains("rent") || q.Contains("asset") || q.Contains("equipment") ||
                            q.Contains("customer") || q.Contains("on rent") || q.Contains("order")) &&
                           !q.Contains("quote") && !q.Contains("monday") && !q.Contains("converted")) ||
                          (q.Contains("overdue") && (q.Contains("asset") || q.Contains("equipment") || q.Contains("rent")));
        var ezItems = snapshot.ExternalWorkSample
            .Where(i => string.Equals(i.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ezOrders = snapshot.EzRentOrders ?? [];
        var ezRollupsForMatch = ezItems.Count > 0 ? BuildEzCustomerRollups(ezItems) : [];
        var namedCustomer = FindEzCustomerMatch(
            q,
            ezRollupsForMatch.Concat(
                BuildEzOrderCustomerNames(ezOrders).Select(c =>
                    new EzCustomerRollup(c, 0, 0, 0, 0, 0, 0, 0, []))).ToList());
        var wantsRentalMoney = q.Contains("dollar") || q.Contains("$") || q.Contains("daily") ||
                               q.Contains("weekly") || q.Contains("monthly") || q.Contains("yearly") ||
                               q.Contains("annual") || q.Contains("per month") || q.Contains("per year") ||
                               q.Contains("per day") || q.Contains("rate") || q.Contains("revenue") ||
                               q.Contains("amount") || q.Contains("total") || q.Contains("on rent") ||
                               q.Contains("on-rent") || q.Contains("billing") || q.Contains("billed") ||
                               q.Contains("price") || q.Contains("mtd") || q.Contains("ytd") ||
                               q.Contains("month to date") || q.Contains("year to date");
        if ((ezItems.Count > 0 || ezOrders.Count > 0) &&
            (asksRentals || (namedCustomer is not null && wantsRentalMoney)))
        {
            var historyPeriod = DetectEzHistoryPeriod(q);
            var wantsCurrentDaily = (q.Contains("daily") || q.Contains("per day") || q.Contains("/day") ||
                                     q.Contains("run rate") || q.Contains("run-rate") ||
                                     ((q.Contains("on rent") || q.Contains("on-rent")) &&
                                      !q.Contains("month") && !q.Contains("year") && !q.Contains("mtd") &&
                                      !q.Contains("ytd") && !q.Contains("billed") && !q.Contains("revenue"))) &&
                                    historyPeriod is null;
            var asksLocation = q.Contains("location") || q.Contains("permi") || q.Contains("northern") ||
                               q.Contains("shop") || q.Contains("where");

            if (wantsRentalMoney && historyPeriod is not null)
            {
                if (namedCustomer is null && LooksLikeNamedCustomerQuestion(q, null))
                {
                    return BuildEzCustomerMissReply(q, ezRollupsForMatch);
                }

                return BuildEzHistoryMoneyReply(ezOrders, namedCustomer?.Customer, historyPeriod, q);
            }

            if (wantsRentalMoney && !wantsCurrentDaily)
            {
                if (namedCustomer is null && LooksLikeNamedCustomerQuestion(q, null))
                {
                    return BuildEzCustomerMissReply(q, ezRollupsForMatch);
                }

                // Default money questions (monthly/yearly/total/revenue) → historical MTD + YTD + recent months.
                return BuildEzHistoryMoneyReply(
                    ezOrders,
                    namedCustomer?.Customer,
                    DetectEzHistoryPeriod(q) ?? DefaultHistoryPeriod(),
                    q);
            }

            if (wantsRentalMoney && wantsCurrentDaily && ezItems.Count > 0)
            {
                if (namedCustomer is null && LooksLikeNamedCustomerQuestion(q, null))
                {
                    return BuildEzCustomerMissReply(q, ezRollupsForMatch);
                }

                return BuildEzCurrentDailyReply(ezItems, ezRollupsForMatch, namedCustomer, q);
            }

            if (asksLocation && ezItems.Count > 0)
            {
                return BuildEzLocationReply(namedCustomer?.Items ?? ezItems, namedCustomer?.Customer, q);
            }

            var overdue = ezItems
                .Where(i => string.Equals(i.Status, "Overdue return", StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
                .ToList();
            var onlyOverdue = q.Contains("overdue") && !q.Contains("checked out");
            var scoped = namedCustomer?.Items ?? ezItems;
            var listAssets = onlyOverdue
                ? overdue.Where(i => namedCustomer is null ||
                    string.Equals(i.Assignee, namedCustomer.Customer, StringComparison.OrdinalIgnoreCase)).ToList()
                : scoped.OrderByDescending(ReadDailyRate).ThenBy(i => i.DueAt ?? DateTimeOffset.MaxValue).ToList();
            if (listAssets.Count == 0 && ezOrders.Count == 0)
            {
                return namedCustomer is null
                    ? "No EZRentOut checked-out assets or orders in the current pull."
                    : $"No matching EZRentOut assets for {namedCustomer.Customer} in the current pull.";
            }

            if (listAssets.Count == 0)
            {
                return BuildEzHistoryMoneyReply(
                    ezOrders,
                    namedCustomer?.Customer,
                    DefaultHistoryPeriod(),
                    q);
            }

            var daily = listAssets.Sum(ReadDailyRate);
            var summary = namedCustomer is null
                ? $"EZRentOut has {ezItems.Count} assets checked out ({overdue.Count} overdue) at about ${daily:0.##}/day right now."
                : $"{namedCustomer.Customer} has {listAssets.Count} assets checked out at about ${daily:0.##}/day right now.";
            if (!WantsDetails(q))
            {
                return summary;
            }

            var sbAssets = new StringBuilder();
            sbAssets.AppendLine(summary);
            sbAssets.AppendLine();
            sbAssets.AppendLine(onlyOverdue ? "Overdue returns:" : "Checked out (highest daily $ first):");
            foreach (var item in listAssets.Take(16))
            {
                sbAssets.AppendLine($"- {FormatWorkLine(item)}");
            }

            return sbAssets.ToString().Trim();
        }

        var asksInventory = q.Contains("inventory") || q.Contains("out of stock") || q.Contains("shortage") ||
                            ((q.Contains("out") || q.Contains("low")) && (q.Contains("part") || q.Contains("stock")));
        if (asksInventory && snapshot.InventoryAlerts.Count > 0)
        {
            var onlyLow = q.Contains("low") && !q.Contains("out") && !q.Contains("shortage");
            var onlyOut = (q.Contains("out") || q.Contains("shortage")) && !q.Contains("low");
            var wantOut = !onlyLow;
            var wantLow = !onlyOut;
            var summary =
                $"Inventory is at {snapshot.Counts.InventoryOut} out and {snapshot.Counts.InventoryLow} low right now.";
            if (!WantsDetails(q))
            {
                return summary;
            }

            var sb = new StringBuilder();
            sb.AppendLine(summary);
            sb.AppendLine();
            if (wantOut)
            {
                sb.AppendLine("OUT (worst first):");
                foreach (var a in snapshot.InventoryAlerts
                             .Where(x => x.Severity == "Out")
                             .OrderBy(x => x.AvailableQuantity)
                             .Take(8))
                {
                    sb.AppendLine($"- {a.EnvironmentName}: {a.Name} (avail {a.AvailableQuantity}, min {a.MinimumQuantity})");
                }
            }

            if (wantLow)
            {
                if (wantOut)
                {
                    sb.AppendLine();
                }

                sb.AppendLine("LOW:");
                foreach (var a in snapshot.InventoryAlerts
                             .Where(x => x.Severity == "Low")
                             .OrderBy(x => x.AvailableQuantity - x.MinimumQuantity)
                             .Take(8))
                {
                    sb.AppendLine($"- {a.EnvironmentName}: {a.Name} (avail {a.AvailableQuantity}, min {a.MinimumQuantity})");
                }
            }

            return sb.ToString().Trim();
        }

        var asksAging = q.Contains("aging") && (q.Contains("quote") || q.Contains("monday"));
        if (asksAging)
        {
            var aging = snapshot.QuotesSample
                .Where(x => x.Metadata?.GetValueOrDefault("aging") == "true")
                .OrderByDescending(x => int.TryParse(x.Metadata?.GetValueOrDefault("ageDays"), out var d) ? d : 0)
                .Take(8)
                .ToList();
            var summary = $"There are {snapshot.Counts.AgingQuotes} aging quotes right now.";
            if (!WantsDetails(q))
            {
                return summary;
            }

            var sb = new StringBuilder();
            sb.AppendLine(summary);
            sb.AppendLine();
            foreach (var quote in aging)
            {
                var age = quote.Metadata?.GetValueOrDefault("ageDays") ?? "?";
                var region = quote.Metadata?.GetValueOrDefault("region");
                var owner = string.IsNullOrWhiteSpace(quote.Assignee) ? "Unassigned" : quote.Assignee;
                var regionBit = string.IsNullOrWhiteSpace(region) ? "" : $" | {region}";
                sb.AppendLine($"- [{quote.Status}] {age}d{regionBit} | {owner}: {quote.Title}{FormatQuoteMxLink(quote)}");
            }

            return sb.ToString().Trim();
        }

        return null;
    }

    private async Task<OverviewSnapshotDto> ResolveSnapshotAsync(
        Guid organizationId,
        Guid userId,
        OverviewFocus focus,
        bool refreshFacts,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{organizationId:N}:{FocusCacheKey(focus)}";
        var useSharedOps = OpsSnapshotFocus.IsCompatibleWithDefault(focus) ||
                           FocusCacheKey(focus) == FocusCacheKey(OpsSnapshotFocus.CreateDefault());

        if (!refreshFacts)
        {
            if (SnapshotCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Snapshot;
            }

            if (_opsSnapshotOptions.Enabled && useSharedOps)
            {
                var stored = await _opsSnapshots.TryGetFreshAsync(
                    organizationId,
                    IOpsSnapshotStore.DefaultFocusKey,
                    cancellationToken);
                if (stored is not null)
                {
                    SnapshotCache[cacheKey] = (stored, DateTimeOffset.UtcNow.Add(SnapshotCacheTtl));
                    return stored;
                }
            }
        }

        var snapshot = await GetSnapshotAsync(organizationId, userId, focus, cancellationToken);

        if (_opsSnapshotOptions.Enabled && useSharedOps)
        {
            try
            {
                var ttl = TimeSpan.FromMinutes(Math.Clamp(_opsSnapshotOptions.TimeToLiveMinutes, 2, 240));
                var notes = snapshot.Notes.ToList();
                if (!notes.Any(n => n.Contains("Shared DB ops snapshot", StringComparison.OrdinalIgnoreCase)))
                {
                    notes.Insert(
                        0,
                        $"Shared DB ops snapshot generated {snapshot.GeneratedAt:u} " +
                        "(live rebuild; saved for all users until next refresh).");
                    snapshot = snapshot with { Notes = notes };
                }

                await _opsSnapshots.UpsertReadyAsync(
                    organizationId,
                    IOpsSnapshotStore.DefaultFocusKey,
                    snapshot,
                    ttl,
                    cancellationToken);
            }
            catch
            {
                // Serving the live snapshot is enough if persistence fails.
            }
        }

        SnapshotCache[cacheKey] = (snapshot, DateTimeOffset.UtcNow.Add(SnapshotCacheTtl));
        return snapshot;
    }

    private static string FocusCacheKey(OverviewFocus focus) =>
        string.Join('|',
            focus.IncludeInbox,
            focus.IncludeTasks,
            focus.IncludeApprovals,
            focus.IncludeMaintainX,
            focus.IncludeMaintainXInventory,
            focus.IncludeEZRentOut,
            focus.IncludeMonday,
            focus.IncludeConnectorHealth,
            focus.CompletionLookbackDays);

    private static List<OverviewChatTurnDto> NormalizeChatTurns(IReadOnlyList<OverviewChatTurnDto>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return [];
        }

        var cleaned = new List<OverviewChatTurnDto>();
        foreach (var raw in messages.TakeLast(20))
        {
            var role = (raw.Role ?? string.Empty).Trim().ToLowerInvariant();
            if (role is not ("user" or "assistant"))
            {
                continue;
            }

            var content = (raw.Content ?? string.Empty).Trim();
            if (content.Length == 0)
            {
                continue;
            }

            if (content.Length > 4000)
            {
                content = content[..4000];
            }

            cleaned.Add(new OverviewChatTurnDto(role, content));
        }

        // Drop leading assistant turns; require a user question at the end.
        while (cleaned.Count > 0 && cleaned[0].Role == "assistant")
        {
            cleaned.RemoveAt(0);
        }

        if (cleaned.Count == 0 || cleaned[^1].Role != "user")
        {
            return [];
        }

        return cleaned;
    }

    private static string BuildFallbackNarrative(OverviewSnapshotDto snapshot, OverviewFocus focus)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Overview (facts only — AI not configured):");
        sb.AppendLine(BuildFactSheet(snapshot));
        if (!string.IsNullOrWhiteSpace(focus.CustomPrompt))
        {
            sb.AppendLine($"Focus note: {focus.CustomPrompt}");
        }

        return sb.ToString().Trim();
    }

    private static string BuildChatFactSheet(OverviewSnapshotDto snapshot, string question)
    {
        var terms = TokenizeQuestion(question);
        var sb = new StringBuilder();
        sb.AppendLine($"Totals: physicalOpen={snapshot.Counts.ExternalOpenWork}, onHold(physically done)={snapshot.Counts.OnHoldAwaitingClose}, completed={snapshot.Counts.RecentlyCompleted}, agingQuotes={snapshot.Counts.AgingQuotes}, inventoryOut={snapshot.Counts.InventoryOut}, inventoryLow={snapshot.Counts.InventoryLow}");
        sb.AppendLine($"Completion window: {snapshot.CompletionWindowLabel}");
        sb.AppendLine("Physical work = OPEN + IN_PROGRESS. ON_HOLD = physically finished (ignore unless asked).");
        sb.AppendLine("Use WO# numbers and people names exactly as written. Do not invent rows.");
        sb.AppendLine();

        var mxPhysical = snapshot.ExternalWorkSample
            .Where(i => i.SourceSystem == "MaintainX" && IsPhysicalActive(i))
            .ToList();
        var ezRentals = snapshot.ExternalWorkSample
            .Where(i => string.Equals(i.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine("## Workload by person (physical OPEN/IN_PROGRESS)");
        foreach (var group in mxPhysical
                     .GroupBy(i => i.Assignee ?? "Unassigned")
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key))
        {
            sb.AppendLine($"- {group.Key}: {group.Count()} physical");
        }

        sb.AppendLine();
        sb.AppendLine("## Workload by area (physical)");
        foreach (var group in mxPhysical
                     .GroupBy(i => i.Metadata?.GetValueOrDefault("area") ?? "Unknown")
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key))
        {
            sb.AppendLine($"- {group.Key}: {group.Count()} physical");
        }

        if (ezRentals.Count > 0 || snapshot.EzRentOrders.Count > 0)
        {
            var overdueCount = ezRentals.Count(i =>
                string.Equals(i.Status, "Overdue return", StringComparison.OrdinalIgnoreCase));
            var dailyTotal = ezRentals.Sum(ReadDailyRate);
            var now = DateTimeOffset.UtcNow;
            sb.AppendLine();
            sb.AppendLine("## EZRentOut");
            sb.AppendLine(
                $"Current checked-out assets: {ezRentals.Count} ({overdueCount} overdue); " +
                $"daily run-rate ${dailyTotal:0.##}/day (point-in-time list rates — NOT MTD/YTD).");
            if (ezRentals.Count > 0)
            {
                sb.AppendLine("### Current on-rent by customer (daily run-rate)");
                foreach (var row in BuildEzCustomerRollups(ezRentals).OrderByDescending(r => r.DailyTotal).Take(20))
                {
                    sb.AppendLine(
                        $"- {row.Customer}: ${row.DailyTotal:0.##}/day | {row.AssetCount} assets" +
                        (row.OverdueCount > 0 ? $" | {row.OverdueCount} overdue" : ""));
                }
            }

            AppendEzHistoryFactSheet(sb, snapshot.EzRentOrders, customer: FindEzCustomerMatch(
                question,
                BuildEzCustomerRollups(ezRentals).Concat(
                    BuildEzOrderCustomerNames(snapshot.EzRentOrders).Select(c =>
                        new EzCustomerRollup(c, 0, 0, 0, 0, 0, 0, 0, []))).ToList())?.Customer);
        }

        AppendMondayBilledFactSheet(
            sb,
            snapshot.QuotesSample,
            FindQuotePartyFilter(question, snapshot.QuotesSample));

        var scoredWork = mxPhysical
            .Select(i => (Item: i, Score: ScoreHaystack(WorkItemHaystack(i), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Title)
            .ToList();

        var scoredRentals = ezRentals
            .Select(i => (Item: i, Score: ScoreHaystack(WorkItemHaystack(i), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.DueAt ?? DateTimeOffset.MaxValue)
            .ToList();

        var scoredCompleted = snapshot.RecentlyCompleted
            .Select(i => (Item: i, Score: ScoreHaystack(WorkItemHaystack(i), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        var scoredQuotes = snapshot.QuotesSample
            .Select(q => (Item: q, Score: ScoreHaystack(QuoteHaystack(q), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        var scoredInventory = snapshot.InventoryAlerts
            .Select(a => (Item: a, Score: ScoreHaystack(InventoryHaystack(a), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.AvailableQuantity)
            .ToList();

        var hasMatches = scoredWork.Count > 0 || scoredRentals.Count > 0 || scoredCompleted.Count > 0 ||
                         scoredQuotes.Count > 0 || scoredInventory.Count > 0;

        if (terms.Count > 0 && hasMatches)
        {
            sb.AppendLine();
            sb.AppendLine("## Matches for this question");
            foreach (var hit in scoredWork.Take(28))
            {
                sb.AppendLine($"- {FormatWorkLine(hit.Item)}");
            }

            foreach (var hit in scoredRentals.Take(16))
            {
                sb.AppendLine($"- RENTAL | {FormatWorkLine(hit.Item)}");
            }

            foreach (var hit in scoredCompleted.Take(12))
            {
                sb.AppendLine($"- COMPLETED | {FormatWorkLine(hit.Item)}");
            }

            foreach (var hit in scoredQuotes.Take(16))
            {
                sb.AppendLine($"- QUOTE | {FormatQuoteLine(hit.Item)}");
            }

            foreach (var hit in scoredInventory.Take(20))
            {
                sb.AppendLine($"- INV {hit.Item.Severity.ToUpperInvariant()} | {hit.Item.EnvironmentName} | {hit.Item.Name} | avail={hit.Item.AvailableQuantity} min={hit.Item.MinimumQuantity}");
            }
        }
        else if (terms.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Matches for this question");
            sb.AppendLine("- (no WO / rental / quote / inventory rows matched the question tokens — see rollups and lists below)");
        }

        var matchedIds = scoredWork.Select(x => x.Item.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sb.AppendLine();
        sb.AppendLine("## Physical WO lines");
        var remaining = mxPhysical
            .Where(i => !matchedIds.Contains(i.ExternalId))
            .Take(terms.Count > 0 && scoredWork.Count > 0 ? 24 : 48);
        foreach (var item in remaining)
        {
            sb.AppendLine($"- {FormatWorkLine(item)}");
        }

        if (ezRentals.Count > 0)
        {
            var rentalMatched = scoredRentals.Select(x => x.Item.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var customerMatch = FindEzCustomerMatch(question, BuildEzCustomerRollups(ezRentals));
            sb.AppendLine();
            sb.AppendLine("## EZRentOut checked-out assets");
            IEnumerable<ExternalWorkItemDto> rentalLines = ezRentals
                .Where(i => !rentalMatched.Contains(i.ExternalId));
            if (customerMatch is not null)
            {
                rentalLines = customerMatch.Items.Where(i => !rentalMatched.Contains(i.ExternalId));
                sb.AppendLine($"Filtered to customer: {customerMatch.Customer} (${customerMatch.DailyTotal:0.##}/day).");
            }

            foreach (var item in rentalLines
                         .OrderByDescending(ReadDailyRate)
                         .ThenBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
                         .Take(customerMatch is not null ? 30 : (terms.Count > 0 && scoredRentals.Count > 0 ? 12 : 20)))
            {
                sb.AppendLine($"- {FormatWorkLine(item)}");
            }
        }

        if (snapshot.RecentlyCompleted.Count > 0)
        {
            var completedMatched = scoredCompleted.Select(x => x.Item.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            sb.AppendLine();
            sb.AppendLine($"## Completed lines ({snapshot.CompletionWindowLabel})");
            foreach (var item in snapshot.RecentlyCompleted
                         .Where(i => !completedMatched.Contains(i.ExternalId))
                         .Take(16))
            {
                sb.AppendLine($"- {FormatWorkLine(item)}");
            }
        }

        if (snapshot.QuotesSample.Count > 0)
        {
            var quoteMatched = scoredQuotes.Select(x => x.Item.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            sb.AppendLine();
            sb.AppendLine("## Quote lines");
            foreach (var q in snapshot.QuotesSample
                         .Where(q => !quoteMatched.Contains(q.ExternalId))
                         .Take(terms.Count > 0 && scoredQuotes.Count > 0 ? 12 : 28))
            {
                sb.AppendLine($"- {FormatQuoteLine(q)}");
            }
        }

        if (snapshot.InventoryAlerts.Count > 0)
        {
            var invMatched = scoredInventory.Select(x => $"{x.Item.EnvironmentName}:{x.Item.PartId}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            sb.AppendLine();
            sb.AppendLine("## Inventory OUT (worst first)");
            foreach (var a in snapshot.InventoryAlerts
                         .Where(x => x.Severity == "Out")
                         .Where(x => !invMatched.Contains($"{x.EnvironmentName}:{x.PartId}"))
                         .OrderBy(x => x.AvailableQuantity)
                         .Take(20))
            {
                sb.AppendLine($"- OUT | {a.EnvironmentName} | {a.Name} | avail={a.AvailableQuantity} min={a.MinimumQuantity}");
            }

            sb.AppendLine();
            sb.AppendLine("## Inventory LOW");
            foreach (var a in snapshot.InventoryAlerts
                         .Where(x => x.Severity == "Low")
                         .Where(x => !invMatched.Contains($"{x.EnvironmentName}:{x.PartId}"))
                         .OrderBy(x => x.AvailableQuantity - x.MinimumQuantity)
                         .Take(16))
            {
                sb.AppendLine($"- LOW | {a.EnvironmentName} | {a.Name} | avail={a.AvailableQuantity} min={a.MinimumQuantity}");
            }
        }

        return sb.ToString().Trim();
    }

    private async Task<OverviewSnapshotDto> EnrichSnapshotForQuestionAsync(
        OverviewSnapshotDto snapshot,
        string question,
        CancellationToken cancellationToken)
    {
        var terms = TokenizeQuestion(question);
        if (terms.Count == 0)
        {
            return snapshot;
        }

        var topMatches = snapshot.ExternalWorkSample
            .Where(i => i.SourceSystem == "MaintainX")
            .Select(i => (Item: i, Score: ScoreHaystack(WorkItemHaystack(i), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .Where(i => string.IsNullOrWhiteSpace(i.Metadata?.GetValueOrDefault("comments")))
            .Take(10)
            .ToList();

        if (topMatches.Count == 0)
        {
            return snapshot;
        }

        var notes = new List<string>();
        var enriched = await EnrichMaintainXCommentsAsync(topMatches, notes, cancellationToken);
        var byId = enriched.ToDictionary(i => i.ExternalId, StringComparer.OrdinalIgnoreCase);
        var work = snapshot.ExternalWorkSample
            .Select(i => byId.TryGetValue(i.ExternalId, out var e) ? e : i)
            .ToList();

        return snapshot with { ExternalWorkSample = work };
    }

    private static string FormatWorkLine(ExternalWorkItemDto item)
    {
        if (string.Equals(item.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
        {
            var due = item.DueAt?.ToString("yyyy-MM-dd") ?? "no due date";
            var customer = item.Assignee
                ?? item.Metadata?.GetValueOrDefault("customer")
                ?? "Unassigned customer";
            var daily = item.Metadata?.GetValueOrDefault("dailyRate");
            var monthly = FormatMoneyAmount(ReadMonthlyRate(item));
            var dailyBit = string.IsNullOrWhiteSpace(daily) ? "" : $" | ${daily}/day";
            var monthlyBit = string.IsNullOrWhiteSpace(monthly) ? "" : $" | ${monthly}/mo";
            var location = item.Metadata?.GetValueOrDefault("location");
            var locationBit = string.IsNullOrWhiteSpace(location) ? "" : $" | {location}";
            return $"{FormatAssetLabel(item)} | {customer}{dailyBit}{monthlyBit} | {item.Status} | due={due}{locationBit} | {item.Title}";
        }

        var area = item.Metadata?.GetValueOrDefault("area") ?? "";
        var who = string.IsNullOrWhiteSpace(item.Assignee) ? "Unassigned" : item.Assignee;
        var quoteBit = string.IsNullOrWhiteSpace(item.Metadata?.GetValueOrDefault("linkedQuotes"))
            ? ""
            : $" | quotes: {item.Metadata!["linkedQuotes"]}";
        var comments = item.Metadata?.GetValueOrDefault("comments");
        var commentBit = string.IsNullOrWhiteSpace(comments) ? "" : $" | comments: {comments}";
        return $"{FormatWoLabel(item)} | {area} | {item.Status} | {who} | {item.Title}{quoteBit}{commentBit}";
    }

    private static string FormatQuoteLine(ExternalWorkItemDto quote)
    {
        var age = quote.Metadata?.GetValueOrDefault("ageDays") ?? "?";
        var region = quote.Metadata?.GetValueOrDefault("region");
        var owner = string.IsNullOrWhiteSpace(quote.Assignee) ? "Unassigned" : quote.Assignee;
        var regionBit = string.IsNullOrWhiteSpace(region) ? "" : $" | {region}";
        var number = quote.Metadata?.GetValueOrDefault("quoteNumber");
        var numberBit = string.IsNullOrWhiteSpace(number) ? "" : $" #{number}";
        var amount = quote.Metadata?.GetValueOrDefault("amountText");
        var amountBit = string.IsNullOrWhiteSpace(amount) ? "" : $" | {amount}";
        var customer = quote.Metadata?.GetValueOrDefault("customer");
        var customerBit = string.IsNullOrWhiteSpace(customer) ? "" : $" | {customer}";
        var project = quote.Metadata?.GetValueOrDefault("project");
        var projectBit = string.IsNullOrWhiteSpace(project) ? "" : $" | project:{project}";
        var lines = quote.Metadata?.GetValueOrDefault("subitemCount");
        var linesBit = string.IsNullOrWhiteSpace(lines) || lines == "0" ? "" : $" | {lines} lines";
        var summary = quote.Metadata?.GetValueOrDefault("subitemSummary");
        var summaryBit = string.IsNullOrWhiteSpace(summary) ? "" : $" || lines: {summary}";
        var scope = quote.Metadata?.GetValueOrDefault("scopeOfWork");
        var scopeBit = string.IsNullOrWhiteSpace(scope) ? "" : $" || scope: {scope}";
        return $"[{quote.Status}] {age}d{regionBit}{numberBit}{amountBit}{customerBit}{projectBit}{linesBit} | {owner}: {quote.Title}{FormatQuoteMxLink(quote)}{scopeBit}{summaryBit}";
    }

    private static string FormatBilledQuoteLine(ExternalWorkItemDto quote)
    {
        var billedDate = quote.Metadata?.GetValueOrDefault("billedDate");
        var dateBit = string.IsNullOrWhiteSpace(billedDate) ? "" : $" billed {billedDate}";
        var number = quote.Metadata?.GetValueOrDefault("quoteNumber");
        var numberBit = string.IsNullOrWhiteSpace(number) ? "" : $" #{number}";
        var amount = quote.Metadata?.GetValueOrDefault("amountText");
        var amountBit = string.IsNullOrWhiteSpace(amount) ? "" : $" | {amount}";
        var region = quote.Metadata?.GetValueOrDefault("region");
        var regionBit = string.IsNullOrWhiteSpace(region) ? "" : $" | {region}";
        var owner = string.IsNullOrWhiteSpace(quote.Assignee) ? "Unassigned" : quote.Assignee;
        var so = quote.Metadata?.GetValueOrDefault("soNumber");
        var soBit = string.IsNullOrWhiteSpace(so) ? "" : $" | SO {so}";
        var sap = quote.Metadata?.GetValueOrDefault("sapInvoice");
        var sapBit = string.IsNullOrWhiteSpace(sap) ? "" : $" | SAP {sap}";
        var po = quote.Metadata?.GetValueOrDefault("poNumber");
        var poBit = string.IsNullOrWhiteSpace(po) ? "" : $" | PO {po}";
        var type = quote.Metadata?.GetValueOrDefault("quoteType");
        var typeBit = string.IsNullOrWhiteSpace(type) ? "" : $" | {type}";
        return $"[{quote.Status}]{dateBit}{numberBit}{amountBit}{regionBit}{typeBit}{soBit}{sapBit}{poBit} | {owner}: {quote.Title}";
    }

    private static List<string> TokenizeQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return [];
        }

        return System.Text.RegularExpressions.Regex.Matches(question.ToLowerInvariant(), @"[a-z0-9][a-z0-9\-]{1,}")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2 && !ChatStopWords.Contains(t))
            .Distinct()
            .Take(28)
            .ToList();
    }

    private static double ScoreHaystack(string haystack, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || string.IsNullOrWhiteSpace(haystack))
        {
            return 0;
        }

        var lower = haystack.ToLowerInvariant();
        double score = 0;
        foreach (var term in terms)
        {
            var idx = 0;
            var hits = 0;
            while ((idx = lower.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
            {
                hits++;
                idx += term.Length;
                if (hits > 6)
                {
                    break;
                }
            }

            if (hits > 0)
            {
                // Prefer exact-ish identifiers / names.
                score += hits * (term.Length >= 5 ? 1.6 : term.Length >= 3 ? 1.1 : 0.7);
            }
        }

        return score;
    }

    private static string WorkItemHaystack(ExternalWorkItemDto item)
    {
        var parts = new List<string?>
        {
            item.SourceSystem,
            item.Title,
            item.Assignee,
            item.Status,
            item.EnvironmentName,
            FormatWoLabel(item),
            item.Metadata?.GetValueOrDefault("area"),
            item.Metadata?.GetValueOrDefault("sequentialId"),
            item.Metadata?.GetValueOrDefault("assetName"),
            item.Metadata?.GetValueOrDefault("rawState"),
            item.Metadata?.GetValueOrDefault("checkoutOn"),
            item.Metadata?.GetValueOrDefault("description"),
            item.Metadata?.GetValueOrDefault("comments"),
            item.Metadata?.GetValueOrDefault("linkedQuotes"),
            item.Metadata?.GetValueOrDefault("rawStatus"),
        };

        if (string.Equals(item.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(FormatAssetLabel(item));
            parts.Add(item.Metadata?.GetValueOrDefault("customer"));
            parts.Add(item.Metadata?.GetValueOrDefault("location"));
            parts.Add(item.Metadata?.GetValueOrDefault("dailyRate"));
            parts.Add(item.Metadata?.GetValueOrDefault("monthlyRate"));
            parts.Add(item.Metadata?.GetValueOrDefault("weeklyRate"));
            parts.Add("ezrentout rental checked out asset equipment on rent daily monthly yearly annual");
        }

        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string QuoteHaystack(ExternalWorkItemDto quote) =>
        string.Join(
            ' ',
            quote.Title,
            quote.Assignee,
            quote.Status,
            quote.Metadata?.GetValueOrDefault("region"),
            quote.Metadata?.GetValueOrDefault("quoteNumber"),
            quote.Metadata?.GetValueOrDefault("project"),
            quote.Metadata?.GetValueOrDefault("customer"),
            quote.Metadata?.GetValueOrDefault("contact"),
            quote.Metadata?.GetValueOrDefault("scopeOfWork"),
            quote.Metadata?.GetValueOrDefault("quoteComments"),
            quote.Metadata?.GetValueOrDefault("subitemSummary"),
            quote.Metadata?.GetValueOrDefault("amount"),
            quote.Metadata?.GetValueOrDefault("amountText"),
            quote.Metadata?.GetValueOrDefault("poNumber"),
            quote.Metadata?.GetValueOrDefault("soNumber"),
            quote.Metadata?.GetValueOrDefault("sapInvoice"),
            quote.Metadata?.GetValueOrDefault("billedAt"),
            quote.Metadata?.GetValueOrDefault("billedMonth"),
            quote.Metadata?.GetValueOrDefault("billedDate"),
            quote.Metadata?.GetValueOrDefault("bucket"),
            quote.Metadata?.GetValueOrDefault("partsLabor"),
            quote.Metadata?.GetValueOrDefault("dayRate"),
            quote.Metadata?.GetValueOrDefault("maintainXWoNumber"),
            quote.Metadata?.GetValueOrDefault("maintainXTitle"),
            quote.Metadata?.GetValueOrDefault("maintainXAssignee"),
            "converted billed monday quote");

    private static string InventoryHaystack(InventoryAlertDto alert) =>
        string.Join(' ', alert.Name, alert.EnvironmentName, alert.Area, alert.PartId, alert.Severity);

    private static readonly HashSet<string> ChatStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "was", "one",
        "our", "out", "has", "have", "been", "from", "they", "with", "this", "that", "what", "when",
        "where", "which", "while", "about", "into", "than", "then", "them", "these", "those", "will",
        "would", "could", "should", "their", "there", "here", "just", "like", "also", "only", "give",
        "me", "brief", "ops", "recap", "today", "right", "now", "most", "who", "how", "many", "any",
        "please", "show", "list", "tell", "need", "know", "looking", "find", "get", "does", "did"
    };

    private async Task<(string Block, IReadOnlyList<KnowledgeSourceDto> Sources)> BuildKnowledgeBlockAsync(
        Guid organizationId,
        string query,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("KNOWLEDGE EXCERPTS:");
        IReadOnlyList<KnowledgeSourceDto> sources = [];
        try
        {
            var excerpts = await _knowledge.SearchAsync(organizationId, query, limit: 8, cancellationToken);
            if (excerpts.Count == 0)
            {
                sb.AppendLine("(none matched — no indexed docs or no overlap with the query)");
                var catalog = await _knowledge.ListIndexedCatalogAsync(organizationId, limit: 40, cancellationToken);
                if (catalog.Count > 0)
                {
                    sb.AppendLine("INDEXED KNOWLEDGE LIBRARY (titles — ask again with a document name if relevant):");
                    foreach (var item in catalog)
                    {
                        sb.AppendLine($"- {item.Title}");
                    }
                }
            }
            else
            {
                sources = excerpts
                    .GroupBy(e => e.DocumentId)
                    .Select(g =>
                    {
                        var first = g.First();
                        return new KnowledgeSourceDto(first.DocumentId, first.Title, first.FileName);
                    })
                    .Take(6)
                    .ToList();

                foreach (var excerpt in excerpts)
                {
                    var tagBit = string.IsNullOrWhiteSpace(excerpt.Tags)
                        ? ""
                        : $" tags={TrimCatalogTags(excerpt.Tags)}";
                    sb.AppendLine(
                        $"- [{excerpt.Title} · {excerpt.FileName} #{excerpt.Ordinal} score={excerpt.Score:0.0}{tagBit}] {excerpt.Text}");
                }
            }
        }
        catch
        {
            sb.AppendLine("(knowledge search unavailable)");
        }

        sb.AppendLine();
        sb.AppendLine("PRIOR ASK HISTORY (org learning):");
        try
        {
            var history = await _askHistory.SearchAsync(organizationId, query, limit: 5, cancellationToken);
            if (history.Count == 0)
            {
                sb.AppendLine("(no prior Ask chats matched)");
            }
            else
            {
                foreach (var hit in history)
                {
                    sb.AppendLine($"- [{hit.SessionTitle} · {hit.Role}] {hit.Text}");
                }
            }
        }
        catch
        {
            sb.AppendLine("(ask history unavailable)");
        }

        return (sb.ToString().TrimEnd(), sources);
    }

    private static string BuildKnowledgeSearchQuery(string question, IReadOnlyList<OverviewChatTurnDto> turns)
    {
        if (!WantsKnowledgeSourceDocument(question) || turns.Count <= 1)
        {
            return question;
        }

        // Follow-ups like "send me that PDF" need prior question context for retrieval.
        var prior = turns
            .Take(turns.Count - 1)
            .Reverse()
            .Where(t => t.Role is "user" or "assistant")
            .Take(4)
            .Reverse()
            .Select(t => t.Content);
        return string.Join("\n", prior.Append(question));
    }

    private static bool WantsKnowledgeSourceDocument(string question)
    {
        var q = question.Trim().ToLowerInvariant();
        return q.Contains("download") ||
               q.Contains("source document") ||
               q.Contains("source file") ||
               q.Contains("original document") ||
               q.Contains("original pdf") ||
               q.Contains("original file") ||
               q.Contains("send me the pdf") ||
               q.Contains("send me the file") ||
               q.Contains("send me that") ||
               q.Contains("give me the pdf") ||
               q.Contains("give me the file") ||
               q.Contains("give me the document") ||
               q.Contains("get me the pdf") ||
               q.Contains("get me the file") ||
               q.Contains("can i get the") ||
               q.Contains("attach the") ||
               q.Contains("link to the") ||
               (q.Contains("source") && (q.Contains("pdf") || q.Contains("doc") || q.Contains("file") ||
                                         q.Contains("manual") || q.Contains("that")));
    }

    private static bool LooksLikeSourceOnlyRequest(string question)
    {
        var q = question.Trim().ToLowerInvariant();
        // Short asks that are mainly about fetching the file, not explaining contents.
        if (q.Length > 120)
        {
            return false;
        }

        return WantsKnowledgeSourceDocument(question) &&
               !(q.Contains("how do") || q.Contains("how to") || q.Contains("explain") ||
                 q.Contains("what does") || q.Contains("summarize") || q.Contains("walk me"));
    }

    private static string BuildKnowledgeSourceReply(IReadOnlyList<KnowledgeSourceDto> sources)
    {
        if (sources.Count == 1)
        {
            return $"Here's the source document — use Preview below to view “{sources[0].Title}” ({sources[0].FileName}), then Download from the previewer if you want a copy.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Here are the matching source documents — use Preview below to view them (Download is in the previewer):");
        foreach (var s in sources)
        {
            sb.AppendLine($"- {s.Title} ({s.FileName})");
        }

        return sb.ToString().TrimEnd();
    }

    private static string AppendKnowledgeSourceMarkers(
        string reply,
        IReadOnlyList<KnowledgeSourceDto> sources)
    {
        if (sources.Count == 0)
        {
            return reply;
        }

        var sb = new StringBuilder(reply.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("‹knowledge-sources›");
        foreach (var s in sources)
        {
            // Pipe-delimited: id|title|fileName — UI parses this into Preview buttons.
            sb.AppendLine($"{s.DocumentId:D}|{SanitizeSourceField(s.Title)}|{SanitizeSourceField(s.FileName)}");
        }

        sb.Append("‹/knowledge-sources›");
        return sb.ToString();
    }

    private static string SanitizeSourceField(string value) =>
        value.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string TrimCatalogTags(string tags) =>
        tags.Length <= 120 ? tags : tags[..120] + "…";

    private static bool LooksLikeKnowledgeQuestion(string question)
    {
        var q = question.Trim().ToLowerInvariant();
        if (q.Length < 8)
        {
            return false;
        }

        // Live ops questions should stay on the fact-sheet path.
        if (q.Contains("who has") || q.Contains("how many") || q.Contains("open work") ||
            q.Contains("on rent") || q.Contains("on-rent") || q.Contains("ezrent") ||
            q.Contains("quote") || q.Contains("billed") || q.Contains("inventory") ||
            q.Contains("work order") || q.Contains("wo#") || q.Contains("maintainx"))
        {
            return false;
        }

        return q.Contains("how do") || q.Contains("how to") || q.Contains("procedure") ||
               q.Contains("manual") || q.Contains("tutorial") || q.Contains("datasheet") ||
               q.Contains("wiring") || q.Contains("schematic") || q.Contains("install") ||
               q.Contains("setup") || q.Contains("set up") || q.Contains("harness") ||
               q.Contains("modbus") || q.Contains("vfd") || q.Contains("plc") ||
               q.Contains("pdf") || q.Contains("documentation") || q.Contains("sop") ||
               q.Contains("knowledge") || q.Contains("according to") || q.Contains("in the doc") ||
               q.Contains("strap") || q.Contains("iso tank");
    }

    private static string BuildFactSheet(OverviewSnapshotDto snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated: {snapshot.GeneratedAt:u}");
        sb.AppendLine($"Completion window: {snapshot.CompletionWindowLabel}");
        sb.AppendLine(
            $"Totals: physicalOpen={snapshot.Counts.ExternalOpenWork}, onHold(physically done)={snapshot.Counts.OnHoldAwaitingClose}, completed={snapshot.Counts.RecentlyCompleted}, agingQuotes={snapshot.Counts.AgingQuotes}, quotesLinkedToMX={snapshot.Counts.QuotesWithMaintainXLink}, inventoryOut={snapshot.Counts.InventoryOut}, inventoryLow={snapshot.Counts.InventoryLow}");
        sb.AppendLine("Status: OPEN/IN_PROGRESS=physical work still open (PRIMARY). ON_HOLD=physically finished — back office can close anytime (ignore unless asked). DONE=closed.");
        sb.AppendLine("Cite tickets as WO#<number> (never raw database ids). Cite people by name.");
        sb.AppendLine();

        var mxAll = snapshot.ExternalWorkSample.Where(i => i.SourceSystem == "MaintainX").ToList();
        var mxPhysical = mxAll.Where(IsPhysicalActive).ToList();
        var mxOnHold = mxAll.Where(IsOnHold).ToList();

        sb.AppendLine("## Physical work by person (OPEN / IN_PROGRESS)");
        if (mxPhysical.Count == 0)
        {
            sb.AppendLine("- none in sample");
        }
        else
        {
            foreach (var group in mxPhysical.GroupBy(i => i.Assignee ?? "Unassigned").OrderBy(g => g.Key))
            {
                sb.AppendLine($"{group.Key} ({group.Count()}):");
                foreach (var item in group.Take(10))
                {
                    AppendMxLine(sb, item, includeDesc: true);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Physical work by area");
        foreach (var group in mxPhysical.GroupBy(i => i.Metadata?.GetValueOrDefault("area") ?? "Unknown").OrderBy(g => g.Key))
        {
            sb.AppendLine($"{group.Key}: {group.Count()} in sample");
            foreach (var item in group.Take(8))
            {
                AppendMxLine(sb, item, includeDesc: false);
            }
        }

        if (mxOnHold.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## ON_HOLD (physically done — FYI only, not a field backlog)");
            sb.AppendLine($"Count in sample: {mxOnHold.Count}. Do not treat as open physical work.");
            foreach (var item in mxOnHold.Take(6))
            {
                AppendMxLine(sb, item, includeDesc: false);
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## Completed ({snapshot.CompletionWindowLabel})");
        if (snapshot.RecentlyCompleted.Count == 0)
        {
            sb.AppendLine("- none in window");
        }
        else
        {
            foreach (var group in snapshot.RecentlyCompleted
                         .GroupBy(i => i.Metadata?.GetValueOrDefault("area") ?? i.EnvironmentName ?? "Unknown")
                         .OrderBy(g => g.Key))
            {
                sb.AppendLine($"{group.Key}:");
                foreach (var item in group.Take(10))
                {
                    AppendMxLine(sb, item, includeDesc: false);
                    var comments = item.Metadata?.GetValueOrDefault("comments");
                    if (!string.IsNullOrWhiteSpace(comments))
                    {
                        sb.AppendLine($"      notes: {comments}");
                    }
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Monday Quotes");
        var quotes = snapshot.QuotesSample;
        if (quotes.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            void QuoteBucket(string label, Func<ExternalWorkItemDto, bool> pred)
            {
                var rows = quotes.Where(pred).Take(14).ToList();
                sb.AppendLine($"{label} ({rows.Count} shown):");
                if (rows.Count == 0)
                {
                    sb.AppendLine("  - none");
                    return;
                }

                foreach (var q in rows)
                {
                    var age = q.Metadata?.GetValueOrDefault("ageDays") ?? "?";
                    var region = q.Metadata?.GetValueOrDefault("region");
                    var owner = string.IsNullOrWhiteSpace(q.Assignee) ? "Unassigned" : q.Assignee;
                    var regionBit = string.IsNullOrWhiteSpace(region) ? "" : $" | {region}";
                    var mxBit = FormatQuoteMxLink(q);
                    sb.AppendLine($"  - [{q.Status}] {age}d{regionBit} | {owner}: {q.Title}{mxBit}");
                }
            }

            QuoteBucket("Aging Sent/Draft", q => q.Metadata?.GetValueOrDefault("aging") == "true");
            QuoteBucket(
                "Linked to physical MX work",
                q => q.Metadata?.GetValueOrDefault("crossRefPhysicalMaintainX") == "true");
            QuoteBucket(
                "Linked to ON_HOLD MX (physically done)",
                q => q.Metadata?.GetValueOrDefault("crossRefOnHoldMaintainX") == "true");
            QuoteBucket("Draft opportunities", q => q.Metadata?.GetValueOrDefault("bucket") == "draft_opportunity");
            QuoteBucket(
                "Ready to be billed",
                q => q.Metadata?.GetValueOrDefault("bucket") == "ready_to_bill" ||
                     string.Equals(q.Status, "Ready to be Billed", StringComparison.OrdinalIgnoreCase));
            QuoteBucket(
                "Other MX-linked",
                q => !string.IsNullOrWhiteSpace(q.Metadata?.GetValueOrDefault("maintainXWorkOrderId"))
                     && q.Metadata?.GetValueOrDefault("crossRefPhysicalMaintainX") != "true"
                     && q.Metadata?.GetValueOrDefault("crossRefOnHoldMaintainX") != "true");
            QuoteBucket("Other pipeline", q => q.Metadata?.GetValueOrDefault("bucket") == "pipeline");
        }

        AppendMondayBilledFactSheet(sb, quotes, partyFilter: null);

        sb.AppendLine();
        sb.AppendLine("## EZRentOut");
        var ezRentals = snapshot.ExternalWorkSample
            .Where(i => string.Equals(i.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (ezRentals.Count == 0 && snapshot.EzRentOrders.Count == 0)
        {
            sb.AppendLine("- none (or source disabled)");
        }
        else
        {
            var overdue = ezRentals.Count(i =>
                string.Equals(i.Status, "Overdue return", StringComparison.OrdinalIgnoreCase));
            var dailyTotal = ezRentals.Sum(ReadDailyRate);
            sb.AppendLine(
                $"Current checked-out assets: {ezRentals.Count} ({overdue} overdue); " +
                $"daily run-rate ${dailyTotal:0.##}/day (point-in-time — NOT historical MTD/YTD).");
            if (ezRentals.Count > 0)
            {
                sb.AppendLine("Current on-rent by customer (daily run-rate):");
                foreach (var row in BuildEzCustomerRollups(ezRentals).OrderByDescending(r => r.DailyTotal).Take(20))
                {
                    sb.AppendLine(
                        $"  - {row.Customer}: ${row.DailyTotal:0.##}/day | {row.AssetCount} assets" +
                        (row.OverdueCount > 0 ? $" | {row.OverdueCount} overdue" : ""));
                }

                sb.AppendLine("Sample assets (highest daily $):");
                foreach (var item in ezRentals.OrderByDescending(ReadDailyRate).Take(12))
                {
                    sb.AppendLine($"  - {FormatWorkLine(item)}");
                }
            }

            AppendEzHistoryFactSheet(sb, snapshot.EzRentOrders, customer: null);
        }

        sb.AppendLine();
        sb.AppendLine("## Inventory alerts (name + qty — no part database ids)");
        if (snapshot.InventoryAlerts.Count == 0)
        {
            sb.AppendLine("- none (or no minima configured)");
        }
        else
        {
            foreach (var group in snapshot.InventoryAlerts.GroupBy(a => a.EnvironmentName).OrderBy(g => g.Key))
            {
                var outs = group.Where(a => a.Severity == "Out").ToList();
                var lows = group.Where(a => a.Severity == "Low").ToList();
                sb.AppendLine($"{group.Key}: out={outs.Count} shown, low={lows.Count} shown");
                foreach (var a in outs.Take(16))
                {
                    sb.AppendLine(
                        $"  - OUT {a.Name}: avail={a.AvailableQuantity}, min={a.MinimumQuantity}" +
                        (string.IsNullOrWhiteSpace(a.Area) ? "" : $", area={a.Area}"));
                }

                foreach (var a in lows.Take(16))
                {
                    sb.AppendLine(
                        $"  - LOW {a.Name}: avail={a.AvailableQuantity}, min={a.MinimumQuantity}" +
                        (string.IsNullOrWhiteSpace(a.Area) ? "" : $", area={a.Area}"));
                }
            }
        }

        if (snapshot.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## System notes");
            foreach (var note in snapshot.Notes.Take(6))
            {
                sb.AppendLine($"- {note}");
            }
        }

        return sb.ToString().Trim();
    }

    private static void AppendMxLine(StringBuilder sb, ExternalWorkItemDto item, bool includeDesc)
    {
        var area = item.Metadata?.GetValueOrDefault("area") ?? item.EnvironmentName ?? "";
        var wo = FormatWoLabel(item);
        var who = string.IsNullOrWhiteSpace(item.Assignee) ? "Unassigned" : item.Assignee;
        sb.AppendLine($"  - {wo} | {area} | [{item.Status}] | {who} | {item.Title}");
        if (!includeDesc)
        {
            return;
        }

        var desc = item.Metadata?.GetValueOrDefault("description");
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.AppendLine($"      desc: {desc}");
        }

        var comments = item.Metadata?.GetValueOrDefault("comments");
        if (!string.IsNullOrWhiteSpace(comments))
        {
            sb.AppendLine($"      notes: {comments}");
        }

        var linkedQuotes = item.Metadata?.GetValueOrDefault("linkedQuotes");
        if (!string.IsNullOrWhiteSpace(linkedQuotes))
        {
            sb.AppendLine($"      linked quotes: {linkedQuotes}");
        }
    }

    private static string FormatWoLabel(ExternalWorkItemDto item)
    {
        if (string.Equals(item.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
        {
            return FormatAssetLabel(item);
        }

        var seq = item.Metadata?.GetValueOrDefault("sequentialId");
        return !string.IsNullOrWhiteSpace(seq) ? $"WO#{seq}" : "WO#(unknown)";
    }

    private static string FormatAssetLabel(ExternalWorkItemDto item)
    {
        var name = item.Metadata?.GetValueOrDefault("assetName");
        if (!string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(item.ExternalId) &&
            !string.Equals(name, item.ExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return $"Asset:{name} ({item.ExternalId})";
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"Asset:{name}";
        }

        return string.IsNullOrWhiteSpace(item.ExternalId) ? "Asset:(unknown)" : $"Asset:{item.ExternalId}";
    }

    private static string FormatQuoteMxLink(ExternalWorkItemDto quote)
    {
        var woNumber = quote.Metadata?.GetValueOrDefault("maintainXWoNumber");
        var title = quote.Metadata?.GetValueOrDefault("maintainXTitle");
        var physical = quote.Metadata?.GetValueOrDefault("crossRefPhysicalMaintainX") == "true";
        var onHold = quote.Metadata?.GetValueOrDefault("crossRefOnHoldMaintainX") == "true";
        var titleBit = string.IsNullOrWhiteSpace(title) ? "" : $" {TrimTitle(title, 48)}";

        if (!string.IsNullOrWhiteSpace(woNumber))
        {
            if (physical)
            {
                return $" | linked physical WO#{woNumber}{titleBit}";
            }

            if (onHold)
            {
                return $" | linked ON_HOLD (done) WO#{woNumber}{titleBit}";
            }

            return $" | linked WO#{woNumber}{titleBit}";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            if (physical)
            {
                return $" | linked physical MX ({TrimTitle(title, 48)})";
            }

            if (onHold)
            {
                return $" | linked ON_HOLD MX ({TrimTitle(title, 48)})";
            }

            return $" | linked MX ({TrimTitle(title, 48)})";
        }

        var raw = quote.Metadata?.GetValueOrDefault("maintainXWorkOrderId");
        return string.IsNullOrWhiteSpace(raw) ? "" : " | linked MX (number not in current open/completed sample)";
    }

    private static string TrimTitle(string title, int max) =>
        title.Length <= max ? title : title[..max] + "…";

    private static string RawStatus(ExternalWorkItemDto item) =>
        item.Metadata?.GetValueOrDefault("rawStatus")?.Trim() ?? "";

    private static bool IsPhysicalActive(ExternalWorkItemDto item)
    {
        if (!string.Equals(item.SourceSystem, "MaintainX", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var raw = RawStatus(item);
        return raw.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("IN PROGRESS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOnHold(ExternalWorkItemDto item)
    {
        var raw = RawStatus(item);
        return raw.Equals("ON_HOLD", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("ON HOLD", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record EzCustomerRollup(
        string Customer,
        decimal DailyTotal,
        decimal WeeklyTotal,
        decimal MonthlyTotal,
        decimal YearlyTotal,
        decimal RentCollectedTotal,
        int AssetCount,
        int OverdueCount,
        IReadOnlyList<ExternalWorkItemDto> Items);

    private sealed record EzLocationRollup(
        string Location,
        decimal DailyTotal,
        decimal MonthlyTotal,
        decimal YearlyTotal,
        int AssetCount);

    private sealed record EzHistoryPeriod(string Label, DateTimeOffset Start, DateTimeOffset EndExclusive);

    private static DateTimeOffset StartOfMonth(DateTimeOffset at) =>
        new(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset StartOfYear(DateTimeOffset at) =>
        new(at.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static EzHistoryPeriod DefaultHistoryPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        return new EzHistoryPeriod("MTD", StartOfMonth(now), StartOfMonth(now).AddMonths(1));
    }

    private static EzHistoryPeriod? DetectEzHistoryPeriod(string q)
    {
        var now = DateTimeOffset.UtcNow;
        if (q.Contains("mtd") || q.Contains("month to date") || q.Contains("month-to-date"))
        {
            return new EzHistoryPeriod("MTD", StartOfMonth(now), StartOfMonth(now).AddMonths(1));
        }

        if (q.Contains("ytd") || q.Contains("year to date") || q.Contains("year-to-date"))
        {
            return new EzHistoryPeriod("YTD", StartOfYear(now), StartOfYear(now).AddYears(1));
        }

        if (q.Contains("last month") || q.Contains("prior month") || q.Contains("previous month"))
        {
            var start = StartOfMonth(now).AddMonths(-1);
            return new EzHistoryPeriod(start.ToString("MMM yyyy"), start, start.AddMonths(1));
        }

        if (q.Contains("last year") || q.Contains("prior year") || q.Contains("previous year"))
        {
            var start = StartOfYear(now).AddYears(-1);
            return new EzHistoryPeriod(start.Year.ToString(), start, start.AddYears(1));
        }

        // Explicit calendar year: "2025", "in 2024"
        for (var year = now.Year; year >= now.Year - 5; year--)
        {
            if (q.Contains(year.ToString()) &&
                (q.Contains("year") || q.Contains("annual") || q.Contains("yearly") ||
                 q.Contains(" in ") || q.Contains(" for ") || q.Trim() == year.ToString() ||
                 q.Contains($" {year}") || q.Contains($"{year} ")))
            {
                var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                return new EzHistoryPeriod(year.ToString(), start, start.AddYears(1));
            }
        }

        var months = new (string Key, int Month)[]
        {
            ("january", 1), ("jan", 1), ("february", 2), ("feb", 2), ("march", 3), ("mar", 3),
            ("april", 4), ("apr", 4), ("may", 5), ("june", 6), ("jun", 6), ("july", 7), ("jul", 7),
            ("august", 8), ("aug", 8), ("september", 9), ("sep", 9), ("october", 10), ("oct", 10),
            ("november", 11), ("nov", 11), ("december", 12), ("dec", 12),
        };
        foreach (var (key, month) in months.OrderByDescending(m => m.Key.Length))
        {
            if (!q.Contains(key))
            {
                continue;
            }

            var year = now.Year;
            for (var y = now.Year; y >= now.Year - 3; y--)
            {
                if (q.Contains(y.ToString()))
                {
                    year = y;
                    break;
                }
            }

            // If this month is in the future for current year (e.g. asking December in July), use prior year.
            if (year == now.Year && month > now.Month)
            {
                year--;
            }

            var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
            return new EzHistoryPeriod(start.ToString("MMM yyyy"), start, start.AddMonths(1));
        }

        if (q.Contains("yearly") || q.Contains("annual") || q.Contains("per year") ||
            (q.Contains("year") && (q.Contains("total") || q.Contains("revenue") || q.Contains("billed"))))
        {
            return new EzHistoryPeriod("YTD", StartOfYear(now), StartOfYear(now).AddYears(1));
        }

        if (q.Contains("monthly") || q.Contains("per month") ||
            (q.Contains("month") && (q.Contains("total") || q.Contains("revenue") || q.Contains("billed"))))
        {
            return new EzHistoryPeriod("MTD", StartOfMonth(now), StartOfMonth(now).AddMonths(1));
        }

        return null;
    }

    private static decimal ReadMetaDecimal(ExternalWorkItemDto item, params string[] keys)
    {
        foreach (var key in keys)
        {
            var raw = item.Metadata?.GetValueOrDefault(key);
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0m;
    }

    private static decimal ReadDailyRate(ExternalWorkItemDto item) =>
        ReadMetaDecimal(item, "dailyRateValue", "dailyRate");

    private static decimal ReadMonthlyRate(ExternalWorkItemDto item)
    {
        var listed = ReadMetaDecimal(item, "monthlyRateValue", "monthlyRate");
        return listed > 0 ? listed : ReadDailyRate(item) * 30m;
    }

    private static decimal ReadRentCollected(ExternalWorkItemDto item) =>
        ReadMetaDecimal(item, "rentCollectedValue", "rentCollected");

    private static string FormatMoneyAmount(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Historical billed revenue: prorate each order's net_amount across bill_from→bill_to days.
    /// </summary>
    private static decimal SumOrderRevenue(
        IEnumerable<EzRentOrderDto> orders,
        DateTimeOffset start,
        DateTimeOffset endExclusive,
        string? customer = null)
    {
        decimal total = 0m;
        foreach (var order in orders)
        {
            if (!string.IsNullOrWhiteSpace(customer) &&
                !string.Equals(order.Customer, customer, StringComparison.OrdinalIgnoreCase) &&
                !order.Customer.Contains(customer, StringComparison.OrdinalIgnoreCase) &&
                !customer.Contains(order.Customer, StringComparison.OrdinalIgnoreCase))
            {
                // allow compact match via caller passing exact customer from FindEzCustomerMatch
                var compactOrder = new string(order.Customer.Where(char.IsLetterOrDigit).ToArray());
                var compactWanted = new string(customer.Where(char.IsLetterOrDigit).ToArray());
                if (compactWanted.Length < 3 ||
                    !compactOrder.Contains(compactWanted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            total += ProrateOrderAmount(order, start, endExclusive);
        }

        return total;
    }

    private static decimal ProrateOrderAmount(
        EzRentOrderDto order,
        DateTimeOffset start,
        DateTimeOffset endExclusive)
    {
        if (order.NetAmount == 0)
        {
            return 0m;
        }

        if (order.BillFrom is { } bf && order.BillTo is { } bt && bt >= bf)
        {
            var billDays = (bt.Date - bf.Date).Days + 1;
            if (billDays <= 0)
            {
                return 0m;
            }

            var overlapStart = bf > start ? bf.Date : start.Date;
            var overlapEnd = bt.Date < endExclusive.Date.AddDays(-1)
                ? bt.Date
                : endExclusive.Date.AddDays(-1);
            if (overlapEnd < overlapStart)
            {
                return 0m;
            }

            var overlapDays = (overlapEnd - overlapStart).Days + 1;
            return order.NetAmount * overlapDays / billDays;
        }

        var pivot = order.CompletedOn ?? order.CheckedOutOn;
        if (pivot is { } p && p >= start && p < endExclusive)
        {
            return order.NetAmount;
        }

        return 0m;
    }

    private static List<(string Customer, decimal Amount, int Orders)> RollupOrdersByCustomer(
        IEnumerable<EzRentOrderDto> orders,
        DateTimeOffset start,
        DateTimeOffset endExclusive)
    {
        return orders
            .GroupBy(o => o.Customer, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var amount = g.Sum(o => ProrateOrderAmount(o, start, endExclusive));
                var count = g.Count(o => ProrateOrderAmount(o, start, endExclusive) > 0);
                return (Customer: g.First().Customer, Amount: amount, Orders: count);
            })
            .Where(x => x.Amount > 0)
            .OrderByDescending(x => x.Amount)
            .ToList();
    }

    private static void AppendEzHistoryFactSheet(
        StringBuilder sb,
        IReadOnlyList<EzRentOrderDto> orders,
        string? customer)
    {
        if (orders.Count == 0)
        {
            sb.AppendLine("### Historical order revenue");
            sb.AppendLine("- (no EZRentOut orders loaded)");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var periods = new List<EzHistoryPeriod>
        {
            new("MTD", StartOfMonth(now), StartOfMonth(now).AddMonths(1)),
            new("YTD", StartOfYear(now), StartOfYear(now).AddYears(1)),
            new(StartOfMonth(now).AddMonths(-1).ToString("MMM yyyy"),
                StartOfMonth(now).AddMonths(-1), StartOfMonth(now)),
            new(StartOfMonth(now).AddMonths(-2).ToString("MMM yyyy"),
                StartOfMonth(now).AddMonths(-2), StartOfMonth(now).AddMonths(-1)),
            new((now.Year - 1).ToString(), StartOfYear(now).AddYears(-1), StartOfYear(now)),
        };

        sb.AppendLine("### Historical order revenue (net_amount prorated by bill_from→bill_to)");
        sb.AppendLine(
            $"Orders in pull: {orders.Count}. Do NOT multiply current daily run-rate for these figures.");
        foreach (var period in periods)
        {
            var total = SumOrderRevenue(orders, period.Start, period.EndExclusive, customer);
            var label = customer is null ? "All customers" : customer;
            sb.AppendLine($"- {period.Label} · {label}: ${total:0.##}");
        }

        var mtd = periods[0];
        sb.AppendLine($"### {mtd.Label} by customer (order history)");
        var scoped = string.IsNullOrWhiteSpace(customer)
            ? orders
            : orders.Where(o =>
                string.Equals(o.Customer, customer, StringComparison.OrdinalIgnoreCase) ||
                o.Customer.Contains(customer!, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var row in RollupOrdersByCustomer(scoped, mtd.Start, mtd.EndExclusive).Take(15))
        {
            sb.AppendLine($"- {row.Customer}: ${row.Amount:0.##} ({row.Orders} orders contributing)");
        }

        var ytd = periods[1];
        sb.AppendLine($"### {ytd.Label} by customer (order history)");
        foreach (var row in RollupOrdersByCustomer(scoped, ytd.Start, ytd.EndExclusive).Take(15))
        {
            sb.AppendLine($"- {row.Customer}: ${row.Amount:0.##} ({row.Orders} orders contributing)");
        }
    }

    private static bool WantsDetails(string q) =>
        q.Contains("detail") || q.Contains("breakdown") || q.Contains("break down") ||
        q.Contains("line item") || q.Contains("line-item") || q.Contains("list") ||
        q.Contains("which ") || q.Contains("show me") || q.Contains("show the") ||
        q.Contains("by customer") || q.Contains("by asset") || q.Contains("by location") ||
        q.Contains("itemize") || q.Contains("full ") || q.Contains("everything");

    private static string BuildEzHistoryMoneyReply(
        IReadOnlyList<EzRentOrderDto> orders,
        string? customer,
        EzHistoryPeriod period,
        string question)
    {
        if (orders.Count == 0)
        {
            return "I don't have EZRentOut order history loaded, so I can't compute billed MTD/YTD yet.";
        }

        var total = SumOrderRevenue(orders, period.Start, period.EndExclusive, customer);
        var label = customer ?? "All customers";
        var summary =
            $"{label} billed about ${total:0.##} for {period.Label} " +
            $"({period.Start:MMM d}–{period.EndExclusive.AddDays(-1):MMM d, yyyy}), " +
            "based on order net amounts prorated across each order's bill dates.";

        if (!WantsDetails(question))
        {
            // One companion figure when useful, still short.
            if (!string.Equals(period.Label, "MTD", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(period.Label, "YTD", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTimeOffset.UtcNow;
                var mtd = SumOrderRevenue(orders, StartOfMonth(now), StartOfMonth(now).AddMonths(1), customer);
                var ytd = SumOrderRevenue(orders, StartOfYear(now), StartOfYear(now).AddYears(1), customer);
                return $"{summary} For context, MTD is ${mtd:0.##} and YTD is ${ytd:0.##}.";
            }

            return summary;
        }

        var sb = new StringBuilder();
        sb.AppendLine(summary);
        sb.AppendLine();
        var rows = RollupOrdersByCustomer(
            string.IsNullOrWhiteSpace(customer)
                ? orders
                : orders.Where(o =>
                    string.Equals(o.Customer, customer, StringComparison.OrdinalIgnoreCase) ||
                    o.Customer.Contains(customer!, StringComparison.OrdinalIgnoreCase)).ToList(),
            period.Start,
            period.EndExclusive);
        sb.AppendLine($"By customer · {period.Label}:");
        foreach (var row in rows.Take(customer is null ? 20 : 8))
        {
            sb.AppendLine($"- {row.Customer}: ${row.Amount:0.##} ({row.Orders} orders)");
        }

        if (rows.Count == 0)
        {
            sb.AppendLine("- (no billed order amount overlapped this period in the current pull)");
        }

        return sb.ToString().Trim();
    }

    private static string BuildEzCurrentDailyReply(
        IReadOnlyList<ExternalWorkItemDto> items,
        IReadOnlyList<EzCustomerRollup> rollups,
        EzCustomerRollup? customerHit,
        string question)
    {
        if (customerHit is not null)
        {
            var summary =
                $"{customerHit.Customer} is at about ${customerHit.DailyTotal:0.##}/day on rent right now " +
                $"({customerHit.AssetCount} assets checked out).";
            if (!WantsDetails(question))
            {
                return summary;
            }

            var sb = new StringBuilder();
            sb.AppendLine(summary);
            sb.AppendLine();
            sb.AppendLine("Assets (highest daily $ first):");
            foreach (var item in customerHit.Items.OrderByDescending(ReadDailyRate).Take(20))
            {
                sb.AppendLine($"- {FormatWorkLine(item)}");
            }

            return sb.ToString().Trim();
        }

        var daily = items.Sum(ReadDailyRate);
        var allSummary =
            $"EZRentOut is at about ${daily:0.##}/day on rent right now across {items.Count} checked-out assets.";
        if (!WantsDetails(question))
        {
            return allSummary;
        }

        var detail = new StringBuilder();
        detail.AppendLine(allSummary);
        detail.AppendLine();
        detail.AppendLine("By customer:");
        foreach (var row in rollups.OrderByDescending(r => r.DailyTotal).Take(15))
        {
            detail.AppendLine(
                $"- {row.Customer}: ${row.DailyTotal:0.##}/day | {row.AssetCount} assets" +
                (row.OverdueCount > 0 ? $" | {row.OverdueCount} overdue" : ""));
        }

        return detail.ToString().Trim();
    }

    private static string BuildEzLocationReply(
        IReadOnlyList<ExternalWorkItemDto> items,
        string? customer,
        string question)
    {
        var daily = items.Sum(ReadDailyRate);
        var summary =
            $"{customer ?? "EZRentOut"} has {items.Count} assets checked out at about ${daily:0.##}/day.";
        if (!WantsDetails(question) &&
            !question.Contains("location") && !question.Contains("permi") &&
            !question.Contains("northern") && !question.Contains("where"))
        {
            return summary;
        }

        // Location questions are inherently a breakdown.
        var sb = new StringBuilder();
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine("By location:");
        foreach (var row in BuildEzLocationRollups(items).OrderByDescending(r => r.DailyTotal))
        {
            sb.AppendLine($"- {row.Location}: ${row.DailyTotal:0.##}/day | {row.AssetCount} assets");
        }

        return sb.ToString().Trim();
    }

    private static List<string> BuildEzOrderCustomerNames(IEnumerable<EzRentOrderDto> orders) =>
        orders.Select(o => o.Customer)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<EzCustomerRollup> BuildEzCustomerRollups(IEnumerable<ExternalWorkItemDto> items) =>
        items
            .GroupBy(
                i => string.IsNullOrWhiteSpace(i.Assignee)
                    ? (i.Metadata?.GetValueOrDefault("customer") is { Length: > 0 } c ? c : "Unassigned customer")
                    : i.Assignee!.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                return new EzCustomerRollup(
                    g.Key,
                    list.Sum(ReadDailyRate),
                    list.Sum(i =>
                    {
                        var weekly = ReadMetaDecimal(i, "weeklyRateValue", "weeklyRate");
                        return weekly > 0 ? weekly : ReadDailyRate(i) * 7m;
                    }),
                    list.Sum(ReadMonthlyRate),
                    list.Sum(i => ReadMonthlyRate(i) * 12m),
                    list.Sum(ReadRentCollected),
                    list.Count,
                    list.Count(i => string.Equals(i.Status, "Overdue return", StringComparison.OrdinalIgnoreCase)),
                    list);
            })
            .OrderByDescending(r => r.DailyTotal)
            .ThenBy(r => r.Customer, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<EzLocationRollup> BuildEzLocationRollups(IEnumerable<ExternalWorkItemDto> items) =>
        items
            .GroupBy(
                i =>
                {
                    var loc = i.Metadata?.GetValueOrDefault("location");
                    return string.IsNullOrWhiteSpace(loc) ? "Unknown location" : loc.Trim();
                },
                StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                return new EzLocationRollup(
                    g.Key,
                    list.Sum(ReadDailyRate),
                    list.Sum(ReadMonthlyRate),
                    list.Sum(i => ReadMonthlyRate(i) * 12m),
                    list.Count);
            })
            .OrderByDescending(r => r.DailyTotal)
            .ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void AppendMondayBilledFactSheet(
        StringBuilder sb,
        IReadOnlyList<ExternalWorkItemDto> quotes,
        string? partyFilter = null)
    {
        var billed = quotes
            .Where(q => string.Equals(q.Status, "Billed", StringComparison.OrdinalIgnoreCase) ||
                        q.Metadata?.GetValueOrDefault("bucket") == "billed")
            .Where(q => partyFilter is null || QuoteMatchesParty(q, partyFilter))
            .Select(q =>
            {
                DateTimeOffset? when = null;
                if (DateTimeOffset.TryParse(q.Metadata?.GetValueOrDefault("billedAt"), out var billedAt))
                {
                    when = billedAt;
                }

                return (Quote: q, When: when);
            })
            .Where(x => x.When is not null)
            .OrderByDescending(x => x.When)
            .ToList();

        sb.AppendLine();
        sb.AppendLine(
            partyFilter is null
                ? "## Monday quotes converted to Billed"
                : $"## Monday quotes converted to Billed (filtered: {partyFilter})");
        sb.AppendLine(
            "Quote Status \"Billed\" = converted to a billed order (status-change date). " +
            "Not EZRentOut rental order revenue. When a customer/name is in the question, ONLY list matching quotes.");
        if (billed.Count == 0)
        {
            sb.AppendLine(partyFilter is null
                ? "- none in current pull (or no billedAt dates)"
                : $"- none matching {partyFilter} in current pull");
            return;
        }

        foreach (var monthGroup in billed
                     .GroupBy(x => x.When!.Value.ToString("yyyy-MM"))
                     .OrderByDescending(g => g.Key)
                     .Take(8))
        {
            var label = DateTimeOffset.TryParse(monthGroup.Key + "-01", out var monthStart)
                ? monthStart.ToString("MMM yyyy")
                : monthGroup.Key;
            var monthTotal = monthGroup.Sum(x =>
                decimal.TryParse(x.Quote.Metadata?.GetValueOrDefault("amount"), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var amt)
                    ? amt
                    : 0m);
            var totalBit = monthTotal > 0 ? $" · ~${monthTotal:0.##}" : "";
            sb.AppendLine($"{label} ({monthGroup.Count()}{totalBit}):");
            foreach (var row in monthGroup.OrderBy(x => x.When).Take(40))
            {
                sb.AppendLine($"  - {FormatBilledQuoteLine(row.Quote)}");
            }
        }
    }

    private static bool QuoteMatchesParty(ExternalWorkItemDto quote, string party)
    {
        if (string.IsNullOrWhiteSpace(party))
        {
            return true;
        }

        var hay = QuoteHaystack(quote);
        if (hay.Contains(party, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var compactParty = new string(party.Where(char.IsLetterOrDigit).ToArray());
        var compactHay = new string(hay.Where(char.IsLetterOrDigit).ToArray());
        return compactParty.Length >= 4 &&
               compactHay.Contains(compactParty, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindQuotePartyFilter(string question, IReadOnlyList<ExternalWorkItemDto> quotes)
    {
        if (string.IsNullOrWhiteSpace(question) || quotes.Count == 0)
        {
            return null;
        }

        var q = question.Trim().ToLowerInvariant();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var quote in quotes)
        {
            var customer = quote.Metadata?.GetValueOrDefault("customer")?.Trim();
            if (!string.IsNullOrWhiteSpace(customer) && customer.Length >= 3)
            {
                candidates.Add(customer);
            }

            var title = (quote.Title ?? "").Trim();
            foreach (var sep in new[] { " - ", " – ", "-", "—" })
            {
                var idx = title.IndexOf(sep, StringComparison.Ordinal);
                if (idx < 3)
                {
                    continue;
                }

                var prefix = title[..idx].Trim();
                if (prefix.Length >= 3 && prefix.Length <= 48 && !IsWeakQuotePartyToken(prefix))
                {
                    candidates.Add(prefix);
                }

                break;
            }
        }

        string? best = null;
        foreach (var candidate in candidates.OrderByDescending(c => c.Length))
        {
            if (candidate.Length < 3 || IsWeakQuotePartyToken(candidate))
            {
                continue;
            }

            if (q.Contains(candidate.ToLowerInvariant()))
            {
                best = candidate;
                break;
            }

            var compactCandidate = new string(candidate.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var compactQuestion = new string(q.Where(char.IsLetterOrDigit).ToArray());
            if (compactCandidate.Length >= 4 && compactQuestion.Contains(compactCandidate))
            {
                best = candidate;
                break;
            }
        }

        return best;
    }

    private static bool IsWeakQuotePartyToken(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v is "quote" or "quotes" or "sale" or "sales" or "rental" or "rentals" or "service" or
               "services" or "parts" or "part" or "internal" or "shop" or "field" or "meter" or
               "purchase" or "repair" or "please select" or "new mexico" or "east tx" or "south tx";
    }

    private static EzCustomerRollup? FindEzCustomerMatch(string question, IReadOnlyList<EzCustomerRollup> rollups)
    {
        if (string.IsNullOrWhiteSpace(question) || rollups.Count == 0)
        {
            return null;
        }

        var q = question.Trim().ToLowerInvariant();
        var questionTokens = TokenizeCustomerQuery(q);
        if (questionTokens.Count == 0 && q.Length < 3)
        {
            return null;
        }

            EzCustomerRollup? best = null;
        var bestScore = 0;

        foreach (var row in rollups)
        {
            var name = row.Customer.Trim();
            if (name.Length < 2 ||
                name.Equals("Unassigned customer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = ScoreEzCustomerMatch(q, questionTokens, name);
            if (score > bestScore ||
                (score == bestScore && best is not null && row.AssetCount > best.AssetCount))
            {
                bestScore = score;
                best = row;
            }
        }

        // Require a real signal — avoid weak accidental overlaps.
        return bestScore >= 40 ? best : null;
    }

    private static int ScoreEzCustomerMatch(string questionLower, IReadOnlyList<string> questionTokens, string customerName)
    {
        var nameLower = customerName.ToLowerInvariant();
        var score = 0;

        if (questionLower.Contains(nameLower))
        {
            score = Math.Max(score, 1000 + nameLower.Length);
        }

        var compactCustomer = new string(nameLower.Where(char.IsLetterOrDigit).ToArray());
        var compactQuestion = new string(questionLower.Where(char.IsLetterOrDigit).ToArray());
        if (compactCustomer.Length >= 4 && compactQuestion.Contains(compactCustomer))
        {
            score = Math.Max(score, 900 + compactCustomer.Length);
        }

        var customerTokens = TokenizeCustomerQuery(nameLower)
            .Where(t => !EzCustomerStopTokens.Contains(t))
            .ToList();
        if (customerTokens.Count == 0)
        {
            return score;
        }

        // All significant customer tokens present in the question (strong).
        if (customerTokens.Count > 0 && customerTokens.All(t =>
                questionLower.Contains(t) || questionTokens.Contains(t)))
        {
            score = Math.Max(score, 800 + customerTokens.Sum(t => t.Length));
        }

        // Distinctive partial match: any strong customer token appears in the question
        // (e.g. "hondo" → "Hondo Resources, LLC", "elevate" → "ELEVATE ENERGY SERVICES").
        foreach (var token in customerTokens.OrderByDescending(t => t.Length))
        {
            if (token.Length < 4)
            {
                continue;
            }

            if (questionTokens.Contains(token) ||
                questionLower.Contains(token) ||
                // possessive / glued forms: hondo's, hondos
                questionTokens.Any(qt => qt == token || qt == token + "s" || qt.StartsWith(token)))
            {
                score = Math.Max(score, 100 + token.Length * 10);
            }
        }

        // Question tokens contained in the customer name (user typed a short nickname).
        foreach (var qt in questionTokens.Where(t => t.Length >= 4 && !EzCustomerStopTokens.Contains(t)))
        {
            if (nameLower.Contains(qt) || compactCustomer.Contains(qt))
            {
                score = Math.Max(score, 120 + qt.Length * 10);
            }
        }

        return score;
    }

    private static List<string> TokenizeCustomerQuery(string text)
    {
        return System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length >= 3 && !EzCustomerStopTokens.Contains(t))
            .Distinct()
            .ToList();
    }

    private static readonly HashSet<string> EzCustomerStopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "what", "whats", "which", "when",
        "where", "who", "how", "many", "much", "about", "give", "need", "know", "tell", "show",
        "list", "please", "current", "right", "now", "today", "daily", "weekly", "monthly",
        "yearly", "annual", "rate", "rates", "rent", "rental", "rentals", "onrent", "billing",
        "billed", "revenue", "total", "amount", "dollar", "dollars", "asset", "assets",
        "equipment", "order", "orders", "customer", "customers", "ezrent", "ezrentout",
        "llc", "inc", "ltd", "corp", "company", "companies", "services", "service", "group",
        "holdings", "partners", "solutions", "energy", "oil", "gas", "water", "field",
        "oilfield", "oilfiled", "midstream", "resources", "operating", "ops", "usa", "us",
        "co", "of", "to", "is", "are", "me", "my", "our", "their", "his", "her",
    };

    /// <summary>
    /// True when the question looks customer-specific so we must not answer with company-wide totals.
    /// </summary>
    private static bool LooksLikeNamedCustomerQuestion(string q, EzCustomerRollup? matched)
    {
        if (matched is not null)
        {
            return true;
        }

        var tokens = TokenizeCustomerQuery(q);
        // Strip rental/ops vocabulary; anything left is likely a name fragment.
        return tokens.Count > 0;
    }

    private static string? GuessCustomerLabelFromQuestion(string q)
    {
        var tokens = TokenizeCustomerQuery(q);
        return tokens.Count == 0 ? null : string.Join(' ', tokens.Take(4));
    }

    private static string BuildEzCustomerMissReply(
        string question,
        IReadOnlyList<EzCustomerRollup> rollups)
    {
        var label = GuessCustomerLabelFromQuestion(question) ?? "that name";
        var sb = new StringBuilder();
        sb.AppendLine(
            $"I couldn't match a checked-out EZRentOut customer for \"{label}\". " +
            "Try the name as it appears on rent (example: Hondo Resources).");
        if (rollups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top on-rent customers right now:");
            foreach (var row in rollups.OrderByDescending(r => r.DailyTotal).Take(8))
            {
                sb.AppendLine($"- {row.Customer}: ${row.DailyTotal:0.##}/day ({row.AssetCount} assets)");
            }
        }

        return sb.ToString().Trim();
    }
}
