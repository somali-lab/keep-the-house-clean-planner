using System.Globalization;
using System.Text.Json;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Support;

/// <summary>Reads the occurrences, contexts and amounts of the bonus, reward and badge vectors, and writes results in the notation of the vector files.</summary>
public static class PointsVectorArguments
{
    public static string? NullableText(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    public static DateTimeOffset ParseInstant(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? NullableInstant(this JsonElement element, string name) =>
        element.NullableText(name) is { } text ? ParseInstant(text) : null;

    /// <summary>An absent <c>periodOwnerId</c> is "not frozen"; a JSON null is frozen as unassigned.</summary>
    public static BonusOccurrence ReadOccurrence(JsonElement e) => new(
        e.GetProperty("status").GetString() switch
        {
            "open" => OccurrenceStatus.Open,
            "done" => OccurrenceStatus.Done,
            "skipped" => OccurrenceStatus.Skipped,
            var other => throw new NotSupportedException($"Unknown status {other}"),
        },
        DayKeys.Parse(e.GetProperty("plannedDate").GetString()!),
        DayKeys.Parse(e.GetProperty("date").GetString()!),
        e.TryGetProperty("recordedDone", out var recorded) && recorded.GetBoolean(),
        e.NullableText("assigneeId"),
        e.TryGetProperty("periodOwnerId", out _) ? new FrozenOwner(e.NullableText("periodOwnerId")) : null,
        e.NullableText("completedBy"),
        e.NullableInstant("completedAt"));

    public static List<BonusOccurrence> ReadOccurrences(JsonElement items) => [.. items.EnumerateArray().Select(ReadOccurrence)];

    public static BonusContext ReadContext(JsonElement e) => new(
        DayKeys.Parse(e.GetProperty("anchor").GetString()!),
        DayKeys.FindZone(e.GetProperty("timezone").GetString()!),
        DayKeys.Parse(e.GetProperty("today").GetString()!),
        ReadSchedule(e.GetProperty("schedule")),
        e.TryGetProperty("floor", out var floor) ? DayKeys.Parse(floor.GetString()!) : null);

    public static List<BonusScheduleRow> ReadSchedule(JsonElement schedule) =>
        [.. schedule.EnumerateArray().Select(r => new BonusScheduleRow(DayKeys.Parse(r.GetProperty("from").GetString()!), ReadAmounts(r)))];

    public static BonusAmounts ReadAmounts(JsonElement e) => new(
        e.GetProperty("weekDone").GetInt32(),
        e.GetProperty("weekOnTime").GetInt32(),
        e.GetProperty("cycleDone").GetInt32(),
        e.GetProperty("cycleOnTime").GetInt32());

    public static string Iso(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string? Iso(DateTimeOffset? instant) => instant is { } value ? Iso(value) : null;

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
