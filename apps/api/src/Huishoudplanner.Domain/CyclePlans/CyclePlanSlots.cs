namespace Huishoudplanner.Domain.CyclePlans;

/// <summary>What changed between two slot lists of one plan (port of <c>diffSlots</c> in <c>domain/slots.ts</c>).</summary>
public sealed record SlotDiff(
    IReadOnlyList<CyclePlanSlot> Added,
    IReadOnlyList<CyclePlanSlot> Removed,
    IReadOnlyList<(CyclePlanSlot Before, CyclePlanSlot After)> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

public static class CyclePlanSlots
{
    /// <summary>The identity of a slot within a plan; unique because a task may appear once per day.</summary>
    public static string KeyOf(CyclePlanSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return $"{slot.TaskId}:{slot.WeekIndex}:{slot.Weekday}";
    }

    /// <summary>Monday first (0 is Sunday).</summary>
    public static int MondayFirst(int weekday) => (weekday + 6) % 7;

    /// <summary>Stored order: week, Monday-first weekday, sort order, then task id.</summary>
    public static IReadOnlyList<CyclePlanSlot> Sort(IEnumerable<CyclePlanSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return [.. slots
            .OrderBy(s => s.WeekIndex)
            .ThenBy(s => MondayFirst(s.Weekday))
            .ThenBy(s => s.SortOrder)
            .ThenBy(s => s.TaskId, StringComparer.Ordinal)];
    }

    public static SlotDiff Diff(IReadOnlyList<CyclePlanSlot> before, IReadOnlyList<CyclePlanSlot> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var (beforeKeys, beforeByKey) = Index(before);
        var (afterKeys, afterByKey) = Index(after);
        var added = new List<CyclePlanSlot>();
        var changed = new List<(CyclePlanSlot, CyclePlanSlot)>();
        foreach (var key in afterKeys)
        {
            var slot = afterByKey[key];
            if (!beforeByKey.TryGetValue(key, out var old))
            {
                added.Add(slot);
            }
            else if (old.AssigneeId != slot.AssigneeId || old.SortOrder != slot.SortOrder)
            {
                changed.Add((old, slot));
            }
        }

        var removed = beforeKeys.Where(key => !afterByKey.ContainsKey(key)).Select(key => beforeByKey[key]).ToList();
        return new SlotDiff(added, removed, changed);
    }

    /// <summary>Like building a <c>Map</c> from an array: the first position and the last value of a key win.</summary>
    private static (List<string> Keys, Dictionary<string, CyclePlanSlot> ByKey) Index(IReadOnlyList<CyclePlanSlot> slots)
    {
        var keys = new List<string>();
        var byKey = new Dictionary<string, CyclePlanSlot>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var key = KeyOf(slot);
            if (!byKey.ContainsKey(key))
            {
                keys.Add(key);
            }

            byKey[key] = slot;
        }

        return (keys, byKey);
    }
}
