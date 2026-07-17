using System.Collections.Concurrent;
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
    private readonly IAuditEventWriter _audit;

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
        IAuditEventWriter audit)
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
        _audit = audit;
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
                maintainXOpen.AddRange(await _ezRentOut.ListOpenWorkAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                notes.Add($"EZRentOut: {ex.Message}");
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
        var inventoryOut = inventoryAlerts.Count(a => a.Severity == "Out");
        var inventoryLow = inventoryAlerts.Count(a => a.Severity == "Low");
        var inventorySample = inventoryAlerts
            .OrderBy(a => a.Severity == "Out" ? 0 : 1)
            .ThenBy(a => a.AvailableQuantity)
            .Take(120)
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
            mxSample,
            recentlyCompleted.Take(80).ToList(),
            quotes.OrderByDescending(q => int.TryParse(q.Metadata?.GetValueOrDefault("ageDays"), out var d) ? d : 0)
                .Take(120)
                .ToList(),
            inventorySample,
            conversations,
            openTasks,
            pendingApprovals,
            notes);
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
        var knowledgeBlock = await BuildKnowledgeBlockAsync(organizationId, knowledgeQuery, cancellationToken);

        var narrative = (await _ai.CompleteAsync(
            AiTaskKind.Recap,
            [
                new AiChatMessage(
                    "system",
                    """
                    You write a daily ops executive brief for Sable people who already know the business,
                    the areas (Northern / Permian / Shop), MaintainX, and the Monday Quotes board.
                    This is NOT an outsider overview, onboarding doc, or company explainer.

                    Voice:
                    - Insider to insider. Assume shared context; never define Sable, MaintainX, Monday, or status codes.
                    - Lead with physical work that still needs wrenches: OPEN and IN_PROGRESS.
                    - Prefer concrete names, WO ids, quote ages, and part shortages over generalities.
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

        // Always pull live connector data for Ask — do not answer from a stale generic cache.
        var snapshot = await ResolveSnapshotAsync(
            organizationId,
            userId,
            focus,
            refreshFacts: true,
            cancellationToken);
        var question = turns[^1].Content;

        // Pull comments for WOs that best match this question (beyond the generic snapshot enrich).
        snapshot = await EnrichSnapshotForQuestionAsync(snapshot, question, cancellationToken);

        var deterministic = TryBuildDeterministicChatReply(snapshot, question);
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
                    refreshFacts = true,
                    mode = "deterministic",
                    questionChars = question.Length,
                    counts = snapshot.Counts
                }),
                cancellationToken);
            return await PersistAndReplyAsync(
                organizationId, userId, request.SessionId, question, deterministic, snapshot, focus, cancellationToken);
        }

        var factSheet = BuildChatFactSheet(snapshot, question);
        var knowledgeBlock = await BuildKnowledgeBlockAsync(organizationId, question, cancellationToken);

        if (!_ai.IsConfiguredFor(AiTaskKind.Chat) && !_ai.IsConfigured)
        {
            var fallback =
                "AI is not configured. Live counts from the fact sheet:\n" +
                $"mxOpen={snapshot.Counts.ExternalOpenWork}, completed={snapshot.Counts.RecentlyCompleted}, " +
                $"agingQuotes={snapshot.Counts.AgingQuotes}, inventoryOut={snapshot.Counts.InventoryOut}, " +
                $"inventoryLow={snapshot.Counts.InventoryLow}.";
            return await PersistAndReplyAsync(
                organizationId, userId, request.SessionId, question, fallback, snapshot, focus, cancellationToken);
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

                The FACT SHEET was just retrieved from live MaintainX / Monday / inventory and ranked for THIS question.
                KNOWLEDGE EXCERPTS and PRIOR ASK HISTORY were searched against the question text.
                Answer ONLY from those blocks. Prefer live FACT SHEET over prior Ask history when they conflict. Copy names and WO# exactly as written.
                Rules:
                - Prefer "## Matches for this question" when present; otherwise use workload rollups and listed lines.
                - For WO details, copy whole WO lines (WO# | area | status | person | title | comments).
                - Never invent people, WO numbers, parts, quotes, or quantities.
                - Never cite long MaintainX database ids.
                - Short bullets. No company intro. No full recap unless asked.
                - If the answer is not in the FACT SHEET or KNOWLEDGE EXCERPTS, say what is missing (e.g. "no matching WO/part in live pull") — do not invent.
                - ON_HOLD = physically finished; back office can close later. Only discuss ON_HOLD if the user asks.
                - For policy/procedure questions, prefer KNOWLEDGE EXCERPTS and cite the document title.
                """),
            new(
                "user",
                $"""
                LIVE FACT SHEET for question (completion window = {snapshot.CompletionWindowLabel}):
                {factSheet}
                {knowledgeBlock}
                {(custom is null ? "" : "\n" + custom)}
                """)
        };

        foreach (var turn in turns)
        {
            messages.Add(new AiChatMessage(turn.Role, turn.Content));
        }

        messages.Add(new AiChatMessage(
            "user",
            "Answer the latest user question now using the LIVE FACT SHEET and KNOWLEDGE EXCERPTS pulled for this ask. Cite WO# / document titles. Do not invent."));

        var reply = (await _ai.CompleteAsync(AiTaskKind.Chat, messages, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(reply))
        {
            reply = "No answer returned — try rephrasing with a WO#, person name, part, or quote title.";
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
                refreshFacts = true,
                questionChars = turns[^1].Content.Length,
                counts = snapshot.Counts
            }),
            cancellationToken);

        return await PersistAndReplyAsync(
            organizationId, userId, request.SessionId, question, reply, snapshot, focus, cancellationToken);
    }


    private async Task<OverviewChatReplyDto> PersistAndReplyAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        string question,
        string reply,
        OverviewSnapshotDto snapshot,
        OverviewFocus focus,
        CancellationToken cancellationToken)
    {
        var (resolvedSessionId, _) = await _askHistory.AppendTurnAsync(
            organizationId,
            userId,
            sessionId,
            question,
            reply,
            cancellationToken);
        return new OverviewChatReplyDto(DateTimeOffset.UtcNow, reply, snapshot, focus, resolvedSessionId);
    }

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
            var sb = new StringBuilder();
            var scopeLabel = areaFilter is null
                ? "physical open work in the current sample"
                : $"physical open {areaFilter} work in the current sample";
            sb.AppendLine($"{top.Key} has the most {scopeLabel} ({top.Count()}).");
            sb.AppendLine();
            foreach (var item in top.Take(5))
            {
                var area = item.Metadata?.GetValueOrDefault("area") ?? "";
                sb.AppendLine($"- {FormatWoLabel(item)} | {area} | {item.Status} | {item.Title}");
            }

            return sb.ToString().Trim();
        }

        var asksInventory = q.Contains("inventory") || q.Contains("out of stock") || q.Contains("shortage") ||
                            ((q.Contains("out") || q.Contains("low")) && (q.Contains("part") || q.Contains("stock")));
        if (asksInventory && snapshot.InventoryAlerts.Count > 0)
        {
            var onlyLow = q.Contains("low") && !q.Contains("out") && !q.Contains("shortage");
            var onlyOut = (q.Contains("out") || q.Contains("shortage")) && !q.Contains("low");
            var wantOut = !onlyLow;
            var wantLow = !onlyOut;
            var sb = new StringBuilder();
            sb.AppendLine(
                $"Inventory totals: out={snapshot.Counts.InventoryOut}, low={snapshot.Counts.InventoryLow}.");
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
            var sb = new StringBuilder();
            sb.AppendLine($"Aging quotes (Sent/Draft ≥ threshold): {snapshot.Counts.AgingQuotes} total; showing {aging.Count}.");
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
        if (!refreshFacts &&
            SnapshotCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Snapshot;
        }

        var snapshot = await GetSnapshotAsync(organizationId, userId, focus, cancellationToken);
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

        var scoredWork = mxPhysical
            .Select(i => (Item: i, Score: ScoreHaystack(WorkItemHaystack(i), terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Title)
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

        var hasMatches = scoredWork.Count > 0 || scoredCompleted.Count > 0 ||
                         scoredQuotes.Count > 0 || scoredInventory.Count > 0;

        if (terms.Count > 0 && hasMatches)
        {
            sb.AppendLine();
            sb.AppendLine("## Matches for this question");
            foreach (var hit in scoredWork.Take(28))
            {
                sb.AppendLine($"- {FormatWorkLine(hit.Item)}");
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
            sb.AppendLine("- (no WO / quote / inventory rows matched the question tokens — see rollups and lists below)");
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

    private static string WorkItemHaystack(ExternalWorkItemDto item) =>
        string.Join(
            ' ',
            item.Title,
            item.Assignee,
            item.Status,
            item.EnvironmentName,
            FormatWoLabel(item),
            item.Metadata?.GetValueOrDefault("area"),
            item.Metadata?.GetValueOrDefault("sequentialId"),
            item.Metadata?.GetValueOrDefault("description"),
            item.Metadata?.GetValueOrDefault("comments"),
            item.Metadata?.GetValueOrDefault("linkedQuotes"),
            item.Metadata?.GetValueOrDefault("rawStatus"));

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
            quote.Metadata?.GetValueOrDefault("partsLabor"),
            quote.Metadata?.GetValueOrDefault("dayRate"),
            quote.Metadata?.GetValueOrDefault("maintainXWoNumber"),
            quote.Metadata?.GetValueOrDefault("maintainXTitle"),
            quote.Metadata?.GetValueOrDefault("maintainXAssignee"));

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

    private async Task<string> BuildKnowledgeBlockAsync(
        Guid organizationId,
        string query,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("KNOWLEDGE EXCERPTS:");
        try
        {
            var excerpts = await _knowledge.SearchAsync(organizationId, query, limit: 6, cancellationToken);
            if (excerpts.Count == 0)
            {
                sb.AppendLine("(none matched — no indexed docs or no overlap with the query)");
            }
            else
            {
                foreach (var excerpt in excerpts)
                {
                    sb.AppendLine($"- [{excerpt.Title} · {excerpt.FileName} #{excerpt.Ordinal}] {excerpt.Text}");
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

        return sb.ToString().TrimEnd();
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
                "Other MX-linked",
                q => !string.IsNullOrWhiteSpace(q.Metadata?.GetValueOrDefault("maintainXWorkOrderId"))
                     && q.Metadata?.GetValueOrDefault("crossRefPhysicalMaintainX") != "true"
                     && q.Metadata?.GetValueOrDefault("crossRefOnHoldMaintainX") != "true");
            QuoteBucket("Other pipeline", q => q.Metadata?.GetValueOrDefault("bucket") == "pipeline");
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
        var seq = item.Metadata?.GetValueOrDefault("sequentialId");
        return !string.IsNullOrWhiteSpace(seq) ? $"WO#{seq}" : "WO#(unknown)";
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
}
