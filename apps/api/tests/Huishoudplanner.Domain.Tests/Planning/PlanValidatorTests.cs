using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Domain.Tests.Planning;

/// <summary>
/// The scenarios of <c>plan.test.ts</c> written out with semantic assertions (the vectors pin the full results), plus the
/// field-level mapping to <c>ValidationErrors</c>. Omitted on purpose: the fractional <c>weekIndex</c> case, because
/// <see cref="PlanSlot.WeekIndex"/> is an <c>int</c> and a fractional JSON number is refused when the request is bound.
/// </summary>
public class PlanValidatorTests
{
    private const string Anna = "a00000000000000000000001";
    private const string Bram = "b00000000000000000000002";
    private const string Gone = "c00000000000000000000003";

    private static readonly PlanUser[] Users =
    [
        new(Anna, "Anna", true, [2], new(60, 120), new(480, 480)),
        new(Bram, "Bram", true, [], new(60, 120), new(480, 480)),
        new(Gone, "Oud", false, [], new(60, 120), new(480, 480)),
    ];

    private static PlanTask Task(string id, string interval, int minutes, bool active = true) => new(id, id, interval, minutes, active);

    private static readonly PlanTask Weekly = Task("weekly", "1w", 40);
    private static readonly PlanTask Twice = Task("twice", "2w", 10);
    private static readonly PlanTask Quarter = Task("quarter", "quarter", 90);
    private static readonly PlanTask Monthly = Task("monthly", "4wk", 40);
    private static readonly PlanTask Old = Task("old", "1w", 10, false);
    private static readonly PlanTask[] Tasks = [Weekly, Twice, Quarter, Monthly, Old];

    private static PlanSlot Slot(string taskId, int week, int weekday, string? assignee = Bram) => new(taskId, week, weekday, assignee);

    /// <summary>Slots that satisfy every interval exactly with no budget issues.</summary>
    private static List<PlanSlot> FullPlan() =>
    [
        .. Enumerable.Range(0, 4).Select(w => Slot("weekly", w, 1)),
        .. Enumerable.Range(0, 4).SelectMany(w => new[] { Slot("twice", w, 3), Slot("twice", w, 6) }),
        Slot("monthly", 0, 5, Anna),
    ];

    private static PlanValidation Run(IEnumerable<PlanSlot> slots, PlanTask[]? tasks = null, PlanUser[]? users = null) =>
        PlanValidator.Validate(new PlanDraft(slots.ToList()), tasks ?? Tasks, users ?? Users, DueCalculator.DefaultIntervals);

    private static string[] Codes(IEnumerable<PlanIssue> issues) => issues.Select(i => i.Code).ToArray();

    [Fact]
    public void A_plan_that_satisfies_every_rule_has_no_errors_or_warnings()
    {
        var result = Run(FullPlan());

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
    }

    public static TheoryData<string, PlanSlot, string> BadSlots => new()
    {
        { "assignee unavailable on that weekday", Slot("weekly", 0, 2, Anna), "assignee_unavailable" },
        { "unknown task", Slot("nope", 0, 1), "unknown_task" },
        { "inactive task", Slot("old", 0, 1), "inactive_task" },
        { "unknown user", Slot("weekly", 0, 4, "d00000000000000000000004"), "unknown_user" },
        { "inactive user", Slot("weekly", 0, 4, Gone), "inactive_user" },
        { "weekIndex above range", Slot("weekly", 4, 1), "week_index_out_of_range" },
        { "negative weekIndex", Slot("weekly", -1, 1), "week_index_out_of_range" },
        { "weekday above range", Slot("weekly", 0, 7), "weekday_out_of_range" },
    };

    [Theory]
    [MemberData(nameof(BadSlots))]
    public void A_bad_slot_gives_exactly_its_error_on_its_index(string label, PlanSlot bad, string code)
    {
        var slots = FullPlan();
        slots.Add(bad);

        var result = Run(slots);

        Codes(result.Errors).Should().Equal(new[] { code }, label);
        result.Errors[0].SlotIndex.Should().Be(slots.Count - 1);
        result.Errors[0].TaskId.Should().Be(bad.TaskId);
    }

