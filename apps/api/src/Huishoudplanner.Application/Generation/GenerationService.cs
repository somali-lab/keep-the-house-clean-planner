using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Generation;

/// <summary>
/// The generation use cases (requirements 4.3). Port of <c>domain/generation.ts</c> and <c>data/cycles.ts</c>. Every public call is one
/// transaction (or joins the caller's) with the cycle and occurrence writes and their audit entries, so a run either happens completely or
/// not at all; a run that changes nothing writes and audits nothing. What is idempotent stays idempotent through the store: the unique slot
/// index decides which drafts are new, and only those are audited.
/// </summary>
public sealed class GenerationService(
    ForStoringCyclePlans plans,
    ForStoringTasks tasks,
    ForStoringRooms rooms,
    ForStoringSettings settings,
    ForStoringCycles cycles,
    ForStoringOccurrences occurrences,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IGenerationService
{
    private const int CyclePage = CycleListQuery.MaxLimit;

    /// <summary>What one run reads once: the settings and what depends on them and on the plan's tasks.</summary>
    private sealed record Run(
        AuditActor Actor,
        string RunId,
        HouseholdSettings Settings,
        TimeZoneInfo Zone,
        DateTimeOffset Now,
        DateOnly Today,
        IReadOnlyDictionary<string, HouseholdTask> Tasks,
        IReadOnlyDictionary<string, string> RoomNames)
    {
        public int CurrentCycle => Cycles.CycleIndexFor(Today, Settings.CycleAnchorDate);

        public DateTimeOffset TodayStart => DayKeys.FromDayKey(Today, Zone);

        /// <summary>The start of the cycle after the next one: the exclusive end of "the current and the next cycle".</summary>
        public DateTimeOffset UpcomingEnd => DayKeys.FromDayKey(Cycles.CycleStart(CurrentCycle + 2, Settings.CycleAnchorDate), Zone);
    }

    // ---- public use cases

    public async Task<OneOf<GenerationRun, SettingsMissing, ConflictError, PortError>> GenerateUpcomingAsync(AuditActor actor, string runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var ran = await transactions.RunAsync(ct => InTransaction(GenerateUpcomingCoreAsync(actor, runId, ct)), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<GenerationRun, SettingsMissing, ConflictError, PortError>>(
            flow => flow.TryGet(out var value, out var failure) ? value : Fail<GenerationRun>(failure),
            conflict => conflict,
            error => error);
    }

    public async Task<OneOf<GenerationResult, SettingsMissing, ConflictError, PortError>> GenerateCycleAsync(AuditActor actor, int cycleIndex, string runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var ran = await transactions.RunAsync(ct => InTransaction(GenerateCycleCoreAsync(actor, cycleIndex, runId, ct)), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<GenerationResult, SettingsMissing, ConflictError, PortError>>(
            flow => flow.TryGet(out var value, out var failure) ? value : Fail<GenerationResult>(failure),
            conflict => conflict,
            error => error);
    }

    public async Task<OneOf<ReplacementResult, NotFound, SettingsMissing, ConflictError, PortError>> ReplaceUpcomingAsync(
        AuditActor actor, string planId, string runId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var ran = await transactions.RunAsync(ct => InTransaction(ReplaceCoreAsync(actor, planId, runId, reason, ct)), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<ReplacementResult, NotFound, SettingsMissing, ConflictError, PortError>>(
            flow => flow.TryGet(out var value, out var failure)
                ? value
                : failure switch
                {
                    FlowFailure.MissingPlan => new NotFound(),
                    FlowFailure.MissingSettings => new SettingsMissing(),
                    FlowFailure.Port port => port.Error,
                    _ => throw new InvalidOperationException("Unknown failure."),
                },
            conflict => conflict,
            error => error);
    }

    private static async Task<TransactionOutcome<Flow<T>>> InTransaction<T>(Task<Flow<T>> work)
    {
        var flow = await work.ConfigureAwait(false);
        return flow.IsSuccess ? TransactionOutcome.Commit(flow) : TransactionOutcome.Abort(flow);
    }

    private static OneOf<T, SettingsMissing, PortError> FailAs<T>(FlowFailure failure) => failure switch
    {
        FlowFailure.MissingSettings => new SettingsMissing(),
        FlowFailure.Port port => port.Error,
        _ => throw new InvalidOperationException("A generation step that cannot miss a plan reported one."),
    };

    private static OneOf<T, SettingsMissing, ConflictError, PortError> Fail<T>(FlowFailure failure) =>
        FailAs<T>(failure).Match<OneOf<T, SettingsMissing, ConflictError, PortError>>(value => value, missing => missing, error => error);

    // ---- the nightly run

    private async Task<Flow<GenerationRun>> GenerateUpcomingCoreAsync(AuditActor actor, string runId, CancellationToken ct)
    {
        var activeRead = await plans.FindActiveAsync(ct).ConfigureAwait(false);
        if (!activeRead.OfOptional().TryGet(out var plan, out var planFailure))
        {
            return planFailure;
        }

        if (!(await LoadRunAsync(actor, runId, plan, ct).ConfigureAwait(false)).TryGet(out var run, out var runFailure))
        {
            return runFailure;
        }

        // Existing cycles follow the configured anchor, whatever else happens in this run.
        if (!(await AlignAllAsync(run, ct).ConfigureAwait(false)).TryGet(out _, out var alignFailure))
        {
            return alignFailure;
        }

        var current = run.CurrentCycle;
        if (plan is not null)
        {
            if (!(await NeedsReplacementAsync(run, plan, current, ct).ConfigureAwait(false)).TryGet(out var needs, out var needsFailure))
            {
                return needsFailure;
            }

            if (needs)
            {
                if (!(await ReplaceUpcomingCoreAsync(run, plan, ReplacementReasons.NightlyReconciliation, ct).ConfigureAwait(false)).TryGet(out var replaced, out var replaceFailure))
                {
                    return replaceFailure;
                }

                return new GenerationRun(runId, replaced.Removed, replaced.Generated);
            }
        }

        var generated = new List<GenerationResult>();
        foreach (var index in new[] { current, current + 1 })
        {
            if (!(await EnsureCycleAsync(run, index, plan?.Id, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
            {
                return cycleFailure;
            }

            if (!(await GenerateIntoCycleAsync(run, cycle, plan, ct).ConfigureAwait(false)).TryGet(out var result, out var generateFailure))
            {
                return generateFailure;
            }

            generated.Add(result);
        }

        return new GenerationRun(runId, 0, generated);
    }

    // ---- one cycle

    private async Task<Flow<GenerationResult>> GenerateCycleCoreAsync(AuditActor actor, int cycleIndex, string runId, CancellationToken ct)
    {
        var activeRead = await plans.FindActiveAsync(ct).ConfigureAwait(false);
        if (!activeRead.OfOptional().TryGet(out var plan, out var planFailure))
        {
            return planFailure;
        }

        if (!(await LoadRunAsync(actor, runId, plan, ct).ConfigureAwait(false)).TryGet(out var run, out var runFailure))
        {
            return runFailure;
        }

        if (!(await EnsureCycleAsync(run, cycleIndex, plan?.Id, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
        {
            return cycleFailure;
        }

        return await GenerateIntoCycleAsync(run, cycle, plan, ct).ConfigureAwait(false);
    }

    /// <summary>Generates the occurrences of one (already ensured) cycle from the plan: skipped are vacation days, inactive tasks and days before today.</summary>
    private async Task<Flow<GenerationResult>> GenerateIntoCycleAsync(Run run, Cycle cycle, CyclePlan? plan, CancellationToken ct)
    {
        if (plan is null)
        {
            return new GenerationResult(cycle.Index, cycle.Id, null, 0, 0);
        }

        var planned = OccurrencePlanner.Plan(plan.Slots, cycle.Index, run.Settings, run.Tasks, run.Today);
        var drafts = planned.Select(p => OccurrencePlanner.ToDraft(p, cycle.Id, plan.Id, run.Zone, run.RoomNames, run.Now)).ToList();
        IReadOnlyList<Occurrence> inserted = [];
        if (drafts.Count > 0)
        {
            if (!(await occurrences.InsertGeneratedAsync(drafts, ct).ConfigureAwait(false)).Of().TryGet(out inserted!, out var insertFailure))
            {
                return insertFailure;
            }
        }

        foreach (var occurrence in inserted)
        {
            if (!(await RecordAsync(OccurrenceAudit.ForGenerated(run.Actor, occurrence, run.RunId, cycle.Index), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
            {
                return auditFailure;
            }
        }

        return new GenerationResult(cycle.Index, cycle.Id, plan.Id, inserted.Count, drafts.Count - inserted.Count);
    }

    // ---- replacement

    private async Task<Flow<ReplacementResult>> ReplaceCoreAsync(AuditActor actor, string planId, string runId, string reason, CancellationToken ct)
    {
        var found = await plans.FindAsync(planId, ct).ConfigureAwait(false);
        if (!found.OfRequired().TryGet(out var plan, out var planFailure))
        {
            return planFailure;
        }

        if (!(await LoadRunAsync(actor, runId, plan, ct).ConfigureAwait(false)).TryGet(out var run, out var runFailure))
        {
            return runFailure;
        }

        return await ReplaceUpcomingCoreAsync(run, plan, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The replacement rule (plan section 1.4): the open, generated, not-dragged occurrences from today to the end of the next cycle are removed
    /// (audited) and generated again from the plan; the two cycles are pointed at the plan. Done, skipped, dragged and ad-hoc ones stay.
    /// </summary>
    private async Task<Flow<ReplacementResult>> ReplaceUpcomingCoreAsync(Run run, CyclePlan plan, string reason, CancellationToken ct)
    {
        var current = run.CurrentCycle;
        var ensured = new List<Cycle>();
        foreach (var index in new[] { current, current + 1 })
        {
            if (!(await EnsureCycleAsync(run, index, plan.Id, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
            {
                return cycleFailure;
            }

            if (!(await SetCyclePlanAsync(run, cycle, plan.Id, ct).ConfigureAwait(false)).TryGet(out var pointed, out var pointFailure))
            {
                return pointFailure;
            }

            ensured.Add(pointed);
        }

        if (!(await occurrences.FindReplaceableBetweenAsync(run.TodayStart, run.UpcomingEnd, ct).ConfigureAwait(false)).Of().TryGet(out var replaceable, out var findFailure))
        {
            return findFailure;
        }

        var removed = 0;
        if (replaceable.Count > 0)
        {
            if (!(await occurrences.DeleteAsync([.. replaceable.Select(o => o.Id)], ct).ConfigureAwait(false)).Of().TryGet(out removed, out var deleteFailure))
            {
                return deleteFailure;
            }

            foreach (var occurrence in replaceable)
            {
                if (!(await RecordAsync(OccurrenceAudit.ForRemoved(run.Actor, occurrence, run.RunId, plan.Id, reason), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
                {
                    return auditFailure;
                }
            }
        }

        var generated = new List<GenerationResult>();
        foreach (var cycle in ensured)
        {
            if (!(await GenerateIntoCycleAsync(run, cycle, plan, ct).ConfigureAwait(false)).TryGet(out var result, out var generateFailure))
            {
                return generateFailure;
            }

            generated.Add(result);
        }

        return new ReplacementResult(removed, generated);
    }

    /// <summary>
    /// True when the plan expects an occurrence the store lacks, or a replaceable occurrence differs from what the plan expects (day, assignee,
    /// plan). Ensures the current and the next cycle first, as the Node server does.
    /// </summary>
    private async Task<Flow<bool>> NeedsReplacementAsync(Run run, CyclePlan plan, int current, CancellationToken ct)
    {
        var expected = new List<PlannedOccurrence>();
        foreach (var index in new[] { current, current + 1 })
        {
            if (!(await EnsureCycleAsync(run, index, plan.Id, ct).ConfigureAwait(false)).TryGet(out _, out var cycleFailure))
            {
                return cycleFailure;
            }

            expected.AddRange(OccurrencePlanner.Plan(plan.Slots, index, run.Settings, run.Tasks, run.Today));
        }

        // Only generated occurrences occupy a slot (ADR-0009): the store is asked for those, by the day they were planned for.
        if (!(await occurrences.FindGeneratedPlannedBetweenAsync(run.TodayStart, run.UpcomingEnd, ct).ConfigureAwait(false)).Of().TryGet(out var existing, out var findFailure))
        {
            return findFailure;
        }

        return OccurrenceReconciliation.NeedsReplacement(expected, existing, plan.Id, run.TodayStart, run.Zone);
    }

    // ---- cycles

    /// <summary>Realigns every stored cycle to the configured anchor (a no-op for cycles that already match).</summary>
    private async Task<Flow<int>> AlignAllAsync(Run run, CancellationToken ct)
    {
        CycleCursor? after = null;
        var count = 0;
        while (true)
        {
            if (!(await cycles.ListAsync(after, CyclePage, ct).ConfigureAwait(false)).Of().TryGet(out var page, out var listFailure))
            {
                return listFailure;
            }

            foreach (var cycle in page)
            {
                if (!(await AlignAsync(run, cycle, ct).ConfigureAwait(false)).TryGet(out _, out var alignFailure))
                {
                    return alignFailure;
                }

                count++;
            }

            if (page.Count < CyclePage)
            {
                return count;
            }

            after = CycleCursor.After(page[^1]);
        }
    }

    /// <summary>The cycle of an index, created when missing. An existing cycle is realigned when the anchor changed (audited with its reason).</summary>
    private async Task<Flow<Cycle>> EnsureCycleAsync(Run run, int index, string? planId, CancellationToken ct)
    {
        var found = await cycles.FindByIndexAsync(index, ct).ConfigureAwait(false);
        if (!found.OfOptional().TryGet(out var existing, out var findFailure))
        {
            return findFailure;
        }

        if (existing is not null)
        {
            return await AlignAsync(run, existing, ct).ConfigureAwait(false);
        }

        var anchor = run.Settings.CycleAnchorDate;
        var inserted = await cycles.InsertAsync(
            new NewCycle(index, Cycles.CycleStart(index, anchor), Cycles.CycleEnd(index, anchor), planId, run.Now, run.RunId),
            ct).ConfigureAwait(false);
        if (!inserted.Of().TryGet(out var created, out var insertFailure))
        {
            return insertFailure;
        }

        if (!(await RecordAsync(CycleAudit.ForCreate(run.Actor, created), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        return created;
    }

    private async Task<Flow<Cycle>> AlignAsync(Run run, Cycle cycle, CancellationToken ct)
    {
        var anchor = run.Settings.CycleAnchorDate;
        var start = Cycles.CycleStart(cycle.Index, anchor);
        var end = Cycles.CycleEnd(cycle.Index, anchor);
        if (cycle.StartDate == start && cycle.EndDate == end)
        {
            return cycle;
        }

        var updated = await cycles.UpdateBoundsAsync(cycle.Id, start, end, run.Now, run.RunId, ct).ConfigureAwait(false);
        if (updated.TryPickT1(out _, out var rest))
        {
            return cycle; // The cycle vanished meanwhile: nothing to realign, as in the Node server.
        }

        if (rest.TryPickT1(out var updateError, out var after))
        {
            return updateError;
        }

        if (!(await RecordAsync(CycleAudit.ForAnchorAlignment(run.Actor, cycle, after, run.RunId), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        return after;
    }

    /// <summary>Points the cycle at the plan it is now generated from; a no-op (nothing written or audited) when it already is.</summary>
    private async Task<Flow<Cycle>> SetCyclePlanAsync(Run run, Cycle cycle, string planId, CancellationToken ct)
    {
        if (cycle.PlanId == planId)
        {
            return cycle;
        }

        var updated = await cycles.UpdatePlanAsync(cycle.Id, planId, run.Now, run.RunId, ct).ConfigureAwait(false);
        if (updated.TryPickT1(out _, out var rest))
        {
            return cycle;
        }

        if (rest.TryPickT1(out var updateError, out var after))
        {
            return updateError;
        }

        if (!(await RecordAsync(CycleAudit.ForPlan(run.Actor, cycle.Id, cycle.PlanId, planId, run.RunId), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        return after;
    }

    // ---- reading what a run needs

    /// <summary>
    /// Reads the settings and, for a plan, the tasks of its slots and the names of their rooms: bounded by what the plan uses, never the whole
    /// collection. Done once per run, so every cycle of the run sees the same values.
    /// </summary>
    private async Task<Flow<Run>> LoadRunAsync(AuditActor actor, string runId, CyclePlan? plan, CancellationToken ct)
    {
        if (!(await settings.GetAsync(ct).ConfigureAwait(false)).Of().TryGet(out var current, out var settingsFailure))
        {
            return settingsFailure;
        }

        var now = time.GetUtcNow();
        var zone = DayKeys.FindZone(current.Timezone);
        var taskMap = new Dictionary<string, HouseholdTask>(StringComparer.Ordinal);
        var roomNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plan is { Slots.Count: > 0 })
        {
            var taskIds = plan.Slots.Select(s => s.TaskId).Distinct(StringComparer.Ordinal).ToList();
            if (!(await tasks.FindManyAsync(taskIds, ct).ConfigureAwait(false)).Of().TryGet(out var found, out var taskFailure))
            {
                return taskFailure;
            }

            foreach (var task in found)
            {
                taskMap[task.Id] = task;
            }

            var roomIds = found.Where(t => t.Active).Select(t => t.RoomId).Distinct(StringComparer.Ordinal).ToList();
            if (roomIds.Count > 0)
            {
                if (!(await rooms.FindManyAsync(roomIds, ct).ConfigureAwait(false)).Of().TryGet(out var foundRooms, out var roomFailure))
                {
                    return roomFailure;
                }

                foreach (var room in foundRooms)
                {
                    roomNames[room.Id] = room.Name;
                }
            }
        }

        return new Run(actor, runId, current, zone, now, DayKeys.Today(zone, now), taskMap, roomNames);
    }

    private async Task<Flow<bool>> RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match<Flow<bool>>(_ => true, error => error);
    }
}
