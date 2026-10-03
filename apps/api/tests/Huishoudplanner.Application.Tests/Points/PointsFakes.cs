using Huishoudplanner.Application.Points;
using Huishoudplanner.Application.Tests.Generation;
using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Application.Tests.Rooms;
using Huishoudplanner.Application.Tests.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>Records the calls of the occurrence use cases to the ledger; can fail on demand.</summary>
internal sealed class FakeExecutionPoints : IExecutionPointsService
{
    public List<(string OccurrenceId, PointsSyncReason Reason, string ActorId)> Calls { get; } = [];

    public PortError? Failure { get; set; }

    public Task<OneOf<SyncOutcome, PortError>> SyncAsync(AuditActor actor, string occurrenceId, PointsSyncReason reason, CancellationToken cancellationToken)
    {
        Calls.Add((occurrenceId, reason, actor.ActorId));
        return Task.FromResult<OneOf<SyncOutcome, PortError>>(Failure is { } failure ? failure : SyncOutcome.Unchanged);
    }
}

/// <summary>An in-memory ledger with the semantics of the Mongo store: the unique key, the compare-and-set of a reconciliation, the list order.</summary>
internal sealed class FakePointEntryStore : ForStoringPointEntries
{
    private int counter;

    public List<PointEntry> Items { get; set; } = [];

    public int Writes { get; private set; }

    public int BulkWrites { get; private set; }

    public PortError? Failure { get; set; }

    public PortError? WriteFailure { get; set; }

    /// <summary>Fails only the bonus read and write, so the execution part of a run can commit.</summary>
    public PortError? BonusFailure { get; set; }

    /// <summary>Runs once, just before the next bulk write looks at the stored entries: a live sync that landed after the reconciliation read.</summary>
    public Action? ConcurrentWriteBeforeNextBulk { get; set; }

