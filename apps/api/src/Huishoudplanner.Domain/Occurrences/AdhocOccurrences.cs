using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Occurrences;

/// <summary>What an ad-hoc occurrence is: an extra execution of an existing task or a one-off task without a task record (ADR-0009). The wire names are the <c>meta.kind</c> of the audit entry.</summary>
public enum AdhocKind
{
    Extra,
    OneOff,
}

public static class AdhocKindNames
{
    public static string ToWire(AdhocKind kind) => kind switch
    {
        AdhocKind.Extra => "extra",
        AdhocKind.OneOff => "one_off",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// An extra execution of an existing task (ADR-0009), planned on a day or, with <see cref="Done"/>, recorded as already done today.
/// <see cref="Assignee"/> is <see langword="null"/> when the request did not say (the task's default assignee, the actor when done), an
/// <see cref="AssigneeChoice"/> with no user for "anyone". <see cref="RequestId"/> is the optional client idempotency key.
/// </summary>
public sealed record ExtraExecutionCommand(
    string TaskId,
    DateOnly Date,
    AssigneeChoice? Assignee = null,
    bool Done = false,
    string? RequestId = null);

/// <summary>
/// A one-off task (ADR-0009): work done once, with no task record, so name, duration and room live in the snapshot fields only.
/// <see cref="Assignee"/> is <see langword="null"/> when the request did not say (unassigned, the actor when done). <see cref="Points"/> is the
/// chosen value (ADR-0011); <see langword="null"/> means the default for the duration.
/// </summary>
public sealed record OneOffCommand(
    string Name,
    string? RoomId,
    int DurationMinutes,
    DateOnly Date,
    AssigneeChoice? Assignee = null,
    bool Done = false,
    int? Points = null,
    string? RequestId = null);

/// <summary>
/// An ad-hoc occurrence as the store is asked to create it: no plan, planned day equal to its day, and either open or recorded as done
/// (<see cref="CompletedAt"/> set, <see cref="PointsSnapshot"/> fixed). The store assigns the id. The unique index on the request key
/// (<c>occurrences_request_id_unique</c>) makes a second insert with the same <see cref="RequestId"/> fail with <see cref="RequestKeyTaken"/>.
/// </summary>
public sealed record NewAdhocOccurrence(
    string? TaskId,
    string CycleId,
    DateTimeOffset Date,
    string? AssigneeId,
    bool Done,
    DateTimeOffset? CompletedAt,
    int DurationMinutesSnapshot,
    string TaskNameSnapshot,
    string? RoomIdSnapshot,
    string? RoomNameSnapshot,
    string? RequestId,
    int? PointsSnapshot,
    int? PointsOverride,
    DateTimeOffset CreatedAt)
{
    /// <summary>The occurrence as it is stored once the store has assigned <paramref name="id"/>.</summary>
    public Occurrence ToOccurrence(string id) => new(
        id,
        TaskId,
        CycleId,
        null,
        Date,
        Date,
        AssigneeId,
        Done ? OccurrenceStatus.Done : OccurrenceStatus.Open,
        null,
        CompletedAt,
        Done ? AssigneeId : null,
        null,
        DurationMinutesSnapshot,
        TaskNameSnapshot,
        RoomIdSnapshot,
        RoomNameSnapshot,
        OccurrenceOrigin.Adhoc,
        CreatedAt,
        CreatedAt,
        Done,
        RequestId,
        PointsSnapshot,
        PointsOverride);
}

/// <summary>The insert hit the unique request key: another request with the same <c>requestId</c> was stored first. The use case retries and then replays or refuses.</summary>
public readonly record struct RequestKeyTaken;

/// <summary>
/// The answer of creating an ad-hoc occurrence: the occurrence with its non-blocking <see cref="Warnings"/> (<c>task_already_planned</c>), and
/// whether it is new (<see cref="Created"/>, <c>201</c>) or the stored record of a repeated request (<c>200</c>, no warnings, nothing written).
/// </summary>
public sealed record AdhocResult(OccurrenceView View, IReadOnlyList<OccurrenceWarning> Warnings, bool Created);
