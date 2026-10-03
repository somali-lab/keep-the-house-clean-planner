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

        if (occurrence.HasPeriodOwner)
        {
            properties.Add(Pair("periodOwnerId", Id(occurrence.PeriodOwnerId)));
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
        return ChangeSet.Between(StoredFields(occurrence), null, Ignore).ToEntry(
            actor,
            AuditEntity.Occurrence,
            occurrence.Id,
            AuditAction.Delete,
            AuditObject.Of(("runId", runId), ("planId", new AuditObjectId(planId)), ("reason", reason)));
    }

    /// <summary>
    /// An action on an occurrence (<c>updateOccurrence</c> of the Node server): the changed fields between <paramref name="before"/> and
    /// <paramref name="after"/>, and <c>meta</c> with the action detail plus <c>occurrence</c> (task name, room name and day as they were), which
    /// the history shows without a lookup. <see langword="null"/> when nothing changed: a no-op writes and audits nothing (ADR-0004).
    /// </summary>
    public static AuditEntry? ForChange(AuditActor actor, Occurrence before, Occurrence after, AuditAction action, AuditObject? meta)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var afterFields = Fields(after);
        if (before.PointsSnapshot is not null && after.PointsSnapshot is null)
        {
            // A snapshot that is cleared reads as an explicit null, like the Node server writes it.
            afterFields = new AuditObject([.. afterFields.Properties, Pair("pointsSnapshot", AuditNull.Instance)]);
        }

        var change = ChangeSet.Between(Fields(before), afterFields);
        if (change.IsNoOp)
        {
            return null;
        }

        var context = AuditObject.Of(
            ("taskNameSnapshot", before.TaskNameSnapshot),
            ("roomNameSnapshot", before.RoomNameSnapshot is { } room ? AuditValue.FromString(room) : AuditNull.Instance),
            ("date", before.Date));
        var properties = new List<KeyValuePair<string, AuditValue>>(meta?.Properties ?? []) { Pair("occurrence", context) };
        return change.ToEntry(actor, AuditEntity.Occurrence, before.Id, action, new AuditObject(properties));
    }

    /// <summary>
    /// An ad-hoc occurrence that was created (<c>insertAdhocOccurrence</c>): one <c>create</c> entry with every final field in <c>after</c> (also
    /// <c>recordedDone</c> and <c>requestId</c>, which are <c>false</c> and <c>null</c> rather than absent) and <c>meta</c>
    /// <c>{ origin: 'adhoc', kind, recordedDone, requestId }</c>.
    /// </summary>
    public static AuditEntry ForAdhocCreated(AuditActor actor, Occurrence occurrence, AdhocKind kind)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var requestId = occurrence.RequestId is { } key ? AuditValue.FromString(key) : AuditNull.Instance;
        var fields = StoredFields(occurrence, adhoc: true);
        return ChangeSet.Between(null, fields, Ignore).ToEntry(
            actor,
            AuditEntity.Occurrence,
            occurrence.Id,
            AuditAction.Create,
            AuditObject.Of(
                ("origin", "adhoc"),
                ("kind", AdhocKindNames.ToWire(kind)),
                ("recordedDone", occurrence.RecordedDone),
                ("requestId", requestId)));
    }

    /// <summary>Recorded work that was retracted (<c>retractRecordedOccurrence</c>): a <c>delete</c> that keeps every removed field in <c>before</c>, with the reason <c>retract</c>.</summary>
    public static AuditEntry ForRetracted(AuditActor actor, Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return ChangeSet.Between(StoredFields(occurrence), null, Ignore).ToEntry(
            actor,
            AuditEntity.Occurrence,
            occurrence.Id,
            AuditAction.Delete,
            AuditObject.Of(("reason", "retract")));
    }

    /// <summary>An administrator deleted a completed occurrence (<c>deleteOccurrences</c> with the correction reason): every field in <c>before</c>.</summary>
    public static AuditEntry ForCorrectionDelete(AuditActor actor, Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return ChangeSet.Between(StoredFields(occurrence), null, Ignore).ToEntry(
            actor,
            AuditEntity.Occurrence,
            occurrence.Id,
            AuditAction.Delete,
            AuditObject.Of(("correction", "completion")));
    }

    /// <summary>
    /// The document as Node stores it: an ad-hoc document always has <c>recordedDone</c> and <c>requestId</c> (<c>false</c> and <c>null</c> when unset), a generated one
    /// has neither unless set. Used where a whole document is audited (create, removal, retract, correction delete).
    /// </summary>
    private static AuditObject StoredFields(Occurrence occurrence, bool adhoc = false)
    {
        var fields = Fields(occurrence);
        if (!adhoc && occurrence.Origin != OccurrenceOrigin.Adhoc)
        {
            return fields;
        }

        var requestId = occurrence.RequestId is { } key ? AuditValue.FromString(key) : AuditNull.Instance;
        return new AuditObject([.. fields.Properties, Pair("recordedDone", occurrence.RecordedDone), Pair("requestId", requestId)]);
    }

    private static AuditValue Id(string? id) => id is null ? AuditNull.Instance : new AuditObjectId(id);

    private static KeyValuePair<string, AuditValue> Pair(string key, AuditValue value) => new(key, value);
}
