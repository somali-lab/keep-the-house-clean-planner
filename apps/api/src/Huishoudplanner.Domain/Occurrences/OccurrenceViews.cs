using System.Buffers.Text;
using System.Text.Json;
using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Occurrences;

/// <summary>
/// An occurrence as the API shows it (Node <c>toOccurrenceView</c>): the stored fields, the calendar dates as day keys in the household
/// timezone, the derived <see cref="IsOverdue"/> (open and dated before today) and <see cref="MovedFrom"/> (the planned day when the
/// occurrence sits on another day), and where the day falls in the cycles (<see cref="CycleIndex"/>, <see cref="WeekIndex"/>), so the web app
/// computes none of it.
/// </summary>
public sealed record OccurrenceView(
    Occurrence Occurrence,
    DateOnly Date,
    DateOnly PlannedDate,
    bool IsOverdue,
    DateOnly? MovedFrom,
    int CycleIndex,
    int WeekIndex)
{
    public static OccurrenceView From(Occurrence occurrence, TimeZoneInfo zone, DateOnly anchor, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(zone);
        var date = DayKeys.ToDayKey(occurrence.Date, zone);
        var planned = DayKeys.ToDayKey(occurrence.PlannedDate, zone);
        return new OccurrenceView(
            occurrence,
            date,
            planned,
            occurrence.Status == OccurrenceStatus.Open && date < today,
            date == planned ? null : planned,
            Cycles.CycleIndexFor(date, anchor),
            Cycles.WeekIndexFor(date, anchor));
    }
}

/// <summary>A non-blocking remark on a write (requirements 8): <c>assignee_unavailable</c> after a reschedule or an assignment.</summary>
public sealed record OccurrenceWarning(string Code, string Message, IReadOnlyDictionary<string, object?> Details);

/// <summary>The occurrence after a reschedule or an assignment, with its <see cref="Warnings"/>.</summary>
public sealed record OccurrenceChange(OccurrenceView View, IReadOnlyList<OccurrenceWarning> Warnings);

/// <summary>One page of occurrences; <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record OccurrenceList(IReadOnlyList<OccurrenceView> Items, string? NextCursor);

public static class OccurrenceListQuery
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;
}

/// <summary>The direction of the list: oldest day first (the default) or newest day first. Both walk the same sort key (day, task name, id).</summary>
public enum OccurrenceOrder
{
    Ascending,
    Descending,
}

/// <summary>What <c>GET /occurrences</c> asks for: the days <see cref="From"/> to <see cref="To"/> (both included), optional filters and the <see cref="Order"/>.</summary>
public sealed record OccurrenceListRequest(
    DateOnly From,
    DateOnly To,
    string? AssigneeId = null,
    OccurrenceStatus? Status = null,
    int? Limit = null,
    string? Cursor = null,
    OccurrenceOrder Order = OccurrenceOrder.Ascending);

/// <summary>
/// The query the store answers: the instants [<see cref="From"/>, <see cref="ToExclusive"/>) in the display order (day, task name, id), all three
/// reversed for <see cref="OccurrenceOrder.Descending"/>; <see cref="After"/> is the position in that same order.
/// </summary>
public sealed record OccurrenceQuery(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    string? AssigneeId,
    OccurrenceStatus? Status,
    OccurrenceCursor? After,
    int Take,
    OccurrenceOrder Order = OccurrenceOrder.Ascending);

/// <summary>
/// The position after an occurrence in the list order (day instant, task name, id) of its <see cref="Order"/>. Opaque to clients. A cursor only
/// continues the list it came from: the use case refuses one whose <see cref="Order"/> differs from the request.
/// </summary>
public sealed record OccurrenceCursor(DateTimeOffset Date, string TaskName, string Id, OccurrenceOrder Order = OccurrenceOrder.Ascending)
{
    public static OccurrenceCursor After(Occurrence occurrence, OccurrenceOrder order = OccurrenceOrder.Ascending)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return new(occurrence.Date, occurrence.TaskNameSnapshot, occurrence.Id, order);
    }

    /// <summary>An ascending cursor keeps the three-part shape it always had; a descending one adds a fourth part.</summary>
    public string Encode() =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(Order == OccurrenceOrder.Ascending
            ? new object[] { Date.ToUnixTimeMilliseconds(), TaskName, Id }
            : new object[] { Date.ToUnixTimeMilliseconds(), TaskName, Id, "desc" }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out OccurrenceCursor cursor)
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
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is not (3 or 4) ||
                root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt64(out var millis) ||
                root[1].ValueKind != JsonValueKind.String || root[2].ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var order = OccurrenceOrder.Ascending;
            if (root.GetArrayLength() == 4)
            {
                if (root[3].ValueKind != JsonValueKind.String || root[3].GetString() != "desc")
                {
                    return false;
                }

                order = OccurrenceOrder.Descending;
            }

            var id = root[2].GetString()!;
            if (!OccurrenceRules.IsId(id))
            {
                return false;
            }

            cursor = new OccurrenceCursor(DateTimeOffset.FromUnixTimeMilliseconds(millis), root[1].GetString()!, id, order);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// What a use case read about the occurrence and still expects to hold when it writes (the "filter on the state the use case read" of
/// ADR-0021): the status, and for a claim that nobody has the occurrence yet.
/// </summary>
public sealed record OccurrenceGuard(OccurrenceStatus Status, bool RequireUnassigned = false);

/// <summary>The write did not match: the occurrence exists but is no longer in the state the use case read (a lost race).</summary>
public readonly record struct OccurrenceStateChanged;

/// <summary>The newest completion of a task, or none (<see langword="null"/>): what <c>lastCompletedAt</c> is derived from.</summary>
public sealed record LatestCompletion(DateTimeOffset? At);

/// <summary>Who completes: <see cref="CompletedBy"/> (a named person, on behalf of) or <see cref="TakeOver"/> (the actor does it and becomes the assignee), never both (ADR-0011).</summary>
public sealed record CompleteCommand(string? CompletedBy = null, bool TakeOver = false);

/// <summary>An administrator's correction of a completion: its day, its timestamp and the person credited.</summary>
public sealed record EditCompletionCommand(DateOnly Date, DateTimeOffset CompletedAt, string CompletedBy);
