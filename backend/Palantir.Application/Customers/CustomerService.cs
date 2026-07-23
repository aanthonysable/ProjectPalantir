using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Ai;
using Palantir.Application.Connectors;
using Palantir.Application.Overview;
using Palantir.Domain.Entities;

namespace Palantir.Application.Customers;

public sealed class CustomerService : ICustomerService
{
    private static readonly HttpClient PublicLookupHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private readonly IPalantirDbContext _db;
    private readonly IOpsSnapshotStore _opsSnapshots;
    private readonly IMaintainXConnector _maintainX;
    private readonly IEZRentOutConnector _ezRentOut;
    private readonly IMondayConnector _monday;
    private readonly MaintainXOptions _maintainXOptions;
    private readonly IAiCompletionClient _ai;
    private readonly ILogger<CustomerService> _logger;

    public CustomerService(
        IPalantirDbContext db,
        IOpsSnapshotStore opsSnapshots,
        IMaintainXConnector maintainX,
        IEZRentOutConnector ezRentOut,
        IMondayConnector monday,
        IOptions<MaintainXOptions> maintainXOptions,
        IAiCompletionClient ai,
        ILogger<CustomerService> logger)
    {
        _db = db;
        _opsSnapshots = opsSnapshots;
        _maintainX = maintainX;
        _ezRentOut = ezRentOut;
        _monday = monday;
        _maintainXOptions = maintainXOptions.Value;
        _ai = ai;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CustomerSummaryDto>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var customers = _db.Customers
            .Where(c => c.OrganizationId == organizationId)
            .ToList()
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contacts = _db.Contacts
            .Where(c => c.OrganizationId == organizationId)
            .ToList();
        var conversations = _db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.CustomerId != null)
            .ToList();
        var tasks = _db.TaskItems
            .Where(t =>
                t.OrganizationId == organizationId &&
                t.Status != "Completed" &&
                t.Status != "Done")
            .ToList();

