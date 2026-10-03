using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Domain.Tests.Ai;

/// <summary>completeRequiredOccurrences of proposals.ts: partial plans are filled in, shared slots get a concrete available person.</summary>
public class PlanCompleterTests
{
    private const string Weekly = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Twice = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Optional = "cccccccccccccccccccccccc";
    private const string P1 = "111111111111111111111111";
    private const string P2 = "222222222222222222222222";

    private static PlanPromptPayload Payload(int[]? p1Unavailable = null, int maxWeekday = 120) => new(
        PlanModes.Propose,
        [
            new PromptTask(Weekly, "Badkamer", "Badkamer", "1w", "1x per week", 4, 7, 30),
            new PromptTask(Optional, "Optioneel", "Badkamer", "quarter", "kwartaal", null, 91, 60),
            new PromptTask(Twice, "Wastafel", "Badkamer", "2w", "2x per week", 8, 3, 10),
        ],
        [
            new PromptUser(P1, "P1", p1Unavailable ?? [], new PromptMinutes(600, 600), new PromptMinutes(maxWeekday, 240)),
            new PromptUser(P2, "P2", [], new PromptMinutes(600, 600), new PromptMinutes(120, 240)),
        ]);

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee) => new(task, week, weekday, assignee);

    [Fact]
    public void An_empty_proposal_is_filled_with_every_required_occurrence_and_nothing_for_optional_tasks()
    {
        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload(), []);

        plan.Count(s => s.TaskId == Weekly).Should().Be(4);
        plan.Count(s => s.TaskId == Twice).Should().Be(8);
        plan.Count(s => s.TaskId == Optional).Should().Be(0);
        plan.Should().OnlyContain(s => s.AssigneeId != null);
        plan.Select(s => (s.TaskId, s.WeekIndex, s.Weekday)).Distinct().Should().HaveCount(plan.Count);
    }

    [Fact]
    public void The_valid_choices_of_the_model_are_kept_and_the_rest_is_added()
    {
        var chosen = Slot(Weekly, 0, 1, P1);

        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload(), [chosen]);

        plan[0].Should().Be(chosen);
        plan.Count(s => s.TaskId == Weekly).Should().Be(4);
    }

    [Fact]
    public void Extra_and_duplicate_slots_beyond_the_required_count_or_on_the_same_day_are_dropped()
    {
        var proposed = new[]
        {
            Slot(Weekly, 0, 1, P1), Slot(Weekly, 0, 1, P2), Slot(Weekly, 1, 1, P1), Slot(Weekly, 2, 1, P1), Slot(Weekly, 3, 1, P1), Slot(Weekly, 3, 2, P1),
        };

        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload(), proposed);

        plan.Where(s => s.TaskId == Weekly).Select(s => (s.WeekIndex, s.Weekday)).Should().Equal((0, 1), (1, 1), (2, 1), (3, 1));
    }

    [Fact]
    public void An_optional_task_keeps_only_what_the_model_chose_and_gets_a_person_when_it_chose_none()
    {
        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload(), [Slot(Optional, 2, 5, null), Slot(Optional, 2, 5, P2)]);

        plan.Where(s => s.TaskId == Optional).Should().ContainSingle().Which.AssigneeId.Should().NotBeNull();
    }

    [Fact]
    public void A_slot_without_a_person_is_given_somebody_who_is_available_that_day()
    {
        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload([1, 2, 3, 4, 5, 6, 0]), [Slot(Weekly, 0, 3, null)]);

        plan.Should().OnlyContain(s => s.AssigneeId == P2);
    }

    [Fact]
    public void Nobody_available_leaves_the_slot_without_a_person_for_the_caller_to_reject()
    {
        var payload = Payload() with { Users = [Payload().Users[0] with { UnavailableWeekdays = [0, 1, 2, 3, 4, 5, 6] }] };

        var plan = PlanCompleter.CompleteRequiredOccurrences(payload, [Slot(Weekly, 0, 3, null)]);

        plan.Should().OnlyContain(s => s.AssigneeId == null);
    }

    [Fact]
    public void The_daily_maximum_is_respected_when_somebody_fits()
    {
        // P1 may only do 30 minutes on a weekday: the 60 minute optional task goes to P2 even though P1 has the fewest minutes.
        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload(maxWeekday: 30), [Slot(Optional, 0, 1, null)]);

        plan.Single(s => s.TaskId == Optional).AssigneeId.Should().Be(P2);
    }

    [Fact]
    public void The_daily_maximum_is_a_warning_not_a_veto_when_nobody_fits()
    {
        var payload = Payload() with { Users = [Payload().Users[0] with { MaxDailyMinutes = new PromptMinutes(10, 10) }] };

        var plan = PlanCompleter.CompleteRequiredOccurrences(payload, [Slot(Optional, 0, 1, null)]);

        plan.Single(s => s.TaskId == Optional).AssigneeId.Should().Be(P1);
    }

    [Fact]
    public void The_person_with_the_fewest_minutes_so_far_is_preferred()
    {
        var plan = PlanCompleter.CompleteRequiredOccurrences(
            Payload() with { Tasks = [Payload().Tasks[1], Payload().Tasks[1] with { Id = Weekly, Name = "Tweede" }] },
            [Slot(Optional, 0, 1, null), Slot(Weekly, 0, 3, null)]);

        plan.Select(s => s.AssigneeId).Should().Equal(P1, P2);
    }

    [Fact]
    public void A_completed_plan_passes_the_validation_of_the_plan_editor()
    {
        var plan = PlanCompleter.CompleteRequiredOccurrences(Payload([2]), []);
        PlanTask[] tasks = [new(Weekly, "Badkamer", "1w", 30, true), new(Optional, "Optioneel", "quarter", 60, true), new(Twice, "Wastafel", "2w", 10, true)];
        PlanUser[] users =
        [
            new(P1, "P1", true, [2], new DayMinutes(600, 600), new DayMinutes(120, 240)),
            new(P2, "P2", true, [], new DayMinutes(600, 600), new DayMinutes(120, 240)),
        ];

        var validation = PlanValidator.Validate(
            new PlanDraft([.. plan.Select(s => new PlanSlot(s.TaskId, s.WeekIndex, s.Weekday, s.AssigneeId))]),
            tasks,
            users,
            DueCalculator.DefaultIntervals);

        validation.Errors.Should().BeEmpty();
        validation.Warnings.Should().NotContain(w => w.Code == PlanWarningCodes.IntervalMismatch);
    }
}
