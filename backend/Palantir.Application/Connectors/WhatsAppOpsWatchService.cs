using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;

namespace Palantir.Application.Connectors;

public interface IWhatsAppOpsWatchService
{
    Task<IReadOnlyList<WhatsAppGapDto>> ListGapsAsync(CancellationToken cancellationToken = default);
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
    string MatchMethod);

public sealed class WhatsAppOpsWatchService : IWhatsAppOpsWatchService
{
    private static readonly Regex WoNumber = new(
        @"\b(?:WO|work\s*order)[#:\s-]*(\d{2,8})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuoteNumber = new(
        @"\b(?:Q|quote)[#:\s-]*([A-Z0-9-]{2,20})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PoSoNumber = new(
        @"\b(PO|SO)[#:\s-]*([A-Z0-9-]{2,20})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPalantirDbContext _db;
    private readonly IMaintainXConnector _maintainX;
    private readonly IEZRentOutConnector _ezRentOut;
    private readonly IMondayConnector _monday;
    private readonly MaintainXOptions _maintainXOptions;

    public WhatsAppOpsWatchService(
        IPalantirDbContext db,
        IMaintainXConnector maintainX,
        IEZRentOutConnector ezRentOut,
        IMondayConnector monday,
        IOptions<MaintainXOptions> maintainXOptions)
    {
        _db = db;
        _maintainX = maintainX;
        _ezRentOut = ezRentOut;
        _monday = monday;
        _maintainXOptions = maintainXOptions.Value;
    }

    public async Task<IReadOnlyList<WhatsAppGapDto>> ListGapsAsync(
        CancellationToken cancellationToken = default)
    {
        var openWork = new List<ExternalWorkItemDto>();
        foreach (var env in _maintainXOptions.Environments.Where(e => !string.IsNullOrWhiteSpace(e.ApiKey)))
        {
            openWork.AddRange(await _maintainX.ListOpenWorkAsync(env, cancellationToken));
        }

        openWork.AddRange(await _ezRentOut.ListOpenWorkAsync(cancellationToken));
        openWork.AddRange(await _monday.ListOpenWorkAsync(cancellationToken));

        var conversations = _db.Conversations
            .Where(c => c.Channel == "WhatsApp")
            .OrderByDescending(c => c.UpdatedAt)
            .Take(100)
            .ToList();

        var results = new List<WhatsAppGapDto>();
        foreach (var conversation in conversations)
        {
            var messages = _db.Messages
                .Where(m => m.ConversationId == conversation.Id)
                .OrderByDescending(m => m.CreatedAt)
                .Take(40)
                .ToList();

            var haystack = string.Join(
                '\n',
                messages.Select(m => m.Body).Where(b => !string.IsNullOrWhiteSpace(b)));
            var hints = ExtractHints(haystack, conversation.Subject);
            var matches = MatchOpenWork(hints, haystack, openWork);
            var status = matches.Count > 0
                ? "Linked"
                : hints.Count > 0
                    ? "Partial"
                    : "Unmatched";

            var latest = messages.FirstOrDefault();
            results.Add(new WhatsAppGapDto(
                conversation.Id,
                conversation.Subject ?? "(no subject)",
                conversation.UpdatedAt,
                status,
                latest?.Summary ?? latest?.Body ?? "",
                hints,
                matches));
        }

        return results
            .OrderBy(r => r.MatchStatus == "Unmatched" ? 0 : r.MatchStatus == "Partial" ? 1 : 2)
            .ThenByDescending(r => r.UpdatedAt)
            .ToList();
    }

    private static List<string> ExtractHints(string haystack, string? subject)
    {
        var text = $"{subject}\n{haystack}";
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

    private static List<WhatsAppOpsMatchDto> MatchOpenWork(
        IReadOnlyList<string> hints,
        string haystack,
        IReadOnlyList<ExternalWorkItemDto> openWork)
    {
        var matches = new List<WhatsAppOpsMatchDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compactHay = Compact(haystack);

        foreach (var item in openWork)
        {
            var methods = new List<string>();
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
                    }
                }
                else if (hint.StartsWith("Quote:", StringComparison.OrdinalIgnoreCase))
                {
                    var num = hint[6..];
                    if (string.Equals(meta.GetValueOrDefault("quoteNumber"), num, StringComparison.OrdinalIgnoreCase) ||
                        (item.Title?.Contains(num, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        methods.Add("QuoteNumber");
                    }
                }
                else if (hint.Contains(':'))
                {
                    var value = hint[(hint.IndexOf(':') + 1)..];
                    if (string.Equals(meta.GetValueOrDefault("poNumber"), value, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(meta.GetValueOrDefault("soNumber"), value, StringComparison.OrdinalIgnoreCase))
                    {
                        methods.Add("PoSoNumber");
                    }
                }
            }

            var customer = meta.GetValueOrDefault("customer") ?? item.Assignee;
            if (!string.IsNullOrWhiteSpace(customer) && customer.Length >= 4)
            {
                var compactCustomer = Compact(customer);
                if (compactCustomer.Length >= 4 && compactHay.Contains(compactCustomer, StringComparison.Ordinal))
                {
                    methods.Add("CustomerName");
                }
            }

            if (methods.Count == 0)
            {
                continue;
            }

            var key = $"{item.SourceSystem}:{item.EnvironmentName}:{item.ExternalId}";
            if (!seen.Add(key))
            {
                continue;
            }

            matches.Add(new WhatsAppOpsMatchDto(
                item.SourceSystem,
                item.EnvironmentName ?? item.SourceSystem,
                item.ExternalId,
                item.Title ?? "(untitled)",
                string.Join("+", methods.Distinct())));
        }

        return matches.Take(12).ToList();
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
