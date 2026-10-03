using Huishoudplanner.Application.Promotion;
using Huishoudplanner.Application.Tests.CyclePlans;
using Huishoudplanner.Application.Tests.Generation;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;
using OneOf;
using FixedClock = Huishoudplanner.Application.Tests.Rooms.FixedClock;
using CalendarCycles = Huishoudplanner.Domain.Calendar.Cycles;

namespace Huishoudplanner.Application.Tests.Promotion;

/// <summary>An in-memory occurrence reader for the promote rule; it can fail and remembers the plan it was asked about.</summary>
internal sealed class FakePromotionEvidence : ForReadingPromotionEvidence
{
    public List<PromotionOccurrence> Items { get; } = [];

    public PortError? Failure { get; set; }

    public string? AskedFor { get; private set; }

    public Task<OneOf<IReadOnlyList<PromotionOccurrence>, PortError>> FindGeneratedOfPlanAsync(string planId, CancellationToken cancellationToken)
    {
        AskedFor = planId;
        return Task.FromResult(Failure is { } failure
            ? (OneOf<IReadOnlyList<PromotionOccurrence>, PortError>)failure
            : OneOf<IReadOnlyList<PromotionOccurrence>, PortError>.FromT0([.. Items]));
    }
}

/// <summary>
/// The promote suggestions use case against fakes: what it reads, how it turns instants into household day keys, and how it answers without
/// settings or an active plan. The rule itself is tested in the domain; the scenarios of promote.test.ts are in <c>PromoteEndpointTests</c>.
/// </summary>
public sealed class PromoteServiceTests
{
    private const string TaskId = "000000000000000000000001";
    private const string P1 = "111111111111111111111111";

    // Anchor Monday 2026-09-14; cycle 0 plans the slot (week 1, Tuesday) on 22 Sep and cycle 1 on 20 Oct.
    private static readonly DateOnly Anchor = new(2026, 9, 14);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        public CyclePlanWorld Plans { get; } = new();

        public FakeCycleStore Cycles { get; } = new();

        public FakePromotionEvidence Evidence { get; } = new();

        public FakeSettingsStore Settings => Plans.Settings;