    public string NextId() => (0xe00000 + (++counter)).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    public Task<OneOf<PointEntry, NotFound, PortError>> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(failure);
        }

        var found = Items.FirstOrDefault(e => e.Key == key);
        return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(found is null ? new NotFound() : found);
    }

    public Task<OneOf<IReadOnlyList<PointEntry>, PortError>> FindExecutionEntriesAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<PointEntry>, PortError>>(failure);
        }

        return Task.FromResult(OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0([.. Items.Where(e => e.Kind == PointEntryKind.Execution)]));
    }

    /// <summary>Execution entries the store cannot map: not part of <see cref="Items"/>, never touched by the fake.</summary>
    public List<string> UnreadableKeys { get; } = [];

    public int UnreadableWithoutKey { get; set; }

    public Task<OneOf<UnreadableEntries, PortError>> FindUnreadableExecutionEntriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OneOf<UnreadableEntries, PortError>.FromT0(new UnreadableEntries([.. UnreadableKeys], UnreadableWithoutKey)));

    public Task<OneOf<PointEntry, PortError>> InsertExecutionAsync(string key, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if ((WriteFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, PortError>>(failure);
        }

        if (Items.Any(e => e.Key == key))
        {
            return Task.FromResult<OneOf<PointEntry, PortError>>(new PortError("pointEntries.failed: duplicate key"));
        }

        Writes++;
        var entry = Make(NextId(), key, fields, source, at, at);
        Items.Add(entry);
        return Task.FromResult<OneOf<PointEntry, PortError>>(entry);
    }

    public Task<OneOf<PointEntry, NotFound, PortError>> UpdateExecutionAsync(PointEntry current, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if ((WriteFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(failure);
        }

        var index = Items.FindIndex(e => e.Id == current.Id);
        if (index < 0)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        Items[index] = Make(current.Id, current.Key, fields, source, current.CreatedAt, at);
        return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(Items[index]);
    }

    public Task<OneOf<bool, PortError>> DeleteAsync(PointEntry current, CancellationToken cancellationToken)
    {
        if ((WriteFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<bool, PortError>>(failure);
        }

        var removed = Items.RemoveAll(e => e.Id == current.Id) > 0;
        if (removed)
        {
            Writes++;
        }

        return Task.FromResult<OneOf<bool, PortError>>(removed);
    }

    public Task<OneOf<AppliedPointEntryChanges, PortError>> ApplyChangesAsync(PointEntryChanges changes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if ((WriteFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<AppliedPointEntryChanges, PortError>>(failure);
        }

        if (ConcurrentWriteBeforeNextBulk is { } race)
        {
            ConcurrentWriteBeforeNextBulk = null;
            race();
        }

        BulkWrites++;
        int created = 0, updated = 0, removed = 0;
        foreach (var insert in changes.Inserts)
        {
            Items.Add(Make(NextId(), insert.Key, insert.Fields, PointEntrySource.Backfill, at, at));
            created++;
        }

        foreach (var update in changes.Updates)
        {
            var index = Items.FindIndex(e => SameAsRead(e, update.Current));
            if (index >= 0)
            {
                Items[index] = Make(update.Current.Id, update.Current.Key, update.Fields, PointEntrySource.Recompute, update.Current.CreatedAt, at);
                updated++;
            }
        }

        foreach (var delete in changes.Deletes)
        {
            if (Items.RemoveAll(e => SameAsRead(e, delete)) > 0)
            {
                removed++;
            }
        }

        Writes += created + updated + removed;
        return Task.FromResult<OneOf<AppliedPointEntryChanges, PortError>>(new AppliedPointEntryChanges(created, updated, removed));
    }

    public Task<OneOf<IReadOnlyList<PointEntry>, PortError>> FindBonusEntriesAsync(CancellationToken cancellationToken)
    {
        if ((BonusFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<PointEntry>, PortError>>(failure);
        }

        return Task.FromResult(OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0([.. Items.Where(e => e.Kind is PointEntryKind.BonusWeekDone or PointEntryKind.BonusWeekOnTime or PointEntryKind.BonusCycleDone or PointEntryKind.BonusCycleOnTime)]));
    }

    /// <summary>Runs once, just before the next bonus write: another writer changed an entry after the reconciliation read it.</summary>
    public Action? ConcurrentWriteBeforeNextBonusWrite { get; set; }

    public int BonusBulkWrites { get; private set; }

    public Task<OneOf<AppliedBonusChanges, PortError>> ApplyBonusChangesAsync(BonusEntryChanges changes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if ((BonusFailure ?? WriteFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<AppliedBonusChanges, PortError>>(failure);
        }

        if (ConcurrentWriteBeforeNextBonusWrite is { } race)
        {
            ConcurrentWriteBeforeNextBonusWrite = null;
            race();
        }

        BonusBulkWrites++;
        var removed = new List<string>();
        foreach (var delete in changes.Deletes)
        {
            if (Items.RemoveAll(e => SameAsRead(e, delete)) > 0)
            {
                removed.Add(delete.Key);
            }
        }

        var created = new List<string>();
        foreach (var insert in changes.Inserts)
        {
            if (Items.Any(e => e.Key == insert.Key))
            {
                continue;
            }

            Items.Add(new PointEntry(NextId(), insert.Key, insert.Kind, insert.PersonId, insert.Amount, insert.Date, insert.WeekStart, insert.PeriodStart, null, null, string.Empty, PointEntrySource.Recompute, null, null, null, at, at));
            created.Add(insert.Key);
        }

        Writes += created.Count + removed.Count;
        return Task.FromResult<OneOf<AppliedBonusChanges, PortError>>(new AppliedBonusChanges(created, removed));
    }

    public Task<OneOf<IReadOnlyList<PointTotal>, PortError>> SumByPersonAsync(DateTimeOffset? from, DateTimeOffset? toExclusive, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<PointTotal>, PortError>>(failure);
        }

        var totals = Items
            .Where(e => (from is null || e.Date >= from) && (toExclusive is null || e.Date < toExclusive))
            .GroupBy(e => e.PersonId)
            .Select(g => new PointTotal(
                g.Key,
                g.Sum(e => (long)e.Amount),
                g.Count(e => e.Kind == PointEntryKind.Execution),
                g.Where(e => e.Kind is PointEntryKind.BonusWeekDone or PointEntryKind.BonusWeekOnTime or PointEntryKind.BonusCycleDone or PointEntryKind.BonusCycleOnTime).Sum(e => (long)e.Amount),
                g.Where(e => e.Kind == PointEntryKind.Redemption).Sum(e => -(long)e.Amount)))
            .ToList();
        return Task.FromResult(OneOf<IReadOnlyList<PointTotal>, PortError>.FromT0(totals));
    }

    public Task<OneOf<IReadOnlyList<PointEntry>, PortError>> ListAsync(PointEntryQuery query, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<PointEntry>, PortError>>(failure);
        }

        var page = Items
            .Where(e => e.PersonId == query.PersonId && e.Date >= query.From && e.Date < query.ToExclusive)
            .OrderByDescending(e => e.Date).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Where(e => query.After is null || e.Date < query.After.Date || (e.Date == query.After.Date && string.CompareOrdinal(e.Id, query.After.Id) > 0))
            .Take(query.Take)
            .ToList();
        return Task.FromResult(OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0(page));
    }

    private static bool SameAsRead(PointEntry stored, PointEntry read) =>
        stored.Id == read.Id && stored.PersonId == read.PersonId && stored.Amount == read.Amount && stored.Date == read.Date;

    public static PointEntry Make(string id, string key, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset createdAt, DateTimeOffset updatedAt) => new(
        id, key, PointEntryKind.Execution, fields.PersonId, fields.Amount, fields.Date, fields.WeekStart, null, fields.OccurrenceId, fields.TaskId,
        fields.TitleSnapshot, source, null, null, null, createdAt, updatedAt);
}

/// <summary>
/// The field backfill on the in-memory occurrence store: done occurrences, the task values (settable, because a task from before points has none)
/// and the two filtered writes.
/// </summary>
internal sealed class FakeBackfill(FakeOccurrenceStore occurrences) : ForBackfillingPoints
{
    public List<TaskPointValue> Tasks { get; } = [];

    /// <summary>Occurrences whose date reads as missing, like an old row with no date.</summary>
    public HashSet<string> UnreadableIds { get; } = [];

    public PortError? Failure { get; set; }

    public int SnapshotWrites { get; private set; }

    public int TaskWrites { get; private set; }

    /// <summary>Awaited at the start of the done-occurrence read: holds a run inside its transaction until a test lets it go.</summary>
    public Func<Task>? Pause { get; set; }

    public async Task<OneOf<IReadOnlyList<ExecutionSource>, PortError>> FindDoneOccurrencesAsync(CancellationToken cancellationToken)
    {
        if (Pause is { } pause)
        {
            await pause();
        }

        if (Failure is { } failure)
        {
            return failure;
        }

        var done = occurrences.Items
            .Where(o => o.Status == OccurrenceStatus.Done)
            .Select(o => ExecutionSource.From(o) with { Date = UnreadableIds.Contains(o.Id) ? null : o.Date })
            .ToList();
        return OneOf<IReadOnlyList<ExecutionSource>, PortError>.FromT0(done);
    }

    /// <summary>Occurrences whose status, plan day or completion cannot be read as such, for the bonus step.</summary>
    public HashSet<string> UnreadableBonusIds { get; } = [];

    public Task<OneOf<IReadOnlyList<BonusSource>, PortError>> FindBonusOccurrencesAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<BonusSource>, PortError>>(failure);
        }

        var sources = occurrences.Items.Select(o => new BonusSource(
            o.Id,
            UnreadableBonusIds.Contains(o.Id) ? null : o.Status switch
            {
                OccurrenceStatus.Done => Huishoudplanner.Domain.Bonuses.OccurrenceStatus.Done,
                OccurrenceStatus.Skipped => Huishoudplanner.Domain.Bonuses.OccurrenceStatus.Skipped,
                _ => Huishoudplanner.Domain.Bonuses.OccurrenceStatus.Open,
            },
            o.PlannedDate,
            o.Date,
            o.RecordedDone,
            o.AssigneeId,
            o.HasPeriodOwner,
            o.PeriodOwnerId,
            o.CompletedBy,
            o.CompletedAt)).ToList();
        return Task.FromResult(OneOf<IReadOnlyList<BonusSource>, PortError>.FromT0(sources));
    }

    public Task<OneOf<IReadOnlyList<TaskPointValue>, PortError>> FindTaskPointValuesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OneOf<IReadOnlyList<TaskPointValue>, PortError>.FromT0([.. Tasks]));

    public Task<OneOf<DefaultedTasks, PortError>> DefaultMissingTaskPointsAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<DefaultedTasks, PortError>>(failure);
        }

        var missing = Tasks.Where(t => t.Points is null).ToList();
        foreach (var task in missing)
        {
            Tasks[Tasks.IndexOf(task)] = task with { Points = Huishoudplanner.Domain.Limits.TaskPoints.DefaultForDuration(task.DurationMinutes) };
            TaskWrites++;
        }

        return Task.FromResult<OneOf<DefaultedTasks, PortError>>(new DefaultedTasks([.. missing.Select(t => t.Id)], missing.Count));
    }

    public Task<OneOf<int, PortError>> SetMissingSnapshotsAsync(IReadOnlyList<SnapshotWrite> snapshots, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<int, PortError>>(failure);
        }

        var written = 0;
        foreach (var snapshot in snapshots)
        {
            var index = occurrences.Items.FindIndex(o => o.Id == snapshot.OccurrenceId && o.Status == OccurrenceStatus.Done && o.PointsSnapshot is null);
            if (index >= 0)
            {
                occurrences.Items[index] = occurrences.Items[index] with { PointsSnapshot = snapshot.Points };
                written++;
                SnapshotWrites++;
            }
        }

        return Task.FromResult<OneOf<int, PortError>>(written);
    }
}

