using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Tests.Calendar;

/// <summary>Scenarios of cycle.test.ts and the property-style rules the vectors do not export.</summary>
public class CyclesTests
{
    private static readonly DateOnly Anchor = DayKeys.Parse("2026-09-14");
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone(DayKeys.AppTimezone);

    private static readonly int[] Weeks = [0, 1, 2, 3];

    private static DateOnly D(string key) => DayKeys.Parse(key);

    [Fact]
    public void AssertValidAnchor_accepts_Mondays_only()
    {
        Cycles.AssertValidAnchor(Anchor);
        var act = () => Cycles.AssertValidAnchor(D("2026-09-13"));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constants_describe_four_weeks_of_28_days()
    {
        Cycles.CycleDays.Should().Be(28);
        Cycles.CycleWeeks.Should().Be(4);
    }

    [Fact]
    public void CycleStart_and_CycleEnd_bracket_28_days_from_Monday_to_Sunday()
    {
        for (var index = -3; index <= 25; index++)
        {
            var start = Cycles.CycleStart(index, Anchor);
            var end = Cycles.CycleEnd(index, Anchor);
            DayKeys.IsMonday(start).Should().BeTrue();
            DayKeys.WeekdaySun0(end).Should().Be(0);
            DayKeys.DaysBetween(start, end).Should().Be(27);
            Cycles.CycleIndexFor(start, Anchor).Should().Be(index);
            Cycles.CycleIndexFor(end, Anchor).Should().Be(index);
            Cycles.WeekIndexFor(start, Anchor).Should().Be(0);
            Cycles.WeekIndexFor(end, Anchor).Should().Be(3);
        }
    }

    [Fact]
    public void Consecutive_cycles_tile_the_calendar_without_gaps()
    {
        for (var index = -3; index <= 25; index++)
        {
            DayKeys.AddDays(Cycles.CycleEnd(index, Anchor), 1).Should().Be(Cycles.CycleStart(index + 1, Anchor));
        }
    }

    [Fact]
    public void Crossing_the_October_dst_transition_does_not_drift()
    {
        var start = Cycles.CycleStart(1, Anchor);
        var dates = Weeks.Select(w => Cycles.SlotDate(start, w, 0)).ToArray();
        dates.Should().Equal(D("2026-10-18"), D("2026-10-25"), D("2026-11-01"), D("2026-11-08"));
        foreach (var date in dates)
        {
            DayKeys.ToDayKey(DayKeys.FromDayKey(date, Amsterdam), Amsterdam).Should().Be(date);
            Cycles.CycleIndexFor(date, Anchor).Should().Be(1);
        }
    }

    [Fact]
    public void SlotDate_maps_all_28_combinations_to_distinct_dates_in_the_cycle()
    {
        var start = Cycles.CycleStart(2, Anchor);
        var seen = new HashSet<DateOnly>();
        for (var weekIndex = 0; weekIndex < 4; weekIndex++)
        {
            for (var weekday = 0; weekday < 7; weekday++)
            {
                var date = Cycles.SlotDate(start, weekIndex, weekday);
                DayKeys.WeekdaySun0(date).Should().Be(weekday);
                Cycles.WeekIndexFor(date, Anchor).Should().Be(weekIndex);
                Cycles.CycleIndexFor(date, Anchor).Should().Be(2);
                seen.Add(date);
            }
        }

        seen.Should().HaveCount(28);
    }

    [Fact]
    public void SlotDate_puts_Sunday_at_the_end_of_the_Monday_first_week()
    {
        Cycles.SlotDate(Anchor, 0, 1).Should().Be(D("2026-09-14"));
        Cycles.SlotDate(Anchor, 0, 0).Should().Be(D("2026-09-20"));
        Cycles.SlotDate(Anchor, 3, 6).Should().Be(D("2026-10-10"));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(0, 7)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void SlotDate_rejects_out_of_range_values(int weekIndex, int weekday)
    {
        var act = () => Cycles.SlotDate(Anchor, weekIndex, weekday);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
