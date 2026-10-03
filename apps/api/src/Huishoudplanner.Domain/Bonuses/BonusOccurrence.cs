namespace Huishoudplanner.Domain.Bonuses;

public enum OccurrenceStatus
{
    Open,
    Done,
    Skipped,
}

/// <summary>
/// The period owner frozen on an occurrence: the assignee at the moment the planned week had ended and the occurrence was
/// first assigned, claimed, taken over or completed. <see cref="PersonId"/> <see langword="null"/> means it was unassigned.
/// </summary>
public readonly record struct FrozenOwner(string? PersonId);

/// <summary>The part of an occurrence that bonuses and the reward goal need (TS <c>BonusOccurrence</c>). Ids are 24-character hex strings.</summary>
/// <param name="Status">Open, done or skipped.</param>
/// <param name="PlannedDate">The slot day; survives a reschedule.</param>
/// <param name="Date">Where the occurrence stands now.</param>
/// <param name="RecordedDone">Created directly in the done state (no plan). Older data without the field reads as <see langword="false"/>.</param>
/// <param name="AssigneeId">The assignee, or <see langword="null"/> when unassigned.</param>
/// <param name="Frozen">The frozen period owner; <see langword="null"/> (no value at all, TS <c>undefined</c>) means the assignee.</param>
/// <param name="CompletedBy">The person who completed it.</param>
/// <param name="CompletedAt">The completion instant.</param>
public sealed record BonusOccurrence(
    OccurrenceStatus Status,
    DateOnly PlannedDate,
    DateOnly Date,
    bool RecordedDone,
    string? AssigneeId,
    FrozenOwner? Frozen,
    string? CompletedBy,
    DateTimeOffset? CompletedAt)
{
    /// <summary>The day that decides the week and the cycle: the plan for planned work, the date for recorded work (TS <c>periodDayOf</c>).</summary>
    public DateOnly PeriodDay => RecordedDone ? Date : PlannedDate;

    /// <summary>The person the occurrence is planned for as far as a period is concerned: the frozen owner, else the assignee (TS <c>periodOwnerOf</c>).</summary>
    public string? PeriodOwnerId => Frozen is { } frozen ? frozen.PersonId : AssigneeId;

    /// <summary>The person who receives the points of done work: <c>CompletedBy</c>, else the assignee; nobody while not done (TS <c>creditedOf</c>).</summary>
    public string? CreditedId => Status == OccurrenceStatus.Done ? CompletedBy ?? AssigneeId : null;
}

/// <summary>One person's view of an occurrence: the item that goes into that person's set.</summary>
public sealed record Placement(string Person, BonusOccurrence Item);
