namespace Huishoudplanner.Domain.Occurrences;

/// <summary>The values of <c>occurrences.status</c>.</summary>
public enum OccurrenceStatus
{
    Open,
    Done,
    Skipped,
}

/// <summary>The values of <c>occurrences.origin</c>: planned from a slot, or created ad hoc (an extra execution or a one-off task, ADR-0009).</summary>
public enum OccurrenceOrigin
{
    Generated,
    Adhoc,
}

/// <summary>The wire names stored in <c>occurrences</c>, identical to the Node server's.</summary>
public static class OccurrenceNames
{
    public static string ToWire(OccurrenceStatus status) => status switch
    {
        OccurrenceStatus.Open => "open",
        OccurrenceStatus.Done => "done",
        OccurrenceStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string ToWire(OccurrenceOrigin origin) => origin switch
    {
        OccurrenceOrigin.Generated => "generated",
        OccurrenceOrigin.Adhoc => "adhoc",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public static bool TryParseStatus(string? wire, out OccurrenceStatus status)
    {
        (var known, status) = wire switch
        {
            "open" => (true, OccurrenceStatus.Open),
            "done" => (true, OccurrenceStatus.Done),
            "skipped" => (true, OccurrenceStatus.Skipped),
            _ => (false, default(OccurrenceStatus)),
        };
        return known;
    }

    public static bool TryParseOrigin(string? wire, out OccurrenceOrigin origin)
    {
        (var known, origin) = wire switch
        {
            "generated" => (true, OccurrenceOrigin.Generated),
            "adhoc" => (true, OccurrenceOrigin.Adhoc),
            _ => (false, default(OccurrenceOrigin)),
        };
        return known;
    }
}

/// <summary>
/// A concrete instance of a slot on a real day, carrying what actually happened (requirements 3, <c>occurrences</c>). The document is
/// shared with the Node server, so the model holds every stored field. <see cref="Date"/> and <see cref="PlannedDate"/> are the instants
/// of 00:00 in the household timezone of the day the occurrence sits on and of the day of its slot (kept when it is dragged). Name,
/// duration and room are snapshots taken at creation (ADR-0011): a later change of the task never rewrites history.
/// </summary>
/// <remarks>
/// Slice 3.1 only creates and removes generated occurrences and refreshes room snapshots; slice 3.2 adds the actions on top of this type
/// (completing, skipping, rescheduling, assigning) without changing it. A stored field that is missing on older data reads as its
/// default (<see cref="RecordedDone"/> false, the others <see langword="null"/>).
/// </remarks>
public sealed record Occurrence(
    string Id,
    string? TaskId,
    string CycleId,
    string? PlanId,
    DateTimeOffset Date,
    DateTimeOffset PlannedDate,
    string? AssigneeId,
    OccurrenceStatus Status,
    OccurrenceStatus? StatusBeforeCompletion,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    string? SkipReason,
    int DurationMinutesSnapshot,
    string TaskNameSnapshot,
    string? RoomIdSnapshot,
    string? RoomNameSnapshot,
    OccurrenceOrigin Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool RecordedDone = false,
    string? RequestId = null,
    int? PointsSnapshot = null,
    int? PointsOverride = null,
    string? PeriodOwnerId = null,
    bool PeriodOwnerFrozen = false)
{
    /// <summary>
    /// Whether <c>periodOwnerId</c> is stored at all. A stored <see langword="null"/> means "frozen as unassigned" (ADR-0012); a missing field means
    /// "the assignee". <see cref="PeriodOwnerId"/> alone cannot tell them apart, so the stores set <see cref="PeriodOwnerFrozen"/> when the field exists.
    /// </summary>
    public bool HasPeriodOwner => PeriodOwnerFrozen || PeriodOwnerId is not null;
}

/// <summary>
/// A generated occurrence as the store is asked to create it: open, unassigned work of a slot with its snapshots, on a day that equals its
/// planned day. The store assigns the id. Idempotent on (<see cref="CycleId"/>, <see cref="TaskId"/>, <see cref="Date"/>) among generated
/// occurrences (the partial unique index <c>occurrences_generated_slot_unique</c>).
/// </summary>
public sealed record NewGeneratedOccurrence(
    string TaskId,
    string CycleId,
    string PlanId,
    DateTimeOffset Date,
    string? AssigneeId,
    int DurationMinutesSnapshot,
    string TaskNameSnapshot,
    string RoomIdSnapshot,
    string? RoomNameSnapshot,
    DateTimeOffset CreatedAt)
{
    /// <summary>The occurrence as it is stored once the store has assigned <paramref name="id"/>.</summary>
    public Occurrence ToOccurrence(string id) => new(
        id,
        TaskId,
        CycleId,
        PlanId,
        Date,
        Date,
        AssigneeId,
        OccurrenceStatus.Open,
        null,
        null,
        null,
        null,
        DurationMinutesSnapshot,
        TaskNameSnapshot,
        RoomIdSnapshot,
        RoomNameSnapshot,
        OccurrenceOrigin.Generated,
        CreatedAt,
        CreatedAt);
}
