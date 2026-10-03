using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Tasks;

/// <summary>
/// How a task appears in the audit log, as the Node server writes it (<c>createTask</c>, <c>updateTask</c> and
/// <c>recordTaskChange</c> of <c>data/tasks.ts</c>): the stored fields without id and timestamps, and a change of the default
/// assignee as its own <c>assign</c> entry next to the <c>update</c> entry of the other fields.
/// </summary>
public static class TaskAudit
{
    public static AuditObject Fields(HouseholdTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return AuditObject.Of(
            ("name", task.Name),
            ("roomId", new AuditObjectId(task.RoomId)),
            ("intervalKey", task.IntervalKey),
            ("durationMinutes", task.DurationMinutes),
            ("points", task.Points),
            ("defaultAssigneeId", task.DefaultAssigneeId is { } assignee ? new AuditObjectId(assignee) : AuditNull.Instance),
            ("notes", task.Notes),
            ("tags", new AuditArray([.. task.Tags.Select(AuditValue.FromString)])),
            ("active", task.Active),
            ("lastCompletedAt", task.LastCompletedAt is { } at ? AuditValue.FromInstant(at) : AuditNull.Instance));
    }

    /// <summary>The entry of a creation: every field as it became.</summary>
    public static AuditEntry ForCreate(AuditActor actor, HouseholdTask task) =>
        ChangeSet.Between(null, Fields(task)).ToEntry(actor, AuditEntity.Task, task.Id, AuditAction.Create);

    /// <summary>
    /// The entries of a change that is not a no-op: an <c>update</c> for the changed fields other than the default assignee (when there
    /// are any), then an <c>assign</c> for the default assignee (when it changed).
    /// </summary>
    public static IReadOnlyList<AuditEntry> ForChange(AuditActor actor, string taskId, ChangeSet change)
    {
        ArgumentNullException.ThrowIfNull(change);
        const string assignee = "defaultAssigneeId";
        var entries = new List<AuditEntry>();
        var rest = new FieldDiff(Without(change.Diff.Before, assignee), Without(change.Diff.After, assignee));
        if (!rest.IsEmpty)
        {
            entries.Add(AuditEntry.For(actor, AuditEntity.Task, taskId, AuditAction.Update, rest));
        }

        if (change.Diff.Before[assignee] is not null || change.Diff.After[assignee] is not null)
        {
            var diff = new FieldDiff(
                AuditObject.Of((assignee, change.Diff.Before[assignee] ?? AuditNull.Instance)),
                AuditObject.Of((assignee, change.Diff.After[assignee] ?? AuditNull.Instance)));
            entries.Add(AuditEntry.For(actor, AuditEntity.Task, taskId, AuditAction.Assign, diff));
        }

        return entries;
    }

    private static AuditObject Without(AuditObject source, string key) =>
        new(source.Properties.Where(p => p.Key != key));
}
