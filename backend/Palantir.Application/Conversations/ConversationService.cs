using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;
using Palantir.Domain.Workflow;

namespace Palantir.Application.Conversations;

public sealed class ConversationService : IConversationService
{
    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;

    public ConversationService(IPalantirDbContext db, IAuditEventWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<IReadOnlyList<ConversationDto>> ListAsync(
        Guid organizationId,
        Guid? assignedUserId,
        bool? unassignedOnly,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Conversations.Where(c => c.OrganizationId == organizationId);
        if (assignedUserId.HasValue)
        {
            query = query.Where(c => c.AssignedUserId == assignedUserId);
        }

        if (unassignedOnly == true)
        {
            query = query.Where(c => c.AssignedUserId == null);
        }

        var items = query
            .ToList()
            .OrderByDescending(c => c.UpdatedAt)
            .Select(Map)
            .ToList();

        return Task.FromResult<IReadOnlyList<ConversationDto>>(items);
    }

    public Task<ConversationDto?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId);
        return Task.FromResult(conversation is null ? null : Map(conversation));
    }

    public async Task<ConversationDto> CreateAsync(
        CreateConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var conversation = new Conversation
        {
            OrganizationId = request.OrganizationId,
            Channel = request.Channel,
            Subject = request.Subject,
            CustomerId = request.CustomerId,
            ProjectId = request.ProjectId,
            AssignedUserId = request.AssignedUserId,
            Status = ConversationStatus.Open
        };

        _db.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            request.OrganizationId,
            "conversation.created",
            actorUserId,
            nameof(Conversation),
            conversation.Id,
            JsonSerializer.Serialize(new { conversation.Channel, conversation.Subject }),
            cancellationToken);

        return Map(conversation);
    }

    public Task<IReadOnlyList<MessageDto>> ListMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var items = _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToList()
            .OrderBy(m => m.CreatedAt)
            .Select(MapMessage)
            .ToList();

        return Task.FromResult<IReadOnlyList<MessageDto>>(items);
    }

    public async Task<MessageDto> AddMessageAsync(
        Guid conversationId,
        AddMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversation = _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

        var message = new Message
        {
            ConversationId = conversationId,
            Direction = request.Direction,
            Body = request.Body,
            SenderUserId = request.SenderUserId,
            IsInternalNote = request.IsInternalNote
        };

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        _db.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            request.IsInternalNote ? "conversation.internal_note_added" : "conversation.message_added",
            request.SenderUserId,
            nameof(Message),
            message.Id,
            cancellationToken: cancellationToken);

        return MapMessage(message);
    }

    public async Task<ConversationDto> ClaimAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        ConversationAssignment.Claim(conversation, userId);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            "conversation.claimed",
            userId,
            nameof(Conversation),
            conversation.Id,
            cancellationToken: cancellationToken);

        return Map(conversation);
    }

    public async Task<ConversationDto> AssignAsync(
        Guid conversationId,
        AssignConversationRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        ConversationAssignment.Assign(conversation, request.UserId, request.TeamId);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            "conversation.assigned",
            actorUserId,
            nameof(Conversation),
            conversation.Id,
            JsonSerializer.Serialize(new { request.UserId, request.TeamId }),
            cancellationToken);

        return Map(conversation);
    }

    public async Task<ConversationDto> ReleaseAsync(
        Guid conversationId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(conversationId);
        ConversationAssignment.Release(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            conversation.OrganizationId,
            "conversation.released",
            actorUserId,
            nameof(Conversation),
            conversation.Id,
            cancellationToken: cancellationToken);

        return Map(conversation);
    }

    private Conversation RequireConversation(Guid conversationId) =>
        _db.Conversations.FirstOrDefault(c => c.Id == conversationId)
        ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");

    private static ConversationDto Map(Conversation c) =>
        new(c.Id, c.OrganizationId, c.Subject, c.Channel, c.Status, c.AssignedUserId, c.AssignedTeamId, c.CreatedAt, c.UpdatedAt);

    private static MessageDto MapMessage(Message m) =>
        new(m.Id, m.ConversationId, m.Direction, m.Body, m.Summary, m.SenderUserId, m.IsInternalNote, m.CreatedAt);
}
