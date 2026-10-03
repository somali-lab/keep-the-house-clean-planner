using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Application.Settings;

/// <summary>
/// Port of <c>apps/server/src/routes/settings.ts</c> and <c>updateSettings</c> of <c>data/settings.ts</c>. The read, the checks, the
/// write and its audit entry are one transaction: two administrators setting the bonus amounts at once serialise on the settings
/// document instead of overwriting each other (the Node server needed a compare-and-set for that).
/// </summary>
public sealed class SettingsService(
    ForStoringSettings store,
    ForStoringTasks tasks,
    ForRecordingAudit audit,
    ForRunningTransactions transactions,
    TimeProvider time) : ISettingsService
{
    /// <summary>The conflict code of the runner when concurrent writers kept winning after its last attempt.</summary>
    private const string RunnerConflictCode = "write_conflict";

    public async Task<OneOf<SettingsView, SettingsMissing, PortError>> GetAsync(CancellationToken cancellationToken)
    {
        var read = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return read.Match<OneOf<SettingsView, SettingsMissing, PortError>>(
            settings => ViewOf(settings),
            missing => missing,
            error => error);
    }

    public async Task<OneOf<SettingsView, ValidationErrors, IntervalInUse, ConflictError, SettingsMissing, PortError>> UpdateAsync(
        Actor actor, SettingsPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(patch);
        if (SettingsRules.Validate(patch) is { } invalid)
        {
            return invalid;
        }

        var run = await transactions
            .RunAsync(token => UpdateInTransactionAsync(actor, patch, token), cancellationToken)
            .ConfigureAwait(false);
        return run.Match<OneOf<SettingsView, ValidationErrors, IntervalInUse, ConflictError, SettingsMissing, PortError>>(
            outcome => outcome.Match<OneOf<SettingsView, ValidationErrors, IntervalInUse, ConflictError, SettingsMissing, PortError>>(
                view => view,
                inUse => inUse,
                missing => missing,
                error => error),
            conflict => patch.PeriodBonuses is not null && conflict.Code == RunnerConflictCode
                ? new ConflictError("bonus_schedule_conflict", "The bonus amounts were changed by someone else; reload and try again.")
                : conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<SettingsView, IntervalInUse, SettingsMissing, PortError>>> UpdateInTransactionAsync(
        Actor actor, SettingsPatch patch, CancellationToken cancellationToken)
    {
        static TransactionOutcome<OneOf<SettingsView, IntervalInUse, SettingsMissing, PortError>> Abort(
            OneOf<SettingsView, IntervalInUse, SettingsMissing, PortError> value) => TransactionOutcome.Abort(value);

        var read = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var rest))
        {
            return Abort(missing);
        }

        if (rest.TryPickT1(out var readError, out var current))
        {
            return Abort(readError);
        }

        var blocked = await BlockedIntervalsAsync(current, patch, cancellationToken).ConfigureAwait(false);
        if (blocked.TryPickT1(out var usageError, out var inUse))
        {
            return Abort(usageError);
        }

        if (inUse.Count > 0)
        {
            return Abort(new IntervalInUse(inUse));
        }

        var changes = ChangesOf(current, patch);
        var scheduleChanged = changes.BonusSchedule is not null;
        var change = ChangeSet.Between(
            SettingsAudit.ToAudit(current, scheduleChanged),
            SettingsAudit.ToAudit(changes.ApplyTo(current), scheduleChanged));
        if (change.IsNoOp)
        {
            return TransactionOutcome.Commit<OneOf<SettingsView, IntervalInUse, SettingsMissing, PortError>>(ViewOf(current));
        }

        var written = await store.UpdateAsync(changes, cancellationToken).ConfigureAwait(false);
        if (written.TryPickT1(out var vanished, out var writtenRest))
        {
            return Abort(vanished);
        }

        if (writtenRest.TryPickT1(out var writeError, out var updated))
        {
            return Abort(writeError);
        }

        var recorded = await audit
            .RecordAsync(
                change.ToEntry(AuditActor.From(actor), AuditEntity.Settings, SettingsIds.Singleton, AuditAction.Update),
                cancellationToken)
            .ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<SettingsView, IntervalInUse, SettingsMissing, PortError>>(ViewOf(updated)),
            error => Abort(error));
    }

    /// <summary>The removed intervals that tasks still use (empty when nothing is removed or nothing is in use).</summary>
    private async Task<OneOf<IReadOnlyList<string>, PortError>> BlockedIntervalsAsync(
        HouseholdSettings current, SettingsPatch patch, CancellationToken cancellationToken)
    {
        if (patch.Intervals is not { } intervals)
        {
            return Array.Empty<string>();
        }

        var kept = intervals.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var removed = current.Intervals.Select(i => i.Key).Where(key => !kept.Contains(key)).ToList();
        if (removed.Count == 0)
        {
            return Array.Empty<string>();
        }

        var used = await tasks.GetIntervalKeysInUseAsync(cancellationToken).ConfigureAwait(false);
        return used.Match<OneOf<IReadOnlyList<string>, PortError>>(
            keys => removed.Where(key => keys.Contains(key, StringComparer.Ordinal)).ToList(),
            error => error);
    }

    /// <summary>
    /// What the write sets. A conversion or goals equal to the ones in force (a missing value is the default) is left out, and the
    /// amounts become a schedule row from today only when that changes the schedule (ADR-0012, requirements 4.12).
    /// </summary>
    private SettingsChanges ChangesOf(HouseholdSettings current, SettingsPatch patch)
    {
        IReadOnlyList<BonusScheduleRow>? schedule = null;
        if (patch.PeriodBonuses is { } amounts)
        {
            var today = DayKeys.Today(DayKeys.FindZone(current.Timezone), time);
            var existing = current.BonusSchedule ?? [];
            var next = BonusSchedule.WithAmounts(existing, amounts, today);
            if (!next.SequenceEqual(existing))
            {
                schedule = next;
            }
        }

        return new SettingsChanges
        {
            CycleAnchorDate = patch.CycleAnchorDate,
            VacationRanges = patch.VacationRanges,
            Intervals = patch.Intervals,
            AiProvider = patch.AiProvider,
            AiPrompts = patch.AiPrompts,
            AiPromptTemplates = patch.AiPromptTemplates,
            CompletionControl = patch.CompletionControl,
            PromoteThreshold = patch.PromoteThreshold,
            BonusSchedule = schedule,
            CurrencyCode = patch.CurrencyCode == (current.CurrencyCode ?? SettingsDefaults.CurrencyCode) ? null : patch.CurrencyCode,
            CentsPerPoint = patch.CentsPerPoint == (current.CentsPerPoint ?? SettingsDefaults.CentsPerPoint) ? null : patch.CentsPerPoint,
            RewardGoals = patch.RewardGoals == (current.RewardGoals ?? RewardGoals.Automatic) ? null : patch.RewardGoals,
        };
    }

    private SettingsView ViewOf(HouseholdSettings settings) =>
        SettingsView.Of(settings, DayKeys.Today(DayKeys.FindZone(settings.Timezone), time));
}
