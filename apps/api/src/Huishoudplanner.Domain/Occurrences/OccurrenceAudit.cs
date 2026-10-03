using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Occurrences;

/// <summary>
/// How an occurrence appears in the audit log, as the Node server writes it (<c>data/occurrences.ts</c>): the stored fields without id
/// and timestamps (a null is kept, an optional field that is not set is absent), so a creation lists every field in <c>after</c> and a
/// removal every field in <c>before</c>. Slice 3.2 adds the per-action entries on top of <see cref="Fields"/>.
/// </summary>
public static class OccurrenceAudit
{
    /// <summary>The fields Node ignores when it diffs an occurrence: <c>createdAt</c> and <c>updatedAt</c> (the id is never part of the fields).</summary>
    public static IReadOnlyList<string> Ignore { get; } = ["createdAt", "updatedAt"];

    public static AuditObject Fields(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var properties = new List<KeyValuePair<string, AuditValue>>
        {
            Pair("taskId", Id(occurrence.TaskId)),
            Pair("cycleId", Id(occurrence.CycleId)),
            Pair("planId", Id(occurrence.PlanId)),
            Pair("date", occurrence.Date),
            Pair("plannedDate", occurrence.PlannedDate),
            Pair("assigneeId", Id(occurrence.AssigneeId)),
            Pair("status", OccurrenceNames.ToWire(occurrence.Status)),
            Pair("statusBeforeCompletion", occurrence.StatusBeforeCompletion is { } before ? AuditValue.FromString(OccurrenceNames.ToWire(before)) : AuditNull.Instance),
            Pair("completedAt", occurrence.CompletedAt is { } completedAt ? AuditValue.FromInstant(completedAt) : AuditNull.Instance),
            Pair("completedBy", Id(occurrence.CompletedBy)),
            Pair("skipReason", occurrence.SkipReason is { } reason ? AuditValue.FromString(reason) : AuditNull.Instance),
            Pair("durationMinutesSnapshot", occurrence.DurationMinutesSnapshot),
            Pair("taskNameSnapshot", occurrence.TaskNameSnapshot),
            Pair("roomIdSnapshot", Id(occurrence.RoomIdSnapshot)),
            Pair("roomNameSnapshot", occurrence.RoomNameSnapshot is { } room ? AuditValue.FromString(room) : AuditNull.Instance),
            Pair("origin", OccurrenceNames.ToWire(occurrence.Origin)),
        };
        if (occurrence.RecordedDone)
        {
            properties.Add(Pair("recordedDone", true));
        }

        if (occurrence.RequestId is { } requestId)
        {
            properties.Add(Pair("requestId", requestId));
        }

        if (occurrence.PointsSnapshot is { } snapshot)
        {
            properties.Add(Pair("pointsSnapshot", snapshot));
        }

        if (occurrence.PointsOverride is { } points)
        {
            properties.Add(Pair("pointsOverride", points));
        }

        if (occurrence.PeriodOwnerId is { } owner)
        {
            properties.Add(Pair("periodOwnerId", new AuditObjectId(owner)));
        }

        return new AuditObject(properties);
    }

    /// <summary>A generated occurrence that a generation run inserted; <c>meta</c> names the run and the cycle index.</summary>
    public static AuditEntry ForGenerated(AuditActor actor, Occurrence occurrence, string runId, int cycleIndex)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return ChangeSet.Between(null, Fields(occurrence), Ignore).ToEntry(
            actor,
            AuditEntity.Occurrence,
            occurrence.Id,
            AuditAction.Create,
            AuditObject.Of(("runId", runId), ("cycleIndex", cycleIndex)));
    }

    /// <summary>An occurrence that a plan change or the reconciliation removed to regenerate it; <c>meta</c> names the run, the plan and the reason.</summary>
    public static AuditEntry ForRemoved(AuditActor actor, Occurrence occurrence, string runId, string planId, string reason)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return ChangeSet.Between(Fields(occurrence), null, Ignore).ToEntry(
            actor,
            AuditEntity.Occurrence,
            occurrence.Id,
            AuditAction.Delete,
            AuditObject.Of(("runId", runId), ("planId", new AuditObjectId(planId)), ("reason", reason)));
    }

    private static AuditValue Id(string? id) => id is null ? AuditNull.Instance : new AuditObjectId(id);

    private static KeyValuePair<string, AuditValue> Pair(string key, AuditValue value) => new(key, value);
}
