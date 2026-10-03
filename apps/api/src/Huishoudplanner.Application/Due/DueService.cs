using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Due;

/// <summary>
/// The due engine use case (requirements 4.5; port of <c>routes/due.ts</c> and the data gathering of <c>domain/due.ts</c>). It reads the
/// active tasks page by page, the rooms and the occurrences of exactly those tasks, ranks them with <see cref="DueCalculator"/> and pages
/// the result. Nothing is written.
/// </summary>
public sealed class DueService(
    ForStoringTasks tasks,
    ForStoringRooms rooms,
    ForStoringSettings settings,
    ForReadingDueOccurrences occurrences,
    TimeProvider time) : IDueService
{
    private const int ReadPage = TaskListQuery.MaxLimit;

    public async Task<OneOf<DueList, ValidationErrors, SettingsMissing, PortError>> GetDueAsync(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var take = limit ?? DueListQuery.DefaultLimit;
        if (take is < 1 or > DueListQuery.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + DueListQuery.MaxLimit];
        }

        DueCursor? after = null;
        if (cursor is not null)
        {
            if (DueCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["invalid_cursor"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var ranking = await RankAsync(cancellationToken).ConfigureAwait(false);
        if (ranking.TryPickT1(out var missing, out var rest))
        {
            return missing;
        }

        if (rest.TryPickT1(out var failure, out var ranked))
        {
            return failure;
        }

        var window = ranked.Results.Where(r => after is null || after.Precedes(r)).Take(take + 1).ToList();
        var page = window.Take(take).ToList();
        var nextCursor = window.Count > take ? DueCursor.After(page[^1]).Encode() : null;

        var described = await DescribeAsync(ranked, page, cancellationToken).ConfigureAwait(false);
        if (described.TryPickT1(out var describeFailure, out var items))
        {
            return describeFailure;
        }

        return new DueList(ranked.Today, items, nextCursor, DueSummary.Of(ranked.Results));
    }

    public async Task<OneOf<DueSummary, SettingsMissing, PortError>> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var ranking = await RankAsync(cancellationToken).ConfigureAwait(false);
        if (ranking.TryPickT1(out var missing, out var rest))
        {
            return missing;
        }

        if (rest.TryPickT1(out var failure, out var ranked))
        {
            return failure;
        }

        return DueSummary.Of(ranked.Results);
    }

    private sealed record Ranked(
        DateOnly Today,
        TimeZoneInfo Zone,
        IReadOnlyList<Interval> Intervals,
        IReadOnlyDictionary<string, HouseholdTask> Tasks,
        IReadOnlyDictionary<string, DateTimeOffset> FirstPlanned,
        IReadOnlyList<DueResult> Results);

    private async Task<OneOf<Ranked, SettingsMissing, PortError>> RankAsync(CancellationToken ct)
    {
        var current = await settings.GetAsync(ct).ConfigureAwait(false);
        if (current.TryPickT1(out var missing, out var rest))
        {
            return missing;
        }

        if (rest.TryPickT1(out var settingsFailure, out var household))
        {
            return settingsFailure;
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var today = DayKeys.Today(zone, time.GetUtcNow());

        var active = new List<HouseholdTask>();
        TaskCursor? position = null;
        while (true)
        {
            var read = await tasks.ListAsync(null, true, position, ReadPage, ct).ConfigureAwait(false);
            if (read.TryPickT1(out var taskFailure, out var page))
            {
                return taskFailure;
            }

            active.AddRange(page);
            if (page.Count < ReadPage)
            {
                break;
            }

            position = TaskCursor.After(page[^1]);
        }

        var firstPlanned = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var ids in active.Select(t => t.Id).Chunk(ReadPage))
        {
            var found = await occurrences.FindFirstGeneratedPlannedDatesAsync(ids, ct).ConfigureAwait(false);
            if (found.TryPickT1(out var plannedFailure, out var planned))
            {
                return plannedFailure;
            }

            foreach (var (id, date) in planned)
            {
                firstPlanned[id] = date;
            }
        }

        var periods = household.Intervals.ToDictionary(i => i.Key, i => i.PeriodDays, StringComparer.Ordinal);
        var inputs = active.Select(t => new DueTaskInput(
            t.Id,
            t.Active,
            t.IntervalKey,
            t.LastCompletedAt,
            DueCalculator.InitialDueDateOf(
                firstPlanned.TryGetValue(t.Id, out var first) ? first : null,
                t.CreatedAt,
                periods.GetValueOrDefault(t.IntervalKey),
                zone)));
        var results = DueCalculator.ComputeDue(inputs, household.Intervals, today, zone);
        return new Ranked(today, zone, household.Intervals, active.ToDictionary(t => t.Id, StringComparer.Ordinal), firstPlanned, results);
    }

    /// <summary>The names, labels and next open occurrence of the entries of one page: reads only what the page shows.</summary>
    private async Task<OneOf<IReadOnlyList<DueItem>, PortError>> DescribeAsync(Ranked ranked, List<DueResult> page, CancellationToken ct)
    {
        if (page.Count == 0)
        {
            return OneOf<IReadOnlyList<DueItem>, PortError>.FromT0([]);
        }

        var pageTasks = page.Select(r => ranked.Tasks[r.TaskId]).ToList();
        var roomIds = pageTasks.Select(t => t.RoomId).Distinct(StringComparer.Ordinal).ToList();
        var foundRooms = await rooms.FindManyAsync(roomIds, ct).ConfigureAwait(false);
        if (foundRooms.TryPickT1(out var roomFailure, out var roomList))
        {
            return roomFailure;
        }

        var next = await occurrences.FindNextOpenAsync(DayKeys.FromDayKey(ranked.Today, ranked.Zone), page.Select(r => r.TaskId).ToList(), ct).ConfigureAwait(false);
        if (next.TryPickT1(out var nextFailure, out var upcoming))
        {
            return nextFailure;
        }

        var roomNames = roomList.ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
        var labels = ranked.Intervals.ToDictionary(i => i.Key, i => i.Label, StringComparer.Ordinal);
        var items = new List<DueItem>(page.Count);
        foreach (var result in page)
        {
            var task = ranked.Tasks[result.TaskId];
            var first = ranked.FirstPlanned.TryGetValue(task.Id, out var planned) ? planned : (DateTimeOffset?)null;
            items.Add(new DueItem(
                task.Id,
                task.Name,
                task.RoomId,
                roomNames.GetValueOrDefault(task.RoomId),
                task.IntervalKey,
                labels.GetValueOrDefault(task.IntervalKey, task.IntervalKey),
                result.PeriodDays,
                result.DaysSince,
                result.Ratio,
                result.State,
                task.LastCompletedAt,
                DueCalculator.InitialDueDateOf(first, task.CreatedAt, result.PeriodDays, ranked.Zone),
                upcoming.TryGetValue(task.Id, out var occurrence)
                    ? new DueNextOccurrence(occurrence.Id, DayKeys.ToDayKey(occurrence.Date, ranked.Zone), occurrence.AssigneeId)
                    : null));
        }

        return items;
    }
}
