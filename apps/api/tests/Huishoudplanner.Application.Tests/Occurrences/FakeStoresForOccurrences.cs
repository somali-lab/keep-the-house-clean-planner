using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using OneOf;

namespace Huishoudplanner.Application.Tests.Generation
{
    /// <summary>The reads and the guarded update of the occurrence actions on the in-memory occurrence store (slice 3.2).</summary>
    internal sealed partial class FakeOccurrenceStore
    {
        /// <summary>Runs once, just before the next update looks at the stored state: a concurrent writer that got there first.</summary>
        public Action? ConcurrentWriteBeforeNextUpdate { get; set; }

        public int Updates { get; private set; }

        public Task<OneOf<Occurrence, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(failure);
            }

            var found = Items.FirstOrDefault(o => o.Id == id);
            return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(found is null ? new NotFound() : found);
        }

        public Task<OneOf<IReadOnlyList<Occurrence>, PortError>> ListAsync(OccurrenceQuery query, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<IReadOnlyList<Occurrence>, PortError>>(failure);
            }

            IReadOnlyList<Occurrence> page = [.. Items
                .Where(o => o.Date >= query.From && o.Date < query.ToExclusive)
                .Where(o => query.AssigneeId is null || o.AssigneeId == query.AssigneeId)
                .Where(o => query.Status is null || o.Status == query.Status)
                .OrderBy(o => o.Date).ThenBy(o => o.TaskNameSnapshot, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal)
                .Where(o => query.After is null || After(o, query.After))
                .Take(query.Take)];
            return Task.FromResult<OneOf<IReadOnlyList<Occurrence>, PortError>>(OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(page));
        }

        private static bool After(Occurrence o, OccurrenceCursor cursor)
        {
            var byDate = o.Date.CompareTo(cursor.Date);
            if (byDate != 0)
            {
                return byDate > 0;
            }

            var byName = string.CompareOrdinal(o.TaskNameSnapshot, cursor.TaskName);
            return byName != 0 ? byName > 0 : string.CompareOrdinal(o.Id, cursor.Id) > 0;
        }

        public Task<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>> UpdateAsync(
            Occurrence before, Occurrence after, OccurrenceGuard guard, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>>(failure);
            }

            if (ConcurrentWriteBeforeNextUpdate is { } race)
            {
                ConcurrentWriteBeforeNextUpdate = null;
                race();
            }

            var index = Items.FindIndex(o => o.Id == before.Id);
            if (index < 0)
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>>(new NotFound());
            }

            var stored = Items[index];
            if (stored.Status != guard.Status || (guard.RequireUnassigned && stored.AssigneeId is not null))
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>>(new OccurrenceStateChanged());
            }

            Updates++;
            Items[index] = after with { UpdatedAt = updatedAt };
            return Task.FromResult<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>>(Items[index]);
        }

        /// <summary>Runs once, just before the next ad-hoc insert checks the request key: another request with the same key that got there first.</summary>
        public Action? ConcurrentInsertBeforeNextAdhoc { get; set; }

        private readonly List<Occurrence> committedByOthers = [];

        /// <summary>A concurrent writer committed this occurrence: it survives the rollback of the transaction that is running.</summary>
        public void CommitOther(Occurrence occurrence)
        {
            Items.Add(occurrence);
            committedByOthers.Add(occurrence);
        }

        public IReadOnlyList<Occurrence> TakeCommittedByOthers()
        {
            var taken = committedByOthers.ToList();
            committedByOthers.Clear();
            return taken;
        }

        /// <summary>Runs once, just before the next guarded delete looks at the stored state: another retract that got there first.</summary>
        public Action? ConcurrentDeleteBeforeNextRetract { get; set; }

        public int AdhocInserts { get; private set; }

        public int AdhocInsertAttempts { get; private set; }

        /// <summary>Every insert fails on the unique request key, as when the record that holds it is never visible to the use case.</summary>
        public bool KeyAlwaysTaken { get; set; }

        public Task<OneOf<Occurrence, NotFound, PortError>> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(failure);
            }

            var found = Items.FirstOrDefault(o => o.RequestId == requestId);
            return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(found is null ? new NotFound() : found);
        }

        public Task<OneOf<int, PortError>> CountOpenOfTaskOnAsync(string taskId, DateTimeOffset day, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<int, PortError>>(failure);
            }

            return Task.FromResult<OneOf<int, PortError>>(Items.Count(o => o.TaskId == taskId && o.Status == OccurrenceStatus.Open && o.Date == day));
        }

        public Task<OneOf<Occurrence, RequestKeyTaken, PortError>> InsertAdhocAsync(NewAdhocOccurrence draft, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<Occurrence, RequestKeyTaken, PortError>>(failure);
            }

            AdhocInsertAttempts++;
            if (KeyAlwaysTaken)
            {
                return Task.FromResult<OneOf<Occurrence, RequestKeyTaken, PortError>>(new RequestKeyTaken());
            }

            if (ConcurrentInsertBeforeNextAdhoc is { } race)
            {
                ConcurrentInsertBeforeNextAdhoc = null;
                race();
            }

            // The unique index on requestId: a key that is already stored cannot be inserted again.
            if (draft.RequestId is not null && Items.Any(o => o.RequestId == draft.RequestId))
            {
                return Task.FromResult<OneOf<Occurrence, RequestKeyTaken, PortError>>(new RequestKeyTaken());
            }

            AdhocInserts++;
            Writes++;
            var stored = draft.ToOccurrence(NextId());
            Items.Add(stored);
            return Task.FromResult<OneOf<Occurrence, RequestKeyTaken, PortError>>(stored);
        }

        public Task<OneOf<Occurrence, NotFound, PortError>> DeleteRecordedAsync(string id, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(failure);
            }

            if (ConcurrentDeleteBeforeNextRetract is { } race)
            {
                ConcurrentDeleteBeforeNextRetract = null;
                race();
            }

            var found = Items.FirstOrDefault(o => o.Id == id && o.Origin == OccurrenceOrigin.Adhoc && o.RecordedDone && o.Status == OccurrenceStatus.Done);
            if (found is null)
            {
                return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(new NotFound());
            }

            Items.Remove(found);
            Writes++;
            return Task.FromResult<OneOf<Occurrence, NotFound, PortError>>(found);
        }

        public Task<OneOf<LatestCompletion, PortError>> FindLatestCompletionAsync(string taskId, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<LatestCompletion, PortError>>(failure);
            }

            var latest = Items
                .Where(o => o.TaskId == taskId && o.Status == OccurrenceStatus.Done && o.CompletedAt is not null)
                .Select(o => o.CompletedAt)
                .Max();
            return Task.FromResult<OneOf<LatestCompletion, PortError>>(new LatestCompletion(latest));
        }
    }
}

namespace Huishoudplanner.Application.Tests.Tasks
{
    /// <summary>The <c>lastCompletedAt</c> write of the occurrence use cases on the in-memory task store.</summary>
    internal sealed partial class FakeTaskStore
    {
        public int LastCompletedWrites { get; private set; }

        public Task<OneOf<Success, NotFound, PortError>> SetLastCompletedAtAsync(string id, DateTimeOffset? lastCompletedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<Success, NotFound, PortError>>(failure);
            }

            var index = Items.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return Task.FromResult<OneOf<Success, NotFound, PortError>>(new NotFound());
            }

            LastCompletedWrites++;
            Items[index] = Items[index] with { LastCompletedAt = lastCompletedAt, UpdatedAt = updatedAt, Version = Items[index].Version + 1 };
            return Task.FromResult<OneOf<Success, NotFound, PortError>>(new Success());
        }
    }
}
