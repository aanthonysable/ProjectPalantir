using System.Text.Json;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

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
        CancellationToken cancellationToken = default)
    {
        var query = _db.Conversations.Where(c => c.OrganizationId == organizationId);
        if (assignedUserId.HasValue)
        {
            query = query.Where(c => c.AssignedUserId == assignedUserId);
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

    public async Task AddMessageAsync(
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
    }

    private static ConversationDto Map(Conversation c) =>
        new(c.Id, c.OrganizationId, c.Subject, c.Channel, c.Status, c.AssignedUserId, c.CreatedAt, c.UpdatedAt);
}
