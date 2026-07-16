using Palantir.Domain.Entities;

namespace Palantir.Domain.Workflow;

public static class ConversationAssignment
{
    public static void Claim(Conversation conversation, Guid userId)
    {
        if (conversation.AssignedUserId is Guid assigned && assigned != userId)
        {
            throw new InvalidOperationException(
                "Conversation is already claimed by another user.");
        }

        conversation.AssignedUserId = userId;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void Assign(Conversation conversation, Guid? userId, Guid? teamId)
    {
        if (userId is null && teamId is null)
        {
            throw new ArgumentException("Assign requires a user or team.");
        }

        conversation.AssignedUserId = userId;
        conversation.AssignedTeamId = teamId;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void Release(Conversation conversation)
    {
        conversation.AssignedUserId = null;
        conversation.AssignedTeamId = null;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
