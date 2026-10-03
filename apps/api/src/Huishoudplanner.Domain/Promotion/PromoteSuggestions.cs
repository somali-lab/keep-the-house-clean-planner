using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Promotion;

/// <summary>A slot of the active plan, as the promote rule reads it. <see cref="Weekday"/> is 0=Sunday..6=Saturday.</summary>
public sealed record PromoteSlot(string TaskId, int WeekIndex, int Weekday, string? AssigneeId);

/// <summary>A generated cycle: its index and first day.</summary>
public sealed record PromoteCycle(int Index, DateOnly StartDate);

/// <summary>A generated occurrence of the active plan reduced to what the rule needs: where it was planned, where it sits now and who has it.</summary>
public sealed record PromoteOccurrence(string Id, string TaskId, DateOnly PlannedDay, DateOnly Day, string? AssigneeId);

public sealed record PromoteInput(
    string PlanId,
    IReadOnlyList<PromoteSlot> Slots,
    IReadOnlyList<PromoteCycle> Cycles,
    IReadOnlyList<PromoteOccurrence> Occurrences,
    IReadOnlyDictionary<string, string> TaskNames,
    DateOnly Anchor,
    DateOnly Today,
    int Threshold,
    IReadOnlyList<DismissedPromotion> Dismissed);

public sealed record PromoteFromSlot(int WeekIndex, int Weekday, string? AssigneeId);

/// <summary>
/// A suggestion to change one slot of the active plan. <see cref="ToAssigneeId"/> is set only when every move also went to the same other
/// person. <see cref="EvidenceIds"/> are the moved occurrences, newest cycle first.
/// </summary>
public sealed record PromoteSuggestion(
    string PlanId,
    string TaskId,
    string TaskName,
    PromoteFromSlot FromSlot,
    int ToWeekday,
    string? ToAssigneeId,
    IReadOnlyList<string> EvidenceIds)
{
    public bool Equals(PromoteSuggestion? other) =>
        other is not null &&
        PlanId == other.PlanId && TaskId == other.TaskId && TaskName == other.TaskName && FromSlot == other.FromSlot &&
        ToWeekday == other.ToWeekday && ToAssigneeId == other.ToAssigneeId && EvidenceIds.SequenceEqual(other.EvidenceIds, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(PlanId, TaskId, FromSlot, ToWeekday, ToAssigneeId);
}

/// <summary>
/// Requirements 4.6; port of <c>computePromoteSuggestions</c> (apps/server/src/domain/promote.ts, pure part). A slot is suggested for change when,
/// in each of the last <c>Threshold</c> consecutive cycles, its occurrence was dragged to the same weekday within the same week of the same cycle
/// (and, optionally, always to the same other person). Newer cycles that have not been decided (planned in the future and not moved) are ignored.
/// A dismissed suggestion returns only with newer evidence.
/// </summary>
public static class PromoteSuggestionCalculator
{
    private sealed record Move(string Id, int Weekday, string? AssigneeId);

    private sealed record Entry(PromoteCycle Cycle, PromoteOccurrence Occurrence);

    public static IReadOnlyList<PromoteSuggestion> Compute(PromoteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Threshold < 1)
        {
            return [];
        }

        var newestFirst = input.Cycles.OrderByDescending(c => c.Index).ToList();
        var byPlannedDay = new Dictionary<(string Task, DateOnly Day), PromoteOccurrence>();
        foreach (var occurrence in input.Occurrences)
        {
            byPlannedDay[(occurrence.TaskId, occurrence.PlannedDay)] = occurrence;
        }

        var suggestions = new List<PromoteSuggestion>();
        foreach (var slot in input.Slots)
        {
            var suggestion = ForSlot(input, slot, newestFirst, byPlannedDay);
            if (suggestion is not null)
            {
                suggestions.Add(suggestion);
            }
        }

        return suggestions;
    }

    private static PromoteSuggestion? ForSlot(
        PromoteInput input,
        PromoteSlot slot,
        List<PromoteCycle> newestFirst,
        Dictionary<(string Task, DateOnly Day), PromoteOccurrence> byPlannedDay)
    {
        var history = new List<Entry>();
        foreach (var cycle in newestFirst)
        {
            var planned = Cycles.SlotDate(cycle.StartDate, slot.WeekIndex, slot.Weekday);
            if (byPlannedDay.TryGetValue((slot.TaskId, planned), out var occurrence))
            {
                history.Add(new Entry(cycle, occurrence));
            }
        }

        // Skip the cycles still to come that nobody has touched yet.
        var start = 0;
        while (start < history.Count && history[start].Occurrence.Day == history[start].Occurrence.PlannedDay && history[start].Occurrence.PlannedDay > input.Today)
        {
            start++;
        }

        var recent = history.Skip(start).Take(input.Threshold).ToList();
        if (recent.Count < input.Threshold)
        {
            return null;
        }

        for (var i = 0; i < recent.Count; i++)
        {
            if (recent[i].Cycle.Index != recent[0].Cycle.Index - i)
            {
                return null;
            }
        }

        var moves = new List<Move>(recent.Count);
        foreach (var (cycle, occurrence) in recent)
        {
            if (occurrence.Day == occurrence.PlannedDay ||
                Cycles.CycleIndexFor(occurrence.Day, input.Anchor) != cycle.Index ||
                Cycles.WeekIndexFor(occurrence.Day, input.Anchor) != slot.WeekIndex)
            {
                return null;
            }

            moves.Add(new Move(occurrence.Id, DayKeys.WeekdaySun0(occurrence.Day), occurrence.AssigneeId));
        }

        var toWeekday = moves[0].Weekday;
        if (moves.Any(m => m.Weekday != toWeekday))
        {
            return null;
        }

        var firstAssignee = moves[0].AssigneeId;
        var toAssigneeId = firstAssignee is not null && firstAssignee != slot.AssigneeId && moves.All(m => m.AssigneeId == firstAssignee)
            ? firstAssignee
            : null;

        var evidence = moves.Select(m => m.Id).ToList();
        var dismissed = input.Dismissed.Any(d =>
            d.PlanId == input.PlanId && d.TaskId == slot.TaskId && d.WeekIndex == slot.WeekIndex && d.Weekday == slot.Weekday &&
            d.ToWeekday == toWeekday && d.ToAssigneeId == toAssigneeId && d.LastEvidenceId == evidence[0]);
        if (dismissed)
        {
            return null;
        }

        return new PromoteSuggestion(
            input.PlanId,
            slot.TaskId,
            input.TaskNames.GetValueOrDefault(slot.TaskId, string.Empty),
            new PromoteFromSlot(slot.WeekIndex, slot.Weekday, slot.AssigneeId),
            toWeekday,
            toAssigneeId,
            evidence);
    }
}