    [Fact]
    public void The_same_unavailable_weekday_is_fine_for_another_user_and_for_an_unassigned_slot()
    {
        Run([.. FullPlan(), Slot("quarter", 0, 2, Bram)]).Errors.Should().BeEmpty();
        Run([.. FullPlan(), Slot("quarter", 0, 2, null)]).Errors.Should().BeEmpty();
    }

    [Fact]
    public void The_same_task_twice_on_one_day_is_refused_even_for_different_assignees()
    {
        Codes(Run([.. FullPlan(), Slot("weekly", 0, 1, Anna)]).Errors).Should().Equal("duplicate_task_day");
    }

    [Fact]
    public void The_same_task_on_the_same_weekday_in_different_weeks_is_fine()
    {
        Run([Slot("quarter", 0, 1), Slot("quarter", 1, 1)]).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Five_slots_for_a_twice_weekly_task_is_a_warning_not_an_error()
    {
        var slots = FullPlan().Where(s => s.TaskId != "twice")
            .Concat(Enumerable.Range(0, 5).Select(i => Slot("twice", i % 4, i < 4 ? 3 : 4)));

        var result = Run(slots);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Equal(new PlanIssue("interval_mismatch", TaskId: "twice", Placed: 5, Required: 8));
    }

    [Fact]
    public void A_three_times_weekly_task_needs_twelve_slots_per_cycle()
    {
        var slots = Enumerable.Range(0, 11).Select(i => Slot("three-times-weekly", i / 3, (i % 3) + 1));

        var result = Run(slots, [Task("three-times-weekly", "3w", 10)]);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Equal(new PlanIssue("interval_mismatch", TaskId: "three-times-weekly", Placed: 11, Required: 12));
    }

    [Fact]
    public void An_active_task_that_is_not_placed_at_all_is_warned_about()
    {
        Run(FullPlan().Where(s => s.TaskId != "monthly")).Warnings.Should()
            .Equal(new PlanIssue("interval_mismatch", TaskId: "monthly", Placed: 0, Required: 1));
    }

    [Fact]
    public void A_task_without_a_grid_interval_is_never_short()
    {
        var some = Run([.. FullPlan(), Slot("quarter", 2, 4, null)]);

        some.Warnings.Should().BeEmpty();
        some.Summary.Tasks.Single(t => t.TaskId == "quarter").Should().Be(new TaskSummary("quarter", 1, null));
    }

    [Fact]
    public void The_task_summary_leaves_out_inactive_tasks_without_slots()
    {
        Run(FullPlan()).Summary.Tasks.Should().Equal(
            new TaskSummary("weekly", 4, 4),
            new TaskSummary("twice", 8, 8),
            new TaskSummary("quarter", 0, null),
            new TaskSummary("monthly", 1, 1));
    }

    [Fact]
    public void An_unknown_interval_key_is_not_grid_planned()
    {
        var result = Run([Slot("odd", 0, 1)], [Task("odd", "mystery", 5)]);

        result.Warnings.Should().BeEmpty();
        result.Summary.Tasks.Should().Equal(new TaskSummary("odd", 1, null));
    }

    [Fact]
    public void The_weekday_budget_applies_Monday_to_Friday_as_one_total()
    {
        var result = Run([Slot("weekly", 0, 1), Slot("monthly", 0, 2)], [Weekly, Monthly]);

        result.Warnings.Where(w => w.Code == "over_budget").Should().Equal(new PlanIssue("over_budget", UserId: Bram, WeekIndex: 0, Period: BudgetPeriod.Weekday, Minutes: 80, Budget: 60));
        result.Summary.Days.Where(d => d.WeekIndex == 0 && d.Weekday is 1 or 2)
            .Select(d => d.Users.Single(u => u.UserId == Bram).OverBudget).Should().Equal(false, false);
    }

    [Fact]
    public void The_weekend_budget_is_used_on_Saturday_and_Sunday()
    {
        Run([Slot("weekly", 0, 6), Slot("monthly", 0, 6)], [Weekly, Monthly]).Warnings.Where(w => w.Code == "over_budget").Should().BeEmpty();

        Run([Slot("weekly", 0, 0), Slot("monthly", 0, 0), Slot("quarter", 0, 0)], [Weekly, Monthly, Quarter]).Warnings.Where(w => w.Code == "over_budget").Should()
            .Equal(new PlanIssue("over_budget", UserId: Bram, WeekIndex: 0, Period: BudgetPeriod.Weekend, Minutes: 170, Budget: 120));
    }

    [Fact]
    public void Budget_for_picks_the_weekend_or_weekday_maximum()
    {
        var user = Users[0] with { MaxDailyMinutes = new(45, 75) };

        PlanValidator.BudgetFor(user, 6).Should().Be(75);
        PlanValidator.BudgetFor(user, 0).Should().Be(75);
        PlanValidator.BudgetFor(user, 5).Should().Be(45);
    }

    [Fact]
    public void Exactly_at_budget_is_fine()
    {
        var result = Run(
            [Slot("weekly", 0, 1), Slot("quarter", 0, 1, Anna)],
            [Task("weekly", "1w", 60), Quarter],
            [Users[1], Users[0] with { DailyBudgetMinutes = new(90, 120) }]);

        result.Warnings.Where(w => w.Code == "over_budget").Should().BeEmpty();
    }

    [Fact]
    public void Days_are_summarised_Monday_first_for_active_users_only()
    {
        var days = Run([Slot("weekly", 0, 1, Anna), Slot("quarter", 0, 1, Bram)]).Summary.Days;

        days.Should().HaveCount(28);
        days.Take(7).Select(d => d.Weekday).Should().Equal(1, 2, 3, 4, 5, 6, 0);
        days[0].Should().BeEquivalentTo(new DaySummary(0, 1, [new(Anna, 40, 480, false), new(Bram, 90, 480, false)], 0));
    }

    [Fact]
    public void One_day_over_the_daily_maximum_gives_a_daily_warning()
    {
        var bram = Users[1] with { DailyBudgetMinutes = new(200, 200), MaxDailyMinutes = new(60, 90) };

        var result = Run([Slot("weekly", 0, 1), Slot("monthly", 0, 1)], [Weekly, Monthly], [Users[0], bram, Users[2]]);

        result.Warnings.Where(w => w.Code == "daily_over_budget").Should()
            .Equal(new PlanIssue("daily_over_budget", UserId: Bram, WeekIndex: 0, Weekday: 1, Minutes: 80, Budget: 60));
    }

    [Fact]
    public void Unassigned_slots_are_totalled_apart_and_count_against_no_budget()
    {
        var result = Run([Slot("quarter", 2, 3, null), Slot("weekly", 2, 3, null), Slot("twice", 2, 4, Anna)]);

        result.Warnings.Where(w => w.Code == "over_budget").Should().BeEmpty();
        var wednesday = result.Summary.Days.Single(d => d is { WeekIndex: 2, Weekday: 3 });
        wednesday.UnassignedMinutes.Should().Be(130);
        wednesday.Users.Should().OnlyContain(u => u.Minutes == 0);
        result.Summary.Weeks[2].Should().BeEquivalentTo(new WeekSummary(2, [new(Anna, 10), new(Bram, 0)], 130));
    }

    [Fact]
    public void Minutes_are_totalled_per_week_per_user()
    {
        Run(FullPlan()).Summary.Weeks.Select(w => w.Users.Single(u => u.UserId == Bram).Minutes).Should().Equal(60, 60, 60, 60);
    }

    [Fact]
    public void Errors_map_to_field_paths_with_the_code_as_message_key()
    {
        var slots = new List<PlanSlot>
        {
            Slot("weekly", 0, 1),
            Slot("nope", 9, 9, "d00000000000000000000004"),
            Slot("weekly", 0, 2, Anna),
            Slot("quarter", 0, 1),
            Slot("quarter", 0, 1),
        };

        var errors = Run(slots).ToValidationErrors().Errors;

        errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["slots[1].weekIndex"] = ["week_index_out_of_range"],
            ["slots[1].weekday"] = ["weekday_out_of_range"],
            ["slots[1].taskId"] = ["unknown_task"],
            ["slots[1].assigneeId"] = ["unknown_user"],
            ["slots[2].assigneeId"] = ["assignee_unavailable"],
            ["slots[4].taskId"] = ["duplicate_task_day"],
        });
    }

    [Fact]
    public void A_valid_plan_maps_to_no_field_errors() => Run(FullPlan()).ToValidationErrors().Errors.Should().BeEmpty();
}
