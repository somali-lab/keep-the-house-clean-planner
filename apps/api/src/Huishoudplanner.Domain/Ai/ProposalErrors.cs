using System.Globalization;
using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Domain.Ai;

/// <summary>The messages that are fed back to the model when its proposal is rejected (<c>describePlanError</c> of the Node server).</summary>
public static class ProposalErrors
{
    /// <summary>The code of a slot for a task outside the requested selection.</summary>
    public const string TaskNotInSelection = "task_not_in_selection";

    /// <summary>The code of a slot that still has no concrete person after the completion.</summary>
    public const string AssigneeRequired = "assignee_required";

    /// <summary><c>code (slot 3, task id, user id, weekIndex 1, weekday 2)</c>: only the parts the issue carries.</summary>
    public static string Describe(PlanIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var where = new List<string>();
        if (issue.SlotIndex is { } slot)
        {
            where.Add(string.Create(CultureInfo.InvariantCulture, $"slot {slot}"));
        }

        if (issue.TaskId is { Length: > 0 } task)
        {
            where.Add($"task {task}");
        }

        if (issue.UserId is { Length: > 0 } user)
        {
            where.Add($"user {user}");
        }

        if (issue.WeekIndex is { } week)
        {
            where.Add(string.Create(CultureInfo.InvariantCulture, $"weekIndex {week}"));
        }

        if (issue.Weekday is { } weekday)
        {
            where.Add(string.Create(CultureInfo.InvariantCulture, $"weekday {weekday}"));
        }

        return where.Count == 0 ? issue.Code : $"{issue.Code} ({string.Join(", ", where)})";
    }

    public static string NotInSelection(int slotIndex, string taskId) =>
        string.Create(CultureInfo.InvariantCulture, $"{TaskNotInSelection} (slot {slotIndex}, task {taskId})");

    public static string NoAssignee(int slotIndex, int weekIndex, int weekday) =>
        string.Create(CultureInfo.InvariantCulture, $"{AssigneeRequired} (slot {slotIndex}, weekIndex {weekIndex}, weekday {weekday})");
}
