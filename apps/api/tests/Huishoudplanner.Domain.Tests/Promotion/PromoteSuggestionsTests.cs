using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Promotion;

/// <summary>
/// The pure rule of promote suggestions (<c>computePromoteSuggestions</c> of apps/server/src/domain/promote.ts). Anchor Monday 2026-09-14; the slot
/// is week index 1, Tuesday (2): cycle 0 plans it on 22 Sep, cycle 1 on 20 Oct, cycle 2 on 17 Nov.
/// </summary>
public sealed class PromoteSuggestionsTests
{
    private const string Plan = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Task = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string P1 = "111111111111111111111111";
    private const string P2 = "222222222222222222222222";

    private static readonly DateOnly Anchor = new(2026, 9, 14);

    private static DateOnly Planned(int cycle) => Cycles.SlotDate(Cycles.CycleStart(cycle, Anchor), 1, 2);

    private static PromoteOccurrence Occ(string id, int cycle, DateOnly? day = null, string? assignee = P1) =>
        new(id, Task, Planned(cycle), day ?? Planned(cycle), assignee);

    private static PromoteCycle Cycle(int index) => new(index, Cycles.CycleStart(index, Anchor));

    private static IReadOnlyList<PromoteSuggestion> Run(
        IEnumerable<PromoteOccurrence> occurrences,
        DateOnly? today = null,
        int threshold = 2,
        int cycles = 3,
        IReadOnlyList<DismissedPromotion>? dismissed = null) =>
        PromoteSuggestionCalculator.Compute(new PromoteInput(
            Plan,
            [new PromoteSlot(Task, 1, 2, P1)],
            [.. Enumerable.Range(0, cycles).Select(Cycle)],
            [.. occurrences],
            new Dictionary<string, string> { [Task] = "Badkamer schoonmaken" },
            Anchor,
            today ?? new DateOnly(2026, 9, 14),
            threshold,
            dismissed ?? []));

    [Fact]
    public void ASingleMoveIsNoSuggestion()
    {
        Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1)], cycles: 2).Should().BeEmpty();
    }

    [Fact]
    public void TwoConsecutiveCyclesMovedTheSameWaySuggestTheNewWeekday_newestEvidenceFirst()
    {
        var result = Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21))], cycles: 2);

        result.Should().ContainSingle().Which.Should().Be(new PromoteSuggestion(
            Plan, Task, "Badkamer schoonmaken", new PromoteFromSlot(1, 2, P1), 3, null, ["o1", "o0"]));
    }

    [Fact]
    public void MovesToDifferentWeekdaysAreNoSuggestion()
    {
        Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 22))], cycles: 2).Should().BeEmpty();
    }

    [Fact]
    public void TheOtherPersonIsIncludedWhenEveryMoveWentToThem()
    {
        var result = Run([Occ("o0", 0, new DateOnly(2026, 9, 24), P2), Occ("o1", 1, new DateOnly(2026, 10, 22), P2)], cycles: 2);

        result.Single().Should().Match<PromoteSuggestion>(s => s.ToWeekday == 4 && s.ToAssigneeId == P2);
    }

    [Fact]
    public void TheOtherPersonIsLeftOutWhenTheMovesWentToDifferentPeopleOrToTheSlotAssignee()
    {
        Run([Occ("o0", 0, new DateOnly(2026, 9, 24), P2), Occ("o1", 1, new DateOnly(2026, 10, 22), null)], cycles: 2)
            .Single().ToAssigneeId.Should().BeNull();
        Run([Occ("o0", 0, new DateOnly(2026, 9, 24)), Occ("o1", 1, new DateOnly(2026, 10, 22))], cycles: 2)
            .Single().ToAssigneeId.Should().BeNull();
    }

    [Fact]
    public void ACycleThatIsNotDecidedYetIsIgnored_butAnUntouchedPastCycleBreaksTheRun()
    {
        // Cycle 2 is still to come and untouched (today 12 Oct): skipped, the two moved cycles before it count.
        var future = Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21)), Occ("o2", 2)], today: new DateOnly(2026, 10, 12));
        future.Single().EvidenceIds.Should().Equal("o1", "o0");

        // Cycle 2 passed without a move: it is a decided cycle that stayed put, so no run of two moves ends at it.
        var past = Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21)), Occ("o2", 2)], today: new DateOnly(2026, 12, 1));
        past.Should().BeEmpty();
    }

    [Fact]
    public void AMoveToAnotherWeekOfTheCycleIsNoEvidence()
    {
        // 29 Sep is a Tuesday of week index 2: the occurrence left its week.
        Run([Occ("o0", 0, new DateOnly(2026, 9, 29)), Occ("o1", 1, new DateOnly(2026, 10, 21))], cycles: 2).Should().BeEmpty();
    }

    [Fact]
    public void AMoveIntoAnotherCycleIsNoEvidence()
    {
        Run([Occ("o0", 0, new DateOnly(2026, 10, 14)), Occ("o1", 1, new DateOnly(2026, 10, 21))], cycles: 2).Should().BeEmpty();
    }

    [Fact]
    public void TooFewCyclesInHistoryOrAGapBetweenThemIsNoSuggestion()
    {
        Run([Occ("o1", 1, new DateOnly(2026, 10, 21))], cycles: 2).Should().BeEmpty();

        // Cycle 1 has no occurrence for the slot: cycles 2 and 0 are not consecutive.
        Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o2", 2, new DateOnly(2026, 11, 18))]).Should().BeEmpty();
    }

    [Fact]
    public void AHigherThresholdNeedsMoreEvidence()
    {
        var moves = new[] { Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21)) };

        Run(moves, threshold: 3, cycles: 2).Should().BeEmpty();
        Run([.. moves, Occ("o2", 2, new DateOnly(2026, 11, 18))], threshold: 3).Single().EvidenceIds.Should().Equal("o2", "o1", "o0");
    }

    [Fact]
    public void ANonPositiveThresholdSuggestsNothing()
    {
        Run([Occ("o0", 0, new DateOnly(2026, 9, 23))], threshold: 0, cycles: 1).Should().BeEmpty();
    }

    [Fact]
    public void ADismissedSuggestionStaysAwayUntilTheNewestEvidenceChanges()
    {
        var dismissal = new DismissedPromotion(Plan, Task, 1, 2, 3, null, "o1");
        var moves = new[] { Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21)) };

        Run(moves, cycles: 2, dismissed: [dismissal]).Should().BeEmpty();

        var newer = Run([.. moves, Occ("o2", 2, new DateOnly(2026, 11, 18))], dismissed: [dismissal]);
        newer.Single().EvidenceIds[0].Should().Be("o2");
    }

    [Fact]
    public void ADismissalOfAnotherTargetDoesNotHideTheSuggestion()
    {
        var other = new DismissedPromotion(Plan, Task, 1, 2, 4, null, "o1");

        Run([Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21))], cycles: 2, dismissed: [other]).Should().HaveCount(1);
    }

    [Fact]
    public void ATaskWithoutAKnownNameGetsAnEmptyName()
    {
        var input = new PromoteInput(
            Plan,
            [new PromoteSlot(Task, 1, 2, P1)],
            [Cycle(0), Cycle(1)],
            [Occ("o0", 0, new DateOnly(2026, 9, 23)), Occ("o1", 1, new DateOnly(2026, 10, 21))],
            new Dictionary<string, string>(),
            Anchor,
            Anchor,
            2,
            []);

        PromoteSuggestionCalculator.Compute(input).Single().TaskName.Should().BeEmpty();
    }
}