/// <summary>Runs the work once; an aborted run restores the ledger, the occurrences, the tasks and the audit log, like a rolled back transaction.</summary>
internal sealed class PointsTransactions(FakePointEntryStore ledger, FakeOccurrenceStore occurrences, FakeBackfill backfill, FakeAudit audit) : ForRunningTransactions
{
    public int Runs { get; private set; }

    public int Aborts { get; private set; }

    public ConflictError? ConflictInsteadOfRunning { get; set; }

    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        if (ConflictInsteadOfRunning is { } conflict)
        {
            return conflict;
        }

        Runs++;
        var ledgerBefore = ledger.Items.ToList();
        var occurrencesBefore = occurrences.Items.ToList();
        var tasksBefore = backfill.Tasks.ToList();
        var auditBefore = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            ledger.Items = ledgerBefore;
            occurrences.Items = occurrencesBefore;
            backfill.Tasks.Clear();
            backfill.Tasks.AddRange(tasksBefore);
            audit.Entries = auditBefore;
        }

        return outcome.Value;
    }
}

/// <summary>
/// The real <see cref="PointsService"/> on in-memory ports, next to the occurrence world (same occurrences, tasks, people, settings, audit log and
/// clock: Wednesday 2026-09-16 10:00 Amsterdam, cycle 0 from Monday 2026-09-14).
/// </summary>
internal sealed class PointsWorld
{
    public OccurrenceWorld Occ { get; } = new();

