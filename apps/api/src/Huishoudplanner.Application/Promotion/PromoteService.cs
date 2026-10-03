using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;
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
    ICyclePlanService cyclePlans,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IPromoteService
{
    public const string PlanNotActiveCode = "plan_not_active";

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

    // ---- apply

    public async Task<OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>> ApplyAsync(
        Actor actor, ApplyPromotionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (PromotionRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        var normalised = PromotionRules.Normalise(command);
        var ran = await transactions.RunAsync(ct => ApplyInTransactionAsync(actor, normalised, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>>(
            outcome => outcome,
            conflict => conflict,
            error => error);
    }

    // The plan is read and saved in one transaction, so a plan that stops being the active one in between cannot be changed. The slot save
    // joins this transaction (validation, slot diff, audit with the promotedFrom meta and the synchronisation of the upcoming occurrences).
    private async Task<TransactionOutcome<OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>>> ApplyInTransactionAsync(
        Actor actor, ApplyPromotionCommand command, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>> Abort(
            OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing> value) => TransactionOutcome.Abort(value);

        var found = await plans.FindActiveAsync(ct).ConfigureAwait(false);
        if (found.TryPickT2(out var findError, out var rest))
        {
            return Abort(findError);
        }

        if (rest.IsT1 || rest.AsT0.Id != command.PlanId)
        {
            return Abort(new ConflictError(PlanNotActiveCode, "Suggestions can only change the active plan"));
        }

        var plan = rest.AsT0;
        var original = plan.Slots.FirstOrDefault(s => s.TaskId == command.TaskId && s.WeekIndex == command.WeekIndex && s.Weekday == command.Weekday);
        if (original is null)
        {
            return Abort(new SlotNotFound());
        }

        var moved = original with { Weekday = command.ToWeekday, AssigneeId = command.ToAssigneeId ?? original.AssigneeId };
        var slots = plan.Slots.Select(s => ReferenceEquals(s, original) ? moved : s).ToList();
        var meta = new List<KeyValuePair<string, AuditValue>>
        {
            new("promotedFrom", AuditObject.Of(
                ("weekIndex", original.WeekIndex),
                ("weekday", original.Weekday),
                ("assigneeId", original.AssigneeId is { } assignee ? new AuditObjectId(assignee) : AuditNull.Instance))),
            new("toWeekday", command.ToWeekday),
        };
        if (command.ToAssigneeId is { } toAssignee)
        {
            meta.Add(new("toAssigneeId", toAssignee));
        }

        var saved = await cyclePlans.ReplaceSlotsAsync(actor, plan.Id, slots, ct, new AuditObject(meta)).ConfigureAwait(false);
        var result = saved.Match<OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>>(
            ok => ok,
            _ => new ConflictError(PlanNotActiveCode, "Suggestions can only change the active plan"),
            validation => validation,
            rejected => rejected,
            conflict => conflict,
            error => error,
            missing => missing);
        return result.IsT0 ? TransactionOutcome.Commit(result) : Abort(result);
    }

    // ---- dismiss

    public async Task<OneOf<Success, ValidationErrors, ConflictError, PortError, SettingsMissing>> DismissAsync(
        Actor actor, DismissedPromotion promotion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(promotion);
        if (PromotionRules.Validate(promotion) is { } invalid)
        {
            return invalid;
        }

        var normalised = PromotionRules.Normalise(promotion);
        var ran = await transactions.RunAsync(ct => DismissInTransactionAsync(actor, normalised, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, ValidationErrors, ConflictError, PortError, SettingsMissing>>(
            outcome => outcome.Match<OneOf<Success, ValidationErrors, ConflictError, PortError, SettingsMissing>>(
                success => success, missing => missing, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<Success, SettingsMissing, PortError>>> DismissInTransactionAsync(
        Actor actor, DismissedPromotion promotion, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Success, SettingsMissing, PortError>> Abort(OneOf<Success, SettingsMissing, PortError> value) => TransactionOutcome.Abort(value);

        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var rest))
        {
            return Abort(missing);
        }

        if (rest.TryPickT1(out var readError, out var current))
        {
            return Abort(readError);
        }

        var changes = new SettingsChanges { DismissedPromotions = PromotionRules.Dismiss(current.DismissedPromotions, promotion) };
        var change = ChangeSet.Between(SettingsAudit.ToAudit(current), SettingsAudit.ToAudit(changes.ApplyTo(current)));
        if (change.IsNoOp)
        {
            return TransactionOutcome.Commit<OneOf<Success, SettingsMissing, PortError>>(new Success());
        }

        var written = await settings.UpdateAsync(changes, ct).ConfigureAwait(false);
        if (written.TryPickT1(out var vanished, out var writtenRest))
        {
            return Abort(vanished);
        }

        if (writtenRest.TryPickT1(out var writeError, out _))
        {
            return Abort(writeError);
        }

        var recorded = await audit
            .RecordAsync(change.ToEntry(AuditActor.From(actor), AuditEntity.Settings, SettingsIds.Singleton, AuditAction.Update), ct)
            .ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Success, SettingsMissing, PortError>>(new Success()),
            error => Abort(error));
    }
}
