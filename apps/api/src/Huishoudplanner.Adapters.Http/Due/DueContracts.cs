using System.ComponentModel;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Adapters.Http.Due;

/// <summary>The first open occurrence of a task from today on.</summary>
public sealed record DueNextOccurrenceResponse(string Id, DateOnly Date, string? AssigneeId);

/// <summary>One task of the due list. <c>state</c> is <c>ok</c>, <c>due</c> or <c>overdue</c>.</summary>
public sealed record DueItemResponse(
    string TaskId,
    string TaskName,
    string RoomId,
    [property: Description("Null when the room no longer exists.")] string? RoomName,
    string IntervalKey,
    string IntervalLabel,
    int PeriodDays,
    [property: Description("Local calendar days since the last completion; for a task never completed zero before its initial due date, then one interval plus the days since that date.")] int DaysSince,
    [property: Description("daysSince divided by periodDays: 1 or more is due, 1.5 or more overdue.")] double Ratio,
    string State,
    DateTimeOffset? LastCompletedAt,
    [property: Description("The day a never-completed task first counts as due.")] DateOnly InitialDueDate,
    DueNextOccurrenceResponse? NextOccurrence)
{
    internal static DueItemResponse From(DueItem item) => new(
        item.TaskId,
        item.TaskName,
        item.RoomId,
        item.RoomName,
        item.IntervalKey,
        item.IntervalLabel,
        item.PeriodDays,
        item.DaysSince,
        item.Ratio,
        item.State switch
        {
            DueState.Due => "due",
            DueState.Overdue => "overdue",
            _ => "ok",
        },
        item.LastCompletedAt,
        item.InitialDueDate,
        item.NextOccurrence is { } next ? new DueNextOccurrenceResponse(next.Id, next.Date, next.AssigneeId) : null);
}

/// <summary>How many tasks are due and overdue over the whole list.</summary>
public sealed record DueSummaryResponse(int Due, int Overdue);

/// <summary>One page of the ranked due list; <c>nextCursor</c> is null on the last page, <c>summary</c> counts the whole list.</summary>
public sealed record DueListResponse(DateOnly Today, IReadOnlyList<DueItemResponse> Items, string? NextCursor, DueSummaryResponse Summary);