    public FakePointEntryStore Ledger { get; } = new();

    public FakeBackfill Backfill { get; }

    public PointsTransactions Transactions { get; }

    public ReconcileGate Gate { get; } = new();

    public PointsService Service { get; }

    public PointsWorld()
    {
        Backfill = new FakeBackfill(Occ.Occurrences);
        Transactions = new PointsTransactions(Ledger, Occ.Occurrences, Backfill, Occ.Audit);
        Service = new PointsService(
            Ledger,
            Backfill,
            Occ.SettingsStore,
            new Users.FakeUserStore(Occ.People),
            Occ.Occurrences,
            Transactions,
            Occ.Audit,
            Occ.Clock,
            Gate);
        foreach (var task in Occ.TaskStore.Items)
        {
            Backfill.Tasks.Add(new TaskPointValue(task.Id, task.Points, task.DurationMinutes));
        }
    }

    public static AuditActor Actor(Huishoudplanner.Domain.Users.User user) => AuditActor.From(OccurrenceWorld.Actor(user));

    /// <summary>A done occurrence as the occurrence use cases leave it, with its snapshot.</summary>
    public Occurrence Done(Huishoudplanner.Domain.Tasks.HouseholdTask task, string day, Huishoudplanner.Domain.Users.User? assignee, Huishoudplanner.Domain.Users.User? completedBy, int? snapshot = null) =>
        Occ.Seed(task, day, assignee, OccurrenceStatus.Done, o => o with
        {
            StatusBeforeCompletion = OccurrenceStatus.Open,
            CompletedAt = OccurrenceWorld.At(day).AddHours(10),
            CompletedBy = completedBy?.Id,
            PointsSnapshot = snapshot ?? task.Points,
        });

    public IEnumerable<AuditEntry> PointsAudit(AuditAction? action = null) => Occ.Entries(AuditEntity.Points, action);
}
