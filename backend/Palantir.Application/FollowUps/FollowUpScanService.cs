using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Ai;
using Palantir.Application.Overview;
using Palantir.Application.Tasks;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Application.FollowUps;

public sealed class FollowUpScanService : IFollowUpScanService
{
    public const string MarkerPrefix = "[Palantir follow-up]";
    public const string FingerprintPrefix = "follow-up:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPalantirDbContext _db;
    private readonly IAiCompletionClient _ai;
    private readonly ITaskService _tasks;
    private readonly IOpsSnapshotStore _opsSnapshots;
    private readonly IOptions<FollowUpScanOptions> _options;
    private readonly ILogger<FollowUpScanService> _logger;

    public FollowUpScanService(
        IPalantirDbContext db,
        IAiCompletionClient ai,
        ITaskService tasks,
        IOpsSnapshotStore opsSnapshots,
        IOptions<FollowUpScanOptions> options,
        ILogger<FollowUpScanService> logger)
    {
        _db = db;
        _ai = ai;
        _tasks = tasks;
        _opsSnapshots = opsSnapshots;
        _options = options;
        _logger = logger;
    }

    public async Task<FollowUpScanResult> ScanOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        var notes = new List<string>();

        if (!_ai.IsConfiguredFor(AiTaskKind.FollowUp) && !_ai.IsConfigured)
        {
            notes.Add("AI is not configured; skipped follow-up scan.");
            return new FollowUpScanResult(organizationId, 0, 0, 0, notes);
        }

        var users = _db.Users
            .Where(u => u.OrganizationId == organizationId && u.IsActive)
            .ToList();
        if (users.Count == 0)
        {
            notes.Add("No active users.");
            return new FollowUpScanResult(organizationId, 0, 0, 0, notes);
        }

