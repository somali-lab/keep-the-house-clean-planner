using Huishoudplanner.Domain.Bonuses;

namespace Huishoudplanner.Domain.Points;

/// <summary>The kinds of a ledger entry (<c>pointEntries.kind</c>): an execution, the four derived bonuses (ADR-0012) and a booked redemption.</summary>
public enum PointEntryKind
{
    Execution,
    BonusWeekDone,
    BonusWeekOnTime,
    BonusCycleDone,
    BonusCycleOnTime,
    Redemption,
}

/// <summary>The path that wrote the current value of an entry (<c>pointEntries.source</c>).</summary>
public enum PointEntrySource
{
    Live,
    Backfill,
    Recompute,
}

/// <summary>Why an execution entry was created, changed or removed; recorded in the audit meta (requirements 4.9).</summary>
public enum PointsSyncReason
{
    Complete,
    Recorded,
    Uncomplete,
    Retract,
    Correction,
}

/// <summary>What started a reconciliation (ADR-0011): the start of the host, the nightly job, an import or an administrator.</summary>
public enum PointsRecomputeTrigger
{
    Startup,
    Nightly,
    Import,
    Admin,
}

/// <summary>
/// Which transaction of a reconciliation wrote a summary entry: the execution entries and the field migration, or the bonuses. Node wrote one
/// combined entry; here the bonus step is a transaction of its own, so a failing bonus step never undoes the executions.
/// </summary>
public enum PointsRecomputeStep
{
    Executions,
    Bonuses,
}

/// <summary>The wire names stored and published, identical to the Node server's.</summary>
public static class PointNames
{
    public static string ToWire(PointEntryKind kind) => kind switch
    {
        PointEntryKind.Execution => "execution",
        PointEntryKind.BonusWeekDone => BonusKind.WeekDone.ToLedgerKind(),
        PointEntryKind.BonusWeekOnTime => BonusKind.WeekOnTime.ToLedgerKind(),
        PointEntryKind.BonusCycleDone => BonusKind.CycleDone.ToLedgerKind(),
        PointEntryKind.BonusCycleOnTime => BonusKind.CycleOnTime.ToLedgerKind(),
        PointEntryKind.Redemption => "redemption",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>An exact, case-sensitive match; a name this application does not know is not a kind.</summary>
    public static bool TryParseKind(string? wire, out PointEntryKind kind)
    {
        foreach (var candidate in Enum.GetValues<PointEntryKind>())
        {
            if (string.Equals(ToWire(candidate), wire, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static string ToWire(PointEntrySource source) => source switch
    {
        PointEntrySource.Live => "live",
        PointEntrySource.Backfill => "backfill",
        PointEntrySource.Recompute => "recompute",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static bool TryParseSource(string? wire, out PointEntrySource source)
    {
        foreach (var candidate in Enum.GetValues<PointEntrySource>())
        {
            if (string.Equals(ToWire(candidate), wire, StringComparison.Ordinal))
            {
                source = candidate;
                return true;
            }
        }

        source = default;
        return false;
    }

    public static string ToWire(PointsSyncReason reason) => reason switch
    {
        PointsSyncReason.Complete => "complete",
        PointsSyncReason.Recorded => "recorded",
        PointsSyncReason.Uncomplete => "uncomplete",
        PointsSyncReason.Retract => "retract",
        PointsSyncReason.Correction => "correction",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    public static string ToWire(PointsRecomputeStep step) => step switch
    {
        PointsRecomputeStep.Executions => "executions",
        PointsRecomputeStep.Bonuses => "bonuses",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    public static string ToWire(PointsRecomputeTrigger trigger) => trigger switch
    {
        PointsRecomputeTrigger.Startup => "startup",
        PointsRecomputeTrigger.Nightly => "nightly",
        PointsRecomputeTrigger.Import => "import",
        PointsRecomputeTrigger.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
    };
}

/// <summary>
/// One entry of the points ledger (requirements 3, <c>pointEntries</c>; ADR-0011). An entry of kind <see cref="PointEntryKind.Execution"/> is a pure
/// function of one occurrence. The bonus kinds (ADR-0012) are written only by the bonus step of the reconciliation, the redemption (slice 4.3) by
/// its own writer; both are read and listed like any other entry but are never written by the execution sync or the reconciliation of an
/// execution. Dates are the instants of local midnight in the household timezone.
/// </summary>
public sealed record PointEntry(
    string Id,
    string Key,
    PointEntryKind Kind,
    string PersonId,
    int Amount,
    DateTimeOffset Date,
    DateTimeOffset WeekStart,
    DateTimeOffset? PeriodStart,
    string? OccurrenceId,
    string? TaskId,
    string TitleSnapshot,
    PointEntrySource Source,
    string? Note,
    int? CentsPerPointSnapshot,
    string? CurrencyCodeSnapshot,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Whether the fields an execution sync derives from its occurrence equal these of the entry.</summary>
    public bool Matches(ExecutionEntryFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return PersonId == fields.PersonId &&
            Amount == fields.Amount &&
            Date == fields.Date &&
            WeekStart == fields.WeekStart &&
            OccurrenceId == fields.OccurrenceId &&
            TaskId == fields.TaskId &&
            string.Equals(TitleSnapshot, fields.TitleSnapshot, StringComparison.Ordinal);
    }
}

/// <summary>
/// The ledger fields an occurrence is expected to produce (<c>PointEntryFields</c> of the Node server): the person credited, the points, the day
/// and the Monday of its week, the occurrence and its task (<see langword="null"/> for a one-off task) and the task name as it was.
/// </summary>
public sealed record ExecutionEntryFields(
    string PersonId,
    int Amount,
    DateTimeOffset Date,
    DateTimeOffset WeekStart,
    string OccurrenceId,
    string? TaskId,
    string TitleSnapshot);

/// <summary>An execution entry the reconciliation has to insert.</summary>
public sealed record PointEntryInsert(string Key, ExecutionEntryFields Fields);

/// <summary>An execution entry the reconciliation has to bring to <paramref name="Fields"/>; <paramref name="Current"/> is what was read.</summary>
public sealed record PointEntryUpdate(PointEntry Current, ExecutionEntryFields Fields);

/// <summary>The differences of one reconciliation of the execution entries.</summary>
public sealed record PointEntryChanges(
    IReadOnlyList<PointEntryInsert> Inserts,
    IReadOnlyList<PointEntryUpdate> Updates,
    IReadOnlyList<PointEntry> Deletes)
{
    public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Deletes.Count == 0;
}

/// <summary>What a bulk write really did; a guarded write that missed (the entry changed after it was read) is not counted.</summary>
public sealed record AppliedPointEntryChanges(int Created, int Updated, int Removed);
