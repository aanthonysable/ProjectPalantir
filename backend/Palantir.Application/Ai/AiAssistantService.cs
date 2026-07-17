using System.Text;
using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Application.Connectors;
using Palantir.Application.Outbound;
using Palantir.Domain.Entities;

namespace Palantir.Application.Ai;

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly IPalantirDbContext _db;
    private readonly IAiCompletionClient _ai;
    private readonly IOutboundEmailService _outbound;
    private readonly IMicrosoftGraphConnectorService _graph;
    private readonly IAuditEventWriter _audit;

    public AiAssistantService(
        IPalantirDbContext db,
        IAiCompletionClient ai,
        IOutboundEmailService outbound,
        IMicrosoftGraphConnectorService graph,
        IAuditEventWriter audit)
    {
        _db = db;
        _ai = ai;
        _outbound = outbound;
        _graph = graph;
        _audit = audit;
    }

    public async Task<ConversationSummaryResult> SummarizeConversationAsync(
        Guid conversationId,
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured(AiTaskKind.Summarize);
        var conversation = RequireConversation(conversationId);
        await EnsureFullEmailBodiesAsync(conversation, userId, cancellationToken);
        var transcript = BuildTranscript(conversationId, includePriorAiSummaries: false);

        var summary = (await _ai.CompleteAsync(
            AiTaskKind.Summarize,
            [
                new AiChatMessage(
                    "system",
                    """
                    You are Palantir, an AI assistant for Sable Automation Solutions.
                    Summarize the conversation for an employee who needs to act quickly.
                    Rules:
                    - Use the FULL message bodies in the transcript (not just previews).
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
        EnsureConfigured(AiTaskKind.DraftReply);
        var conversation = RequireConversation(conversationId);
        await EnsureFullEmailBodiesAsync(conversation, userId, cancellationToken);
        var transcript = BuildTranscript(conversationId, includePriorAiSummaries: false);

        var draftBody = (await _ai.CompleteAsync(
            AiTaskKind.DraftReply,
            [
                new AiChatMessage(
                    "system",
                    """
                    You are Palantir, an AI assistant for Sable Automation Solutions.
                    Draft a professional email reply the employee can review before sending.
                    Rules:
                    - Use the FULL message bodies in the transcript (not just previews).
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

    private void EnsureConfigured(AiTaskKind task)
    {
        if (!_ai.IsConfiguredFor(task) && !_ai.IsConfigured)
        {
            throw new InvalidOperationException(
                "AI is not configured. Add Gemini (Ai:Providers:gemini:ApiKey) and/or start Ollama (Ai:Providers:ollama). See Admin → AI providers.");
        }
    }

    private Conversation RequireConversation(Guid conversationId) =>
        _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
        ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

    /// <summary>
    /// For Email threads, pull full Graph bodies before AI so summaries/drafts are not based on previews.
    /// </summary>
    private async Task EnsureFullEmailBodiesAsync(
        Conversation conversation,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(conversation.Channel, "Email", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var messages = _db.Messages
            .Where(m => m.ConversationId == conversation.Id && m.ProviderMessageId != null)
            .ToList();

        var changed = false;
        foreach (var message in messages)
        {
            var accountId = TryReadConnectedAccountId(message.ProviderMetadataJson);
            if (accountId is null || string.IsNullOrWhiteSpace(message.ProviderMessageId))
            {
                continue;
            }

            try
            {
                var full = await _graph.GetMailMessageAsync(
                    accountId.Value,
                    userId,
                    message.ProviderMessageId,
                    cancellationToken);
                if (full is null || string.IsNullOrWhiteSpace(full.BodyText))
                {
                    continue;
                }

                var from = string.IsNullOrWhiteSpace(full.From) ? "unknown sender" : full.From;
                var newBody = $"From: {from}\n\n{full.BodyText}".Trim();
                if (string.IsNullOrWhiteSpace(message.Body) || newBody.Length > message.Body.Length + 20)
                {
                    message.Body = newBody;
                    message.Summary = full.Preview;
                    changed = true;
                }
            }
            catch
            {
                // Keep stored body; AI will still run on what we have.
            }
        }

        if (changed)
        {
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private string BuildTranscript(Guid conversationId, bool includePriorAiSummaries)
    {
        var messages = _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToList()
            .OrderBy(m => m.CreatedAt)
            .TakeLast(50)
            .Where(m => includePriorAiSummaries || m.Summary != "AI summary")
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

    private static Guid? TryReadConnectedAccountId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("connectedAccountId", out var id) &&
                Guid.TryParse(id.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
            return null;
        }

        return null;
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
