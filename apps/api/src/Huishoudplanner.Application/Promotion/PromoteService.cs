using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Promotion;
using OneOf;

namespace Huishoudplanner.Application.Promotion;

/// <summary>
/// The promote suggestions use case (requirements 4.6; the data gathering of <c>computePromoteSuggestions</c> in apps/server/src/domain/promote.ts).
/// It reads the settings, the active plan, the cycles, the generated occurrences of that plan and the names of the tasks in its slots, and lets
/// <see cref="PromoteSuggestionCalculator"/> decide. Nothing is written, so nothing is audited.
/// </summary>
public sealed class PromoteService(
    ForStoringSettings settings,
    ForStoringCyclePlans plans,
    ForStoringCycles cycles,
    ForStoringTasks tasks,
    ForReadingPromotionEvidence evidence,
    TimeProvider time) : IPromoteService
{
    private const int CyclePage = 200;

    public async Task<OneOf<IReadOnlyList<PromoteSuggestion>, PortError>> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (current.TryPickT2(out var settingsFailure, out var rest))
        {
            return settingsFailure;
        }

        if (rest.IsT1)
        {
            return Empty();
        }

        var household = rest.AsT0;
        var active = await plans.FindActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.TryPickT2(out var planFailure, out var planRest))
        {
            return planFailure;
        }

        if (planRest.IsT1)
        {
            return Empty();
        }

        var plan = planRest.AsT0;
        var zone = DayKeys.FindZone(household.Timezone);

        var allCycles = new List<Cycle>();
        CycleCursor? after = null;
        while (true)
        {
            var read = await cycles.ListAsync(after, CyclePage, cancellationToken).ConfigureAwait(false);
            if (read.TryPickT1(out var cycleFailure, out var page))
            {
                return cycleFailure;
            }

            allCycles.AddRange(page);
            if (page.Count < CyclePage)
            {
                break;
            }

            after = CycleCursor.After(page[^1]);
        }

        var occurrences = await evidence.FindGeneratedOfPlanAsync(plan.Id, cancellationToken).ConfigureAwait(false);
        if (occurrences.TryPickT1(out var evidenceFailure, out var found))
        {
            return evidenceFailure;
        }

        var taskIds = plan.Slots.Select(s => s.TaskId).Distinct(StringComparer.Ordinal).ToList();
        var named = await tasks.FindManyAsync(taskIds, cancellationToken).ConfigureAwait(false);
        if (named.TryPickT1(out var taskFailure, out var taskList))
        {
            return taskFailure;
        }

        var input = new PromoteInput(
            plan.Id,
            [.. plan.Slots.Select(s => new PromoteSlot(s.TaskId, s.WeekIndex, s.Weekday, s.AssigneeId))],
            [.. allCycles.Select(c => new PromoteCycle(c.Index, c.StartDate))],
            [.. found.Select(o => new PromoteOccurrence(o.Id, o.TaskId, DayKeys.ToDayKey(o.PlannedDate, zone), DayKeys.ToDayKey(o.Date, zone), o.AssigneeId))],
            taskList.ToDictionary(t => t.Id, t => t.Name, StringComparer.Ordinal),
            household.CycleAnchorDate,
            DayKeys.Today(zone, time.GetUtcNow()),
            household.PromoteThreshold,
            household.DismissedPromotions);
        return OneOf<IReadOnlyList<PromoteSuggestion>, PortError>.FromT0(PromoteSuggestionCalculator.Compute(input));
    }

    private static OneOf<IReadOnlyList<PromoteSuggestion>, PortError> Empty() =>
        OneOf<IReadOnlyList<PromoteSuggestion>, PortError>.FromT0([]);
}
