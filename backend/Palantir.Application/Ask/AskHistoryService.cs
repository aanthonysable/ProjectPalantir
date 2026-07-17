using System.Text.RegularExpressions;
using Palantir.Application.Abstractions;
using Palantir.Domain.Entities;

namespace Palantir.Application.Ask;

public sealed record AskSessionSummaryDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record AskMessageDto(
    Guid Id,
    string Role,
    string Content,
    int Ordinal,
    DateTimeOffset CreatedAt);

public sealed record AskSessionDetailDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AskMessageDto> Messages);

public sealed record AskHistoryExcerptDto(
    Guid SessionId,
    string SessionTitle,
    string Role,
    string Text,
    double Score);

public interface IAskHistoryService
{
    Task<IReadOnlyList<AskSessionSummaryDto>> ListSessionsAsync(
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<AskSessionDetailDto?> GetSessionAsync(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<(Guid SessionId, string Title)> AppendTurnAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        string userMessage,
        string assistantReply,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AskHistoryExcerptDto>> SearchAsync(
        Guid organizationId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default);
}

public sealed class AskHistoryService : IAskHistoryService
{
    private readonly IPalantirDbContext _db;

    public AskHistoryService(IPalantirDbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<AskSessionSummaryDto>> ListSessionsAsync(
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var sessions = _db.AskSessions
            .Where(s => s.OrganizationId == organizationId && s.UserId == userId)
            .ToList()
            .OrderByDescending(s => s.UpdatedAt)
            .Take(80)
            .ToList();

        var counts = _db.AskMessages
            .Where(m => sessions.Select(s => s.Id).Contains(m.SessionId))
            .ToList()
            .GroupBy(m => m.SessionId)
            .ToDictionary(g => g.Key, g => g.Count());

        IReadOnlyList<AskSessionSummaryDto> result = sessions
            .Select(s => new AskSessionSummaryDto(
                s.Id,
                s.Title,
                s.CreatedAt,
                s.UpdatedAt,
                counts.GetValueOrDefault(s.Id)))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<AskSessionDetailDto?> GetSessionAsync(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = _db.AskSessions.FirstOrDefault(s =>
            s.Id == sessionId && s.OrganizationId == organizationId && s.UserId == userId);
        if (session is null)
        {
            return Task.FromResult<AskSessionDetailDto?>(null);
        }

        var messages = _db.AskMessages
            .Where(m => m.SessionId == sessionId)
            .ToList()
            .OrderBy(m => m.Ordinal)
            .Select(m => new AskMessageDto(m.Id, m.Role, m.Content, m.Ordinal, m.CreatedAt))
            .ToList();

        return Task.FromResult<AskSessionDetailDto?>(
            new AskSessionDetailDto(session.Id, session.Title, session.CreatedAt, session.UpdatedAt, messages));
    }

    public async Task DeleteSessionAsync(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = _db.AskSessions.FirstOrDefault(s =>
                          s.Id == sessionId && s.OrganizationId == organizationId && s.UserId == userId)
                      ?? throw new InvalidOperationException("Chat was not found.");

        foreach (var message in _db.AskMessages.Where(m => m.SessionId == sessionId).ToList())
        {
            _db.Remove(message);
        }

        _db.Remove(session);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(Guid SessionId, string Title)> AppendTurnAsync(
        Guid organizationId,
        Guid userId,
        Guid? sessionId,
        string userMessage,
        string assistantReply,
        CancellationToken cancellationToken = default)
    {
        var userContent = (userMessage ?? string.Empty).Trim();
        var assistantContent = (assistantReply ?? string.Empty).Trim();
        if (userContent.Length == 0)
        {
            throw new InvalidOperationException("User message is required.");
        }

        if (userContent.Length > 8000)
        {
            userContent = userContent[..8000];
        }

        if (assistantContent.Length > 16000)
        {
            assistantContent = assistantContent[..16000];
        }

        AskSession session;
        if (sessionId is null || sessionId == Guid.Empty)
        {
            session = new AskSession
            {
                OrganizationId = organizationId,
                UserId = userId,
                Title = MakeTitle(userContent),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.Add(session);
        }
        else
        {
            session = _db.AskSessions.FirstOrDefault(s =>
                          s.Id == sessionId && s.OrganizationId == organizationId && s.UserId == userId)
                      ?? throw new InvalidOperationException("Chat was not found.");
            session.UpdatedAt = DateTimeOffset.UtcNow;
            if (string.Equals(session.Title, "New chat", StringComparison.OrdinalIgnoreCase))
            {
                session.Title = MakeTitle(userContent);
            }
        }

        var nextOrdinal = _db.AskMessages.Where(m => m.SessionId == session.Id).ToList().Count;
        _db.Add(new AskMessage
        {
            SessionId = session.Id,
            Role = "user",
            Content = userContent,
            Ordinal = nextOrdinal,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _db.Add(new AskMessage
        {
            SessionId = session.Id,
            Role = "assistant",
            Content = string.IsNullOrWhiteSpace(assistantContent) ? "(empty reply)" : assistantContent,
            Ordinal = nextOrdinal + 1,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
        return (session.Id, session.Title);
    }

    public Task<IReadOnlyList<AskHistoryExcerptDto>> SearchAsync(
        Guid organizationId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<AskHistoryExcerptDto>>([]);
        }

        var sessions = _db.AskSessions
            .Where(s => s.OrganizationId == organizationId)
            .ToList()
            .OrderByDescending(s => s.UpdatedAt)
            .Take(120)
            .ToDictionary(s => s.Id);

        if (sessions.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<AskHistoryExcerptDto>>([]);
        }

        var sessionIds = sessions.Keys.ToHashSet();
        var scored = _db.AskMessages
            .Where(m => sessionIds.Contains(m.SessionId))
            .ToList()
            .Select(m =>
            {
                var score = Score(m.Content, terms);
                return (Message: m, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Message.CreatedAt)
            .Take(Math.Clamp(limit, 1, 12))
            .Select(x =>
            {
                var session = sessions[x.Message.SessionId];
                return new AskHistoryExcerptDto(
                    session.Id,
                    session.Title,
                    x.Message.Role,
                    TrimExcerpt(x.Message.Content),
                    x.Score);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<AskHistoryExcerptDto>>(scored);
    }

    private static string MakeTitle(string userMessage)
    {
        var flat = Regex.Replace(userMessage, @"\s+", " ").Trim();
        if (flat.Length == 0)
        {
            return "New chat";
        }

        return flat.Length <= 72 ? flat : flat[..72] + "…";
    }

    private static List<string> Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return Regex.Matches(input.ToLowerInvariant(), @"[a-z0-9][a-z0-9\-]{1,}")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2)
            .Distinct()
            .Take(24)
            .ToList();
    }

    private static double Score(string text, IReadOnlyList<string> terms)
    {
        var lower = text.ToLowerInvariant();
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
                score += hits * (term.Length >= 5 ? 1.4 : 1.0);
            }
        }

        return score;
    }

    private static string TrimExcerpt(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 420 ? flat : flat[..420] + "…";
    }
}
