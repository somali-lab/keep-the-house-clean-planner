using Huishoudplanner.Application.Occurrences;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Sheets;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Export;

/// <summary>
/// The four sheets of requirements section 6 (port of <c>routes/export.ts</c> and the data gathering of <c>domain/pdf/sheets.ts</c>). The week sheets
/// are built from the generated occurrences, not from the template, whatever their status or origin, so a rescheduled item sits on the day it is
/// on now and a one-off task is printed from its snapshots. The layout rules live in <see cref="SheetBuilder"/>, the drawing in the
/// <see cref="ForRenderingSheets"/> adapter. Nothing is written.
/// </summary>
public sealed class ExportService(
    ForStoringSettings settings,
    ForStoringCycles cycles,
    ForStoringCyclePlans plans,
    ForStoringOccurrences occurrences,
    ForStoringTasks tasks,
    ForStoringRooms rooms,
    ForStoringUsers users,
    IDueService due,
    ForRenderingSheets renderer,
    TimeProvider time) : IExportService
{
    private const int ReadPage = 200;

    private const string UnknownAssignee = "?";

    public async Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportScheduleAsync(
        string? fromWeek, string? weeks, string? orientation, string? totals, string? language, CancellationToken cancellationToken)
    {
        var query = new ExportQuery();
        var first = query.Week("fromWeek", fromWeek);
        var count = query.WeekCount("weeks", weeks);
        var layout = query.Orientation("orientation", orientation);
        var withTotals = query.Flag("totals", totals);
        var sheetLanguage = query.Language("language", language);
        if (!query.IsValid)
        {
            return new ValidationErrors(query.Errors);
        }

        return Finish(await RunWeeksAsync(first!.Value, count!.Value, async (zone, inputs) =>
        {
            var sheet = SheetBuilder.Schedule(inputs, new ScheduleOptions(layout, withTotals, sheetLanguage, time.GetUtcNow(), zone));
            return (await renderer.RenderWeekScheduleAsync(sheet, cancellationToken).ConfigureAwait(false)).AsStep();
        }, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportDayAsync(
        string? day, string? language, CancellationToken cancellationToken)
    {
        var query = new ExportQuery();
        var parsed = query.Day("date", day);
        var sheetLanguage = query.Language("language", language);
        if (!query.IsValid)
        {
            return new ValidationErrors(query.Errors);
        }

        return Finish(await RunWeeksAsync(DayKeys.MondayOf(parsed!.Value), 1, async (zone, inputs) =>
        {
            var sheet = SheetBuilder.Day(inputs[0], parsed.Value, sheetLanguage, time.GetUtcNow(), zone);
            return (await renderer.RenderDayAsync(sheet, cancellationToken).ConfigureAwait(false)).AsStep();
        }, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportDueListAsync(
        string? language, CancellationToken cancellationToken)
    {
        var query = new ExportQuery();
        var sheetLanguage = query.Language("language", language);
        if (!query.IsValid)
        {
            return new ValidationErrors(query.Errors);
        }

        return Finish(await DueSheetAsync(sheetLanguage, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>> ExportTaskListAsync(
        string? language, CancellationToken cancellationToken)
    {
        var query = new ExportQuery();
        var sheetLanguage = query.Language("language", language);
        if (!query.IsValid)
        {
            return new ValidationErrors(query.Errors);
        }

        return Finish(await TaskListSheetAsync(sheetLanguage, cancellationToken).ConfigureAwait(false));
    }

    private static OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError> Finish(Step<RenderedSheet> step) =>
        step.TryGet(out var sheet, out var refusal)
            ? sheet
            : refusal.Value.Match<OneOf<RenderedSheet, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
                _ => new PortError("export.unexpected_not_found"), errors => errors, conflict => conflict, missing => missing, error => error);

    // ---- the week sheets

    private async Task<Step<RenderedSheet>> RunWeeksAsync(
        DateOnly firstMonday, int weekCount, Func<TimeZoneInfo, IReadOnlyList<WeekSheetInput>, Task<Step<RenderedSheet>>> render, CancellationToken ct)
    {
        if (!(await settings.GetAsync(ct).ConfigureAwait(false)).AsStep().TryGet(out var household, out var settingsFailure))
        {
            return settingsFailure;
        }

        var zone = DayKeys.FindZone(household.Timezone);
        if (!(await GatherWeeksAsync(household, zone, firstMonday, weekCount, ct).ConfigureAwait(false)).TryGet(out var inputs, out var failure))
        {
            return failure;
        }

        return await render(zone, inputs).ConfigureAwait(false);
    }

    /// <summary>The weeks from <paramref name="firstMonday"/> on, or <c>weeks_not_generated</c> naming every week whose cycle does not exist.</summary>
    private async Task<Step<List<WeekSheetInput>>> GatherWeeksAsync(
        HouseholdSettings household, TimeZoneInfo zone, DateOnly firstMonday, int weekCount, CancellationToken ct)
    {
        var anchor = household.CycleAnchorDate;
        var mondays = Enumerable.Range(0, weekCount).Select(i => DayKeys.AddDays(firstMonday, i * 7)).ToList();

        var cycleByIndex = new Dictionary<int, Cycle?>();
        foreach (var index in mondays.Select(m => Cycles.CycleIndexFor(m, anchor)).Distinct())
        {
            if (!(await cycles.FindByIndexAsync(index, ct).ConfigureAwait(false)).AsOptional().TryGet(out var cycle, out var cycleFailure))
            {
                return cycleFailure;
            }

            cycleByIndex[index] = cycle;
        }

        var missing = mondays.Where(m => cycleByIndex[Cycles.CycleIndexFor(m, anchor)] is null).Select(DayKeys.IsoWeekLabel).ToList();
        if (missing.Count > 0)
        {
            return new ConflictError(
                "weeks_not_generated",
                "Some weeks have not been generated yet.",
                new Dictionary<string, object?> { ["weeks"] = missing });
        }

        var rangeStart = DayKeys.FromDayKey(firstMonday, zone);
        var rangeEnd = DayKeys.FromDayKey(DayKeys.AddDays(firstMonday, weekCount * 7), zone);
        if (!(await ReadOccurrencesAsync(rangeStart, rangeEnd, ct).ConfigureAwait(false)).TryGet(out var planned, out var occurrenceFailure))
        {
            return occurrenceFailure;
        }

        if (!(await RoomFallbacksAsync(planned, ct).ConfigureAwait(false)).TryGet(out var roomOfTask, out var roomFailure))
        {
            return roomFailure;
        }

        if (!(await NamesOfAssigneesAsync(planned, ct).ConfigureAwait(false)).TryGet(out var assigneeNames, out var userFailure))
        {
            return userFailure;
        }

        var themes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var planId in cycleByIndex.Values.Select(c => c!.PlanId).Where(id => id is not null).Distinct(StringComparer.Ordinal))
        {
            if (!(await plans.FindAsync(planId!, ct).ConfigureAwait(false)).AsOptional().TryGet(out var plan, out var planFailure))
            {
                return planFailure;
            }

            themes[planId!] = plan?.WeekThemes ?? [];
        }

        var linesByDay = planned
            .GroupBy(o => DayKeys.ToDayKey(o.Date, zone))
            .ToDictionary(
                g => g.Key,
                g => g.Select(o => new SheetLineInput(
                    o.TaskNameSnapshot,
                    o.RoomNameSnapshot ?? (o.TaskId is { } taskId ? roomOfTask.GetValueOrDefault(taskId) : null),
                    o.AssigneeId is { } assignee ? assigneeNames.GetValueOrDefault(assignee, UnknownAssignee) : null,
                    o.DurationMinutesSnapshot)).ToList());

        List<WeekSheetInput> result = [.. mondays.Select(monday =>
        {
            var cycle = cycleByIndex[Cycles.CycleIndexFor(monday, anchor)]!;
            var weekIndex = Cycles.WeekIndexFor(monday, anchor);
            var theme = cycle.PlanId is { } planId && themes.TryGetValue(planId, out var weekThemes) && weekIndex < weekThemes.Count
                ? weekThemes[weekIndex]
                : string.Empty;
            return new WeekSheetInput(
                monday,
                DayKeys.AddDays(monday, 6),
                weekIndex + 1,
                theme,
                [.. Enumerable.Range(0, 7).Select(offset =>
                {
                    var day = DayKeys.AddDays(monday, offset);
                    return new SheetDayInput(day, linesByDay.TryGetValue(day, out var lines) ? lines : []);
                })]);
        })];
        return result;
    }

    private async Task<Step<List<Occurrence>>> ReadOccurrencesAsync(DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken ct)
    {
        var all = new List<Occurrence>();
        OccurrenceCursor? after = null;
        while (true)
        {
            var read = await occurrences.ListAsync(new OccurrenceQuery(from, toExclusive, null, null, after, OccurrenceListQuery.MaxLimit), ct).ConfigureAwait(false);
            if (!read.AsStep().TryGet(out var page, out var failure))
            {
                return failure;
            }

            all.AddRange(page);
            if (page.Count < OccurrenceListQuery.MaxLimit)
            {
                return all;
            }

            after = OccurrenceCursor.After(page[^1]);
        }
    }

    /// <summary>The room of a task, only for occurrences without a room snapshot (older data): the room the task sits in now.</summary>
    private async Task<Step<Dictionary<string, string?>>> RoomFallbacksAsync(List<Occurrence> planned, CancellationToken ct)
    {
        var taskIds = planned.Where(o => o.RoomNameSnapshot is null && o.TaskId is not null).Select(o => o.TaskId!).Distinct(StringComparer.Ordinal).ToList();
        var roomOfTask = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (taskIds.Count == 0)
        {
            return roomOfTask;
        }

        var taskRooms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in taskIds.Chunk(ReadPage))
        {
            if (!(await tasks.FindManyAsync(chunk, ct).ConfigureAwait(false)).AsStep().TryGet(out var found, out var failure))
            {
                return failure;
            }

            foreach (var task in found)
            {
                taskRooms[task.Id] = task.RoomId;
            }
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in taskRooms.Values.Distinct(StringComparer.Ordinal).Chunk(ReadPage))
        {
            if (!(await rooms.FindManyAsync(chunk, ct).ConfigureAwait(false)).AsStep().TryGet(out var found, out var failure))
            {
                return failure;
            }

            foreach (var room in found)
            {
                names[room.Id] = room.Name;
            }
        }

        foreach (var (taskId, roomId) in taskRooms)
        {
            roomOfTask[taskId] = names.GetValueOrDefault(roomId);
        }

        return roomOfTask;
    }

    private async Task<Step<Dictionary<string, string>>> NamesOfAssigneesAsync(List<Occurrence> planned, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in planned.Where(o => o.AssigneeId is not null).Select(o => o.AssigneeId!).Distinct(StringComparer.Ordinal))
        {
            if (!(await users.FindAsync(id, ct).ConfigureAwait(false)).AsOptional().TryGet(out var user, out var failure))
            {
                return failure;
            }

            if (user is not null)
            {
                names[id] = user.Name;
            }
        }

        return names;
    }

    // ---- the due list and the task list

    private async Task<Step<RenderedSheet>> DueSheetAsync(SheetLanguage language, CancellationToken ct)
    {
        if (!(await settings.GetAsync(ct).ConfigureAwait(false)).AsStep().TryGet(out var household, out var settingsFailure))
        {
            return settingsFailure;
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var rows = new List<DueRowInput>();
        DateOnly? today = null;
        string? cursor = null;
        do
        {
            var page = await due.GetDueAsync(DueListQuery.MaxLimit, cursor, ct).ConfigureAwait(false);
            if (page.TryPickT3(out var portError, out var rest))
            {
                return portError;
            }

            if (rest.TryPickT2(out var missing, out var rest2))
            {
                return missing;
            }

            if (rest2.TryPickT1(out var invalid, out var list))
            {
                return invalid;
            }

            today ??= list.Today;
            rows.AddRange(list.Items.Select(item => new DueRowInput(
                item.TaskName, item.RoomName, item.IntervalLabel, item.DaysSince, item.State, item.NextOccurrence?.Date)));
            cursor = list.NextCursor;
        }
        while (cursor is not null);

        var sheet = SheetBuilder.DueList(rows, today!.Value, language, time.GetUtcNow(), zone);
        return (await renderer.RenderDueListAsync(sheet, ct).ConfigureAwait(false)).AsStep();
    }

    private async Task<Step<RenderedSheet>> TaskListSheetAsync(SheetLanguage language, CancellationToken ct)
    {
        if (!(await settings.GetAsync(ct).ConfigureAwait(false)).AsStep().TryGet(out var household, out var settingsFailure))
        {
            return settingsFailure;
        }

        var all = new List<HouseholdTask>();
        TaskCursor? position = null;
        while (true)
        {
            if (!(await tasks.ListAsync(null, null, position, ReadPage, ct).ConfigureAwait(false)).AsStep().TryGet(out var page, out var taskFailure))
            {
                return taskFailure;
            }

            all.AddRange(page);
            if (page.Count < ReadPage)
            {
                break;
            }

            position = TaskCursor.After(page[^1]);
        }

        var roomNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in all.Select(t => t.RoomId).Distinct(StringComparer.Ordinal).Chunk(ReadPage))
        {
            if (!(await rooms.FindManyAsync(chunk, ct).ConfigureAwait(false)).AsStep().TryGet(out var found, out var roomFailure))
            {
                return roomFailure;
            }

            foreach (var room in found)
            {
                roomNames[room.Id] = room.Name;
            }
        }

        var labels = household.Intervals.ToDictionary(i => i.Key, i => i.Label, StringComparer.Ordinal);
        var unknownRoom = SheetBuilder.UnknownRoom(language);
        var rows = all.Select(task => new TaskListRowInput(
            task.Name,
            roomNames.GetValueOrDefault(task.RoomId, unknownRoom),
            labels.GetValueOrDefault(task.IntervalKey, task.IntervalKey),
            task.DurationMinutes,
            task.Active)).ToList();
        var sheet = SheetBuilder.TaskList(rows, language, time.GetUtcNow(), DayKeys.FindZone(household.Timezone));
        return (await renderer.RenderTaskListAsync(sheet, ct).ConfigureAwait(false)).AsStep();
    }
}
