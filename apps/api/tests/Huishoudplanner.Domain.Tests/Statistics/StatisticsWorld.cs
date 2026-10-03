using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Statistics;

namespace Huishoudplanner.Domain.Tests.Statistics;

/// <summary>
/// The fixed dataset of <c>apps/server/test/stats.test.ts</c>, as plain records. Anchor Monday 2026-09-14; cycle 0 = 14 Sep to 11 Oct,
/// cycle 1 = 12 Oct to 8 Nov. The household is Europe/Amsterdam and "now" is Wednesday 14 Oct 2026.
///   A "Badkamer schoonmaken" (Badkamer, 1w, 30 min): every Monday, Persoon 1
///   B "Keuken dweilen"       (Keuken, 2wk, 20 min): Thursday of weeks 1 and 3, Persoon 2
///   C "Ramen lappen"         (Woonkamer, 4wk, 45 min): Saturday of week 2, unassigned
/// Cycle 0: A 14 Sep done by P1, A 21 Sep done by P2, A 28 Sep skipped, A 5 Oct left open; B 17 Sep done by P2, B 1 Oct done by P2 (on 2 Oct);
/// C moved from 26 to 27 Sep, then done by P1 (claimed: the assignee becomes P1). Cycle 1: A 12 Oct done by P1; the rest still to come.
/// </summary>
internal static class StatisticsWorld
{
    public const string Timezone = "Europe/Amsterdam";

    public const string Cycle0 = "cycle-0";
    public const string Cycle1 = "cycle-1";
    public const string P1 = "person-1";
    public const string P2 = "person-2";
    public const string A = "task-a";
    public const string B = "task-b";
    public const string C = "task-c";
    public const string Badkamer = "room-badkamer";
    public const string Keuken = "room-keuken";
    public const string Woonkamer = "room-woonkamer";

    public static readonly DateOnly Anchor = new(2026, 9, 14);

    public static readonly DateTimeOffset Now = Instant("2026-10-14T08:00:00Z");

    public static readonly TimeZoneInfo Zone = DayKeys.FindZone(Timezone);

    public static IReadOnlyList<StatisticsPerson> People { get; } = [new(P1, "Persoon 1", true), new(P2, "Persoon 2", true)];

    public static IReadOnlyList<StatisticsRoom> AllRooms { get; } = [new(Badkamer, "Badkamer"), new(Keuken, "Keuken"), new(Woonkamer, "Woonkamer")];

    public static IReadOnlyList<StatisticsTask> AllTasks { get; } =
    [
        new(A, "Badkamer schoonmaken", Badkamer, "1w", true),
        new(B, "Keuken dweilen", Keuken, "2wk", true),
        new(C, "Ramen lappen", Woonkamer, "4wk", true),
    ];

    public static IReadOnlyList<Interval> Intervals { get; } = DueCalculator.DefaultIntervals;

    public static IReadOnlyList<Cycle> AllCycles { get; } =
    [
        new(Cycle0, 0, Day("2026-09-14"), Day("2026-10-11"), null, DateTimeOffset.UnixEpoch, "run"),
        new(Cycle1, 1, Day("2026-10-12"), Day("2026-11-08"), null, DateTimeOffset.UnixEpoch, "run"),
    ];

    public static IReadOnlyList<StatisticsOccurrence> AllOccurrences { get; } =
    [
        // cycle 0
        Occ(Cycle0, A, "2026-09-14", P1, OccurrenceStatus.Done, "2026-09-14T18:00:00Z", P1),
        Occ(Cycle0, A, "2026-09-21", P1, OccurrenceStatus.Done, "2026-09-21T18:00:00Z", P2),
        Occ(Cycle0, A, "2026-09-28", P1, OccurrenceStatus.Skipped),
        Occ(Cycle0, A, "2026-10-05", P1, OccurrenceStatus.Open),
        Occ(Cycle0, B, "2026-09-17", P2, OccurrenceStatus.Done, "2026-09-17T18:00:00Z", P2),
        Occ(Cycle0, B, "2026-10-01", P2, OccurrenceStatus.Done, "2026-10-02T09:00:00Z", P2),
        Occ(Cycle0, C, "2026-09-27", P1, OccurrenceStatus.Done, "2026-09-27T10:00:00Z", P1, planned: "2026-09-26"),

        // cycle 1
        Occ(Cycle1, A, "2026-10-12", P1, OccurrenceStatus.Done, "2026-10-12T18:00:00Z", P1),
        Occ(Cycle1, A, "2026-10-19", P1, OccurrenceStatus.Open),
        Occ(Cycle1, A, "2026-10-26", P1, OccurrenceStatus.Open),
        Occ(Cycle1, A, "2026-11-02", P1, OccurrenceStatus.Open),
        Occ(Cycle1, B, "2026-10-15", P2, OccurrenceStatus.Open),
        Occ(Cycle1, B, "2026-10-29", P2, OccurrenceStatus.Open),
        Occ(Cycle1, C, "2026-10-24", null, OccurrenceStatus.Open),
    ];

    public static DateOnly Day(string key) => DayKeys.Parse(key);

    public static DateTimeOffset Instant(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();

    /// <summary>An occurrence of a task on a day (planned on the same day unless <paramref name="planned"/> says otherwise), with the snapshot of the task.</summary>
    public static StatisticsOccurrence Occ(
        string cycle,
        string? task,
        string day,
        string? assignee,
        OccurrenceStatus status,
        string? completedAt = null,
        string? completedBy = null,
        string? planned = null,
        bool recorded = false,
        int? minutes = null,
        string? name = null,
        string? room = null)
    {
        var template = AllTasks.FirstOrDefault(t => t.Id == task);
        var duration = minutes ?? (task switch { A => 30, B => 20, C => 45, _ => 10 });
        return new StatisticsOccurrence(
            cycle,
            task,
            DayKeys.FromDayKey(Day(day), Zone),
            DayKeys.FromDayKey(Day(planned ?? day), Zone),
            assignee,
            status,
            completedAt is null ? null : Instant(completedAt),
            completedBy,
            duration,
            name ?? template?.Name ?? "Eenmalig",
            room ?? template?.RoomId,
            recorded);
    }

    public static StatisticsScope Scope(int cycles = 4, int? weeks = null, DateTimeOffset? now = null) =>
        StatisticsScope.Select(AllCycles, Timezone, Anchor, now ?? Now, new StatisticsPeriod(cycles, weeks));

    public static DateTimeOffset StartOfToday(DateTimeOffset? now = null) =>
        DayKeys.FromDayKey(DayKeys.ToDayKey(now ?? Now, Zone), Zone);
}
