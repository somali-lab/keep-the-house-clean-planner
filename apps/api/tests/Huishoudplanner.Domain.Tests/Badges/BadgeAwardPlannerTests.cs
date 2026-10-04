using Huishoudplanner.Domain.Badges;

namespace Huishoudplanner.Domain.Tests.Badges;

/// <summary>The awards as a pure function of the data (ADR-0014): who holds what and since when, and the differences with what is stored.</summary>
public class BadgeAwardPlannerTests
{
    private const string Toilet = "a00000000000000000000001";
    private const string Mop = "a00000000000000000000002";
    private const string P1 = "b00000000000000000000001";
    private const string P2 = "b00000000000000000000002";
    private static readonly DateTimeOffset Created = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour = 8) => new(2026, 9, day, hour, 0, 0, TimeSpan.Zero);

    private static Badge MakeBadge(string id, BadgeRule rule, bool active = true, string? name = null) =>
        new(id, name ?? "Badge " + id[^1], string.Empty, rule, active, null, null, Created, Created);

    private static BadgeRule Executions(int threshold, params string[] tasks) => new(BadgeRuleType.Executions, tasks, threshold);

    private static CreditedExecution Done(string person, string id, string? task, DateTimeOffset at, int minutes = 10) =>
        new(person, new BadgeExecution(id, task, minutes, at));

    private static BadgeAward Stored(string badge, string person, DateTimeOffset at) =>
        new("c" + Guid.NewGuid().ToString("N")[..23], BadgeAward.KeyOf(badge, person), badge, person, at, Created, Created);

    private static BadgeAwardPlan PlanFor(
        IReadOnlyList<Badge> badges,
        BadgeEvaluationScope scope,
        IReadOnlyList<BadgeAward>? stored = null,
        IReadOnlyList<CreditedExecution>? executions = null,
        IReadOnlyList<OnTimeWeek>? onTime = null) =>
        BadgeAwardPlanner.Plan(BadgeAwardPlanner.Select(badges, scope), scope, stored ?? [], executions ?? [], onTime ?? []);

    private const string B1 = "d00000000000000000000001";
    private const string B2 = "d00000000000000000000002";

    [Fact]
    public void ThePersonWhoReachesTheThreshold_isAwarded_atTheMomentItWasCrossed()
    {
        var badge = MakeBadge(B1, Executions(2, Toilet));
        var executions = new[] { Done(P1, "e1", Toilet, At(16)), Done(P1, "e2", Toilet, At(17, 9)), Done(P1, "e3", Toilet, At(18)) };

        var plan = PlanFor([badge], BadgeEvaluationScope.Everybody, executions: executions);

        plan.Inserts.Should().ContainSingle().Which.Should().Be(new PlannedAward(B1, P1, At(17, 9)));
        plan.Updates.Should().BeEmpty();
        plan.Deletes.Should().BeEmpty();
    }

    [Fact]
    public void BelowTheThreshold_nothingIsAwarded()
    {
        var plan = PlanFor([MakeBadge(B1, Executions(3, Toilet))], BadgeEvaluationScope.Everybody, executions: [Done(P1, "e1", Toilet, At(16))]);

        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AnAwardThatMatchesTheData_isLeftAlone_soRecomputingChangesNothing()
    {
        var badge = MakeBadge(B1, Executions(1, Toilet));

        var plan = PlanFor([badge], BadgeEvaluationScope.Everybody, [Stored(B1, P1, At(16))], [Done(P1, "e1", Toilet, At(16))]);

        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AnAwardWithoutData_isRemoved()
    {
        var badge = MakeBadge(B1, Executions(1, Toilet));
        var stray = Stored(B1, P2, At(16));

        var plan = PlanFor([badge], BadgeEvaluationScope.Everybody, [stray], [Done(P1, "e1", Toilet, At(16))]);

        plan.Deletes.Should().ContainSingle().Which.Should().Be(stray);
        plan.Inserts.Should().ContainSingle().Which.PersonId.Should().Be(P1);
    }

    [Fact]
    public void WhenEarlierWorkIsAdded_theMomentOfTheAwardMoves()
    {
        var badge = MakeBadge(B1, Executions(2, Toilet));
        var current = Stored(B1, P1, At(18));

        var plan = PlanFor(
            [badge],
            BadgeEvaluationScope.Everybody,
            [current],
            [Done(P1, "e1", Toilet, At(17)), Done(P1, "e2", Toilet, At(18)), Done(P1, "e3", Toilet, At(16))]);

        plan.Updates.Should().ContainSingle().Which.Should().Be(new PlannedMove(current, At(17)));
    }

    [Fact]
    public void AnInactiveBadge_holdsNoAwards()
    {
        var badge = MakeBadge(B1, Executions(1, Toilet), active: false);
        var stored = Stored(B1, P1, At(16));

        var plan = PlanFor([badge], BadgeEvaluationScope.Everybody, [stored], [Done(P1, "e1", Toilet, At(16))]);

        plan.Deletes.Should().ContainSingle().Which.Should().Be(stored);
        plan.Inserts.Should().BeEmpty();
    }

    [Fact]
    public void AnAwardOfADeletedBadge_isRemoved()
    {
        var orphan = Stored(B2, P1, At(16));

        var plan = PlanFor([], BadgeEvaluationScope.Everybody, [orphan]);

        plan.Deletes.Should().ContainSingle().Which.Should().Be(orphan);
    }

    [Fact]
    public void OnTimeWeeks_countTheBonusesOfThePerson_andAwardAtTheDateOfTheThresholdWeek()
    {
        var badge = MakeBadge(B1, BadgeRule.OnTimeWeeks(2));

        var plan = PlanFor(
            [badge],
            BadgeEvaluationScope.Everybody,
            onTime: [new OnTimeWeek(P1, At(27)), new OnTimeWeek(P1, At(20)), new OnTimeWeek(P2, At(20))]);

        plan.Inserts.Should().ContainSingle().Which.Should().Be(new PlannedAward(B1, P1, At(27)));
    }

    [Fact]
    public void AScopeOfPeople_addsNobodyElse()
    {
        var badge = MakeBadge(B1, Executions(1, Toilet));
        var scope = new BadgeEvaluationScope([P1]);

        var plan = PlanFor([badge], scope, executions: [Done(P1, "e1", Toilet, At(16))]);

        plan.Inserts.Should().ContainSingle().Which.PersonId.Should().Be(P1);
    }

    // ---- which badges a change can have affected

    [Fact]
    public void AChangedTask_leavesTheBadgesThatDoNotCoverItUntouched_withTheirAwards()
    {
        var mopBadge = MakeBadge(B1, Executions(1, Mop));
        var toiletBadge = MakeBadge(B2, Executions(1, Toilet));
        var scope = new BadgeEvaluationScope([P1], TaskKnown: true, TaskId: Toilet);

        var selection = BadgeAwardPlanner.Select([mopBadge, toiletBadge], scope);
        var plan = BadgeAwardPlanner.Plan(selection, scope, [Stored(B1, P1, At(16))], [Done(P1, "e2", Toilet, At(16))], []);

        selection.Untouched.Should().BeEquivalentTo([B1]);
        selection.Evaluated.Should().ContainSingle().Which.Id.Should().Be(B2);
        plan.Deletes.Should().BeEmpty("the award of the mop badge is not looked at");
        plan.Inserts.Should().ContainSingle().Which.BadgeId.Should().Be(B2);
    }

    [Fact]
    public void AOneOffTask_onlyCountsForARuleOnEveryTask()
    {
        var everyTask = MakeBadge(B1, Executions(1));
        var toilet = MakeBadge(B2, Executions(1, Toilet));
        var scope = new BadgeEvaluationScope([P1], TaskKnown: true, TaskId: null);

        var selection = BadgeAwardPlanner.Select([everyTask, toilet], scope);

        selection.Evaluated.Select(b => b.Id).Should().Equal(B1);
        selection.Untouched.Should().BeEquivalentTo([B2]);
    }

    [Fact]
    public void AnOnTimeWeeksBadge_isNeverUntouched_andNeedsNoExecutions()
    {
        var onTime = MakeBadge(B1, BadgeRule.OnTimeWeeks(1));
        var scope = new BadgeEvaluationScope([P1], TaskKnown: true, TaskId: Toilet);

        var selection = BadgeAwardPlanner.Select([onTime], scope);

        selection.Evaluated.Should().ContainSingle();
        selection.NeedsExecutions.Should().BeFalse();
        selection.NeedsOnTimeWeeks.Should().BeTrue();
    }

    [Fact]
    public void ExecutionsAreOnlyNeeded_whenAnEvaluatedBadgeCountsThem()
    {
        var mop = MakeBadge(B1, Executions(1, Mop));

        BadgeAwardPlanner.Select([mop], new BadgeEvaluationScope([P1], true, Toilet)).NeedsExecutions.Should().BeFalse();
        BadgeAwardPlanner.Select([mop], BadgeEvaluationScope.Everybody).NeedsExecutions.Should().BeTrue();
    }

    [Fact]
    public void Names_includeDeletedBadgesTheCallerNames()
    {
        var badge = MakeBadge(B1, Executions(1), name: "Eén");

        var selection = BadgeAwardPlanner.Select([badge], BadgeEvaluationScope.Everybody, new Dictionary<string, string> { [B2] = "Weg" });

        selection.Names.Should().Contain(new KeyValuePair<string, string>(B1, "Eén")).And.Contain(new KeyValuePair<string, string>(B2, "Weg"));
    }
}
