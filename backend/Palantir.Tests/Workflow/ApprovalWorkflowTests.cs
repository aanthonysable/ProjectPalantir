using Palantir.Domain.Enums;
using Palantir.Domain.Entities;
using Palantir.Domain.Workflow;
using FluentAssertions;
using Xunit;

namespace Palantir.Tests.Workflow;

public class ApprovalWorkflowTests
{
    [Theory]
    [InlineData(ApprovalStatus.Pending, ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Pending, ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Pending, ApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Pending, ApprovalStatus.Cancelled)]
    public void Can_transition_from_pending(ApprovalStatus from, ApprovalStatus to)
    {
        ApprovalWorkflow.CanTransition(from, to).Should().BeTrue();
        ApprovalWorkflow.Transition(from, to).Should().Be(to);
    }

    [Theory]
    [InlineData(ApprovalStatus.Approved, ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Rejected, ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Expired, ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Cancelled, ApprovalStatus.Approved)]
    public void Rejects_invalid_transitions(ApprovalStatus from, ApprovalStatus to)
    {
        ApprovalWorkflow.CanTransition(from, to).Should().BeFalse();
        var act = () => ApprovalWorkflow.Transition(from, to);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ApprovalRequest_approve_sets_completion_metadata()
    {
        var approval = new ApprovalRequest
        {
            RequestedForUserId = Guid.NewGuid()
        };
        var actor = Guid.NewGuid();

        approval.TransitionTo(ApprovalStatus.Approved, actor);

        approval.Status.Should().Be(ApprovalStatus.Approved);
        approval.CompletedByUserId.Should().Be(actor);
        approval.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void ApprovalRequest_cannot_approve_twice()
    {
        var approval = new ApprovalRequest
        {
            RequestedForUserId = Guid.NewGuid()
        };
        approval.TransitionTo(ApprovalStatus.Approved, Guid.NewGuid());

        var act = () => approval.TransitionTo(ApprovalStatus.Rejected, Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>();
    }
}
