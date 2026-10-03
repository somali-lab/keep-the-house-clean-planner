using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Generation;

/// <summary>
/// The decision of <c>upcomingOccurrencesNeedReplacement</c> in <c>domain/generation.ts</c>: does a generation run have to replace the
/// upcoming occurrences because they no longer match the active plan? Only generated occurrences occupy a slot (ADR-0009), and only
/// open, untouched ones may be replaced.
/// </summary>
public sealed class OccurrenceReconciliationTests
{
    private const string Task1 = "a00000000000000000000001";
    private const string Room = "b00000000000000000000001";
    private const string Anna = "c00000000000000000000001";
    private const string Bram = "c00000000000000000000002";
    private const string Plan = "e00000000000000000000001";
    private const string OtherPlan = "e00000000000000000000002";

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private static readonly DateOnly Today = new(2026, 9, 14);

    private static HouseholdTask Task() => new(Task1, "Badkamer", Room, "1w", 30, 30, null, true, string.Empty, [], null, Now, Now);

    private static PlannedOccurrence Expected(DateOnly day, string? assignee = null) => new(0, day, Task(), assignee);

    private static DateTimeOffset Midnight(DateOnly day) => DayKeys.FromDayKey(day, Amsterdam);

    private static Occurrence Existing(
        DateOnly day,
        string? assignee = null,
        string plan = Plan,
        OccurrenceStatus status = OccurrenceStatus.Open,
        OccurrenceOrigin origin = OccurrenceOrigin.Generated,
        DateOnly? movedTo = null) =>
        new(
            "d" + day.DayNumber.ToString("x23", System.Globalization.CultureInfo.InvariantCulture),
            Task1,
            "f00000000000000000000001",
            plan,
            Midnight(movedTo ?? day),
            Midnight(day),
            assignee,
            status,
            null,
            null,
            null,
            null,
            30,
            "Badkamer",
            Room,
            "Badkamer",
            origin,
            Now,
            Now);

    private static bool Needs(PlannedOccurrence[] expected, Occurrence[] existing) =>
        OccurrenceReconciliation.NeedsReplacement(expected, existing, Plan, Midnight(Today), Amsterdam);

    [Fact]
    public void A_run_that_matches_the_plan_needs_no_replacement()
    {
        var day = new DateOnly(2026, 9, 16);

        Needs([Expected(day, Anna)], [Existing(day, Anna)]).Should().BeFalse();
    }

    [Fact]
    public void A_missing_occurrence_needs_a_replacement()
    {
        Needs([Expected(new DateOnly(2026, 9, 16))], []).Should().BeTrue();
    }

    [Fact]
    public void A_slot_that_moved_to_another_day_needs_a_replacement()
    {
        Needs([Expected(new DateOnly(2026, 9, 17))], [Existing(new DateOnly(2026, 9, 16))]).Should().BeTrue();
    }

    [Fact]
    public void An_occurrence_that_the_plan_no_longer_has_needs_a_replacement()
    {
        Needs([], [Existing(new DateOnly(2026, 9, 16))]).Should().BeTrue();
    }

    [Fact]
    public void Another_assignee_needs_a_replacement()
    {
        var day = new DateOnly(2026, 9, 16);

        Needs([Expected(day, Bram)], [Existing(day, Anna)]).Should().BeTrue();
        Needs([Expected(day, null)], [Existing(day, Anna)]).Should().BeTrue();
        Needs([Expected(day, Anna)], [Existing(day, null)]).Should().BeTrue();
    }

    [Fact]
    public void An_occurrence_of_another_plan_needs_a_replacement()
    {
        var day = new DateOnly(2026, 9, 16);

        Needs([Expected(day, Anna)], [Existing(day, Anna, OtherPlan)]).Should().BeTrue();
    }

    [Theory]
    [InlineData(OccurrenceStatus.Done)]
    [InlineData(OccurrenceStatus.Skipped)]
    public void Work_that_was_done_or_skipped_is_not_replaceable_even_when_it_differs(OccurrenceStatus status)
    {
        var day = new DateOnly(2026, 9, 16);

        Needs([Expected(day, Anna)], [Existing(day, Bram, status: status)]).Should().BeFalse();
    }

    [Fact]
    public void An_occurrence_that_was_dragged_to_another_day_is_not_replaceable_even_when_it_differs()
    {
        var day = new DateOnly(2026, 9, 16);

        Needs([Expected(day, Anna)], [Existing(day, Bram, movedTo: new DateOnly(2026, 9, 18))]).Should().BeFalse();
    }

    [Fact]
    public void An_ad_hoc_occurrence_never_occupies_a_slot_and_never_triggers_a_replacement()
    {
        var day = new DateOnly(2026, 9, 16);

        Needs([Expected(day, Anna)], [Existing(day, Anna, origin: OccurrenceOrigin.Adhoc)]).Should().BeTrue("the generated one is still missing");
        Needs([], [Existing(day, Anna, origin: OccurrenceOrigin.Adhoc)]).Should().BeFalse();
    }

    [Fact]
    public void KeyOf_names_the_task_and_the_planned_instant()
    {
        OccurrenceReconciliation.KeyOf(Task1, Midnight(Today)).Should().Be($"{Task1}:{Midnight(Today).ToUnixTimeMilliseconds()}");
        OccurrenceReconciliation.KeyOf(null, Midnight(Today)).Should().StartWith("none:");
    }

    [Fact]
    public void IsReplaceable_asks_for_an_open_generated_occurrence_on_its_planned_day_from_today_on()
    {
        var from = Midnight(Today);

        OccurrenceReconciliation.IsReplaceable(Existing(new DateOnly(2026, 9, 14)), from).Should().BeTrue();
        OccurrenceReconciliation.IsReplaceable(Existing(new DateOnly(2026, 9, 13)), from).Should().BeFalse("before today");
        OccurrenceReconciliation.IsReplaceable(Existing(new DateOnly(2026, 9, 16), status: OccurrenceStatus.Done), from).Should().BeFalse();
        OccurrenceReconciliation.IsReplaceable(Existing(new DateOnly(2026, 9, 16), movedTo: new DateOnly(2026, 9, 17)), from).Should().BeFalse();
        OccurrenceReconciliation.IsReplaceable(Existing(new DateOnly(2026, 9, 16), origin: OccurrenceOrigin.Adhoc), from).Should().BeFalse();
    }
}
