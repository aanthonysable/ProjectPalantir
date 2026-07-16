using Palantir.Domain.Entities;
using Palantir.Domain.Workflow;
using FluentAssertions;
using Xunit;

namespace Palantir.Tests.Workflow;

public class ConversationAssignmentTests
{
    [Fact]
    public void Claim_unassigned_conversation()
    {
        var conversation = new Conversation();
        var userId = Guid.NewGuid();

        ConversationAssignment.Claim(conversation, userId);

        conversation.AssignedUserId.Should().Be(userId);
    }

    [Fact]
    public void Claim_already_owned_by_same_user_is_idempotent()
    {
        var userId = Guid.NewGuid();
        var conversation = new Conversation { AssignedUserId = userId };

        ConversationAssignment.Claim(conversation, userId);

        conversation.AssignedUserId.Should().Be(userId);
    }

    [Fact]
    public void Claim_fails_when_owned_by_another_user()
    {
        var conversation = new Conversation { AssignedUserId = Guid.NewGuid() };

        var act = () => ConversationAssignment.Claim(conversation, Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Assign_and_release()
    {
        var conversation = new Conversation();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        ConversationAssignment.Assign(conversation, userId, teamId);
        conversation.AssignedUserId.Should().Be(userId);
        conversation.AssignedTeamId.Should().Be(teamId);

        ConversationAssignment.Release(conversation);
        conversation.AssignedUserId.Should().BeNull();
        conversation.AssignedTeamId.Should().BeNull();
    }
}
