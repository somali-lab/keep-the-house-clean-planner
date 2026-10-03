using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Tests.Due;

public sealed class DueListTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    [Fact]
    public void The_summary_counts_due_and_overdue_states_only()
    {
        DueSummary.Of([DueState.Ok, DueState.Due, DueState.Overdue, DueState.Overdue, DueState.Ok]).Should().Be(new DueSummary(1, 2));
        DueSummary.Of(Array.Empty<DueState>()).Should().Be(new DueSummary(0, 0));
    }

    [Fact]
    public void The_summary_of_a_computed_list_is_the_nightly_job_answer()
    {
        var tasks = new[]
        {
            new DueTaskInput("a", true, "1w", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 14)),
            new DueTaskInput("b", true, "1w", new DateTimeOffset(2026, 11, 30, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 14)),
            new DueTaskInput("c", true, "1w", new DateTimeOffset(2026, 12, 6, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 14)),
        };

        var ranked = DueCalculator.ComputeDue(tasks, DueCalculator.DefaultIntervals, new DateOnly(2026, 12, 7), Amsterdam);

        DueSummary.Of(ranked).Should().Be(new DueSummary(1, 1));
    }

    [Fact]
    public void The_initial_due_date_is_the_first_planned_day_or_one_interval_after_creation()
    {
        var created = new DateTimeOffset(2026, 9, 14, 22, 30, 0, TimeSpan.Zero); // already 15 September in Amsterdam

        DueCalculator.InitialDueDateOf(new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), created, 7, Amsterdam).Should().Be(new DateOnly(2026, 9, 21));
        DueCalculator.InitialDueDateOf(null, created, 7, Amsterdam).Should().Be(new DateOnly(2026, 9, 22));
    }

    [Fact]
    public void A_cursor_round_trips_and_precedes_only_what_ranks_after_it()
    {
        var cursor = new DueCursor(14, 7, "bbbbbbbbbbbbbbbbbbbbbbbb");

        DueCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();
        decoded.Should().Be(cursor);

        cursor.Precedes(new DueResult("aaaaaaaaaaaaaaaaaaaaaaaa", 14, 7, 2.0, DueState.Overdue)).Should().BeFalse("same position, smaller id ranks before");
        cursor.Precedes(new DueResult("bbbbbbbbbbbbbbbbbbbbbbbb", 14, 7, 2.0, DueState.Overdue)).Should().BeFalse("the cursor item itself is not after it");
        cursor.Precedes(new DueResult("cccccccccccccccccccccccc", 14, 7, 2.0, DueState.Overdue)).Should().BeTrue();
        cursor.Precedes(new DueResult("a", 20, 14, 20.0 / 14, DueState.Overdue)).Should().BeTrue("a lower ratio ranks after");
        cursor.Precedes(new DueResult("a", 28, 7, 4.0, DueState.Overdue)).Should().BeFalse("a higher ratio ranks before");
        cursor.Precedes(new DueResult("a", 13, 7, 13.0 / 7, DueState.Overdue)).Should().BeTrue();
        cursor.Precedes(new DueResult("a", 28, 14, 2.0, DueState.Overdue)).Should().BeFalse("equal ratio, more days since ranks before");
        cursor.Precedes(new DueResult("a", 7, 3, 7.0 / 3, DueState.Overdue)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("***")]
    [InlineData("W10")]
    [InlineData("WzEsMiwzXQ")]
    [InlineData("WzEsMCwiYSJd")]
    [InlineData("Wy0xLDcsImEiXQ")]
    public void A_foreign_cursor_does_not_decode(string? value)
    {
        DueCursor.TryDecode(value, out _).Should().BeFalse();
    }
}
