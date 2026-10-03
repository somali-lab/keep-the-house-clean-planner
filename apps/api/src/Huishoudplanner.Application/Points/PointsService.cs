using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Points;

/// <summary>
/// The points ledger use cases (requirements 4.12, ADR-0011): the live sync of one execution entry, the reconciliation of the whole ledger and
/// the two reads. Port of <c>domain/points.ts</c> for the execution entries and the bonuses; the redemptions (4.3) and the badge step
/// (4.5) join the same reconciliation later.
/// </summary>
/// <remarks>
/// <para>The Node server serialised every ledger write in one in-process queue because it had no transactions. Here every write is one transaction
/// with its audit entry (ADR-0021): a live sync joins the transaction of its occurrence use case, a reconciliation is one transaction that
/// re-reads everything when a concurrent check-off wins a write conflict, and the <see cref="ReconcileGate"/> keeps two reconciliations of this
/// process from running at once (ADR-0005: one process).</para>
/// <para>A sync audits every real change of its entry; a reconciliation audits one summary and none per entry; a run that changes nothing
/// writes and audits nothing.</para>
/// </remarks>
public sealed class PointsService(
    ForStoringPointEntries entries,
    ForBackfillingPoints backfill,
    ForStoringSettings settings,
    ForStoringUsers users,
    ForStoringOccurrences occurrences,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time,
    ReconcileGate gate) : IPointsService, IExecutionPointsService
{
    private const int UserPageSize = 200;

    /// <summary>The start of the message when the bonus step failed after the execution part of the run was committed.</summary>
    public const string BonusStepFailed = "points.bonus_step_failed: the execution entries are reconciled, the bonus step failed. ";

    // ---- reads

    public async Task<OneOf<PointsBalances, ValidationErrors, SettingsMissing, PortError>> BalancesAsync(DateOnly? fromDay, DateOnly? toDay, CancellationToken cancellationToken)
    {
        var errors = PointsRules.CheckRange(fromDay, toDay);
        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryPickT0(out var household, out var readFailure))
        {
            return readFailure.Match<OneOf<PointsBalances, ValidationErrors, SettingsMissing, PortError>>(missing => missing, error => error);
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var totals = await entries.SumByPersonAsync(
            fromDay is { } start ? DayKeys.FromDayKey(start, zone) : null,
            toDay is { } end ? DayKeys.FromDayKey(DayKeys.AddDays(end, 1), zone) : null,
            cancellationToken).ConfigureAwait(false);
        if (!totals.TryPickT0(out var sums, out var totalsFailure))
        {
            return totalsFailure;
        }

        // The user list is as long as the household: read in pages like every other list, never in one unbounded read.
        var people = new List<User>();
        UserCursor? after = null;
        do
        {
            var page = await users.ListAsync(new UserQuery(null, after, UserPageSize), cancellationToken).ConfigureAwait(false);
            if (!page.TryPickT0(out var found, out var pageFailure))
            {
                return pageFailure;
            }

            people.AddRange(found.Items);
            after = found.NextCursor is { } next && UserCursor.TryParse(next, out var cursor) ? cursor : null;
        }
        while (after is not null);

        return PointsRules.BuildBalances(fromDay, toDay, household, people, sums);
    }

    public async Task<OneOf<PointEntryList, ValidationErrors, SettingsMissing, PortError>> EntriesAsync(PointEntriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = PointsRules.CheckEntries(request);
        PointEntryCursor? after = null;
        if (request.Cursor is not null)
        {
            if (PointEntryCursor.TryDecode(request.Cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["invalid_cursor"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryPickT0(out var household, out var readFailure))
        {
            return readFailure.Match<OneOf<PointEntryList, ValidationErrors, SettingsMissing, PortError>>(missing => missing, error => error);
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var take = request.Limit ?? PointsEntryLimits.DefaultLimit;
        var query = new PointEntryQuery(
            request.PersonId.ToLowerInvariant(),
            DayKeys.FromDayKey(request.From, zone),
            DayKeys.FromDayKey(DayKeys.AddDays(request.To, 1), zone),
            after,
            take + 1);
        var found = await entries.ListAsync(query, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<PointEntryList, ValidationErrors, SettingsMissing, PortError>>(
            items => items.Count > take
                ? new PointEntryList([.. items.Take(take).Select(e => PointEntryView.From(e, zone))], PointEntryCursor.After(items[take - 1]).Encode())
                : new PointEntryList([.. items.Select(e => PointEntryView.From(e, zone))], null),
            error => error);
    }

    // ---- the live sync of one execution

    public async Task<OneOf<SyncOutcome, PortError>> SyncAsync(AuditActor actor, string occurrenceId, PointsSyncReason reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(occurrenceId);
        var ran = await transactions.RunAsync(ct => SyncCoreAsync(actor, occurrenceId.ToLowerInvariant(), reason, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<SyncOutcome, PortError>>(
            outcome => outcome,
            _ => new PortError("points.write_conflict: a concurrent change kept winning the write of the ledger entry."),
            error => error);
    }

    /// <summary>
    /// Makes the single entry of the occurrence match it: it reads the occurrence, the entry and the settings inside the transaction, so it always
    /// writes what is true at that moment.
    /// </summary>
    private async Task<TransactionOutcome<OneOf<SyncOutcome, PortError>>> SyncCoreAsync(AuditActor actor, string occurrenceId, PointsSyncReason reason, CancellationToken ct)
    {
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.IsT2)
        {
            return Abort<SyncOutcome>(read.AsT2);
        }

        if (read.IsT1)
        {
            // Without settings the ledger cannot be dated and is left alone.
            return TransactionOutcome.Commit<OneOf<SyncOutcome, PortError>>(SyncOutcome.Unchanged);
        }

        var zone = DayKeys.FindZone(read.AsT0.Timezone);
        var occurrenceRead = await occurrences.FindAsync(occurrenceId, ct).ConfigureAwait(false);
        if (occurrenceRead.IsT2)
        {
            return Abort<SyncOutcome>(occurrenceRead.AsT2);
        }

        var key = ExecutionPoints.Key(occurrenceId);
        var storedRead = await entries.FindByKeyAsync(key, ct).ConfigureAwait(false);
        if (storedRead.IsT2)
        {
            return Abort<SyncOutcome>(storedRead.AsT2);
        }

        var stored = storedRead.IsT0 ? storedRead.AsT0 : null;
        var expected = occurrenceRead.IsT0 ? ExecutionPoints.Expect(ExecutionSource.From(occurrenceRead.AsT0), zone).Fields : null;
        var now = Now();

        if (expected is null)
        {
            if (stored is null)
            {
                return TransactionOutcome.Commit<OneOf<SyncOutcome, PortError>>(SyncOutcome.Unchanged);
            }

            var deleted = await entries.DeleteAsync(stored, ct).ConfigureAwait(false);
            if (deleted.IsT1)
            {
                return Abort<SyncOutcome>(deleted.AsT1);
            }

            if (!deleted.AsT0)
            {
                return TransactionOutcome.Commit<OneOf<SyncOutcome, PortError>>(SyncOutcome.Unchanged);
            }

            return await RecordAsync(PointsAudit.ForDelete(actor, stored, occurrenceId, reason), SyncOutcome.Deleted, ct).ConfigureAwait(false);
        }

        if (stored is null)
        {
            var inserted = await entries.InsertExecutionAsync(key, expected, PointEntrySource.Live, now, ct).ConfigureAwait(false);
            if (inserted.IsT1)
            {
                return Abort<SyncOutcome>(inserted.AsT1);
            }

            return await RecordAsync(PointsAudit.ForCreate(actor, inserted.AsT0, occurrenceId, reason), SyncOutcome.Created, ct).ConfigureAwait(false);
        }

        // A no-op writes and audits nothing, also when only the bookkeeping (the source) would differ.
        if (PointsAudit.ForUpdate(actor, stored, expected, occurrenceId, reason) is not { } entry)
        {
            return TransactionOutcome.Commit<OneOf<SyncOutcome, PortError>>(SyncOutcome.Unchanged);
        }

        var updated = await entries.UpdateExecutionAsync(stored, expected, PointEntrySource.Live, now, ct).ConfigureAwait(false);
        if (updated.IsT2)
        {
            return Abort<SyncOutcome>(updated.AsT2);
        }

        return updated.IsT1
            ? TransactionOutcome.Commit<OneOf<SyncOutcome, PortError>>(SyncOutcome.Unchanged)
            : await RecordAsync(entry, SyncOutcome.Updated, ct).ConfigureAwait(false);
    }

    private async Task<TransactionOutcome<OneOf<SyncOutcome, PortError>>> RecordAsync(AuditEntry entry, SyncOutcome outcome, CancellationToken ct)
    {
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.IsT1 ? Abort<SyncOutcome>(recorded.AsT1) : TransactionOutcome.Commit<OneOf<SyncOutcome, PortError>>(outcome);
    }

    // ---- the reconciliation

    public async Task<OneOf<PointsRecomputeResult, ConflictError, PortError>> RecomputeAsync(AuditActor actor, PointsRecomputeTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ran = await transactions.RunAsync(ct => ReconcileCoreAsync(actor, trigger, ct), cancellationToken).ConfigureAwait(false);
            if (!ran.TryPickT0(out var inner, out var runFailure))
            {
                return runFailure.Match<OneOf<PointsRecomputeResult, ConflictError, PortError>>(conflict => conflict, error => error);
            }

            if (!inner.TryPickT0(out var execution, out var executionFailure))
            {
                return executionFailure;
            }

            // The execution part is committed. The bonus step is a second transaction: when it fails the caller gets the failure, and the
            // execution entries, the defaulted task points and the snapshots stay as they are (a partial success, repaired by the next run).
            var bonusRan = await transactions.RunAsync(ct => BonusCoreAsync(actor, trigger, ct), cancellationToken).ConfigureAwait(false);
            if (!bonusRan.TryPickT0(out var bonusInner, out var bonusRunFailure))
            {
                return bonusRunFailure.Match<OneOf<PointsRecomputeResult, ConflictError, PortError>>(
                    conflict => conflict,
                    error => new PortError(BonusStepFailed + error.Message));
            }

            if (!bonusInner.TryPickT0(out var bonus, out var bonusFailure))
            {
                return new PortError(BonusStepFailed + bonusFailure.Message);
            }

            var skipped = new HashSet<string>(execution.SkippedIds, StringComparer.Ordinal);
            skipped.UnionWith(bonus.SkippedIds);
            return execution.Result with
            {
                Skipped = skipped.Count + execution.WithoutKey,
                BonusesCreated = bonus.Report.Created,
                BonusesRemoved = bonus.Report.Removed,
                BonusChanges = bonus.Report.Listed,
                BonusChangesTotal = bonus.Report.Total,
                BonusChangesTruncated = bonus.Report.Truncated,
                BonusStepSkipped = bonus.Skipped,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Steps 1 to 4 of <c>reconcileNow</c>: migrate the fields (tasks without points, done occurrences without a snapshot), compute the expected
    /// entry of every done occurrence and apply the differences in bulk, then the week and cycle bonuses. The stored entries are read before the occurrences, and every change is a
    /// compare-and-set on the entry that was read, so a check-off that lands meanwhile is never undone. Everything is read again when a concurrent
    /// transaction wins a write conflict and the attempt runs again.
    /// </summary>
    private async Task<TransactionOutcome<OneOf<ExecutionStep, PortError>>> ReconcileCoreAsync(AuditActor actor, PointsRecomputeTrigger trigger, CancellationToken ct)
    {
        var result = PointsRecomputeResult.Empty(trigger);
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.IsT2)
        {
            return Abort<ExecutionStep>(read.AsT2);
        }

        if (read.IsT1)
        {
            return TransactionOutcome.Commit<OneOf<ExecutionStep, PortError>>(new ExecutionStep(result, new HashSet<string>(), 0));
        }

        var zone = DayKeys.FindZone(read.AsT0.Timezone);
        var now = Now();

        var stored = await entries.FindExecutionEntriesAsync(ct).ConfigureAwait(false);
        if (stored.IsT1)
        {
            return Abort<ExecutionStep>(stored.AsT1);
        }

        var unreadable = await entries.FindUnreadableExecutionEntriesAsync(ct).ConfigureAwait(false);
        if (unreadable.IsT1)
        {
            return Abort<ExecutionStep>(unreadable.AsT1);
        }

        // Step 1: migrate the fields. Both writes filter on the missing field, so a second run matches nothing.
        var defaulted = await backfill.DefaultMissingTaskPointsAsync(ct).ConfigureAwait(false);
        if (defaulted.IsT1)
        {
            return Abort<ExecutionStep>(defaulted.AsT1);
        }

        var migrated = new HashSet<string>(defaulted.AsT0.TaskIds, StringComparer.Ordinal);
        var doneRead = await backfill.FindDoneOccurrencesAsync(ct).ConfigureAwait(false);
        if (doneRead.IsT1)
        {
            return Abort<ExecutionStep>(doneRead.AsT1);
        }

        var done = doneRead.AsT0;
        var snapshotsSet = 0;
        if (done.Any(d => d.PointsSnapshot is null))
        {
            var taskRead = await backfill.FindTaskPointValuesAsync(ct).ConfigureAwait(false);
            if (taskRead.IsT1)
            {
                return Abort<ExecutionStep>(taskRead.AsT1);
            }

            var writes = ExecutionPoints.MissingSnapshots(done, taskRead.AsT0.ToDictionary(t => t.Id, StringComparer.Ordinal), migrated);
            var written = await backfill.SetMissingSnapshotsAsync(writes, ct).ConfigureAwait(false);
            if (written.IsT1)
            {
                return Abort<ExecutionStep>(written.AsT1);
            }

            snapshotsSet = written.AsT0;
            // Continue with the values that were just written, without reading every done occurrence again.
            var byId = writes.ToDictionary(w => w.OccurrenceId, w => w.Points, StringComparer.Ordinal);
            done = [.. done.Select(d => d.PointsSnapshot is null && byId.TryGetValue(d.Id, out var points) ? d with { PointsSnapshot = points } : d)];
        }

        // Steps 2 and 3: the expected entry of every done occurrence, and the differences.
        var plan = PointsReconciliation.Plan(stored.AsT0, done, zone, new HashSet<string>(unreadable.AsT0.Keys, StringComparer.Ordinal));
        var applied = new AppliedPointEntryChanges(0, 0, 0);
        if (!plan.Changes.IsEmpty)
        {
            var writtenEntries = await entries.ApplyChangesAsync(plan.Changes, now, ct).ConfigureAwait(false);
            if (writtenEntries.IsT1)
            {
                return Abort<ExecutionStep>(writtenEntries.AsT1);
            }

            applied = writtenEntries.AsT0;
        }

        var (listed, total, truncated) = PointsReconciliation.LimitCorrections(plan.Corrections);
        result = result with
        {
            TasksDefaulted = defaulted.AsT0.Count,
            SnapshotsSet = snapshotsSet,
            Created = applied.Created,
            Updated = applied.Updated,
            Removed = applied.Removed,
            Unattributed = plan.Unattributed,
            Skipped = plan.SkippedIds.Count + unreadable.AsT0.WithoutKey,
            Corrections = listed,
            CorrectionsTotal = total,
            CorrectionsTruncated = truncated,
        };

        // One summary for the whole run; a run that changes nothing writes and audits nothing (ADR-0004).
        if (result.ChangedAnything)
        {
            var recorded = await audit.RecordAsync(PointsAudit.ForRecompute(actor, result), ct).ConfigureAwait(false);
            if (recorded.IsT1)
            {
                return Abort<ExecutionStep>(recorded.AsT1);
            }
        }

        return TransactionOutcome.Commit<OneOf<ExecutionStep, PortError>>(new ExecutionStep(result, plan.SkippedIds, unreadable.AsT0.WithoutKey));
    }

    /// <summary>What the bonus transaction did: the report, the occurrences it could not read, and whether the whole step was skipped.</summary>
    private sealed record BonusStep(BonusReport Report, IReadOnlySet<string> SkippedIds, bool Skipped);

    /// <summary>The execution transaction (steps 1 to 3): its result with the bonus fields at 0, and what the final answer needs of it.</summary>
    private sealed record ExecutionStep(PointsRecomputeResult Result, IReadOnlySet<string> SkippedIds, int WithoutKey);

    /// <summary>
    /// Step 4 of <c>reconcileNow</c> (ADR-0012), in a transaction of its own after the execution entries were committed: reads the stored bonuses and
    /// every occurrence, plans the week and cycle bonuses that must exist and applies the differences. A bonus has one writer, this
    /// reconciliation: no live sync touches it. A change writes its own summary entry (<c>step: bonuses</c>); no change writes nothing. A cycle
    /// anchor that is no Monday makes the cycle calculation undefined: the step is skipped, not failed.
    /// </summary>
    private async Task<TransactionOutcome<OneOf<BonusStep, PortError>>> BonusCoreAsync(AuditActor actor, PointsRecomputeTrigger trigger, CancellationToken ct)
    {
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.IsT2)
        {
            return Abort<BonusStep>(read.AsT2);
        }

        if (read.IsT1)
        {
            return TransactionOutcome.Commit<OneOf<BonusStep, PortError>>(new BonusStep(BonusReport.None, new HashSet<string>(), false));
        }

        var household = read.AsT0;
        if (!DayKeys.IsMonday(household.CycleAnchorDate))
        {
            return TransactionOutcome.Commit<OneOf<BonusStep, PortError>>(new BonusStep(BonusReport.None, new HashSet<string>(), true));
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var now = Now();
        // The ledger is read before the occurrences: an entry written in between is then unknown to this run.
        var storedBonuses = await entries.FindBonusEntriesAsync(ct).ConfigureAwait(false);
        if (storedBonuses.IsT1)
        {
            return Abort<BonusStep>(storedBonuses.AsT1);
        }

        var sources = await backfill.FindBonusOccurrencesAsync(ct).ConfigureAwait(false);
        if (sources.IsT1)
        {
            return Abort<BonusStep>(sources.AsT1);
        }

        var plan = PointsReconciliation.PlanBonuses(
            storedBonuses.AsT0,
            sources.AsT0,
            new BonusSettings(household.CycleAnchorDate, household.BonusSchedule ?? [], household.BonusFloor),
            zone,
            DayKeys.ToDayKey(now, zone));
        var applied = AppliedBonusChanges.None;
        if (!plan.Changes.IsEmpty)
        {
            var written = await entries.ApplyBonusChangesAsync(plan.Changes, now, ct).ConfigureAwait(false);
            if (written.IsT1)
            {
                return Abort<BonusStep>(written.AsT1);
            }

            applied = written.AsT0;
        }

        var report = PointsReconciliation.Describe(plan, applied);
        if (report.Created + report.Removed > 0)
        {
            var summary = PointsRecomputeResult.Empty(trigger) with
            {
                Skipped = plan.SkippedIds.Count,
                BonusesCreated = report.Created,
                BonusesRemoved = report.Removed,
                BonusChanges = report.Listed,
                BonusChangesTotal = report.Total,
                BonusChangesTruncated = report.Truncated,
            };
            var recorded = await audit.RecordAsync(PointsAudit.ForRecompute(actor, summary, PointsRecomputeStep.Bonuses), ct).ConfigureAwait(false);
            if (recorded.IsT1)
            {
                return Abort<BonusStep>(recorded.AsT1);
            }
        }

        return TransactionOutcome.Commit<OneOf<BonusStep, PortError>>(new BonusStep(report, plan.SkippedIds, false));
    }

    private static TransactionOutcome<OneOf<T, PortError>> Abort<T>(PortError error) =>
        TransactionOutcome.Abort<OneOf<T, PortError>>(error);

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
}
