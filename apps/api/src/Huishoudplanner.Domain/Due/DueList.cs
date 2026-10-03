using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Huishoudplanner.Domain.Due;

/// <summary>The first open occurrence of a task from today on, when the grid still has one planned.</summary>
public sealed record DueNextOccurrence(string Id, DateOnly Date, string? AssigneeId);

/// <summary>
/// One entry of the due list as the API shows it (port of <c>DueItem</c> in <c>domain/due.ts</c>): the ranked result with the names and
/// the dates around it. <see cref="RoomName"/> is <see langword="null"/> when the room no longer exists.
/// </summary>
public sealed record DueItem(
    string TaskId,
    string TaskName,
    string RoomId,
    string? RoomName,
    string IntervalKey,
    string IntervalLabel,
    int PeriodDays,
    int DaysSince,
    double Ratio,
    DueState State,
    DateTimeOffset? LastCompletedAt,
    DateOnly InitialDueDate,
    DueNextOccurrence? NextOccurrence);

/// <summary>How many tasks are due and how many are overdue; the <c>due</c> object of the generation answer (requirements 4.8).</summary>
public sealed record DueSummary(int Due, int Overdue)
{
    public static DueSummary Of(IEnumerable<DueState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var due = 0;
        var overdue = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case DueState.Due:
                    due++;
                    break;
                case DueState.Overdue:
                    overdue++;
                    break;
                default:
                    break;
            }
        }

        return new DueSummary(due, overdue);
    }

    public static DueSummary Of(IEnumerable<DueResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return Of(results.Select(r => r.State));
    }

    public static DueSummary Of(IEnumerable<DueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Of(items.Select(i => i.State));
    }
}

/// <summary>
/// One page of the ranked due list. <see cref="Summary"/> counts the whole list, not the page; <see cref="NextCursor"/> is
/// <see langword="null"/> on the last page.
/// </summary>
public sealed record DueList(DateOnly Today, IReadOnlyList<DueItem> Items, string? NextCursor, DueSummary Summary);

/// <summary>Paging limits of the due list.</summary>
public static class DueListQuery
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>
/// The position after an item in the ranking (ratio descending, days since descending, task id). Holds the days and the period, not the
/// ratio, so the position stays exact and survives the task it points at being deactivated. Opaque to clients.
/// </summary>
public sealed record DueCursor(int DaysSince, int PeriodDays, string TaskId)
{
    public static DueCursor After(DueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.DaysSince, result.PeriodDays, result.TaskId);
    }

    /// <summary>True when an entry with these values ranks after the cursor position.</summary>
    public bool Precedes(DueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var own = (double)DaysSince / PeriodDays;
        if (result.Ratio != own)
        {
            return result.Ratio < own;
        }

        if (result.DaysSince != DaysSince)
        {
            return result.DaysSince < DaysSince;
        }

        return string.CompareOrdinal(result.TaskId, TaskId) > 0;
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { DaysSince, PeriodDays, TaskId }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out DueCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 3 ||
                root[0].ValueKind != JsonValueKind.Number || root[1].ValueKind != JsonValueKind.Number ||
                root[2].ValueKind != JsonValueKind.String ||
                !root[0].TryGetInt32(out var days) || !root[1].TryGetInt32(out var period) || days < 0 || period < 1)
            {
                return false;
            }

            cursor = new DueCursor(days, period, root[2].GetString()!);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{DaysSince}/{PeriodDays} {TaskId}");
}
