using System.Text;
using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Application.Outbound;
using Palantir.Domain.Entities;

namespace Palantir.Application.Ai;

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly IPalantirDbContext _db;
    private readonly IAiCompletionClient _ai;
    private readonly IOutboundEmailService _outbound;
    private readonly IAuditEventWriter _audit;

    public AiAssistantService(
        IPalantirDbContext db,
        IAiCompletionClient ai,
        IOutboundEmailService outbound,
        IAuditEventWriter audit)
    {
        _db = db;
        _ai = ai;
        _outbound = outbound;
        _audit = audit;
    }

    public async Task<ConversationSummaryResult> SummarizeConversationAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var conversation = RequireConversation(conversationId);
        var transcript = BuildTranscript(conversationId);

        var summary = (await _ai.CompleteAsync(
            [
                new AiChatMessage(
                    "system",
                    """
                    You are Palantir, an AI assistant for Sable Automation Solutions.
                    Summarize the conversation for an employee who needs to act quickly.
                    Rules:
                    - Be concise (3-6 short bullets or a short paragraph).
                    - Call out open questions, urgency, and suggested next action.
                    - Do not invent facts that are not in the transcript.
                    - Do not send or claim that anything was sent.
                    """),
                new AiChatMessage(
                    "user",
                    $"""
                    Channel: {conversation.Channel}
                    Subject: {conversation.Subject ?? "(none)"}

                    Transcript:
                    {transcript}
                    """)
            ],
            cancellationToken)).Trim();

        var note = new Message
        {
            ConversationId = conversationId,
            Direction = "Internal",
            SenderUserId = userId,
            Body = summary,
            Summary = "AI summary",
            IsInternalNote = true,
            ProviderMetadataJson = JsonSerializer.Serialize(new
            {
                kind = "ai.summary",
                model = "configured"
            }),
            CreatedAt = DateTimeOffset.UtcNow
        };

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        _db.Add(note);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            organizationId,
            "ai.summary_created",
            userId,
            nameof(Message),
            note.Id,
            JsonSerializer.Serialize(new { conversationId }),
            cancellationToken);

        return new ConversationSummaryResult(conversationId, summary, note.Id);
    }

    public async Task<ReplyDraftResult> DraftReplyForApprovalAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        string? guidance = null,
        Guid? connectedAccountId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var conversation = RequireConversation(conversationId);
        var transcript = BuildTranscript(conversationId);

        var draftBody = (await _ai.CompleteAsync(
            [
                new AiChatMessage(
                    "system",
                    """
                    You are Palantir, an AI assistant for Sable Automation Solutions.
                    Draft a professional email reply the employee can review before sending.
                    Rules:
                    - Output ONLY the email body text (no subject line, no markdown fences).
                    - Be concise, courteous, and specific to the transcript.
                    - Do not invent commitments, pricing, or technical claims.
                    - If information is missing, ask a clear clarifying question.
                    - External sends require human approval; never claim the message was sent.
                    """),
                new AiChatMessage(
                    "user",
                    $"""
                    Channel: {conversation.Channel}
                    Subject: {conversation.Subject ?? "(none)"}
                    Extra guidance from user: {(string.IsNullOrWhiteSpace(guidance) ? "(none)" : guidance)}

                    Transcript:
                    {transcript}
                    """)
            ],
            cancellationToken)).Trim();

        if (draftBody.StartsWith("```"))
        {
            draftBody = StripFence(draftBody);
        }

        var result = await _outbound.CreateReplyForApprovalAsync(
            conversationId,
            organizationId,
            userId,
            draftBody,
            connectedAccountId,
            createdByAi: true,
            cancellationToken);

        await _audit.WriteAsync(
            organizationId,
            "ai.draft_reply_created",
            userId,
            nameof(Draft),
            result.DraftId,
            JsonSerializer.Serialize(new { result.ApprovalId, result.ToAddress }),
            cancellationToken);

        return result;
    }

    private void EnsureConfigured()
    {
        if (!_ai.IsConfigured)
        {
            throw new InvalidOperationException(
                "AI is not configured. Set Ai:ApiKey (user-secrets) or OPENAI_API_KEY, then restart the API.");
        }
    }

    private Conversation RequireConversation(Guid conversationId) =>
        _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
        ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

    private string BuildTranscript(Guid conversationId)
    {
        var messages = _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToList()
            .OrderBy(m => m.CreatedAt)
            .TakeLast(30)
            .ToList();

        if (messages.Count == 0)
        {
            throw new InvalidOperationException("Conversation has no messages to summarize or draft from.");
        }

        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var label = message.IsInternalNote
                ? "InternalNote"
                : message.Direction;
            sb.AppendLine($"[{message.CreatedAt:u}] {label}:");
            sb.AppendLine(message.Body ?? "(empty)");
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static string StripFence(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length >= 2 && lines[0].StartsWith("```"))
        {
            lines = lines.Skip(1).ToArray();
        }

        if (lines.Length >= 1 && lines[^1].Trim() == "```")
        {
            lines = lines.Take(lines.Length - 1).ToArray();
        }

        return string.Join('\n', lines).Trim();
    }
}
