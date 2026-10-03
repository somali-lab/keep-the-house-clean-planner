using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Tests.Calendar;

/// <summary>The day projection is a composition of the ported functions; these cases pin the composition, not the date math.</summary>
public class CalendarDayTests
{
    // Monday 2026-09-14 anchors cycle 0; cycle 1 starts Monday 2026-10-12.
    private static readonly DateOnly Anchor = DayKeys.Parse("2026-09-14");

    private static CalendarDay Of(string day) => CalendarDay.Of(DayKeys.Parse(day), Anchor);

    [Fact]
    public void Anchor_day_is_week_zero_of_cycle_zero_and_starts_its_own_week()
    {
        var day = Of("2026-09-14");

        day.Should().Be(new CalendarDay(DayKeys.Parse("2026-09-14"), 1, 0, 0, "2026-W38", DayKeys.Parse("2026-09-14")));
    }

    [Fact]
    public void Sunday_closes_the_week_and_has_weekday_zero()
    {
        var day = Of("2026-09-20");

        day.Weekday.Should().Be(0);
        day.WeekStart.Should().Be(DayKeys.Parse("2026-09-14"));
        day.WeekIndex.Should().Be(0);
        day.IsoWeek.Should().Be("2026-W38");
    }

    [Theory]
    [InlineData("2026-09-21", 0, 1)]
    [InlineData("2026-10-04", 0, 2)]
    [InlineData("2026-10-05", 0, 3)]
    [InlineData("2026-10-11", 0, 3)]
    [InlineData("2026-10-12", 1, 0)]
    [InlineData("2026-09-13", -1, 3)]
    [InlineData("2026-08-17", -1, 0)]
    public void Cycle_and_week_boundaries(string day, int cycleIndex, int weekIndex)
    {
        var projected = Of(day);

        projected.CycleIndex.Should().Be(cycleIndex);
        projected.WeekIndex.Should().Be(weekIndex);
    }

    [Theory]
    [InlineData("2026-03-28", 6, "2026-W13", "2026-03-23")]
    [InlineData("2026-03-29", 0, "2026-W13", "2026-03-23")]
    [InlineData("2026-03-30", 1, "2026-W14", "2026-03-30")]
    [InlineData("2026-10-25", 0, "2026-W43", "2026-10-19")]
    [InlineData("2026-12-31", 4, "2026-W53", "2026-12-28")]
    [InlineData("2027-01-01", 5, "2026-W53", "2026-12-28")]
    public void Dst_days_and_the_iso_year_boundary_follow_the_calendar_not_the_clock(string day, int weekday, string isoWeek, string weekStart)
    {
        var projected = Of(day);

        projected.Weekday.Should().Be(weekday);
        projected.IsoWeek.Should().Be(isoWeek);
        projected.WeekStart.Should().Be(DayKeys.Parse(weekStart));
    }

    [Fact]
    public void Every_week_start_is_a_monday_and_never_after_the_day()
    {
        for (var i = 0; i < 400; i++)
        {
            var day = DayKeys.AddDays(Anchor, i - 100);

            var projected = CalendarDay.Of(day, Anchor);

            DayKeys.IsMonday(projected.WeekStart).Should().BeTrue();
            DayKeys.DaysBetween(projected.WeekStart, day).Should().BeInRange(0, 6);
        }
    }

    [Fact]
    public void A_non_monday_anchor_is_rejected_by_the_ported_cycle_function() =>
        FluentActions.Invoking(() => CalendarDay.Of(Anchor, DayKeys.Parse("2026-09-15")))
            .Should().Throw<ArgumentOutOfRangeException>();
}
