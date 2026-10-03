using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Domain.Tests.Ai;

public class ProposalErrorsTests
{
    [Fact]
    public void An_issue_is_described_by_its_code_and_the_parts_it_carries_in_a_fixed_order()
    {
        var issue = new PlanIssue("assignee_unavailable", SlotIndex: 3, TaskId: "t", UserId: "u", WeekIndex: 1, Weekday: 2);

        ProposalErrors.Describe(issue).Should().Be("assignee_unavailable (slot 3, task t, user u, weekIndex 1, weekday 2)");
    }

    [Fact]
    public void An_issue_without_a_position_is_just_its_code()
    {
        ProposalErrors.Describe(new PlanIssue("unknown_task")).Should().Be("unknown_task");
        ProposalErrors.Describe(new PlanIssue("unknown_task", SlotIndex: 0)).Should().Be("unknown_task (slot 0)");
    }

    [Fact]
    public void The_proposal_specific_messages_use_the_same_shape()
    {
        ProposalErrors.NotInSelection(2, "t").Should().Be("task_not_in_selection (slot 2, task t)");
        ProposalErrors.NoAssignee(5, 1, 6).Should().Be("assignee_required (slot 5, weekIndex 1, weekday 6)");
    }
}
