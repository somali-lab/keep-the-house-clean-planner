using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Application.Tests.Generation;

/// <summary>An in-memory cycle store (index order) that counts writes and can fail.</summary>
internal sealed class FakeCycleStore : ForStoringCycles
{
    private int counter;

    public List<Cycle> Items { get; set; } = [];

    public int Writes { get; private set; }

    public PortError? Failure { get; set; }

    public string NextId() => (0xc00000 + (++counter)).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    public Task<OneOf<IReadOnlyList<Cycle>, PortError>> ListAsync(CycleCursor? after, int take, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Cycle>, PortError>>(failure);
        }

        IReadOnlyList<Cycle> page = [.. Items.OrderBy(c => c.Index).Where(c => after is null || c.Index > after.Index).Take(take)];
        return Task.FromResult<OneOf<IReadOnlyList<Cycle>, PortError>>(OneOf<IReadOnlyList<Cycle>, PortError>.FromT0(page));
    }

    public Task<OneOf<Cycle, NotFound, PortError>> FindByIndexAsync(int index, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Cycle, NotFound, PortError>>(failure);
        }

        var cycle = Items.FirstOrDefault(c => c.Index == index);
        return Task.FromResult<OneOf<Cycle, NotFound, PortError>>(cycle is null ? new NotFound() : cycle);
    }

    public Task<OneOf<Cycle, PortError>> InsertAsync(NewCycle cycle, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Cycle, PortError>>(failure);
        }

        Writes++;
        var stored = new Cycle(NextId(), cycle.Index, cycle.StartDate, cycle.EndDate, cycle.PlanId, cycle.GeneratedAt, cycle.GenerationRunId);
        Items.Add(stored);
        return Task.FromResult<OneOf<Cycle, PortError>>(stored);
    }

    public Task<OneOf<Cycle, NotFound, PortError>> UpdateBoundsAsync(string id, DateOnly startDate, DateOnly endDate, DateTimeOffset generatedAt, string runId, CancellationToken cancellationToken) =>
        Replace(id, c => c with { StartDate = startDate, EndDate = endDate, GeneratedAt = generatedAt, GenerationRunId = runId });

    public Task<OneOf<Cycle, NotFound, PortError>> UpdatePlanAsync(string id, string planId, DateTimeOffset generatedAt, string runId, CancellationToken cancellationToken) =>
        Replace(id, c => c with { PlanId = planId, GeneratedAt = generatedAt, GenerationRunId = runId });

    private Task<OneOf<Cycle, NotFound, PortError>> Replace(string id, Func<Cycle, Cycle> change)
    {
        var index = Items.FindIndex(c => c.Id == id);
        if (index < 0)
        {
            return Task.FromResult<OneOf<Cycle, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        Items[index] = change(Items[index]);
        return Task.FromResult<OneOf<Cycle, NotFound, PortError>>(Items[index]);
    }
}

/// <summary>
/// An in-memory occurrence store with the semantics of the unique slot index: a generated draft is skipped when a generated occurrence of
/// the same cycle, task and day exists, an ad-hoc occurrence never occupies a slot.
/// </summary>
internal sealed partial class FakeOccurrenceStore : ForStoringOccurrences
{
    private int counter;

    public List<Occurrence> Items { get; set; } = [];

    public int Writes { get; private set; }

    public int RoomSnapshotWrites { get; private set; }

    public PortError? Failure { get; set; }

    public string NextId() => (0xd00000 + (++counter)).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    public Task<OneOf<IReadOnlyList<Occurrence>, PortError>> InsertGeneratedAsync(IReadOnlyList<NewGeneratedOccurrence> drafts, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Occurrence>, PortError>>(failure);
        }

        var inserted = new List<Occurrence>();
        foreach (var draft in drafts)
        {
            var taken = Items.Any(o =>
                o.Origin == OccurrenceOrigin.Generated && o.CycleId == draft.CycleId && o.TaskId == draft.TaskId && o.PlannedDate == draft.Date);
            if (taken)
            {
                continue;
            }

            var stored = draft.ToOccurrence(NextId());
            Items.Add(stored);
            inserted.Add(stored);
            Writes++;
        }

        return Task.FromResult<OneOf<IReadOnlyList<Occurrence>, PortError>>(OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(inserted));
    }

    public Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindGeneratedPlannedBetweenAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken) =>
        Query(o => o.Origin == OccurrenceOrigin.Generated && o.PlannedDate >= rangeStart && o.PlannedDate < rangeEnd);

    public Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindReplaceableBetweenAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken) =>
        Query(o => OccurrenceReconciliation.IsReplaceable(o, rangeStart) && o.Date < rangeEnd);

    private Task<OneOf<IReadOnlyList<Occurrence>, PortError>> Query(Func<Occurrence, bool> match)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Occurrence>, PortError>>(failure);
        }

        IReadOnlyList<Occurrence> found = [.. Items.Where(match).OrderBy(o => o.Date).ThenBy(o => o.TaskNameSnapshot, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal)];
        return Task.FromResult<OneOf<IReadOnlyList<Occurrence>, PortError>>(OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(found));
    }

    public Task<OneOf<int, PortError>> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<int, PortError>>(failure);
        }

        var removed = Items.RemoveAll(o => ids.Contains(o.Id));
        Writes += removed;
        return Task.FromResult<OneOf<int, PortError>>(removed);
    }

    public Task<OneOf<int, PortError>> UpdateUpcomingRoomSnapshotsAsync(string taskId, DateTimeOffset from, string roomId, string roomName, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<int, PortError>>(failure);
        }

        var changed = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            var occurrence = Items[i];
            if (occurrence.TaskId == taskId && occurrence.Status == OccurrenceStatus.Open && occurrence.Date >= from)
            {
                Items[i] = occurrence with { RoomIdSnapshot = roomId, RoomNameSnapshot = roomName };
                changed++;
            }
        }

        RoomSnapshotWrites += changed;
        return Task.FromResult<OneOf<int, PortError>>(changed);
    }
}
