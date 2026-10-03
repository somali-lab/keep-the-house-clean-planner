using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Promotion;

public sealed class PromotionRulesTests
{
    private const string Id = "0123456789abcdef01234567";

    private static DismissedPromotion Entry(int toWeekday = 3, string? assignee = null, string evidence = Id) =>
        new(Id, Id, 1, 2, toWeekday, assignee, evidence);

    [Fact]
    public void ValidValuesAreAccepted()
    {
        PromotionRules.Validate(new ApplyPromotionCommand(Id, Id, 0, 0, 6, null)).Should().BeNull();
        PromotionRules.Validate(Entry(assignee: Id)).Should().BeNull();
    }

    [Fact]
    public void EveryBadFieldIsReportedByName()
    {
        var apply = PromotionRules.Validate(new ApplyPromotionCommand("x", "y", 4, -1, 7, "z"))!;
        var dismissed = PromotionRules.Validate(new DismissedPromotion("x", "y", 4, -1, 7, "z", "q"))!;

        apply.Errors.Keys.Should().BeEquivalentTo("planId", "taskId", "weekIndex", "weekday", "toWeekday", "toAssigneeId");
        dismissed.Errors.Keys.Should().BeEquivalentTo("planId", "taskId", "weekIndex", "weekday", "toWeekday", "toAssigneeId", "lastEvidenceId");
    }

    [Fact]
    public void DismissReplacesTheSameTargetAndKeepsTheOthers()
    {
        var other = Entry(toWeekday: 4);
        var older = Entry(evidence: "aaaaaaaaaaaaaaaaaaaaaaaa");
        var newer = Entry(evidence: "bbbbbbbbbbbbbbbbbbbbbbbb");

        PromotionRules.Dismiss([older, other], newer).Should().Equal(other, newer);
        PromotionRules.Dismiss([], newer).Should().Equal(newer);
    }

    [Fact]
    public void TheAssigneeIsPartOfTheTarget()
    {
        var withPerson = Entry(assignee: Id);

        PromotionRules.Dismiss([Entry()], withPerson).Should().Equal(Entry(), withPerson);
    }

    [Fact]
    public void IdsAreStoredInLowerCase()
    {
        var upper = Id.ToUpperInvariant();

        PromotionRules.Normalise(new ApplyPromotionCommand(upper, upper, 1, 2, 3, upper)).Should().Be(new ApplyPromotionCommand(Id, Id, 1, 2, 3, Id));
        PromotionRules.Normalise(new DismissedPromotion(upper, upper, 1, 2, 3, upper, upper)).Should().Be(new DismissedPromotion(Id, Id, 1, 2, 3, Id, Id));
    }
}
