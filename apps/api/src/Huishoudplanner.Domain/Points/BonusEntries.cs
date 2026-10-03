using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Points;

/// <summary>
/// What the bonus step of the reconciliation reads of an occurrence (Node <c>BonusOccurrenceDoc</c>): the eight fields <c>findBonusOccurrences</c>
/// projects, with the instants as stored. A row that cannot be read (an unknown status, a date that is no date) has a <see langword="null"/>
/// <see cref="Status"/>, <see cref="PlannedDate"/> or <see cref="Date"/>; one such row must not stop the others, so it is skipped and counted.
/// </summary>
/// <param name="HasFrozenOwner">The field <c>periodOwnerId</c> exists (also as null, which means it was unassigned).</param>
/// <param name="FrozenOwnerId">The frozen period owner; only meaningful when <paramref name="HasFrozenOwner"/>.</param>
public sealed record BonusSource(
    string Id,
    OccurrenceStatus? Status,
    DateTimeOffset? PlannedDate,
    DateTimeOffset? Date,
    bool RecordedDone,
    string? AssigneeId,
    bool HasFrozenOwner,
    string? FrozenOwnerId,
    string? CompletedBy,
    DateTimeOffset? CompletedAt)
{
    public bool IsReadable => Status is not null && PlannedDate is not null && Date is not null;

    /// <summary>Every person the occurrence could count for: its owner, its frozen owner and the person who did it.</summary>
    public IEnumerable<string> People => new[] { AssigneeId, HasFrozenOwner ? FrozenOwnerId : null, CompletedBy }.OfType<string>();

    /// <summary>The shared bonus model of the row (Node <c>toBonusOccurrence</c>); <see langword="null"/> when it cannot be read.</summary>
    public BonusOccurrence? ToOccurrence(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (Status is not { } status || PlannedDate is not { } planned || Date is not { } date)
        {
            return null;
        }

        return new BonusOccurrence(
            status,
            DayKeys.ToDayKey(planned, zone),
            DayKeys.ToDayKey(date, zone),
            RecordedDone,
            AssigneeId,
            // Missing means the assignee; a frozen null means it was unassigned.
            HasFrozenOwner ? new FrozenOwner(FrozenOwnerId) : null,
            CompletedBy,
            CompletedAt);
    }
}

/// <summary>A bonus entry the reconciliation has to insert: dated on the last day of its period, at local midnight (ADR-0012).</summary>
public sealed record BonusEntryInsert(
    string Key,
    PointEntryKind Kind,
    string PersonId,
    int Amount,
    DateTimeOffset Date,
    DateTimeOffset WeekStart,
    DateTimeOffset PeriodStart);

/// <summary>The bonus differences of one reconciliation: a bonus is only inserted or deleted, never updated (person and period are part of its key).</summary>
public sealed record BonusEntryChanges(IReadOnlyList<BonusEntryInsert> Inserts, IReadOnlyList<PointEntry> Deletes)
{
    public bool IsEmpty => Inserts.Count == 0 && Deletes.Count == 0;
}

/// <summary>What the bonus writes really did, by key: an insert that hit a duplicate key and a delete that missed its compare-and-set are not in it.</summary>
public sealed record AppliedBonusChanges(IReadOnlyList<string> Created, IReadOnlyList<string> Removed)
{
    public static AppliedBonusChanges None { get; } = new([], []);
}

/// <summary>The settings the bonus step reads.</summary>
public sealed record BonusSettings(DateOnly Anchor, IReadOnlyList<BonusScheduleRow> Schedule, DateOnly? Floor);

/// <summary>The bonus plan of a reconciliation and the occurrences it could not read.</summary>
public sealed record BonusPlan(BonusEntryChanges Changes, IReadOnlySet<string> SkippedIds);

/// <summary>What a bonus step did, as the summary and the audit entry report it.</summary>
public sealed record BonusReport(int Created, int Removed, IReadOnlyList<PointsBonusChange> Listed, int Total, bool Truncated)
{
    public static BonusReport None { get; } = new(0, 0, [], 0, false);
}