        public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));

        public CyclePlan Plan { get; }

        public World()
        {
            var room = Plans.Room("Badkamer");
            var task = Plans.Task("Badkamer schoonmaken", room.Id);
            Plans.Tasks.Items[^1] = task with { Id = TaskId };
            Plan = Plans.Plan("Standaard", active: true, slots: new CyclePlanSlot(TaskId, 1, 2, P1));
            Cycles.Items = [.. Enumerable.Range(0, 2).Select(i => new Cycle("c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, CalendarCycles.CycleStart(i, Anchor), CalendarCycles.CycleEnd(i, Anchor), Plan.Id, DateTimeOffset.UnixEpoch, "run"))];
        }

        public PromoteService Service => new(Settings, Plans.Plans, Cycles, Plans.Tasks, Evidence, Plans.Service, Plans.Transactions, Plans.Audit, Clock);

        /// <summary>An occurrence of the slot planned in <paramref name="cycle"/> and now on <paramref name="day"/> (local midnight in Amsterdam).</summary>
        public void Occurrence(string id, int cycle, DateOnly day, string? assignee = P1)
        {
            var zone = DayKeys.FindZone("Europe/Amsterdam");
            var planned = CalendarCycles.SlotDate(CalendarCycles.CycleStart(cycle, Anchor), 1, 2);
            Evidence.Items.Add(new PromotionOccurrence(id, TaskId, DayKeys.FromDayKey(planned, zone), DayKeys.FromDayKey(day, zone), assignee));
        }

        public async Task<IReadOnlyList<PromoteSuggestion>> SuggestAsync() => (await Service.GetSuggestionsAsync(Ct)).AsT0;
    }

    [Fact]
    public async Task SuggestsTheNewWeekdayAfterTwoMovesAndNamesTheTask()
    {
        var w = new World();
        w.Occurrence("a00000000000000000000001", 0, new DateOnly(2026, 9, 23));
        w.Occurrence("a00000000000000000000002", 1, new DateOnly(2026, 10, 21));

        var suggestions = await w.SuggestAsync();

        suggestions.Should().ContainSingle().Which.Should().Be(new PromoteSuggestion(
            w.Plan.Id, TaskId, "Badkamer schoonmaken", new PromoteFromSlot(1, 2, P1), 3, null, ["a00000000000000000000002", "a00000000000000000000001"]));
        w.Evidence.AskedFor.Should().Be(w.Plan.Id);
    }

    [Fact]
    public async Task ReadsTheDaysInTheHouseholdTimezone_soADayThatStartsBeforeUtcMidnightStillCounts()
    {
        // Local midnight of 23 September in Amsterdam is 22:00 UTC on the 22nd: read as UTC days that is the planned day and no move at all.
        var w = new World();
        w.Occurrence("a00000000000000000000001", 0, new DateOnly(2026, 9, 23));
        w.Occurrence("a00000000000000000000002", 1, new DateOnly(2026, 10, 21));

        (await w.SuggestAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task UsesThePromoteThresholdAndTheDismissalsOfTheSettings()
    {
        var w = new World();
        w.Occurrence("a00000000000000000000001", 0, new DateOnly(2026, 9, 23));
        w.Occurrence("a00000000000000000000002", 1, new DateOnly(2026, 10, 21));

        w.Settings.Document = w.Settings.Document! with { PromoteThreshold = 3 };
        (await w.SuggestAsync()).Should().BeEmpty();

        w.Settings.Document = w.Settings.Document with
        {
            PromoteThreshold = 2,
            DismissedPromotions = [new DismissedPromotion(w.Plan.Id, TaskId, 1, 2, 3, null, "a00000000000000000000002")],
        };
        (await w.SuggestAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnswersAnEmptyListWithoutSettingsOrWithoutAnActivePlan_likeTheNodeRoute()
    {
        var w = new World();
        w.Occurrence("a00000000000000000000001", 0, new DateOnly(2026, 9, 23));
        w.Occurrence("a00000000000000000000002", 1, new DateOnly(2026, 10, 21));

        w.Plans.Plans.Items[0] = w.Plan with { Active = false };
        (await w.SuggestAsync()).Should().BeEmpty();
        w.Evidence.AskedFor.Should().BeNull("nothing is read for a plan that is not there");

        w.Plans.Plans.Items[0] = w.Plan;
        w.Settings.Document = null;
        (await w.SuggestAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task APortFailureOfAnyReadIsReturnedAndNeverThrown()
    {
        var failure = new PortError("boom");
        var w = new World();
        w.Settings.Failure = failure;
        (await w.Service.GetSuggestionsAsync(Ct)).AsT1.Should().Be(failure);

        w = new World();
        w.Plans.Plans.Failure = failure;
        (await w.Service.GetSuggestionsAsync(Ct)).AsT1.Should().Be(failure);

        w = new World();
        w.Cycles.Failure = failure;
        (await w.Service.GetSuggestionsAsync(Ct)).AsT1.Should().Be(failure);

        w = new World();
        w.Evidence.Failure = failure;
        (await w.Service.GetSuggestionsAsync(Ct)).AsT1.Should().Be(failure);

        w = new World();
        w.Plans.Tasks.Failure = failure;
        (await w.Service.GetSuggestionsAsync(Ct)).AsT1.Should().Be(failure);
    }

    [Fact]
    public async Task ReadsEveryPageOfCycles()
    {
        var w = new World();
        // 250 cycles in the past with no evidence, then the two moved ones at the end of the list: all pages must be read.
        var extra = Enumerable.Range(2, 248).Select(i => new Cycle("c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, CalendarCycles.CycleStart(i, Anchor), CalendarCycles.CycleEnd(i, Anchor), w.Plan.Id, DateTimeOffset.UnixEpoch, "run"));
        w.Cycles.Items = [.. w.Cycles.Items, .. extra];
        w.Occurrence("a00000000000000000000001", 0, new DateOnly(2026, 9, 23));
        w.Occurrence("a00000000000000000000002", 1, new DateOnly(2026, 10, 21));

        (await w.SuggestAsync()).Should().HaveCount(1);
    }
}