        var mailboxByUser = _db.ConnectedAccounts
            .Where(a => a.Provider == "MicrosoftGraph")
            .ToList()
            .Where(a => users.Any(u => u.Id == a.UserId))
            .GroupBy(a => a.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.PrimaryAddress)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList());

        var lookback = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(opts.LookbackHours, 1, 24 * 14));
        var maxConv = Math.Clamp(opts.MaxConversationsPerRun, 1, 50);
        var maxMsg = Math.Clamp(opts.MaxMessagesPerConversation, 3, 30);

        var conversations = _db.Conversations
            .Where(c =>
                c.OrganizationId == organizationId &&
                c.UpdatedAt >= lookback &&
                c.Status != ConversationStatus.Closed &&
                (c.Channel == "Email" || c.Channel == "WhatsApp"))
            .ToList()
            .OrderByDescending(c => c.IsUnread)
            .ThenByDescending(c => c.UpdatedAt)
            .Take(maxConv)
            .ToList();
        var conversationCount = conversations.Count;

        var openTasks = _db.TaskItems
            .Where(t =>
                t.OrganizationId == organizationId &&
                t.Status != "Completed" &&
                t.Status != "Done")
            .ToList();

        var existingFingerprints = openTasks
            .Select(t => TryReadFingerprint(t.Description))
            .Where(f => f is not null)
            .Select(f => f!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var coveredConversationIds = openTasks
            .Where(t =>
                t.ConversationId.HasValue &&
                t.Description != null &&
                t.Description.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            .Select(t => t.ConversationId!.Value)
            .ToHashSet();

        var candidates = new List<ScanCandidate>();
        foreach (var conversation in conversations)
        {
            if (coveredConversationIds.Contains(conversation.Id))
            {
                continue;
            }

            var messages = _db.Messages
                .Where(m => m.ConversationId == conversation.Id && !m.IsInternalNote)
                .ToList()
                .OrderByDescending(m => m.CreatedAt)
                .Take(maxMsg)
                .OrderBy(m => m.CreatedAt)
                .ToList();

            if (messages.Count == 0)
            {
                continue;
            }

            var candidate = BuildConversationCandidate(conversation, messages, users, mailboxByUser);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        if (opts.IncludeOpenWork)
        {
            candidates.AddRange(await BuildOpenWorkCandidatesAsync(
                organizationId,
                existingFingerprints,
                cancellationToken));
        }

        if (candidates.Count == 0)
        {
            notes.Add("No eligible conversations or open work after filters.");
            return new FollowUpScanResult(organizationId, conversationCount, 0, 0, notes);
        }

        var proposals = await ProposeAsync(candidates, users, cancellationToken);
        var proposalCount = proposals.Count;
        notes.Add($"AI returned {proposalCount} proposal(s) from {candidates.Count} candidate(s).");

        var created = 0;
        var maxTasks = Math.Clamp(opts.MaxTasksPerRun, 1, 40);
        var actorUserId = users
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Id)
            .First();

        foreach (var proposal in proposals.Take(maxTasks))
        {
            if (string.IsNullOrWhiteSpace(proposal.Title))
            {
                continue;
            }

            var fingerprint = proposal.Fingerprint?.Trim();
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                fingerprint = proposal.ConversationId.HasValue
                    ? $"{FingerprintPrefix}conv:{proposal.ConversationId}"
                    : $"{FingerprintPrefix}ops:{Slug(proposal.Title)}";
            }

            if (existingFingerprints.Contains(fingerprint))
            {
                continue;
            }

            var assignee = ResolveAssignee(proposal, users) ?? actorUserId;
            var description =
                $"{MarkerPrefix}\n" +
                $"{fingerprint}\n" +
                (string.IsNullOrWhiteSpace(proposal.Reason) ? "" : $"Why: {proposal.Reason.Trim()}\n") +
                (string.IsNullOrWhiteSpace(proposal.Description) ? "" : proposal.Description.Trim());

            if (!opts.AutoCreate)
            {
                notes.Add($"Proposal (dry-run): {proposal.Title}");
                continue;
            }

            await _tasks.CreateAsync(
                new CreateTaskRequest(
                    organizationId,
                    Truncate(proposal.Title.Trim(), 180),
                    actorUserId,
                    Truncate(description.Trim(), 2000),
                    assignee,
                    proposal.ConversationId,
                    DueAt: proposal.DueAt,
                    Priority: NormalizePriority(proposal.Priority)),
                cancellationToken);

            existingFingerprints.Add(fingerprint);
            created++;
        }

        _logger.LogInformation(
            "Follow-up scan for org {OrganizationId}: reviewed={ReviewedCount}, proposals={ProposalCount}, created={CreatedCount}",
            organizationId,
            conversationCount,
            proposalCount,
            created);

        return new FollowUpScanResult(
            organizationId,
            conversationCount,
            proposalCount,
            created,
            notes);
    }

    private ScanCandidate? BuildConversationCandidate(
        Conversation conversation,
        IReadOnlyList<Message> messages,
        IReadOnlyList<User> users,
        IReadOnlyDictionary<Guid, List<string>> mailboxByUser)
    {
        if (string.Equals(conversation.Channel, "Email", StringComparison.OrdinalIgnoreCase))
        {
            return BuildEmailCandidate(conversation, messages, users, mailboxByUser);
        }

        if (string.Equals(conversation.Channel, "WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWhatsAppCandidate(conversation, messages, users);
        }

        return null;
    }

    private static ScanCandidate? BuildEmailCandidate(
        Conversation conversation,
        IReadOnlyList<Message> messages,
        IReadOnlyList<User> users,
        IReadOnlyDictionary<Guid, List<string>> mailboxByUser)
    {
        var latestInbound = messages.LastOrDefault(m =>
            string.Equals(m.Direction, "Inbound", StringComparison.OrdinalIgnoreCase))
            ?? messages.Last();

        var meta = ParseEmailMeta(latestInbound.ProviderMetadataJson, latestInbound.Body);
        var haystack = $"{conversation.Subject}\n{latestInbound.Body}\n{latestInbound.Summary}";
        if (LooksLikeJunk(meta.From, conversation.Subject, haystack))
        {
            return null;
        }

        var addressedUsers = new List<UserMention>();
        foreach (var user in users)
        {
            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                emails.Add(user.Email.Trim().ToLowerInvariant());
            }

            if (mailboxByUser.TryGetValue(user.Id, out var mailboxes))
            {
                foreach (var m in mailboxes)
                {
                    emails.Add(m);
                }
            }

            var inTo = meta.To.Any(t => emails.Contains(t));
            var inCc = meta.Cc.Any(t => emails.Contains(t));
            if (!inTo && inCc)
            {
                // Courtesy copy — do not create a personal follow-up.
                continue;
            }

            if (inTo || emails.Contains(meta.From))
            {
                addressedUsers.Add(new UserMention(user.Id, user.DisplayName, user.Email, "to-or-from"));
            }
        }

        var hasOutbound = messages.Any(m =>
            string.Equals(m.Direction, "Outbound", StringComparison.OrdinalIgnoreCase));
        if (addressedUsers.Count == 0 && !hasOutbound)
        {
            return null;
        }

        return new ScanCandidate(
            Kind: "email",
            ConversationId: conversation.Id,
            Fingerprint: $"{FingerprintPrefix}conv:{conversation.Id}",
            Subject: conversation.Subject,
            Summary: BuildTranscriptSnippet(conversation, messages),
            MentionedUserIds: addressedUsers.Select(u => u.UserId).Distinct().ToList(),
            Hint: addressedUsers.Count == 0
                ? "Outbound/email thread — only create a task if we owe a follow-up or reply."
                : "Direct email (To/from our mailbox). Ignore if already handled or informational only.");
    }

    private static ScanCandidate? BuildWhatsAppCandidate(
        Conversation conversation,
        IReadOnlyList<Message> messages,
        IReadOnlyList<User> users)
    {
        var recentInbound = messages
            .Where(m => string.Equals(m.Direction, "Inbound", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (recentInbound.Count == 0)
        {
            return null;
        }

        var text = string.Join(
            "\n",
            recentInbound.Select(m => m.Body ?? m.Summary ?? string.Empty));

        var mentioned = new List<UserMention>();
        foreach (var user in users)
        {
            if (IsUserCalledOut(text, user.DisplayName))
            {
                mentioned.Add(new UserMention(user.Id, user.DisplayName, user.Email, "name-callout"));
            }
        }

        if (mentioned.Count == 0)
        {
            // General group chatter — informational only, no assigned task.
            return null;
        }

        return new ScanCandidate(
            Kind: "whatsapp",
            ConversationId: conversation.Id,
            Fingerprint: $"{FingerprintPrefix}conv:{conversation.Id}",
            Subject: conversation.Subject,
            Summary: BuildTranscriptSnippet(conversation, messages),
            MentionedUserIds: mentioned.Select(m => m.UserId).Distinct().ToList(),
            Hint: "WhatsApp call-out by name — create a task for the mentioned user if action is needed.");
    }

    private async Task<IReadOnlyList<ScanCandidate>> BuildOpenWorkCandidatesAsync(
        Guid organizationId,
        HashSet<string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var snapshot = await _opsSnapshots.TryGetFreshAsync(
            organizationId,
            IOpsSnapshotStore.DefaultFocusKey,
            cancellationToken);
        if (snapshot is null)
        {
            return Array.Empty<ScanCandidate>();
        }

        var list = new List<ScanCandidate>();
        foreach (var item in snapshot.ExternalWorkSample.Take(15))
        {
            var fingerprint = $"{FingerprintPrefix}ops:{item.SourceSystem}:{item.ExternalId}";
            if (existingFingerprints.Contains(fingerprint))
            {
                continue;
            }

            // Prefer items that look stalled / overdue / awaiting attention.
            var status = item.Status ?? string.Empty;
            var overdue = item.DueAt.HasValue && item.DueAt.Value < DateTimeOffset.UtcNow;
            var waiting =
                status.Contains("HOLD", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("WAIT", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("QUOTE", StringComparison.OrdinalIgnoreCase) ||
                overdue;

            if (!waiting && list.Count >= 5)
            {
                continue;
            }

            list.Add(new ScanCandidate(
                Kind: "open-work",
                ConversationId: null,
                Fingerprint: fingerprint,
                Subject: $"{item.SourceSystem}: {item.Title}",
                Summary:
                    $"Source={item.SourceSystem}; Env={item.EnvironmentName}; Id={item.ExternalId}; " +
                    $"Status={item.Status}; Assignee={item.Assignee}; Due={item.DueAt:u}; Url={item.Url}",
                MentionedUserIds: Array.Empty<Guid>(),
                Hint: waiting
                    ? "Open ops work that may need follow-up or chase."
                    : "Open ops work — only create a task if something clearly needs attention."));
        }

        return list;
    }

    private async Task<IReadOnlyList<FollowUpProposal>> ProposeAsync(
        IReadOnlyList<ScanCandidate> candidates,
        IReadOnlyList<User> users,
        CancellationToken cancellationToken)
    {
        var userDirectory = string.Join(
            "\n",
            users.Select(u => $"- id={u.Id}; name={u.DisplayName}; email={u.Email}"));

        var payload = new StringBuilder();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            payload.AppendLine($"### Candidate {i + 1}");
            payload.AppendLine($"kind: {c.Kind}");
            payload.AppendLine($"fingerprint: {c.Fingerprint}");
            payload.AppendLine($"conversationId: {c.ConversationId}");
            payload.AppendLine($"subject: {c.Subject}");
            payload.AppendLine($"hint: {c.Hint}");
            if (c.MentionedUserIds.Count > 0)
            {
                payload.AppendLine($"preferredAssignees: {string.Join(',', c.MentionedUserIds)}");
            }

            payload.AppendLine("content:");
            payload.AppendLine(Truncate(c.Summary, 2500));
            payload.AppendLine();
        }

        var raw = await _ai.CompleteAsync(
            AiTaskKind.FollowUp,
            [
                new AiChatMessage(
                    "system",
                    """
                    You are Palantir, an operations assistant for Sable Automation Solutions.
                    Decide which candidates need a personal follow-up task or to-do.

                    Strict rules:
                    - Ignore junk, ads, newsletters, marketing, automated notifications, and no-reply mail.
                    - Ignore courtesy CC / FYI email where no action is requested of the recipient.
                    - WhatsApp without a name call-out must NOT get a task (already filtered).
                    - WhatsApp with a name call-out: create a task only if that person should do something.
                    - Email To: create a task only when a reply, decision, schedule, quote, parts, site visit, or chase is needed.
                    - Outbound email: create a task only if we promised to follow up or are waiting on a customer reply that needs chasing.
                    - Open work: create a task only for stalled / overdue / awaiting-close items that need human chase.
                    - Do not invent facts. Prefer fewer high-quality tasks.
                    - Return ONLY a JSON array (no markdown). Each item:
                      {
                        "title": "short action",
                        "description": "optional context",
                        "reason": "why this needs attention",
                        "conversationId": "guid or null",
                        "fingerprint": "must match candidate fingerprint",
                        "assignedToUserId": "guid or null",
                        "assignedToName": "optional name match",
                        "priority": "Low|Normal|High",
                        "dueAt": "ISO-8601 or null"
                      }
                    - If nothing needs attention, return [].
                    """),
                new AiChatMessage(
                    "user",
                    $"""
                    Org users:
                    {userDirectory}

                    Candidates:
                    {payload}
                    """)
            ],
            cancellationToken);

        return ParseProposals(raw, candidates);
    }

    private static IReadOnlyList<FollowUpProposal> ParseProposals(
        string raw,
        IReadOnlyList<ScanCandidate> candidates)
    {
        var text = StripFence(raw).Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "[]")
        {
            return Array.Empty<FollowUpProposal>();
        }

        try
        {
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                text = text[start..(end + 1)];
            }

            var parsed = JsonSerializer.Deserialize<List<FollowUpProposalDto>>(text, JsonOptions)
                         ?? [];
            var byFingerprint = candidates.ToDictionary(
                c => c.Fingerprint,
                c => c,
                StringComparer.OrdinalIgnoreCase);

            var results = new List<FollowUpProposal>();
            foreach (var item in parsed)
            {
                if (string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
                }

                Guid? conversationId = null;
                if (Guid.TryParse(item.ConversationId, out var cid))
                {
                    conversationId = cid;
                }

                var fingerprint = item.Fingerprint?.Trim();
                if (string.IsNullOrWhiteSpace(fingerprint) && conversationId.HasValue)
                {
                    fingerprint = $"{FingerprintPrefix}conv:{conversationId}";
                }

                if (!string.IsNullOrWhiteSpace(fingerprint) &&
                    byFingerprint.TryGetValue(fingerprint, out var candidate))
                {
                    conversationId ??= candidate.ConversationId;
                    fingerprint = candidate.Fingerprint;
                }

                DateTimeOffset? dueAt = null;
                if (!string.IsNullOrWhiteSpace(item.DueAt) &&
                    DateTimeOffset.TryParse(item.DueAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsedDue))
                {
                    dueAt = parsedDue;
                }

                Guid? assignee = null;
                if (Guid.TryParse(item.AssignedToUserId, out var aid))
                {
                    assignee = aid;
                }

                results.Add(new FollowUpProposal(
                    item.Title.Trim(),
                    item.Description,
                    item.Reason,
                    conversationId,
                    fingerprint,
                    assignee,
                    item.AssignedToName,
                    item.Priority,
                    dueAt));
            }

            return results;
        }
        catch (Exception)
        {
            return Array.Empty<FollowUpProposal>();
        }
    }

    private static Guid? ResolveAssignee(FollowUpProposal proposal, IReadOnlyList<User> users)
    {
        if (proposal.AssignedToUserId.HasValue &&
            users.Any(u => u.Id == proposal.AssignedToUserId.Value))
        {
            return proposal.AssignedToUserId;
        }

        if (!string.IsNullOrWhiteSpace(proposal.AssignedToName))
        {
            var name = proposal.AssignedToName.Trim();
            var match = users.FirstOrDefault(u =>
                u.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                u.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(u.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Id;
            }
        }

        return null;
    }

    private static string BuildTranscriptSnippet(
        Conversation conversation,
        IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Channel={conversation.Channel}; Status={conversation.Status}; Unread={conversation.IsUnread}");
        foreach (var message in messages)
        {
            var body = Truncate((message.Body ?? message.Summary ?? string.Empty).Replace('\r', ' '), 500);
            sb.AppendLine($"[{message.CreatedAt:u}] {message.Direction}: {body}");
        }

        return sb.ToString();
    }

    private static EmailMeta ParseEmailMeta(string? metadataJson, string? body)
    {
        var from = string.Empty;
        var to = new List<string>();
        var cc = new List<string>();

        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                var root = doc.RootElement;
                from = root.TryGetProperty("from", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                to.AddRange(ReadAddressArray(root, "to"));
                cc.AddRange(ReadAddressArray(root, "cc"));
            }
            catch
            {
                // fall through to body headers
            }
        }

        if (string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(body))
        {
            from = MatchHeader(body, "From") ?? string.Empty;
            to.AddRange(SplitAddresses(MatchHeader(body, "To")));
            cc.AddRange(SplitAddresses(MatchHeader(body, "Cc")));
        }

        static string Norm(string value) => value.Trim().ToLowerInvariant();

        return new EmailMeta(
            Norm(from),
            to.Select(Norm).Where(x => x.Length > 0).Distinct().ToList(),
            cc.Select(Norm).Where(x => x.Length > 0).Distinct().ToList());
    }

    private static IEnumerable<string> ReadAddressArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
        {
            yield break;
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    yield return s;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            foreach (var s in SplitAddresses(el.GetString()))
            {
                yield return s;
            }
        }
    }

    private static IEnumerable<string> SplitAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var email = part;
            var lt = part.IndexOf('<');
            var gt = part.IndexOf('>');
            if (lt >= 0 && gt > lt)
            {
                email = part[(lt + 1)..gt];
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                yield return email.Trim();
            }
        }
    }

    private static string? MatchHeader(string body, string header)
    {
        var match = Regex.Match(
            body,
            $@"^{Regex.Escape(header)}:\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool LooksLikeJunk(string from, string? subject, string haystack)
    {
        var blob = $"{from}\n{subject}\n{haystack}".ToLowerInvariant();
        string[] markers =
        [
            "unsubscribe", "view in browser", "email preferences", "no-reply@", "noreply@",
            "donotreply", "do-not-reply", "mailer-daemon", "newsletter", "marketing@",
            "% off", "limited time offer", "you are receiving this email", "promo code",
            "congratulations you've won", "click here to claim"
        ];
        return markers.Any(m => blob.Contains(m, StringComparison.Ordinal));
    }

    private static bool IsUserCalledOut(string text, string displayName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var parts = displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length >= 3)
            .ToList();
        if (parts.Count == 0)
        {
            return false;
        }

        // Full name or @First / @Name style call-outs.
        if (Regex.IsMatch(text, $@"\b{Regex.Escape(displayName)}\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        foreach (var part in parts)
        {
            if (Regex.IsMatch(text, $@"(^|[\s@]){Regex.Escape(part)}\b", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryReadFingerprint(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(FingerprintPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string NormalizePriority(string? priority)
    {
        if (string.Equals(priority, "High", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        if (string.Equals(priority, "Low", StringComparison.OrdinalIgnoreCase))
        {
            return "Low";
        }

        return "Normal";
    }

    private static string StripFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNl = text.IndexOf('\n');
        if (firstNl < 0)
        {
            return text.Trim('`');
        }

        text = text[(firstNl + 1)..];
        var close = text.LastIndexOf("```", StringComparison.Ordinal);
        if (close >= 0)
        {
            text = text[..close];
        }

        return text.Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Slug(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '-' or '_')
            {
                sb.Append('-');
            }

            if (sb.Length >= 48)
            {
                break;
            }
        }

        return sb.ToString().Trim('-');
    }

    private sealed record EmailMeta(string From, IReadOnlyList<string> To, IReadOnlyList<string> Cc);

    private sealed record UserMention(Guid UserId, string DisplayName, string Email, string How);

    private sealed record ScanCandidate(
        string Kind,
        Guid? ConversationId,
        string Fingerprint,
        string? Subject,
        string Summary,
        IReadOnlyList<Guid> MentionedUserIds,
        string Hint);

    private sealed record FollowUpProposal(
        string Title,
        string? Description,
        string? Reason,
        Guid? ConversationId,
        string? Fingerprint,
        Guid? AssignedToUserId,
        string? AssignedToName,
        string? Priority,
        DateTimeOffset? DueAt);

    private sealed class FollowUpProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Reason { get; set; }
        public string? ConversationId { get; set; }
        public string? Fingerprint { get; set; }
        public string? AssignedToUserId { get; set; }
        public string? AssignedToName { get; set; }
        public string? Priority { get; set; }
        public string? DueAt { get; set; }
    }
}
