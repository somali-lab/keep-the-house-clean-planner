using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Tests.Due;

/// <summary>
/// The scenarios of <c>due.test.ts</c> that are not (only) vectors: constants, defaults, the DST and local-day
/// scenarios written out, and C#-specific behaviour. The vectors cover the numeric results.
/// </summary>
public class DueCalculatorTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone(DayKeys.AppTimezone);

    private static DueTaskInput Task(
        string id,
        string intervalKey = "1w",
        string? lastCompletedAt = null,
        bool active = true,
        string initialDueDate = "2026-09-16") =>
        new(id, active, intervalKey, lastCompletedAt is null ? null : DateTimeOffset.Parse(lastCompletedAt, System.Globalization.CultureInfo.InvariantCulture), DayKeys.Parse(initialDueDate));

    private static IReadOnlyList<DueResult> Compute(string today, params DueTaskInput[] tasks) =>
        DueCalculator.ComputeDue(tasks, DueCalculator.DefaultIntervals, DayKeys.Parse(today), Amsterdam);

    [Fact]
    public void Constants_have_the_documented_values()
    {
        DueCalculator.DueRatio.Should().Be(1.0);
        DueCalculator.OverdueRatio.Should().Be(1.5);
    }

    [Fact]
    public void Default_intervals_match_the_shared_package()
    {
        DueCalculator.DefaultIntervals.Select(i => (i.Key, i.PeriodDays, i.PerCycle)).Should().Equal(
            ("daily", 1, (int?)28), ("3w", 2, 12), ("2w", 3, 8), ("1w", 7, 4), ("2wk", 14, 2), ("4wk", 28, 1), ("quarter", 91, null));
    }

    [Theory]
    [InlineData(0, DueState.Ok)]
    [InlineData(0.99, DueState.Ok)]
    [InlineData(1.0, DueState.Due)]
    [InlineData(1.49, DueState.Due)]
    [InlineData(1.5, DueState.Overdue)]
    [InlineData(12, DueState.Overdue)]
    public void Ratio_maps_to_state(double ratio, DueState state) => DueCalculator.DueStateOf(ratio).Should().Be(state);

    [Fact]
    public void Hits_the_exact_boundaries_1_0_and_1_5()
    {
        var byId = Compute(
                "2026-09-16",
                Task("six", lastCompletedAt: "2026-09-10T10:00:00Z"),
                Task("seven", lastCompletedAt: "2026-09-09T10:00:00Z"),
                Task("fortnight-20", "2wk", "2026-08-27T10:00:00Z"),
                Task("fortnight-21", "2wk", "2026-08-26T10:00:00Z"))
            .ToDictionary(r => r.TaskId);
        byId["six"].Should().Match<DueResult>(r => r.DaysSince == 6 && r.State == DueState.Ok);
        byId["seven"].Should().Match<DueResult>(r => r.DaysSince == 7 && r.Ratio == 1 && r.State == DueState.Due);
        byId["fortnight-20"].Should().Match<DueResult>(r => r.DaysSince == 20 && r.State == DueState.Due);
        byId["fortnight-21"].Should().Match<DueResult>(r => r.DaysSince == 21 && r.Ratio == 1.5 && r.State == DueState.Overdue);
    }

    [Fact]
    public void Counts_local_calendar_days_across_DST_changes()
    {
        Compute("2026-10-26", Task("fall", "daily", "2026-10-24T20:30:00Z")).Single().DaysSince.Should().Be(2);
        Compute("2026-03-30", Task("spring", "daily", "2026-03-28T23:30:00Z")).Single().DaysSince.Should().Be(1);
    }

    [Fact]
    public void Uses_the_local_day_not_the_UTC_day_of_the_last_completion() =>
        Compute("2026-09-16", Task("late", "daily", "2026-09-15T23:30:00Z")).Single().DaysSince.Should().Be(0);

    [Fact]
    public void Uses_the_explicit_first_due_date_when_never_completed()
    {
        var input = Task("new", "quarter", initialDueDate: "2026-10-16");
        Compute("2026-09-20", input).Single().Should().Be(new DueResult("new", 0, 91, 0, DueState.Ok));
        Compute("2026-10-16", input).Single().Should().Be(new DueResult("new", 91, 91, 1, DueState.Due));
    }

    [Fact]
    public void Ranks_by_ratio_and_skips_inactive_tasks_and_unknown_intervals()
    {
        Compute(
                "2026-09-16",
                Task("a", lastCompletedAt: "2026-09-12T10:00:00Z"),
                Task("b", "daily", "2026-09-13T10:00:00Z"),
                Task("c", active: false, lastCompletedAt: "2025-01-01T10:00:00Z"),
                Task("d", "mystery"),
                Task("e", "2wk", "2026-09-02T10:00:00Z"))
            .Select(r => r.TaskId).Should().Equal("b", "e", "a");
    }

    [Fact]
    public void Never_reports_negative_days_for_a_completion_in_the_future() =>
        Compute("2026-09-16", Task("x", lastCompletedAt: "2026-09-20T10:00:00Z")).Single().DaysSince.Should().Be(0);

    [Fact]
    public void Ties_on_ratio_and_days_are_ordered_by_ordinal_task_id() =>
        Compute(
                "2026-09-16",
                Task("b", lastCompletedAt: "2026-09-12T10:00:00Z"),
                Task("B", lastCompletedAt: "2026-09-12T10:00:00Z"),
                Task("a", lastCompletedAt: "2026-09-12T10:00:00Z"))
            .Select(r => r.TaskId).Should().Equal("B", "a", "b");

    [Fact]
    public void An_interval_with_zero_period_is_left_out_instead_of_dividing_by_zero() =>
        DueCalculator.ComputeDue(
                [Task("z")],
                [new Interval("1w", "Broken", null, 0)],
                DayKeys.Parse("2026-09-16"),
                Amsterdam)
            .Should().BeEmpty();
}
