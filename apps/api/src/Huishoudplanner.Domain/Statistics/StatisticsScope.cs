using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Generation;

namespace Huishoudplanner.Domain.Statistics;

/// <summary>
/// What a report covers: the cycles of the period, oldest first, and, for a calendar-week period, the instants and day keys it is cut off at
/// (<see cref="From"/> inclusive, <see cref="To"/> exclusive; all <see langword="null"/> for a period of cycles).
/// </summary>
public sealed record StatisticsScope(
    TimeZoneInfo Zone,
    IReadOnlyList<Cycle> Cycles,
    DateTimeOffset? From,
    DateTimeOffset? To,
    DateOnly? FromKey,
    DateOnly? ToKey)
{
    /// <summary>The last N cycles up to and including the current one, or the cycles that touch the last N calendar weeks including the current one (port of <c>scope()</c> in <c>domain/stats.ts</c>).</summary>
    public static StatisticsScope Select(IReadOnlyList<Cycle> allCycles, string timezone, DateOnly anchor, DateTimeOffset now, StatisticsPeriod period)
    {
        ArgumentNullException.ThrowIfNull(allCycles);
        ArgumentNullException.ThrowIfNull(period);
        var zone = DayKeys.FindZone(timezone);
        var today = DayKeys.ToDayKey(now, zone);
        var current = Calendar.Cycles.CycleIndexFor(today, anchor);
        var currentMonday = DayKeys.MondayOf(today);
        DateOnly? fromKey = period.Weeks is { } weeks ? DayKeys.AddDays(currentMonday, -(weeks - 1) * 7) : null;
        DateOnly? toKey = period.Weeks is not null ? DayKeys.AddDays(currentMonday, 7) : null;

        var newestFirst = allCycles
            .Where(c => c.Index <= current && (fromKey is null || c.EndDate >= fromKey) && (toKey is null || c.StartDate < toKey))
            .OrderByDescending(c => c.Index);
        var selected = (period.Weeks is null ? newestFirst.Take(period.Cycles) : newestFirst).Reverse().ToList();
        return new StatisticsScope(
            zone,
            selected,
            fromKey is { } from ? DayKeys.FromDayKey(from, zone) : null,
            toKey is { } to ? DayKeys.FromDayKey(to, zone) : null,
            fromKey,
            toKey);
    }

    /// <summary>Whether the occurrence belongs to a cycle of the scope and, for a week period, lies inside it.</summary>
    public bool Contains(StatisticsOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return Cycles.Any(c => c.Id == occurrence.CycleId) && (From is null || To is null || (occurrence.Date >= From && occurrence.Date < To));
    }
}
