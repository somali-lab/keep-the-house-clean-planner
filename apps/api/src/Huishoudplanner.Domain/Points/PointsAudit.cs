using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Points;

/// <summary>
/// How the ledger appears in the audit log, as the Node server writes it (<c>data/points.ts</c> and <c>domain/points.ts</c>): a live change of an
/// execution entry is its own <c>points</c> entry with <c>meta: { occurrenceId, reason }</c>; a reconciliation records one summary entry
/// (<c>recompute</c>, the fixed ledger id) and none per entry (ADR-0011, requirements 4.9).
/// </summary>
public static class PointsAudit
{
    /// <summary>The fixed id of the ledger as a whole, the entity id of a reconciliation summary (like the settings id).</summary>
    public const string LedgerId = "000000000000000000000002";

    /// <summary>The fields of an entry as an audit diff sees them: everything but the id, the timestamps and the request key.</summary>
    public static AuditObject Fields(PointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var properties = new List<KeyValuePair<string, AuditValue>>
        {
            Pair("key", entry.Key),
            Pair("kind", PointNames.ToWire(entry.Kind)),
            Pair("personId", new AuditObjectId(entry.PersonId)),
            Pair("amount", entry.Amount),
            Pair("date", entry.Date),
            Pair("weekStart", entry.WeekStart),
            Pair("periodStart", entry.PeriodStart is { } period ? AuditValue.FromInstant(period) : AuditNull.Instance),
            Pair("occurrenceId", Id(entry.OccurrenceId)),
            Pair("taskId", Id(entry.TaskId)),
            Pair("titleSnapshot", entry.TitleSnapshot),
            Pair("source", PointNames.ToWire(entry.Source)),
        };
        if (entry.Note is { } note)
        {
            properties.Add(Pair("note", note));
        }

        if (entry.CentsPerPointSnapshot is { } cents)
        {
            properties.Add(Pair("centsPerPointSnapshot", cents));
        }

        if (entry.CurrencyCodeSnapshot is { } currency)
        {
            properties.Add(Pair("currencyCodeSnapshot", currency));
        }

        return new AuditObject(properties);
    }

    /// <summary>A new execution entry: every field in <c>after</c>.</summary>
    public static AuditEntry ForCreate(AuditActor actor, PointEntry entry, string occurrenceId, PointsSyncReason reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ChangeSet.Between(null, Fields(entry), Ignore).ToEntry(actor, AuditEntity.Points, entry.Id, AuditAction.Create, Meta(occurrenceId, reason));
    }

    /// <summary>A removed execution entry: every field in <c>before</c>.</summary>
    public static AuditEntry ForDelete(AuditActor actor, PointEntry entry, string occurrenceId, PointsSyncReason reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ChangeSet.Between(Fields(entry), null, Ignore).ToEntry(actor, AuditEntity.Points, entry.Id, AuditAction.Delete, Meta(occurrenceId, reason));
    }

    /// <summary>
    /// An execution entry brought to <paramref name="fields"/>: the changed fields only, with the title and the amount in the <c>meta</c> for the
    /// history feed (the diff lists changed fields only). <see langword="null"/> when nothing changed: a no-op writes and audits nothing.
    /// </summary>
    public static AuditEntry? ForUpdate(AuditActor actor, PointEntry current, ExecutionEntryFields fields, string occurrenceId, PointsSyncReason reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fields);
        var change = ChangeSet.Between(Derived(current), Derived(fields), Ignore);
        if (change.IsNoOp)
        {
            return null;
        }

        var meta = new AuditObject([.. Meta(occurrenceId, reason).Properties, Pair("titleSnapshot", fields.TitleSnapshot), Pair("amount", fields.Amount)]);
        return change.ToEntry(actor, AuditEntity.Points, current.Id, AuditAction.Update, meta);
    }

    /// <summary>The one summary entry of a reconciliation that changed something (requirements 4.9): the result as <c>meta</c>.</summary>
    public static AuditEntry ForRecompute(AuditActor actor, PointsRecomputeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var meta = AuditObject.Of(
            ("trigger", PointNames.ToWire(result.Trigger)),
            ("tasksDefaulted", result.TasksDefaulted),
            ("snapshotsSet", result.SnapshotsSet),
            ("created", result.Created),
            ("updated", result.Updated),
            ("removed", result.Removed),
            ("unattributed", result.Unattributed),
            ("skipped", result.Skipped),
            ("corrections", new AuditArray([.. result.Corrections.Select(Correction)])),
            ("correctionsTotal", result.CorrectionsTotal),
            ("correctionsTruncated", result.CorrectionsTruncated),
            ("bonusesCreated", result.BonusesCreated),
            ("bonusesRemoved", result.BonusesRemoved),
            ("bonusChanges", new AuditArray([.. result.BonusChanges.Select(BonusChange)])),
            ("bonusChangesTotal", result.BonusChangesTotal),
            ("bonusChangesTruncated", result.BonusChangesTruncated));
        return new AuditEntry(actor, AuditEntity.Points, LedgerId, AuditAction.Recompute, AuditObject.Empty, AuditObject.Empty, meta);
    }

    private static IReadOnlyList<string> Ignore { get; } = ["updatedAt", "createdAt"];

    /// <summary>The seven fields a sync derives from an occurrence; the bookkeeping (source, period) never shows in an update.</summary>
    private static AuditObject Derived(PointEntry entry) => Derived(
        entry.PersonId, entry.Amount, entry.Date, entry.WeekStart, entry.OccurrenceId, entry.TaskId, entry.TitleSnapshot);

    private static AuditObject Derived(ExecutionEntryFields fields) => Derived(
        fields.PersonId, fields.Amount, fields.Date, fields.WeekStart, fields.OccurrenceId, fields.TaskId, fields.TitleSnapshot);

    private static AuditObject Derived(string personId, int amount, DateTimeOffset date, DateTimeOffset weekStart, string? occurrenceId, string? taskId, string title) =>
        AuditObject.Of(
            ("personId", new AuditObjectId(personId)),
            ("amount", amount),
            ("date", date),
            ("weekStart", weekStart),
            ("occurrenceId", Id(occurrenceId)),
            ("taskId", Id(taskId)),
            ("titleSnapshot", title));

    private static AuditObject Meta(string occurrenceId, PointsSyncReason reason) =>
        AuditObject.Of(("occurrenceId", new AuditObjectId(occurrenceId)), ("reason", PointNames.ToWire(reason)));

    private static AuditObject Correction(PointsCorrection correction) =>
        AuditObject.Of(
            ("key", correction.Key),
            ("from", Holding(correction.From)),
            ("to", correction.To is { } to ? Holding(to) : AuditNull.Instance));

    private static AuditObject Holding(PointsHolding holding) =>
        AuditObject.Of(("personId", new AuditObjectId(holding.PersonId)), ("amount", holding.Amount));

    private static AuditObject BonusChange(PointsBonusChange change) =>
        AuditObject.Of(
            ("key", change.Key),
            ("personId", new AuditObjectId(change.PersonId)),
            ("amount", change.Amount),
            ("change", change.Change));

    private static AuditValue Id(string? id) => id is null ? AuditNull.Instance : new AuditObjectId(id);

    private static KeyValuePair<string, AuditValue> Pair(string key, AuditValue value) => new(key, value);
}
