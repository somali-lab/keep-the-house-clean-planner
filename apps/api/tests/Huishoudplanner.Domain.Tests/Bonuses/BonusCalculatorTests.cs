using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Bonuses;

/// <summary>Properties and scenarios of bonuses.test.ts that the golden vectors cannot express (identity, order, mutation, instant precision).</summary>
public class BonusCalculatorTests
{
    private const string Anna = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bram = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone(DayKeys.AppTimezone);
    private static readonly BonusScheduleRow[] Schedule = [new(new DateOnly(2026, 1, 1), new BonusAmounts(5, 3, 20, 10))];

    private static BonusOccurrence Done(DateOnly planned, string person = Anna) => new(
        OccurrenceStatus.Done, planned, planned, false, person, null, person, new DateTimeOffset(planned.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));

    private static BonusContext Context(DateOnly today) => new(new DateOnly(2026, 9, 14), Amsterdam, today, Schedule);

    [Fact]
    public void The_four_bonus_kinds_round_trip_through_their_ledger_names()
    {
        BonusKinds.All.Select(k => k.ToLedgerKind()).Should().Equal(
            "bonus_week_done", "bonus_week_ontime", "bonus_cycle_done", "bonus_cycle_ontime");
        foreach (var kind in BonusKinds.All)
        {
            BonusKinds.TryParse(kind.ToLedgerKind(), out var parsed).Should().BeTrue();
            parsed.Should().Be(kind);
        }

        BonusKinds.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Each_kind_reads_its_own_amount()
    {
        var amounts = new BonusAmounts(1, 2, 3, 4);

        BonusKinds.All.Select(k => amounts.AmountOf(k)).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void The_entries_do_not_depend_on_the_order_of_the_input_and_the_input_is_not_changed()
    {
        BonusOccurrence[] items = [Done(new DateOnly(2026, 9, 21), Bram), Done(new DateOnly(2026, 9, 14)), Done(new DateOnly(2026, 9, 21))];
        var before = items.ToArray();

        var forward = BonusCalculator.ExpectedEntries(items, Context(new DateOnly(2026, 9, 28)));
        var reversed = BonusCalculator.ExpectedEntries([.. items.Reverse()], Context(new DateOnly(2026, 9, 28)));

        reversed.Should().Equal(forward);
        items.Should().Equal(before);
        forward.Select(e => e.PeriodEnd).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Placing_work_done_by_somebody_else_leaves_the_original_occurrence_untouched()
    {
        var occurrence = Done(new DateOnly(2026, 9, 14)) with { CompletedBy = Bram };

        var placements = BonusCalculator.PlacementsOf(occurrence);

        placements.Should().HaveCount(2);
        occurrence.Status.Should().Be(OccurrenceStatus.Done);
        occurrence.RecordedDone.Should().BeFalse();
        occurrence.CompletedBy.Should().Be(Bram);
    }

    [Fact]
    public void A_frozen_unassigned_owner_is_different_from_no_frozen_owner()
    {
        var occurrence = Done(new DateOnly(2026, 9, 14));

        occurrence.PeriodOwnerId.Should().Be(Anna);
        (occurrence with { Frozen = new FrozenOwner(null) }).PeriodOwnerId.Should().BeNull();
        (occurrence with { Frozen = new FrozenOwner(Bram) }).PeriodOwnerId.Should().Be(Bram);
    }

    [Fact]
    public void A_completion_inside_the_cut_off_millisecond_is_late_like_in_javascript()
    {
        // JavaScript dates have whole milliseconds; the cut-off millisecond itself is late, sub-millisecond ticks do not rescue it.
        var cutoff = new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);
        var atCutoffPlusTicks = Done(new DateOnly(2026, 9, 14)) with { CompletedAt = cutoff.AddTicks(5) };
        var justBefore = Done(new DateOnly(2026, 9, 14)) with { CompletedAt = cutoff.AddMilliseconds(-1) };

        BonusCalculator.EvaluateSet([atCutoffPlusTicks], cutoff).AllOnTime.Should().BeFalse();
        BonusCalculator.EvaluateSet([justBefore], cutoff).AllOnTime.Should().BeTrue();
    }

    [Fact]
    public void A_completion_with_another_offset_is_compared_as_an_instant()
    {
        var cutoff = new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);
        var sameInstantOtherOffset = Done(new DateOnly(2026, 9, 14)) with { CompletedAt = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.FromHours(2)) };

        BonusCalculator.EvaluateSet([sameInstantOtherOffset], cutoff).AllOnTime.Should().BeFalse();
    }

    [Fact]
    public void The_on_time_cut_off_makes_a_dst_week_167_or_169_hours_long()
    {
        var autumnStart = DayKeys.FromDayKey(new DateOnly(2026, 10, 19), Amsterdam);
        var springStart = DayKeys.FromDayKey(new DateOnly(2026, 3, 23), Amsterdam);

        (Period.WeekOf(new DateOnly(2026, 10, 19)).OnTimeCutoff(Amsterdam) - autumnStart).TotalHours.Should().Be(169);
        (Period.WeekOf(new DateOnly(2026, 3, 23)).OnTimeCutoff(Amsterdam) - springStart).TotalHours.Should().Be(167);
    }

    [Fact]
    public void A_period_before_the_anchor_has_the_cycle_of_a_negative_index()
    {
        Period.CycleOf(new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 14)).Should().Be(
            new Period(PeriodUnit.Cycle, new DateOnly(2026, 8, 17), new DateOnly(2026, 9, 13)));
    }

    [Fact]
    public void The_bonus_amount_bounds_are_those_of_the_settings_schedule()
    {
        (BonusSchedule.MinAmount, BonusSchedule.MaxAmount).Should().Be((0, 1000));
    }
}
