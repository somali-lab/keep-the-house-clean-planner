using Huishoudplanner.Application.Generation;
using Huishoudplanner.Domain.Activation;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Activation;

/// <summary>
/// Plan activation (requirements 4.3, ADR-0008 as amended, ADR-0021). Port of <c>domain/activationPreview.ts</c>, <c>domain/activation.ts</c> and
/// <c>setActivePlan</c> of <c>data/cyclePlans.ts</c>. The preview is read-only. The activation is one transaction that first writes the shared
/// guard document, then recomputes the preview inside the transaction and compares its token, then writes the plans and their audit entries and
/// replaces the upcoming occurrences through <see cref="IGenerationService"/> (which joins the transaction). A token that no longer matches aborts
/// before any plan write.
/// </summary>
public sealed class ActivationService(
    ForStoringCyclePlans plans,
    ForStoringTasks tasks,
    ForStoringRooms rooms,
    ForStoringSettings settings,
    ForStoringCycles cycles,
    ForReadingOccurrencesForActivation occurrences,
    ForActivatingCyclePlans activation,
    IGenerationService generation,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IActivationService
{
    public async Task<OneOf<ActivationPreview, NotFound, ValidationErrors, SettingsMissing, PortError>> PreviewAsync(string planId, CancellationToken cancellationToken)
    {
        if (!CyclePlanRules.IsId(planId))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var built = await BuildPreviewAsync(planId.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        if (built.TryGet(out var preview, out var failure))
        {
            return preview;
        }

        return failure switch
        {
            FlowFailure.MissingPlan => new NotFound(),
            FlowFailure.MissingSettings => new SettingsMissing(),
            FlowFailure.Port port => port.Error,
            _ => throw new InvalidOperationException("Unknown failure."),
        };
    }

    public async Task<OneOf<PlanActivated, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> ActivateAsync(
        Actor actor, string planId, string previewToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(previewToken);
        if (!CyclePlanRules.IsId(planId))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var id = planId.ToLowerInvariant();
        var ran = await transactions.RunAsync(ct => ActivateInTransactionAsync(actor, id, previewToken, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<PlanActivated, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
            outcome => outcome.Match<OneOf<PlanActivated, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
                activated => activated, notFound => notFound, stale => stale, missing => missing, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<PlanActivated, NotFound, ConflictError, SettingsMissing, PortError>>> ActivateInTransactionAsync(
        Actor actor, string planId, string previewToken, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<PlanActivated, NotFound, ConflictError, SettingsMissing, PortError>> Abort(OneOf<PlanActivated, NotFound, ConflictError, SettingsMissing, PortError> value) =>
            TransactionOutcome.Abort(value);

        // The guard comes first: two concurrent activations both write this document, so one of them gets a write conflict and runs again
        // against the state the other committed (snapshot isolation alone would let both commit, ADR-0021).
        var guard = await activation.TouchGuardAsync(ct).ConfigureAwait(false);
        if (guard.TryPickT1(out var guardMissing, out var guardRest))
        {
            return Abort(guardMissing);
        }

        if (guardRest.TryPickT1(out var guardError, out _))
        {
            return Abort(guardError);
        }

        var built = await BuildPreviewAsync(planId, ct).ConfigureAwait(false);
        if (!built.TryGet(out var fresh, out var failure))
        {
            return Abort(failure switch
            {
                FlowFailure.MissingPlan => new NotFound(),
                FlowFailure.MissingSettings => new SettingsMissing(),
                FlowFailure.Port port => port.Error,
                _ => throw new InvalidOperationException("Unknown failure."),
            });
        }

        if (!string.Equals(fresh.PreviewToken, previewToken, StringComparison.Ordinal))
        {
            return Abort(new ConflictError(ActivationCodes.StalePreview, ActivationCodes.StalePreviewDetail));
        }

        var runId = GenerationRunIds.New();
        var activated = await activation.ActivateAsync(planId, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (activated.TryPickT1(out var gone, out var activatedRest))
        {
            return Abort(gone);
        }

        if (activatedRest.TryPickT1(out var activateError, out var change))
        {
            return Abort(activateError);
        }

        var by = AuditActor.From(actor);
        foreach (var other in change.DeactivatedIds)
        {
            if (!(await RecordAsync(ActivationAudit.ForDeactivated(by, other, planId, runId), ct).ConfigureAwait(false)).TryPickT0(out _, out var deactivatedAuditError))
            {
                return Abort(deactivatedAuditError);
            }
        }

        if (!(await RecordAsync(ActivationAudit.ForActivated(by, change.Before, change.After, runId), ct).ConfigureAwait(false)).TryPickT0(out _, out var activatedAuditError))
        {
            return Abort(activatedAuditError);
        }

        // The deletions and the generated occurrences are the system's work on behalf of the activating profile, with the same run id.
        var replacement = await generation.ReplaceUpcomingAsync(
            new AuditActor(actor.ActorId, AuditSource.System), planId, runId, ReplacementReasons.PlanActivation, ct).ConfigureAwait(false);
        if (replacement.TryPickT1(out var vanished, out var replacementRest))
        {
            return Abort(vanished);
        }

        if (replacementRest.TryPickT1(out var missing, out var settingsRest))
        {
            return Abort(missing);
        }

        if (settingsRest.TryPickT1(out var conflict, out var errorRest))
        {
            return Abort(new PortError($"generation.conflict: the replacement of the upcoming occurrences ended in a conflict ({conflict.Code})."));
        }

        if (errorRest.TryPickT1(out var replaceError, out _))
        {
            return Abort(replaceError);
        }

        return TransactionOutcome.Commit<OneOf<PlanActivated, NotFound, ConflictError, SettingsMissing, PortError>>(
            new PlanActivated(change.After, runId, replacement.AsT0));
    }

    private async Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken ct) =>
        await audit.RecordAsync(entry, ct).ConfigureAwait(false);

    /// <summary>
    /// Everything the projection depends on, read once: the plan and the active plan, the settings, the tasks of the plan and the names of their
    /// rooms (bounded by what the plan uses), the two cycle documents and the occurrences of the window. Joins the running transaction.
    /// </summary>
    private async Task<Flow<ActivationPreview>> BuildPreviewAsync(string planId, CancellationToken ct)
    {
        if (!(await plans.FindAsync(planId, ct).ConfigureAwait(false)).OfRequired().TryGet(out var plan, out var planFailure))
        {
            return planFailure;
        }

        if (!(await plans.FindActiveAsync(ct).ConfigureAwait(false)).OfOptional().TryGet(out var active, out var activeFailure))
        {
            return activeFailure;
        }

        if (!(await settings.GetAsync(ct).ConfigureAwait(false)).Of().TryGet(out var household, out var settingsFailure))
        {
            return settingsFailure;
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var window = ActivationWindow.Of(household, zone, time.GetUtcNow());

        var taskMap = new Dictionary<string, HouseholdTask>(StringComparer.Ordinal);
        var roomNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plan.Slots.Count > 0)
        {
            var taskIds = plan.Slots.Select(s => s.TaskId).Distinct(StringComparer.Ordinal).ToList();
            if (!(await tasks.FindManyAsync(taskIds, ct).ConfigureAwait(false)).Of().TryGet(out var foundTasks, out var taskFailure))
            {
                return taskFailure;
            }

            foreach (var task in foundTasks)
            {
                taskMap[task.Id] = task;
            }

            var roomIds = foundTasks.Select(t => t.RoomId).Distinct(StringComparer.Ordinal).ToList();
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

        if (!(await cycles.FindByIndexAsync(window.CurrentCycle, ct).ConfigureAwait(false)).OfOptional().TryGet(out var currentCycle, out var currentFailure))
        {
            return currentFailure;
        }

        if (!(await cycles.FindByIndexAsync(window.CurrentCycle + 1, ct).ConfigureAwait(false)).OfOptional().TryGet(out var nextCycle, out var nextFailure))
        {
            return nextFailure;
        }

        var cycleIds = new[] { currentCycle, nextCycle }.Where(c => c is not null).Select(c => c!.Id).ToList();
        if (!(await occurrences.FindForActivationAsync(window.From, window.To, cycleIds, ct).ConfigureAwait(false)).Of().TryGet(out var existing, out var existingFailure))
        {
            return existingFailure;
        }

        return ActivationPreviewer.Build(plan, active?.Id, household, zone, window, taskMap, roomNames, currentCycle, nextCycle, existing);
    }
}