        var snapshot = await _opsSnapshots.TryGetFreshAsync(
            organizationId,
            IOpsSnapshotStore.DefaultFocusKey,
            cancellationToken)
            ?? await _opsSnapshots.TryGetLatestReadyAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                cancellationToken);
        var snapshotItems = snapshot is null
            ? new List<ExternalWorkItemDto>()
            : (snapshot.ExternalWorkSample ?? Array.Empty<ExternalWorkItemDto>())
                .Concat(snapshot.QuotesSample ?? Array.Empty<ExternalWorkItemDto>())
                .Concat(snapshot.RecentlyCompleted ?? Array.Empty<ExternalWorkItemDto>())
                .ToList();
        var snapshotOrders = snapshot?.EzRentOrders ?? Array.Empty<EzRentOrderDto>();

        var dirty = false;
        var list = customers.Select(c =>
        {
            var linkedConversations = conversations.Where(x => x.CustomerId == c.Id).ToList();
            var conversationIds = linkedConversations.Select(x => x.Id).ToHashSet();
            var fromDb = ReadStoredActivity(c.MetadataJson).Where(a => !IsLegacyEzAssetRental(a)).ToList();
            var stored = BuildPersistedOpsActivity(c.Name, fromDb, snapshotItems, snapshotOrders);
            var ops = CountOpsKinds(stored.Select(a => a.Kind));
            // Activity payloads are capped; keep full rental/order totals from the snapshot for display.
            var snapshotRentalJobs = snapshotOrders.Count(o =>
                NamesMatch(c.Name, o.Customer) && ClassifyOrderKind(o) == "rental");
            var snapshotCompletedOrders = snapshotOrders.Count(o =>
                NamesMatch(c.Name, o.Customer) && ClassifyOrderKind(o) == "order");
            if (snapshotRentalJobs + snapshotCompletedOrders > ops.Rentals + ops.Orders)
            {
                ops = (ops.WorkOrders, ops.Quotes, snapshotRentalJobs, snapshotCompletedOrders);
            }

            var cachedCounts = ReadCachedCounts(c.MetadataJson);
            if (cachedCounts is { } cached &&
                cached.Rentals + cached.Orders > ops.Rentals + ops.Orders)
            {
                ops = cached;
            }

            if (ShouldPersistActivity(fromDb, stored) &&
                WriteActivityMetadata(c, stored, systems: null, counts: ops))
            {
                dirty = true;
            } else if (cachedCounts is null && ops.Rentals + ops.Orders + ops.Quotes + ops.WorkOrders > 0)
            {
                if (WriteActivityMetadata(c, stored.Count > 0 ? stored : fromDb, systems: null, counts: ops))
                {
                    dirty = true;
                }
            }

            var opsLast = stored
                .Select(a => a.OccurredAt)
                .Where(a => a.HasValue)
                .Select(a => a!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            var lastActivity = linkedConversations
                .Select(x => (DateTimeOffset?)x.UpdatedAt)
                .Concat(opsLast > DateTimeOffset.MinValue ? new DateTimeOffset?[] { opsLast } : [])
                .Where(x => x.HasValue)
                .DefaultIfEmpty(null)
                .Max();
            var openTasks = tasks.Count(t =>
                t.ConversationId.HasValue && conversationIds.Contains(t.ConversationId.Value));

            return new CustomerSummaryDto(
                c.Id,
                c.Name,
                contacts.Count(x => x.CustomerId == c.Id),
                linkedConversations.Count,
                openTasks,
                ops.WorkOrders,
                ops.Quotes,
                ops.Rentals,
                ops.Orders,
                lastActivity);
        }).ToList();

        if (dirty)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return (IReadOnlyList<CustomerSummaryDto>)list;
    }

    public async Task<CustomerDetailDto?> GetAsync(
        Guid organizationId,
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var customer = _db.Customers.FirstOrDefault(c =>
            c.Id == customerId && c.OrganizationId == organizationId);
        if (customer is null)
        {
            return null;
        }

        // Attach matching Email / WhatsApp threads when opening the 360 view.
        await LinkConversationsAsync(
            organizationId,
            [customer],
            takeConversations: 800,
            cancellationToken);

        var contacts = _db.Contacts
            .Where(c => c.OrganizationId == organizationId && c.CustomerId == customerId)
            .ToList()
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ContactDto(c.Id, c.CustomerId, c.DisplayName, c.Email, c.Phone))
            .ToList();

        var conversations = _db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.CustomerId == customerId)
            .ToList();
        var conversationIds = conversations.Select(c => c.Id).ToHashSet();

        var activity = new List<CustomerActivityDto>();

        // Ops work/quotes/rentals saved onto the customer during sync (DB-backed).
        // Legacy rows stored one line per EZ asset — drop those in favor of order/job rows.
        // EZRentOut order/rental rows are deferred until after the ops snapshot so we can
        // prefer freshly mapped titles (job-first) and full asset lists.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredEz = new List<CustomerActivityDto>();
        foreach (var row in ReadStoredActivity(customer.MetadataJson)
                     .Where(a => !IsLegacyEzAssetRental(a)))
        {
            var isEzOrder =
                string.Equals(row.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase) &&
                row.Kind is "rental" or "order";
            if (isEzOrder)
            {
                deferredEz.Add(new CustomerActivityDto(
                    row.Kind,
                    row.Title,
                    row.Detail,
                    row.OccurredAt,
                    row.Url,
                    null,
                    row.SourceSystem));
                continue;
            }

            var key = ActivityKeyForStored(row);
            if (!seen.Add(key))
            {
                continue;
            }

            activity.Add(new CustomerActivityDto(
                row.Kind,
                row.Title,
                row.Detail,
                row.OccurredAt,
                row.Url,
                null,
                row.SourceSystem));
        }

        foreach (var conversation in conversations.OrderByDescending(c => c.UpdatedAt).Take(40))
        {
            activity.Add(new CustomerActivityDto(
                Kind: conversation.Channel.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase)
                    ? "whatsapp"
                    : conversation.Channel.Equals("Email", StringComparison.OrdinalIgnoreCase)
                        ? "email"
                        : "conversation",
                Title: conversation.Subject ?? "(no subject)",
                Detail: $"{conversation.Channel} · {conversation.Status}",
                OccurredAt: conversation.UpdatedAt,
                Url: null,
                ConversationId: conversation.Id,
                SourceSystem: "Palantir"));
        }

        var tasks = _db.TaskItems
            .Where(t =>
                t.OrganizationId == organizationId &&
                t.ConversationId != null &&
                conversationIds.Contains(t.ConversationId.Value))
            .ToList()
            .OrderByDescending(t => t.CreatedAt)
            .Take(20);
        foreach (var task in tasks)
        {
            activity.Add(new CustomerActivityDto(
                "task",
                task.Title,
                $"{task.Status} · {task.Priority}",
                task.CreatedAt,
                null,
                task.ConversationId,
                "Palantir"));
        }

        // Always supplement from ops snapshot so Monday title-only quotes attach even when
        // rentals were already stored from a prior sync.
        var snapshot = await _opsSnapshots.TryGetFreshAsync(
            organizationId,
            IOpsSnapshotStore.DefaultFocusKey,
            cancellationToken)
            ?? await _opsSnapshots.TryGetLatestReadyAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                cancellationToken);
        if (snapshot is not null)
        {
            var snapshotItems = (snapshot.ExternalWorkSample ?? Array.Empty<ExternalWorkItemDto>())
                .Concat(snapshot.QuotesSample ?? Array.Empty<ExternalWorkItemDto>())
                .Concat(snapshot.RecentlyCompleted ?? Array.Empty<ExternalWorkItemDto>());

            foreach (var item in snapshotItems.Where(i =>
                         !i.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase) &&
                         PartyMatches(customer.Name, i)))
            {
                var kind = ClassifyWorkKind(item);
                var key = ActivityKey(item.SourceSystem, item.Title, item.Url);
                if (!seen.Add(key))
                {
                    continue;
                }

                activity.Add(new CustomerActivityDto(
                    kind,
                    item.Title,
                    $"{item.SourceSystem} · {item.Status}",
                    item.DueAt,
                    item.Url,
                    null,
                    item.SourceSystem));
            }

            foreach (var order in (snapshot.EzRentOrders ?? Array.Empty<EzRentOrderDto>())
                         .Where(o => NamesMatch(customer.Name, o.Customer)))
            {
                var row = ToActivityDto(order);
                if (!seen.Add(EzOrderActivityKey(order.OrderId)))
                {
                    continue;
                }

                activity.Add(row);
            }
        }

        // Fill any EZ orders that exist only in persisted metadata (snapshot cold / truncated).
        foreach (var row in deferredEz)
        {
            var orderMatch = Regex.Match(
                row.Title ?? "",
                @"(?:^Order\s+|·\s*Order\s+)(?<id>[A-Za-z0-9_-]+)",
                RegexOptions.IgnoreCase);
            var key = orderMatch.Success
                ? EzOrderActivityKey(orderMatch.Groups["id"].Value)
                : ActivityKey(row.SourceSystem, row.Title, row.Url);

            if (!seen.Add(key))
            {
                continue;
            }

            activity.Add(row);
        }

        // If snapshot still didn't yield ops rows, do a lightweight live open-work pull
        // (no historical EZ order crawl — that belongs on Sync).
        if (!activity.Any(a => a.Kind is "workorder" or "quote" or "rental" or "order"))
        {
            try
            {
                var workItems = new List<ExternalWorkItemDto>();
                foreach (var env in _maintainXOptions.Environments.Where(e => !string.IsNullOrWhiteSpace(e.ApiKey)))
                {
                    try
                    {
                        workItems.AddRange(await _maintainX.ListOpenWorkAsync(env, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MaintainX open-work supplement failed for {Env}", env.Name);
                    }
                }

                // EZ rentals are attached as baskets/orders (jobs), not per-asset open work.
                try
                {
                    workItems.AddRange(await _monday.ListOpenWorkAsync(cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Monday open-work supplement failed");
                }

                foreach (var item in workItems.Where(i =>
                             !i.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase) &&
                             PartyMatches(customer.Name, i)))
                {
                    var key = ActivityKey(item.SourceSystem, item.Title, item.Url);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    activity.Add(new CustomerActivityDto(
                        ClassifyWorkKind(item),
                        item.Title,
                        $"{item.SourceSystem} · {item.Status}",
                        item.DueAt,
                        item.Url,
                        null,
                        item.SourceSystem));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live ops supplement failed for customer {CustomerId}", customerId);
            }
        }

        var deduped = activity
            .GroupBy(a => ActivityKey(a.SourceSystem, a.Title, a.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var ops = CountOpsKinds(deduped.Select(a => a.Kind));
        var cachedCounts = ReadCachedCounts(customer.MetadataJson);
        if (cachedCounts is not null &&
            cachedCounts.Value.Rentals + cachedCounts.Value.Orders + cachedCounts.Value.Quotes +
            cachedCounts.Value.WorkOrders >
            ops.Rentals + ops.Orders + ops.Quotes + ops.WorkOrders)
        {
            ops = cachedCounts.Value;
        }

        var ordered = deduped
            .OrderByDescending(a => a.OccurredAt ?? DateTimeOffset.MinValue)
            .Take(120)
            .ToList();

        var openTaskCount = tasks.Count(t =>
            !string.Equals(t.Status, "Completed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(t.Status, "Done", StringComparison.OrdinalIgnoreCase));

        // Write ops activity through to SQL so browser refresh / other users don't depend on a live snapshot.
        var opsForStore = deduped
            .Where(a => a.Kind is "workorder" or "quote" or "rental" or "order")
            .Select(a => new StoredCustomerActivity(
                a.Kind,
                a.Title,
                a.Detail,
                a.OccurredAt,
                a.Url,
                a.SourceSystem,
                TryExtractOrderId(a.Title)))
            .ToList();
        if (opsForStore.Count > 0 &&
            WriteActivityMetadata(customer, opsForStore, systems: null, counts: ops))
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        var cachedOverview = ReadCachedOverview(customer.MetadataJson);

        return new CustomerDetailDto(
            customer.Id,
            customer.Name,
            contacts,
            ordered,
            conversations.Count,
            openTaskCount,
            ops.WorkOrders,
            ops.Quotes,
            ops.Rentals,
            ops.Orders,
            cachedOverview?.Text,
            cachedOverview?.GeneratedAt);
    }

    public async Task<CustomerSummaryDto> CreateAsync(
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var customer = EnsureCustomer(
            organizationId,
            name,
            JsonSerializer.Serialize(new { source = "manual" }));
        await _db.SaveChangesAsync(cancellationToken);
        return new CustomerSummaryDto(customer.Id, customer.Name, 0, 0, 0, 0, 0, 0, 0, null);
    }

    public async Task<CustomerReconcileResult> ReconcileAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var customersUpserted = 0;
        var contactsUpserted = 0;

        // Canonical party names come only from ops systems — never from mail/WhatsApp.
        var parties = new Dictionary<string, OpsParty>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? name, string source, string? contact = null)
        {
            foreach (var piece in SplitPartyNames(name))
            {
                if (!IsPlausibleOpsCustomerName(piece))
                {
                    continue;
                }

                var key = NormalizeName(piece);
                if (parties.TryGetValue(key, out var existing))
                {
                    if (string.IsNullOrWhiteSpace(existing.Contact) && !string.IsNullOrWhiteSpace(contact))
                    {
                        parties[key] = existing with { Contact = contact.Trim() };
                    }

                    if (!existing.Sources.Contains(source, StringComparer.OrdinalIgnoreCase))
                    {
                        parties[key] = parties[key] with
                        {
                            Sources = existing.Sources.Append(source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                        };
                    }

                    continue;
                }

                parties[key] = new OpsParty(
                    piece.Trim(),
                    string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
                    [source]);
            }
        }

        void ConsiderWorkItem(ExternalWorkItemDto item)
        {
            if (item.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase))
            {
                Consider(ReadPartyName(item.Metadata) ?? item.Assignee, "EZRentOut");
            }
            else if (item.SourceSystem.Equals("MaintainX", StringComparison.OrdinalIgnoreCase))
            {
                Consider(ReadPartyName(item.Metadata), "MaintainX");
                Consider(ReadMeta(item.Metadata, "location"), "MaintainX");
            }
            else if (item.SourceSystem.Equals("Monday", StringComparison.OrdinalIgnoreCase))
            {
                var party = ReadPartyName(item.Metadata);
                if (string.IsNullOrWhiteSpace(party))
                {
                    party = ExtractTitleParty(item.Title);
                }

                Consider(party, "Monday", ReadMeta(item.Metadata, "contact"));
            }
        }

        // Live connector pull (does not depend on the shared ops snapshot cache).
        var live = await CollectLiveOpsPartiesAsync(cancellationToken);
        foreach (var note in live.Notes)
        {
            notes.Add(note);
        }

        foreach (var item in live.WorkItems)
        {
            ConsiderWorkItem(item);
        }

        foreach (var orderCustomer in live.Orders.Select(o => o.Customer))
        {
            Consider(orderCustomer, "EZRentOut");
        }

        // Optional: merge any cached snapshot parties (fast path / extras).
        var snapshot = await _opsSnapshots.TryGetFreshAsync(
            organizationId,
            IOpsSnapshotStore.DefaultFocusKey,
            cancellationToken)
            ?? await _opsSnapshots.TryGetLatestReadyAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                cancellationToken);
        if (snapshot is not null)
        {
            foreach (var quote in snapshot.QuotesSample)
            {
                var party = ReadPartyName(quote.Metadata);
                if (string.IsNullOrWhiteSpace(party))
                {
                    party = ExtractTitleParty(quote.Title);
                }

                Consider(party, "Monday", ReadMeta(quote.Metadata, "contact"));
            }

            foreach (var item in snapshot.ExternalWorkSample.Concat(snapshot.RecentlyCompleted))
            {
                ConsiderWorkItem(item);
            }

            foreach (var order in snapshot.EzRentOrders)
            {
                Consider(order.Customer, "EZRentOut");
            }

            notes.Add("Also merged names from cached ops snapshot.");
        }

        // Collapse nicknames / title prefixes into canonical company names
        // (e.g. "ELEVATE" from Monday titles → "ELEVATE ENERGY SERVICES").
        parties = ConsolidateParties(parties);

        if (parties.Count == 0)
        {
            notes.Add("No usable customer names returned from MaintainX / Monday / EZRentOut.");
        }
        else
        {
            notes.Add($"Collected {parties.Count} unique ops customer name(s).");
        }

        var allWorkItems = live.WorkItems.ToList();
        var allOrders = live.Orders.ToList();
        if (snapshot is not null)
        {
            allWorkItems.AddRange(snapshot.ExternalWorkSample);
            allWorkItems.AddRange(snapshot.QuotesSample);
            allWorkItems.AddRange(snapshot.RecentlyCompleted);
            allOrders.AddRange(snapshot.EzRentOrders);
        }

        var keepIds = new HashSet<Guid>();
        foreach (var party in parties.Values)
        {
            var matchedWork = allWorkItems
                .Where(i =>
                    !i.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase) &&
                    PartyMatches(party.Name, i))
                .GroupBy(i => $"{i.SourceSystem}:{i.ExternalId}")
                .Select(g => g.First())
                .Select(i => new StoredCustomerActivity(
                    Kind: ClassifyWorkKind(i),
                    Title: i.Title,
                    Detail: $"{i.SourceSystem} · {i.Status}",
                    OccurredAt: i.DueAt,
                    Url: i.Url,
                    SourceSystem: i.SourceSystem,
                    ExternalId: i.ExternalId))
                .ToList();

            foreach (var order in allOrders
                         .Where(o => NamesMatch(party.Name, o.Customer))
                         .GroupBy(o => o.OrderId)
                         .Select(g => g.First())
                         .Take(80))
            {
                matchedWork.Add(ToStoredRentalJob(order));
            }

            matchedWork = matchedWork
                .Where(a => !IsLegacyEzAssetRental(a))
                .GroupBy(a => $"{a.SourceSystem}:{a.ExternalId}:{a.Title}")
                .Select(g => g.First())
                .OrderByDescending(a => a.OccurredAt ?? DateTimeOffset.MinValue)
                .Take(120)
                .ToList();

            var prior = FindCustomer(organizationId, party.Name);
            var existed = prior is not null;
            if (prior is not null && matchedWork.Count == 0)
            {
                // Keep previously synced activity when this pass matched nothing new.
                matchedWork = CollapseEzRentalsToJobs(
                        ReadStoredActivity(prior.MetadataJson),
                        allOrders.Where(o => NamesMatch(party.Name, o.Customer)))
                    .ToList();
            }
            else if (prior is not null)
            {
                matchedWork = matchedWork
                    .Concat(ReadStoredActivity(prior.MetadataJson).Where(a => !IsLegacyEzAssetRental(a)))
                    .GroupBy(a => $"{a.SourceSystem}:{a.ExternalId}:{a.Title}")
                    .Select(g => g.First())
                    .OrderByDescending(a => a.OccurredAt ?? DateTimeOffset.MinValue)
                    .Take(120)
                    .ToList();
            }

            var customer = EnsureCustomer(organizationId, party.Name);
            var orderMatches = allOrders.Where(o => NamesMatch(party.Name, o.Customer)).ToList();
            var counts = (
                matchedWork.Count(a => a.Kind == "workorder"),
                matchedWork.Count(a => a.Kind == "quote"),
                orderMatches.Count(o => ClassifyOrderKind(o) == "rental"),
                orderMatches.Count(o => ClassifyOrderKind(o) == "order"));
            WriteActivityMetadata(
                customer,
                matchedWork,
                systems: party.Sources,
                counts: counts);
            keepIds.Add(customer.Id);
            if (!existed)
            {
                customersUpserted++;
            }

            if (!string.IsNullOrWhiteSpace(party.Contact) &&
                EnsureContact(organizationId, customer.Id, party.Contact, null, null))
            {
                contactsUpserted++;
            }
        }

        // Link Email / WhatsApp threads using subject, participants, and message text.
        var opsCustomers = _db.Customers
            .Where(c => c.OrganizationId == organizationId)
            .ToList()
            .Where(c => keepIds.Contains(c.Id))
            .ToList();

        var conversationsLinked = await LinkConversationsAsync(
            organizationId,
            opsCustomers,
            takeConversations: 1000,
            cancellationToken);

        // Always purge non-ops / non-manual customers (the mail/WhatsApp junk).
        var purge = _db.Customers
            .Where(c => c.OrganizationId == organizationId)
            .ToList()
            .Where(c => !keepIds.Contains(c.Id) && !IsManualCustomer(c))
            .ToList();

        var purged = 0;
        foreach (var junk in purge)
        {
            foreach (var contact in _db.Contacts.Where(c => c.CustomerId == junk.Id).ToList())
            {
                _db.Remove(contact);
            }

            foreach (var conversation in _db.Conversations.Where(c => c.CustomerId == junk.Id).ToList())
            {
                conversation.CustomerId = null;
            }

            _db.Remove(junk);
            purged++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        notes.Add(
            parties.Count == 0
                ? $"No ops parties found yet; purged {purged} junk customer(s)."
                : $"Ops parties: {parties.Count}. Added {customersUpserted}, contacts {contactsUpserted}, linked {conversationsLinked}, purged {purged} junk.");
        return new CustomerReconcileResult(
            customersUpserted,
            contactsUpserted,
            conversationsLinked,
            notes);
    }

    public async Task<CustomerCompanyOverviewDto?> GetCompanyOverviewAsync(
        Guid organizationId,
        Guid customerId,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var customer = _db.Customers.FirstOrDefault(c =>
            c.Id == customerId && c.OrganizationId == organizationId);
        if (customer is null)
        {
            return null;
        }

        var cached = ReadCachedOverview(customer.MetadataJson);
        if (!refresh &&
            cached is not null &&
            !string.IsNullOrWhiteSpace(cached.Text) &&
            cached.GeneratedAt > DateTimeOffset.UtcNow.AddDays(-14))
        {
            return new CustomerCompanyOverviewDto(
                customer.Id,
                customer.Name,
                cached.Text,
                cached.GeneratedAt,
                FromCache: true,
                cached.SourceNote);
        }

        if (!_ai.IsConfiguredFor(AiTaskKind.Summarize) && !_ai.IsConfiguredFor(AiTaskKind.Recap))
        {
            var fallback = cached?.Text
                ?? $"No AI provider is configured yet. {customer.Name} is an ops customer in Palantir.";
            return new CustomerCompanyOverviewDto(
                customer.Id,
                customer.Name,
                fallback,
                cached?.GeneratedAt ?? DateTimeOffset.UtcNow,
                FromCache: cached is not null,
                "AI not configured");
        }

        var webBits = await FetchPublicCompanyHintsAsync(customer.Name, cancellationToken);
        var activity = ReadStoredActivity(customer.MetadataJson).Take(12).ToList();
        var contacts = _db.Contacts
            .Where(c => c.OrganizationId == organizationId && c.CustomerId == customerId)
            .ToList();
        var recentThreads = _db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.CustomerId == customerId)
            .ToList()
            .OrderByDescending(c => c.UpdatedAt)
            .Take(8)
            .Select(c => $"{c.Channel}: {c.Subject}")
            .ToList();

        var context = new StringBuilder();
        context.AppendLine($"Company name: {customer.Name}");
        if (contacts.Count > 0)
        {
            context.AppendLine("Known contacts:");
            foreach (var contact in contacts.Take(8))
            {
                context.AppendLine(
                    $"- {contact.DisplayName}" +
                    (string.IsNullOrWhiteSpace(contact.Email) ? "" : $" <{contact.Email}>") +
                    (string.IsNullOrWhiteSpace(contact.Phone) ? "" : $" {contact.Phone}"));
            }
        }

        if (activity.Count > 0)
        {
            context.AppendLine("Recent Palantir ops activity:");
            foreach (var row in activity)
            {
                context.AppendLine($"- [{row.Kind}] {row.Title} ({row.Detail?.Replace('\n', ' ')})");
            }
        }

        var communicationBrief = BuildOrgCommunicationBrief(organizationId, customerId, customer.Name);
        if (!string.IsNullOrWhiteSpace(communicationBrief))
        {
            context.AppendLine("Org-wide communications (any Palantir user — email / WhatsApp):");
            context.AppendLine(communicationBrief);
        }
        else
        {
            context.AppendLine("Org-wide communications: no recent email/WhatsApp matches found for this customer name.");
        }

        if (recentThreads.Count > 0)
        {
            context.AppendLine("Linked email / WhatsApp threads:");
            foreach (var thread in recentThreads)
            {
                context.AppendLine($"- {thread}");
            }
        }

        var domains = ExtractRelatedEmailDomains(organizationId, customerId);
        if (domains.Count > 0)
        {
            context.AppendLine("Email domains seen on linked threads (possible company websites):");
            foreach (var domain in domains)
            {
                context.AppendLine($"- {domain} (likely https://{domain})");
            }
        }

        if (!string.IsNullOrWhiteSpace(webBits))
        {
            context.AppendLine("Public web hints:");
            context.AppendLine(webBits);
        }

        var task = _ai.IsConfiguredFor(AiTaskKind.Recap) ? AiTaskKind.Recap : AiTaskKind.Summarize;
        string overview;
        try
        {
            overview = (await _ai.CompleteAsync(
                task,
                [
                    new AiChatMessage(
                        "system",
                        """
                        You write concise CRM company briefings for Sable Automation Solutions sales/ops staff.

                        Formatting (required):
                        - Do NOT use markdown headings (#, ##, ###) or horizontal rules.
                        - Light **bold** or *italic* is fine when helpful (the UI renders it).
                        - Use short section labels on their own line, then bullet points under each.
                        - Prefer "- " bullets. Keep paragraphs short. No code fences.

                        Include these sections when you have something useful to say:
                        Company
                        - 1-2 bullets on what they do / industry
                        Website & footprint
                        - Website if known; otherwise say unknown
                        - Size/locations only if reasonably known
                        Relevance to Sable
                        - Rentals, meters, water, oilfield services, etc. when implied
                        Recent activity
                        - Brief bullets on open rentals/quotes/work from the context
                        - Who contacted them lately (email / WhatsApp), including last contact timing
                        - Say clearly if nobody in the org has messaged them recently
                        Watch-outs
                        - Uncertainties and gaps

                        Rules:
                        - Prefer facts from the provided context and public web hints (search results + page extracts).
                        - When public web hints include a company website, services description, or locations, surface those in Company / Website & footprint.
                        - Do not invent precise employee counts, revenue, URLs, or message contents.
                        - You may paraphrase public snippets; cite the website URL when known.
                        - Keep it under ~260 words.
                        - No preamble like "Here is an overview".
                        """),
                    new AiChatMessage(
                        "user",
                        $"""
                        Write the company overview for this customer.

                        {context}
                        """)
                ],
                cancellationToken)).Trim();
            overview = AiTextSanitizer.SanitizeProse(overview);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Company overview generation failed for {Customer}", customer.Name);
            overview = cached?.Text
                ?? $"Could not generate an AI overview for {customer.Name} right now.";
            return new CustomerCompanyOverviewDto(
                customer.Id,
                customer.Name,
                overview,
                DateTimeOffset.UtcNow,
                FromCache: false,
                "Generation failed");
        }

        var sourceNote = string.IsNullOrWhiteSpace(webBits)
            ? "AI + Palantir activity"
            : "AI + Palantir activity + public web hints";
        WriteCachedOverview(customer, overview, sourceNote);
        await _db.SaveChangesAsync(cancellationToken);

        return new CustomerCompanyOverviewDto(
            customer.Id,
            customer.Name,
            overview,
            DateTimeOffset.UtcNow,
            FromCache: false,
            sourceNote);
    }

    public async Task<int> WarmFromSnapshotAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _opsSnapshots.TryGetFreshAsync(
            organizationId,
            IOpsSnapshotStore.DefaultFocusKey,
            cancellationToken)
            ?? await _opsSnapshots.TryGetLatestReadyAsync(
                organizationId,
                IOpsSnapshotStore.DefaultFocusKey,
                cancellationToken);
        if (snapshot is null)
        {
            return 0;
        }

        var snapshotItems = (snapshot.ExternalWorkSample ?? Array.Empty<ExternalWorkItemDto>())
            .Concat(snapshot.QuotesSample ?? Array.Empty<ExternalWorkItemDto>())
            .Concat(snapshot.RecentlyCompleted ?? Array.Empty<ExternalWorkItemDto>())
            .Where(i => !i.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var snapshotOrders = snapshot.EzRentOrders ?? Array.Empty<EzRentOrderDto>();

        // Ensure party rows exist for every ops customer name in the snapshot.
        var partyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshotItems)
        {
            foreach (var piece in SplitPartyNames(ReadPartyName(item.Metadata) ?? ExtractTitleParty(item.Title)))
            {
                if (IsPlausibleOpsCustomerName(piece))
                {
                    partyNames.Add(piece.Trim());
                }
            }
        }

        foreach (var order in snapshotOrders)
        {
            if (IsPlausibleOpsCustomerName(order.Customer))
            {
                partyNames.Add(order.Customer.Trim());
            }
        }

        var updated = 0;
        foreach (var name in partyNames.OrderByDescending(n => n.Length))
        {
            var customer = EnsureCustomer(organizationId, name);
            var fromDb = ReadStoredActivity(customer.MetadataJson).Where(a => !IsLegacyEzAssetRental(a)).ToList();
            var stored = BuildPersistedOpsActivity(customer.Name, fromDb, snapshotItems, snapshotOrders);
            if (stored.Count == 0)
            {
                continue;
            }

            var counts = (
                stored.Count(a => a.Kind == "workorder"),
                stored.Count(a => a.Kind == "quote"),
                snapshotOrders.Count(o => NamesMatch(customer.Name, o.Customer) && ClassifyOrderKind(o) == "rental"),
                snapshotOrders.Count(o => NamesMatch(customer.Name, o.Customer) && ClassifyOrderKind(o) == "order"));
            if (WriteActivityMetadata(customer, stored, systems: ["snapshot"], counts: counts))
            {
                updated++;
            }
        }

        if (updated > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Warmed {Count} customer CRM row(s) from ops snapshot for org {OrganizationId}",
            updated,
            organizationId);
        return updated;
    }

    private async Task<LiveOpsPartyPull> CollectLiveOpsPartiesAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var workItems = new List<ExternalWorkItemDto>();
        var orders = new List<EzRentOrderDto>();

        var mxTasks = _maintainXOptions.Environments
            .Where(e => !string.IsNullOrWhiteSpace(e.ApiKey))
            .Select(async env =>
            {
                try
                {
                    var items = await _maintainX.ListOpenWorkAsync(env, cancellationToken);
                    return (Ok: true, Env: env.Name, Items: items, Error: (string?)null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MaintainX customer pull failed for {Env}", env.Name);
                    return (Ok: false, Env: env.Name, Items: (IReadOnlyList<ExternalWorkItemDto>)Array.Empty<ExternalWorkItemDto>(), Error: ex.Message);
                }
            })
            .ToList();

        var ezRentalsTask = Task.Run(async () =>
        {
            try
            {
                var items = await _ezRentOut.ListOpenWorkAsync(cancellationToken);
                return (Ok: true, Items: items, Error: (string?)null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EZRentOut open-work customer pull failed");
                return (Ok: false, Items: (IReadOnlyList<ExternalWorkItemDto>)Array.Empty<ExternalWorkItemDto>(), Error: ex.Message);
            }
        }, cancellationToken);

        var ezOrdersTask = Task.Run(async () =>
        {
            try
            {
                var ezOrders = await _ezRentOut.ListOrdersAsync(cancellationToken);
                return (Ok: true, Orders: ezOrders, Error: (string?)null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EZRentOut orders customer pull failed");
                return (Ok: false, Orders: (IReadOnlyList<EzRentOrderDto>)Array.Empty<EzRentOrderDto>(), Error: ex.Message);
            }
        }, cancellationToken);

        var mondayTask = Task.Run(async () =>
        {
            try
            {
                var items = await _monday.ListOpenWorkAsync(cancellationToken);
                return (Ok: true, Items: items, Error: (string?)null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monday customer pull failed");
                return (Ok: false, Items: (IReadOnlyList<ExternalWorkItemDto>)Array.Empty<ExternalWorkItemDto>(), Error: ex.Message);
            }
        }, cancellationToken);

        var mxResults = await Task.WhenAll(mxTasks);
        foreach (var mx in mxResults)
        {
            if (mx.Ok)
            {
                workItems.AddRange(mx.Items);
                notes.Add($"MaintainX ({mx.Env}): {mx.Items.Count} open work item(s).");
            }
            else
            {
                notes.Add($"MaintainX ({mx.Env}) failed: {mx.Error}");
            }
        }

        var ezRentals = await ezRentalsTask;
        if (ezRentals.Ok)
        {
            workItems.AddRange(ezRentals.Items);
            notes.Add($"EZRentOut rentals: {ezRentals.Items.Count} item(s).");
        }
        else
        {
            notes.Add($"EZRentOut rentals failed: {ezRentals.Error}");
        }

        var ezOrdersResult = await ezOrdersTask;
        if (ezOrdersResult.Ok)
        {
            orders.AddRange(ezOrdersResult.Orders);
            notes.Add($"EZRentOut orders: {ezOrdersResult.Orders.Count} order(s).");
        }
        else
        {
            notes.Add($"EZRentOut orders failed: {ezOrdersResult.Error}");
        }

        var monday = await mondayTask;
        if (monday.Ok)
        {
            workItems.AddRange(monday.Items);
            notes.Add($"Monday: {monday.Items.Count} item(s).");
        }
        else
        {
            notes.Add($"Monday failed: {monday.Error}");
        }

        return new LiveOpsPartyPull(workItems, orders, notes);
    }

    private OverviewSnapshotDto? TryReadLatestReadySnapshot(Guid organizationId)
    {
        var row = _db.OpsSnapshots
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.FocusKey == IOpsSnapshotStore.DefaultFocusKey &&
                x.Status == "Ready")
            .OrderByDescending(x => x.GeneratedAt)
            .FirstOrDefault();
        if (row is null || string.IsNullOrWhiteSpace(row.SnapshotJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OverviewSnapshotDto>(
                row.SnapshotJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private Customer? FindCustomer(Guid organizationId, string name)
    {
        var customers = _db.Customers
            .Where(c => c.OrganizationId == organizationId)
            .ToList();
        var exact = customers.FirstOrDefault(c => NormalizeName(c.Name) == NormalizeName(name));
        if (exact is not null)
        {
            return exact;
        }

        return customers
            .Select(c => (Customer: c, Score: ScorePartyNameMatch(c.Name, name)))
            .Where(x => x.Score >= 100)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Customer.Name.Length)
            .Select(x => x.Customer)
            .FirstOrDefault();
    }

    private Customer EnsureCustomer(Guid organizationId, string name, string? metadataJson = null)
    {
        var existing = FindCustomer(organizationId, name);
        if (existing is not null)
        {
            // Prefer the longer / more complete company name as canonical.
            if (name.Trim().Length > existing.Name.Length + 2 &&
                ScorePartyNameMatch(existing.Name, name) >= 100)
            {
                existing.Name = name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                MergeMetadataJson(existing, metadataJson);
            }

            return existing;
        }

        var customer = new Customer
        {
            OrganizationId = organizationId,
            Name = name.Trim(),
            MetadataJson = metadataJson ?? JsonSerializer.Serialize(new { source = "ops" })
        };
        _db.Add(customer);
        return customer;
    }

    private static List<StoredCustomerActivity> BuildPersistedOpsActivity(
        string customerName,
        IReadOnlyList<StoredCustomerActivity> fromDb,
        IReadOnlyList<ExternalWorkItemDto> snapshotItems,
        IReadOnlyList<EzRentOrderDto> snapshotOrders)
    {
        var stored = CollapseEzRentalsToJobs(
                fromDb,
                snapshotOrders.Where(o => NamesMatch(customerName, o.Customer)))
            .ToList();

        var hasNonEz = stored.Any(a => a.Kind is "workorder" or "quote");
        if (!hasNonEz)
        {
            stored.AddRange(
                snapshotItems
                    .Where(i =>
                        !i.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase) &&
                        PartyMatches(customerName, i))
                    .GroupBy(i => $"{i.SourceSystem}:{i.ExternalId}")
                    .Select(g => g.First())
                    .Select(i => new StoredCustomerActivity(
                        ClassifyWorkKind(i),
                        i.Title,
                        $"{i.SourceSystem} · {i.Status}",
                        i.DueAt,
                        i.Url,
                        i.SourceSystem,
                        i.ExternalId)));
        }

        return stored
            .Where(a => !IsLegacyEzAssetRental(a))
            .GroupBy(a => $"{a.SourceSystem}:{a.ExternalId}:{a.Title}")
            .Select(g => g.First())
            .OrderByDescending(a => a.OccurredAt ?? DateTimeOffset.MinValue)
            .Take(120)
            .ToList();
    }

    private static bool ShouldPersistActivity(
        IReadOnlyList<StoredCustomerActivity> fromDb,
        IReadOnlyList<StoredCustomerActivity> computed)
    {
        if (computed.Count == 0)
        {
            return false;
        }

        if (fromDb.Count == 0)
        {
            return true;
        }

        // Persist when snapshot/live enrichment added meaningful rows.
        return computed.Count > fromDb.Count + 2 ||
               computed.Count(a => a.Kind is "rental" or "order") >
               fromDb.Count(a => a.Kind is "rental" or "order");
    }

    /// <summary>
    /// Merge ops activity into Customer.MetadataJson without wiping overview / other cached fields.
    /// </summary>
    private static bool WriteActivityMetadata(
        Customer customer,
        IReadOnlyList<StoredCustomerActivity> activity,
        string[]? systems,
        (int WorkOrders, int Quotes, int Rentals, int Orders)? counts = null)
    {
        if (activity.Count == 0 && counts is null)
        {
            return false;
        }

        using var activityDoc = JsonDocument.Parse(JsonSerializer.Serialize(activity));
        var map = ReadMetadataMap(customer.MetadataJson);

        if (!map.ContainsKey("source"))
        {
            map["source"] = JsonSerializer.SerializeToElement("ops");
        }

        if (systems is { Length: > 0 })
        {
            map["systems"] = JsonSerializer.SerializeToElement(systems);
        }

        if (activity.Count > 0)
        {
            map["activity"] = activityDoc.RootElement.Clone();
        }

        map["syncedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
        if (counts is { } c)
        {
            map["counts"] = JsonSerializer.SerializeToElement(new
            {
                workOrders = c.WorkOrders,
                quotes = c.Quotes,
                rentals = c.Rentals,
                orders = c.Orders
            });
        }

        var next = WriteMetadataMap(map);
        if (string.Equals(customer.MetadataJson, next, StringComparison.Ordinal))
        {
            return false;
        }

        customer.MetadataJson = next;
        return true;
    }

    private static (int WorkOrders, int Quotes, int Rentals, int Orders)? ReadCachedCounts(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("counts", out var counts) ||
                counts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int ReadInt(string name) =>
                counts.TryGetProperty(name, out var el) && el.TryGetInt32(out var n) ? n : 0;

            return (ReadInt("workOrders"), ReadInt("quotes"), ReadInt("rentals"), ReadInt("orders"));
        }
        catch
        {
            return null;
        }
    }

    private static void MergeMetadataJson(Customer customer, string incomingJson)
    {
        var incoming = ReadMetadataMap(incomingJson);
        var existing = ReadMetadataMap(customer.MetadataJson);
        foreach (var (key, value) in incoming)
        {
            existing[key] = value.Clone();
        }

        // Never let a partial ops write erase a cached overview.
        var priorOverview = ReadCachedOverview(customer.MetadataJson);
        if (priorOverview is not null && !existing.ContainsKey("overview"))
        {
            existing["overview"] = JsonSerializer.SerializeToElement(priorOverview.Text);
            existing["overviewAt"] = JsonSerializer.SerializeToElement(priorOverview.GeneratedAt);
            if (!string.IsNullOrWhiteSpace(priorOverview.SourceNote))
            {
                existing["overviewSource"] = JsonSerializer.SerializeToElement(priorOverview.SourceNote);
            }
        }

        customer.MetadataJson = WriteMetadataMap(existing);
    }

    private static Dictionary<string, JsonElement> ReadMetadataMap(string? metadataJson)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
        }
        catch
        {
            map.Clear();
        }

        return map;
    }

    private static string WriteMetadataMap(Dictionary<string, JsonElement> map)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in map)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MetadataActivityFingerprint(string? metadataJson)
    {
        var rows = ReadStoredActivity(metadataJson)
            .Select(a => $"{a.Kind}|{a.SourceSystem}|{a.ExternalId}|{a.Title}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", rows);
    }

    private static string? TryExtractOrderId(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = Regex.Match(title, @"^Order\s+(?<id>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private bool EnsureContact(
        Guid organizationId,
        Guid customerId,
        string displayName,
        string? email,
        string? phone)
    {
        var contacts = _db.Contacts
            .Where(c => c.OrganizationId == organizationId && c.CustomerId == customerId)
            .ToList();

        var emailNorm = email?.Trim().ToLowerInvariant();
        var phoneNorm = DigitsOnly(phone);
        var existing = contacts.FirstOrDefault(c =>
            (!string.IsNullOrWhiteSpace(emailNorm) &&
             string.Equals(c.Email, emailNorm, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(phoneNorm) && DigitsOnly(c.Phone) == phoneNorm) ||
            string.Equals(c.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var changed = false;
            if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(emailNorm))
            {
                existing.Email = emailNorm;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.Phone) && !string.IsNullOrWhiteSpace(phoneNorm))
            {
                existing.Phone = phoneNorm;
                changed = true;
            }

            return changed;
        }

        _db.Add(new Contact
        {
            OrganizationId = organizationId,
            CustomerId = customerId,
            DisplayName = displayName.Trim(),
            Email = emailNorm,
            Phone = string.IsNullOrWhiteSpace(phoneNorm) ? null : phoneNorm
        });
        return true;
    }

    private static bool IsManualCustomer(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.MetadataJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(customer.MetadataJson);
            return doc.RootElement.TryGetProperty("source", out var source) &&
                   string.Equals(source.GetString(), "manual", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool PartyMatches(string customerName, ExternalWorkItemDto item)
    {
        var left = customerName?.Trim() ?? string.Empty;
        if (left.Length < 3)
        {
            return false;
        }

        var candidates = new List<string?>
        {
            ReadPartyName(item.Metadata),
            ReadMeta(item.Metadata, "location"),
            item.Assignee,
            item.Title,
            ExtractTitleParty(item.Title)
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (NamesMatch(left, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NamesMatch(string? left, string? right) =>
        ScorePartyNameMatch(left ?? string.Empty, right ?? string.Empty) >= 40;

    private static int ScorePartyNameMatch(string leftRaw, string rightRaw)
    {
        var left = NormalizeName(leftRaw);
        var right = NormalizeName(rightRaw);
        if (left.Length < 3 || right.Length < 3)
        {
            return 0;
        }

        var score = 0;
        if (left == right)
        {
            return 1000 + left.Length;
        }

        if (right.Contains(left, StringComparison.Ordinal) || left.Contains(right, StringComparison.Ordinal))
        {
            score = Math.Max(score, 700 + Math.Min(left.Length, right.Length));
        }

        var compactLeft = CompactName(left);
        var compactRight = CompactName(right);
        if (compactLeft.Length >= 4 &&
            (compactRight.Contains(compactLeft, StringComparison.Ordinal) ||
             compactLeft.Contains(compactRight, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 650 + Math.Min(compactLeft.Length, compactRight.Length));
        }

        var leftTokens = SignificantTokens(left);
        var rightTokens = SignificantTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return score;
        }

        if (leftTokens.All(t => rightTokens.Contains(t) || right.Contains(t, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 500 + leftTokens.Sum(t => t.Length));
        }

        if (rightTokens.All(t => leftTokens.Contains(t) || left.Contains(t, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 500 + rightTokens.Sum(t => t.Length));
        }

        foreach (var token in leftTokens.Concat(rightTokens).Distinct().OrderByDescending(t => t.Length))
        {
            if (token.Length < 4)
            {
                continue;
            }

            var inLeft = leftTokens.Contains(token) || left.Contains(token, StringComparison.Ordinal);
            var inRight = rightTokens.Contains(token) || right.Contains(token, StringComparison.Ordinal);
            if (inLeft && inRight)
            {
                score = Math.Max(score, 100 + token.Length * 10);
            }
        }

        return score;
    }

    private async Task<int> LinkConversationsAsync(
        Guid organizationId,
        IReadOnlyList<Customer> customers,
        int takeConversations,
        CancellationToken cancellationToken)
    {
        if (customers.Count == 0)
        {
            return 0;
        }

        var customerIds = customers.Select(c => c.Id).ToHashSet();
        var conversations = _db.Conversations
            .Where(c =>
                c.OrganizationId == organizationId &&
                (c.Channel == "Email" || c.Channel == "WhatsApp"))
            .ToList()
            .OrderByDescending(c => c.UpdatedAt)
            .Take(takeConversations)
            .ToList();

        if (conversations.Count == 0)
        {
            return 0;
        }

        var conversationIds = conversations.Select(c => c.Id).ToHashSet();
        var messagesByConversation = _db.Messages
            .Where(m => conversationIds.Contains(m.ConversationId))
            .ToList()
            .GroupBy(m => m.ConversationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Message>)g.OrderByDescending(m => m.CreatedAt).Take(12).ToList());

        var linked = 0;
        var dirty = false;
        foreach (var conversation in conversations)
        {
            messagesByConversation.TryGetValue(conversation.Id, out var messages);
            var haystack = BuildConversationHaystack(conversation, messages ?? Array.Empty<Message>());

            var ranked = customers
                .Select(c =>
                {
                    var subjectScore = ScoreNameToHaystack(c.Name, conversation.Subject ?? string.Empty);
                    var fullScore = ScoreNameToHaystack(c.Name, haystack);
                    // Prefer subject/title hits; require a stronger score when the hit is only in body/metadata.
                    var score = subjectScore >= 40
                        ? Math.Max(subjectScore, fullScore)
                        : fullScore >= 120
                            ? fullScore
                            : 0;
                    return (Customer: c, Score: score);
                })
                .Where(x => x.Score >= 40 && x.Customer.Name.Length >= 4)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Customer.Name.Length)
                .ToList();

            var match = ranked.Count == 0 ? null : ranked[0].Customer;
            // Avoid noisy single-token collisions when multiple customers score equally weakly.
            if (match is not null && ranked.Count > 1 && ranked[0].Score == ranked[1].Score && ranked[0].Score < 200)
            {
                match = null;
            }
            if (match is null)
            {
                if (conversation.CustomerId.HasValue &&
                    customers.Count > 1 &&
                    !customerIds.Contains(conversation.CustomerId.Value))
                {
                    // Multi-customer reconcile pass: clear links to customers outside the keep set.
                    conversation.CustomerId = null;
                    conversation.UpdatedAt = DateTimeOffset.UtcNow;
                    dirty = true;
                }

                continue;
            }

            if (conversation.CustomerId != match.Id)
            {
                conversation.CustomerId = match.Id;
                conversation.UpdatedAt = DateTimeOffset.UtcNow;
                linked++;
                dirty = true;
            }
        }

        if (dirty)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return linked;
    }

    private static string BuildConversationHaystack(
        Conversation conversation,
        IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(conversation.Subject))
        {
            sb.AppendLine(conversation.Subject);
        }

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Summary))
            {
                sb.AppendLine(message.Summary);
            }

            AppendMetadataHaystack(sb, message.ProviderMetadataJson);

            if (!string.IsNullOrWhiteSpace(message.Body))
            {
                var body = message.Body.Length > 800 ? message.Body[..800] : message.Body;
                sb.AppendLine(body);
            }
        }

        return sb.ToString();
    }

    private static void AppendMetadataHaystack(StringBuilder sb, string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            foreach (var key in new[] { "from", "to", "cc", "notifyName", "participant", "chatId" })
            {
                if (!root.TryGetProperty(key, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(value.GetString());
                }
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            sb.AppendLine(item.GetString());
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore malformed metadata
        }
    }

    private static CachedOverview? ReadCachedOverview(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("overview", out var overview) ||
                overview.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = overview.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            DateTimeOffset generatedAt = DateTimeOffset.MinValue;
            if (doc.RootElement.TryGetProperty("overviewAt", out var at) &&
                at.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(at.GetString(), out var parsed))
            {
                generatedAt = parsed;
            }

            string? sourceNote = null;
            if (doc.RootElement.TryGetProperty("overviewSource", out var source) &&
                source.ValueKind == JsonValueKind.String)
            {
                sourceNote = source.GetString();
            }

            return new CachedOverview(text, generatedAt, sourceNote);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCachedOverview(Customer customer, string overview, string sourceNote)
    {
        var map = ReadMetadataMap(customer.MetadataJson);
        map["overview"] = JsonSerializer.SerializeToElement(overview);
        map["overviewAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
        map["overviewSource"] = JsonSerializer.SerializeToElement(sourceNote);
        customer.MetadataJson = WriteMetadataMap(map);
    }

    private List<string> ExtractRelatedEmailDomains(Guid organizationId, Guid customerId)
    {
        var conversationIds = _db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.CustomerId == customerId)
            .Select(c => c.Id)
            .ToList()
            .ToHashSet();
        if (conversationIds.Count == 0)
        {
            return [];
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gmail.com", "googlemail.com", "yahoo.com", "hotmail.com", "outlook.com", "live.com",
            "icloud.com", "aol.com", "msn.com", "me.com", "dnow.com", "microsoft.com", "sableautomation.com"
        };

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in _db.Messages
                     .Where(m => conversationIds.Contains(m.ConversationId))
                     .OrderByDescending(m => m.CreatedAt)
                     .Take(40)
                     .ToList())
        {
            CollectEmailDomains(domains, message.Body);
            CollectEmailDomains(domains, message.Summary);
            CollectEmailDomains(domains, message.ProviderMetadataJson);
        }

        return domains
            .Where(d => !blocked.Contains(d) && d.Contains('.') && d.Length is >= 4 and <= 80)
            .OrderBy(d => d)
            .Take(6)
            .ToList();
    }

    private static void CollectEmailDomains(HashSet<string> domains, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in Regex.Matches(text, @"[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,})", RegexOptions.IgnoreCase))
        {
            if (match.Groups.Count > 1)
            {
                domains.Add(match.Groups[1].Value.ToLowerInvariant());
            }
        }
    }

    private async Task<string?> FetchPublicCompanyHintsAsync(
        string companyName,
        CancellationToken cancellationToken)
    {
        try
        {
            var name = companyName.Trim();
            if (name.Length < 2)
            {
                return null;
            }

            var queries = new[]
            {
                name,
                $"{name} company",
                $"{name} oilfield OR energy OR water services",
                $"\"{name}\" headquarters OR LinkedIn OR website"
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var hits = new List<WebSearchHit>();
            foreach (var query in queries)
            {
                var pageHits = await SearchDuckDuckGoLiteAsync(query, cancellationToken);
                hits.AddRange(pageHits);
                if (hits.Count >= 12)
                {
                    break;
                }
            }

            // Prefer results whose title/url/snippet mention significant name tokens.
            var tokens = SignificantTokens(NormalizeName(name))
                .Where(t => t.Length >= 4)
                .ToArray();
            var ranked = hits
                .GroupBy(h => NormalizeUrlKey(h.Url), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(h => (Hit: h, Score: ScoreWebHit(h, tokens, name)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Hit.Url.Length)
                .Take(8)
                .Select(x => x.Hit)
                .ToList();

            if (ranked.Count == 0 && hits.Count > 0)
            {
                ranked = hits
                    .GroupBy(h => NormalizeUrlKey(h.Url), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Take(5)
                    .ToList();
            }

            var sb = new StringBuilder();
            if (ranked.Count > 0)
            {
                sb.AppendLine("Search results:");
                foreach (var hit in ranked.Take(6))
                {
                    sb.AppendLine($"- {hit.Title}");
                    sb.AppendLine($"  URL: {hit.Url}");
                    if (!string.IsNullOrWhiteSpace(hit.Snippet))
                    {
                        sb.AppendLine($"  Snippet: {TruncateText(hit.Snippet, 280)}");
                    }
                }
            }

            // Fetch a few likely company pages for richer copy (about / home).
            var fetchTargets = ranked
                .Where(h => IsLikelyCompanySite(h.Url))
                .Select(h => h.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            foreach (var target in fetchTargets)
            {
                var pageText = await FetchPagePlainTextAsync(target, cancellationToken);
                if (string.IsNullOrWhiteSpace(pageText))
                {
                    continue;
                }

                sb.AppendLine();
                sb.AppendLine($"Page extract ({target}):");
                sb.AppendLine(TruncateText(pageText, 1400));
            }

            // Instant Answer as a small extra (often empty for private firms).
            var instant = await FetchDuckDuckGoInstantAnswerAsync(name, cancellationToken);
            if (!string.IsNullOrWhiteSpace(instant))
            {
                sb.AppendLine();
                sb.AppendLine("Instant answer:");
                sb.AppendLine(instant);
            }

            var hints = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(hints))
            {
                return null;
            }

            _logger.LogInformation(
                "Public company research for {Company}: {HitCount} search hits, {PageCount} pages fetched, {Chars} chars",
                name,
                ranked.Count,
                fetchTargets.Count,
                hints.Length);
            return hints;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Public company lookup failed for {Company}", companyName);
            return null;
        }
    }

    private async Task<string?> FetchDuckDuckGoInstantAnswerAsync(
        string companyName,
        CancellationToken cancellationToken)
    {
        try
        {
            var q = Uri.EscapeDataString(companyName.Trim());
            var url =
                $"https://api.duckduckgo.com/?q={q}&format=json&no_html=1&skip_disambig=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "PalantirCRM/1.0");
            using var response = await PublicLookupHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            var sb = new StringBuilder();

            if (root.TryGetProperty("Heading", out var heading) &&
                heading.GetString() is { Length: > 0 } h)
            {
                sb.AppendLine($"Heading: {h}");
            }

            if (root.TryGetProperty("AbstractText", out var abs) &&
                abs.GetString() is { Length: > 0 } abstractText)
            {
                sb.AppendLine(abstractText);
            }

            if (root.TryGetProperty("AbstractURL", out var absUrl) &&
                absUrl.GetString() is { Length: > 0 } urlHint)
            {
                sb.AppendLine($"Source: {urlHint}");
            }

            var hints = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(hints) ? null : hints;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<WebSearchHit>> SearchDuckDuckGoLiteAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var results = new List<WebSearchHit>();
        try
        {
            var url = "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (compatible; PalantirCRM/1.0; +https://localhost)");
            request.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var response = await PublicLookupHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return results;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var linkMatches = Regex.Matches(
                html,
                @"href=""(?<href>[^""]+)""[^>]*class=['""]result-link['""]>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snipMatches = Regex.Matches(
                html,
                @"class=['""]result-snippet['""]>(?<snip>.*?)</(?:td|span|div)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            for (var i = 0; i < linkMatches.Count; i++)
            {
                var href = linkMatches[i].Groups["href"].Value;
                var title = StripHtml(linkMatches[i].Groups["title"].Value);
                var realUrl = ExtractDuckDuckGoTargetUrl(href) ?? href;
                if (!Uri.TryCreate(realUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                var snippet = i < snipMatches.Count
                    ? StripHtml(snipMatches[i].Groups["snip"].Value)
                    : "";
                results.Add(new WebSearchHit(title, uri.ToString(), snippet));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DuckDuckGo lite search failed for query {Query}", query);
        }

        return results;
    }

    private async Task<string?> FetchPagePlainTextAsync(
        string pageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (compatible; PalantirCRM/1.0)");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await PublicLookupHttp.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Length > 0 &&
                !media.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !media.Contains("text", StringComparison.OrdinalIgnoreCase) &&
                !media.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            if (html.Length > 600_000)
            {
                html = html[..600_000];
            }

            return ExtractReadablePageText(html);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch company page {Url}", pageUrl);
            return null;
        }
    }

    private static string? ExtractDuckDuckGoTargetUrl(string href)
    {
        try
        {
            // DDG lite wraps destinations: //duckduckgo.com/l/?uddg=https%3A%2F%2F...
            var match = Regex.Match(href, @"[?&]uddg=([^&]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return Uri.UnescapeDataString(match.Groups[1].Value);
            }

            if (href.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + href;
            }

            return href;
        }
        catch
        {
            return null;
        }
    }

    private static int ScoreWebHit(WebSearchHit hit, IReadOnlyList<string> tokens, string companyName)
    {
        var hay = NormalizeName($"{hit.Title} {hit.Url} {hit.Snippet}");
        var score = 0;
        var name = NormalizeName(companyName);
        if (hay.Contains(name, StringComparison.Ordinal))
        {
            score += 200;
        }

        foreach (var token in tokens)
        {
            if (hay.Contains(token, StringComparison.Ordinal))
            {
                score += 40 + Math.Min(token.Length, 12);
            }
        }

        var host = TryGetHost(hit.Url);
        if (host is not null)
        {
            if (tokens.Any(t => host.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                score += 120;
            }

            if (host.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }

            if (host.Contains("chamberofcommerce", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("yelp.com", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("facebook.com", StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }

            // Downrank obvious wrong verticals / aggregators.
            if (host.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (host.Contains("amazon.", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("ebay.", StringComparison.OrdinalIgnoreCase))
            {
                score -= 80;
            }
        }

        if (IsLikelyCompanySite(hit.Url))
        {
            score += 50;
        }

        return score;
    }

    private static bool IsLikelyCompanySite(string url)
    {
        var host = TryGetHost(url);
        if (host is null)
        {
            return false;
        }

        // Skip social/search/news hubs when choosing pages to scrape; snippets still used.
        string[] skip =
        [
            "linkedin.com", "facebook.com", "twitter.com", "x.com", "instagram.com",
            "youtube.com", "duckduckgo.com", "google.com", "bing.com", "yahoo.com",
            "wikipedia.org", "crunchbase.com", "bloomberg.com", "reuters.com"
        ];
        if (skip.Any(s => host.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                          host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string NormalizeUrlKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim().ToLowerInvariant();
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Host.ToLowerInvariant()}{path.ToLowerInvariant()}";
    }

    private static string StripHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = Regex.Replace(value, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string ExtractReadablePageText(string html)
    {
        var without = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        without = Regex.Replace(without, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        without = Regex.Replace(without, @"<noscript[\s\S]*?</noscript>", " ", RegexOptions.IgnoreCase);
        without = Regex.Replace(without, @"<!--[\s\S]*?-->", " ");
        // Prefer main/article blocks when present.
        var main = Regex.Match(
            without,
            @"<(?:main|article)[^>]*>([\s\S]{200,}?)</(?:main|article)>",
            RegexOptions.IgnoreCase);
        var focus = main.Success ? main.Groups[1].Value : without;
        var text = StripHtml(focus);
        // Drop very short noise tokens.
        return Regex.Replace(text, @"\b(LEARN MORE|Skip to content|Search for:)\b", " ", RegexOptions.IgnoreCase)
            .Trim();
    }

    private static string TruncateText(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars].TrimEnd() + "…";
    }

    private sealed record WebSearchHit(string Title, string Url, string Snippet);

    private static int ScoreNameToHaystack(string customerName, string haystack)
    {
        var name = NormalizeName(customerName);
        var text = NormalizeName(haystack);
        if (name.Length < 4 || text.Length < 3)
        {
            return 0;
        }

        if (text.Contains(name, StringComparison.Ordinal))
        {
            return 1000 + name.Length;
        }

        var score = 0;
        foreach (var token in SignificantTokens(name).Where(t => t.Length >= 4))
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                score = Math.Max(score, 100 + token.Length * 10);
            }
        }

        return score;
    }

    private static Dictionary<string, OpsParty> ConsolidateParties(Dictionary<string, OpsParty> parties)
    {
        var ordered = parties.Values
            .OrderByDescending(p => SignificantTokens(p.Name).Count)
            .ThenByDescending(p => p.Name.Length)
            .ThenByDescending(p => p.Sources.Length)
            .ToList();

        var keep = new List<OpsParty>();
        foreach (var party in ordered)
        {
            var hostIndex = keep.FindIndex(h => ScorePartyNameMatch(h.Name, party.Name) >= 100);
            if (hostIndex < 0)
            {
                keep.Add(party);
                continue;
            }

            var host = keep[hostIndex];
            var preferredName = host.Name.Length >= party.Name.Length ? host.Name : party.Name;
            var contact = host.Contact ?? party.Contact;
            var sources = host.Sources
                .Concat(party.Sources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            keep[hostIndex] = new OpsParty(preferredName, contact, sources);
        }

        return keep.ToDictionary(p => NormalizeName(p.Name), p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractTitleParty(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Monday quotes often look like "ELEVATE - PROJECT" or "ELEVATE- PROJECT".
        var match = Regex.Match(title.Trim(), @"^(?<party>[A-Za-z0-9][A-Za-z0-9 &./']{1,48}?)\s*[-–—:]\s+\S");
        if (!match.Success)
        {
            return null;
        }

        var party = match.Groups["party"].Value.Trim(" -–—:\t".ToCharArray());
        return IsPlausibleOpsCustomerName(party) ? party : null;
    }

    private static string ClassifyWorkKind(ExternalWorkItemDto item)
    {
        if (item.SourceSystem.Equals("Monday", StringComparison.OrdinalIgnoreCase))
        {
            return "quote";
        }

        if (item.SourceSystem.Equals("EZRentOut", StringComparison.OrdinalIgnoreCase))
        {
            return "rental";
        }

        return "workorder";
    }

    private static string ClassifyOrderKind(EzRentOrderDto order)
    {
        var state = order.State?.Trim() ?? "";
        if (state.Equals("checked_out", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("checkin_payment_pending", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("checkout", StringComparison.OrdinalIgnoreCase))
        {
            return "rental";
        }

        return "order";
    }

    private static StoredCustomerActivity ToStoredRentalJob(EzRentOrderDto order)
    {
        var row = ToActivityDto(order);
        return new StoredCustomerActivity(
            row.Kind,
            row.Title,
            row.Detail,
            row.OccurredAt,
            row.Url,
            row.SourceSystem,
            order.OrderId);
    }

    private static CustomerActivityDto ToActivityDto(EzRentOrderDto order)
    {
        var job = string.IsNullOrWhiteSpace(order.JobLabel) ? null : order.JobLabel.Trim();
        // Job / site name is the primary label; order number is secondary.
        var title = job is null
            ? $"Order {order.OrderId}"
            : $"{job} · Order {order.OrderId}";

        var lines = new List<string>
        {
            $"Order #: {order.OrderId}",
            $"Status: {order.State}",
            $"Amount: ${order.NetAmount:0}"
        };

        var assetNames = order.AssetNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (assetNames.Count == 0 && !string.IsNullOrWhiteSpace(order.AssetSummary))
        {
            assetNames = order.AssetSummary
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Regex.Replace(s, @"\s*\(\+\d+\s+more\)$", "", RegexOptions.IgnoreCase).Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        if (assetNames.Count > 0)
        {
            lines.Add(assetNames.Count == 1 ? "Assets (1):" : $"Assets ({assetNames.Count}):");
            foreach (var name in assetNames)
            {
                lines.Add($"- {name}");
            }
        }
        else if (order.AssetCount > 0)
        {
            lines.Add(order.AssetCount == 1 ? "Assets: 1" : $"Assets: {order.AssetCount}");
        }

        if (order.CheckedOutOn.HasValue)
        {
            lines.Add($"Checked out: {order.CheckedOutOn:d}");
        }

        if (order.BillFrom.HasValue || order.BillTo.HasValue)
        {
            lines.Add(
                $"Bill window: {order.BillFrom?.ToString("d") ?? "?"} → {order.BillTo?.ToString("d") ?? "?"}");
        }

        if (order.CompletedOn.HasValue)
        {
            lines.Add($"Completed: {order.CompletedOn:d}");
        }

        lines.Add("Source: EZRentOut");

        return new CustomerActivityDto(
            ClassifyOrderKind(order),
            title,
            string.Join('\n', lines),
            order.CompletedOn ?? order.CheckedOutOn ?? order.BillTo,
            null,
            null,
            "EZRentOut");
    }

    private string BuildOrgCommunicationBrief(
        Guid organizationId,
        Guid customerId,
        string customerName)
    {
        var linked = _db.Conversations
            .Where(c =>
                c.OrganizationId == organizationId &&
                c.CustomerId == customerId &&
                (c.Channel == "Email" || c.Channel == "WhatsApp"))
            .ToList()
            .OrderByDescending(c => c.UpdatedAt)
            .Take(12)
            .ToList();

        var candidates = _db.Conversations
            .Where(c =>
                c.OrganizationId == organizationId &&
                (c.Channel == "Email" || c.Channel == "WhatsApp"))
            .ToList()
            .OrderByDescending(c => c.UpdatedAt)
            .Take(400)
            .Where(c => c.CustomerId != customerId)
            .Select(c => (Conversation: c, Score: ScoreNameToHaystack(customerName, c.Subject ?? string.Empty)))
            .Where(x => x.Score >= 40)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Conversation.UpdatedAt)
            .Take(8)
            .Select(x => x.Conversation)
            .ToList();

        // Enrich unmatched candidates with message haystacks when subject alone is weak.
        if (candidates.Count < 4)
        {
            var extra = _db.Conversations
                .Where(c =>
                    c.OrganizationId == organizationId &&
                    (c.Channel == "Email" || c.Channel == "WhatsApp") &&
                    c.CustomerId != customerId)
                .ToList()
                .OrderByDescending(c => c.UpdatedAt)
                .Take(200)
                .Where(c => linked.All(l => l.Id != c.Id) && candidates.All(x => x.Id != c.Id))
                .ToList();

            var messageMap = _db.Messages
                .Where(m => extra.Select(c => c.Id).Contains(m.ConversationId))
                .ToList()
                .GroupBy(m => m.ConversationId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).Take(6).ToList());

            foreach (var conversation in extra)
            {
                messageMap.TryGetValue(conversation.Id, out var messages);
                var haystack = BuildConversationHaystack(conversation, messages ?? []);
                if (ScoreNameToHaystack(customerName, haystack) < 120)
                {
                    continue;
                }

                candidates.Add(conversation);
                if (candidates.Count >= 8)
                {
                    break;
                }
            }
        }

        var all = linked
            .Concat(candidates)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderByDescending(c => c.UpdatedAt)
            .Take(14)
            .ToList();

        if (all.Count == 0)
        {
            return string.Empty;
        }

        var ids = all.Select(c => c.Id).ToHashSet();
        var latestMessages = _db.Messages
            .Where(m => ids.Contains(m.ConversationId) && !m.IsInternalNote)
            .ToList()
            .GroupBy(m => m.ConversationId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.CreatedAt).First());

        var sb = new StringBuilder();
        DateTimeOffset? lastTouch = null;
        string? lastChannel = null;
        foreach (var conversation in all)
        {
            latestMessages.TryGetValue(conversation.Id, out var latest);
            var when = latest?.CreatedAt ?? conversation.UpdatedAt;
            if (lastTouch is null || when > lastTouch)
            {
                lastTouch = when;
                lastChannel = conversation.Channel;
            }

            var preview = latest?.Summary
                ?? (latest?.Body is { Length: > 0 } body
                    ? (body.Length > 120 ? body[..120] + "…" : body)
                    : conversation.Subject);
            var direction = latest?.Direction ?? "";
            sb.AppendLine(
                $"- {conversation.Channel} · {conversation.Subject ?? "(no subject)"} · {when:u}" +
                (string.IsNullOrWhiteSpace(direction) ? "" : $" · {direction}") +
                (string.IsNullOrWhiteSpace(preview) ? "" : $" · {preview.Replace('\n', ' ')}"));
        }

        if (lastTouch.HasValue)
        {
            var age = DateTimeOffset.UtcNow - lastTouch.Value;
            var ageLabel = age.TotalHours < 48
                ? $"{Math.Max(1, (int)age.TotalHours)}h ago"
                : $"{Math.Max(1, (int)age.TotalDays)}d ago";
            sb.Insert(0, $"Last org contact: {lastChannel} {ageLabel} ({lastTouch:u})\n");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Legacy CRM sync stored one activity row per checked-out EZ asset.
    /// Those titles look like "16X-0003 — …" and should not count as rental jobs.
    /// </summary>
    private static bool IsLegacyEzAssetRental(StoredCustomerActivity activity)
    {
        if (!string.Equals(activity.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(activity.Kind, "rental", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var title = activity.Title ?? "";
        if (title.StartsWith("Order ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Asset identifiers / descriptions from assets/filter.api
        return true;
    }

    private static bool IsLegacyEzAssetRental(CustomerActivityDto activity) =>
        IsLegacyEzAssetRental(new StoredCustomerActivity(
            activity.Kind,
            activity.Title,
            activity.Detail,
            activity.OccurredAt,
            activity.Url,
            activity.SourceSystem,
            null));

    private static IEnumerable<StoredCustomerActivity> CollapseEzRentalsToJobs(
        IEnumerable<StoredCustomerActivity> stored,
        IEnumerable<EzRentOrderDto> orders)
    {
        // Non-EZ rows stay as-is; EZ rentals/orders are always rebuilt from live/snapshot
        // baskets so titles (job-first) and asset lists stay current.
        var kept = stored
            .Where(a =>
                !IsLegacyEzAssetRental(a) &&
                !(string.Equals(a.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase) &&
                  a.Kind is "rental" or "order"))
            .ToList();

        foreach (var order in orders.GroupBy(o => o.OrderId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            kept.Add(ToStoredRentalJob(order));
        }

        return kept;
    }

    private static (int WorkOrders, int Quotes, int Rentals, int Orders) CountOpsKinds(
        IEnumerable<string> kinds)
    {
        var workOrders = 0;
        var quotes = 0;
        var rentals = 0;
        var orders = 0;
        foreach (var kind in kinds)
        {
            if (kind.Equals("workorder", StringComparison.OrdinalIgnoreCase))
            {
                workOrders++;
            }
            else if (kind.Equals("quote", StringComparison.OrdinalIgnoreCase))
            {
                quotes++;
            }
            else if (kind.Equals("rental", StringComparison.OrdinalIgnoreCase))
            {
                rentals++;
            }
            else if (kind.Equals("order", StringComparison.OrdinalIgnoreCase))
            {
                orders++;
            }
        }

        return (workOrders, quotes, rentals, orders);
    }

    private static string ActivityKey(string? sourceSystem, string? title, string? url) =>
        $"{sourceSystem}|{title}|{url}";

    private static string EzOrderActivityKey(string orderId) =>
        ActivityKey("EZRentOut", $"order:{orderId}", null);

    private static string ActivityKeyForStored(StoredCustomerActivity row)
    {
        if (string.Equals(row.SourceSystem, "EZRentOut", StringComparison.OrdinalIgnoreCase) &&
            row.Kind is "rental" or "order")
        {
            if (!string.IsNullOrWhiteSpace(row.ExternalId))
            {
                return EzOrderActivityKey(row.ExternalId);
            }

            var match = Regex.Match(
                row.Title ?? "",
                @"(?:^Order\s+|·\s*Order\s+)(?<id>[A-Za-z0-9_-]+)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return EzOrderActivityKey(match.Groups["id"].Value);
            }
        }

        return ActivityKey(row.SourceSystem, row.Title, row.Url);
    }

    private static List<string> SignificantTokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length >= 3 && !CustomerStopTokens.Contains(t))
            .Distinct()
            .ToList();

    private static string CompactName(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());

    private static readonly HashSet<string> CustomerStopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "llc", "inc", "ltd", "corp", "company",
        "companies", "services", "service", "group", "holdings", "partners", "solutions", "energy",
        "oil", "gas", "water", "field", "oilfield", "midstream", "resources", "operating", "ops",
        "usa", "us", "co", "of", "to", "is", "are"
    };

    private static IReadOnlyList<StoredCustomerActivity> ReadStoredActivity(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return Array.Empty<StoredCustomerActivity>();
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("activity", out var activity) ||
                activity.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<StoredCustomerActivity>();
            }

            return JsonSerializer.Deserialize<List<StoredCustomerActivity>>(
                       activity.GetRawText(),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? [];
        }
        catch
        {
            return Array.Empty<StoredCustomerActivity>();
        }
    }

    private static string? ReadPartyName(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var key in new[] { "customer", "Customer", "customerName", "party", "company" })
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? ReadMeta(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static IEnumerable<string> SplitPartyNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Monday relation cells sometimes look like "Acme Corp (123456)"
            var cleaned = Regex.Replace(part, @"\s*\(\d+\)\s*$", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                yield return cleaned;
            }
        }
    }

    private static bool IsPlausibleOpsCustomerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 120)
        {
            return false;
        }

        string[] blocked =
        [
            "unknown", "unassigned", "n/a", "na", "none", "test", "shop", "warehouse",
            "yard", "office", "sable", "internal", "tbd", "customer", "you", "app", "guest",
            "hello", "deals", "loyalty", "messages", "messaging", "microsoft", "amazon",
            "dnow", "monday", "alec", "adobesign", "inc", "inc.", "llc", "llc.", "ltd", "ltd.",
            "corp", "corp.", "co", "co.", "company"
        ];
        if (blocked.Any(b => trimmed.Equals(b, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // MaintainX hierarchy stubs — not real customers/sites.
        if (trimmed.StartsWith("00-Parent Asset", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject corporate-suffix-only fragments from bad splits.
        if (Regex.IsMatch(trimmed, @"^(inc|llc|ltd|corp|co)\.?$", RegexOptions.IgnoreCase))
        {
            return false;
        }

        if (trimmed.StartsWith('+') || trimmed.Contains('@') || DigitsOnly(trimmed).Length >= 7)
        {
            return false;
        }

        // Reject pure person-looking single tokens from rental assignee noise when all lowercase short
        return true;
    }

    private static string NormalizeName(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string DigitsOnly(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private sealed record StoredCustomerActivity(
        string Kind,
        string Title,
        string? Detail,
        DateTimeOffset? OccurredAt,
        string? Url,
        string? SourceSystem,
        string? ExternalId);

    private sealed record LiveOpsPartyPull(
        IReadOnlyList<ExternalWorkItemDto> WorkItems,
        IReadOnlyList<EzRentOrderDto> Orders,
        IReadOnlyList<string> Notes);

    private sealed record OpsParty(string Name, string? Contact, string[] Sources);

    private sealed record CachedOverview(string Text, DateTimeOffset GeneratedAt, string? SourceNote);
}
