using Huishoudplanner.Application.Due;
using Huishoudplanner.Application.Tests.Rooms;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Application.Tests.Tasks;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Tasks;
using OneOf;
using FixedClock = Huishoudplanner.Application.Tests.Rooms.FixedClock;
using Room = Huishoudplanner.Domain.Rooms.Room;

namespace Huishoudplanner.Application.Tests.Due;

/// <summary>An in-memory occurrence reader for the due list; it can fail and counts the questions it is asked.</summary>
internal sealed class FakeDueOccurrences : ForReadingDueOccurrences
{
    public Dictionary<string, DateTimeOffset> FirstPlanned { get; } = [];

    public Dictionary<string, UpcomingOccurrence> Next { get; } = [];

    public PortError? Failure { get; set; }

    public List<int> AskedAbout { get; } = [];

    public DateTimeOffset? NextFrom { get; private set; }

    public Task<OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>> FindFirstGeneratedPlannedDatesAsync(IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>>(failure);
        }

        AskedAbout.Add(taskIds.Count);
        IReadOnlyDictionary<string, DateTimeOffset> found = FirstPlanned.Where(p => taskIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        return Task.FromResult<OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>>(OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>.FromT0(found));
    }

    public Task<OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>> FindNextOpenAsync(DateTimeOffset from, IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>>(failure);
        }

        NextFrom = from;
        IReadOnlyDictionary<string, UpcomingOccurrence> found = Next.Where(p => taskIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        return Task.FromResult<OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>>(OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>.FromT0(found));
    }
}

/// <summary>The due engine use case against fakes (due-api.test.ts and the ranking, paging and failure rules the Node route had no tests for).</summary>
public sealed class DueServiceTests
{
    // Monday 7 December 2026, 07:00 in Amsterdam.
    private static readonly DateTimeOffset Today = new(2026, 12, 7, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        public TaskWorld Tasks { get; } = new();

        public FakeDueOccurrences Occurrences { get; } = new();

        public FixedClock Clock { get; } = new(Today);

        public Room Room { get; }

        public World()
        {
            Room = Tasks.Room("Badkamer");
        }

        public DueService Service => new(Tasks.TaskStore, Tasks.RoomStore, Tasks.SettingsStore, Occurrences, Clock);

        public HouseholdTask Task(string name, string interval, DateTimeOffset? lastCompleted = null, DateTimeOffset? createdAt = null, bool active = true, string? roomId = null)
        {
            var seeded = Tasks.Seed(name, roomId ?? Room.Id, interval, active: active);
            var task = seeded with { LastCompletedAt = lastCompleted, CreatedAt = createdAt ?? new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero) };
            Tasks.TaskStore.Items[Tasks.TaskStore.Items.FindIndex(t => t.Id == seeded.Id)] = task;
            return task;
        }

        public async Task<DueList> ListAsync(int? limit = null, string? cursor = null) => (await Service.GetDueAsync(limit, cursor, Ct)).AsT0;
    }

    [Fact]
    public async Task A_task_skipped_for_three_cycles_is_overdue_and_ranked_first_while_the_grid_still_plans_it()
    {
        var w = new World();
        var bathroom = w.Task("Badkamer schoonmaken", "1w");
        var vacuum = w.Task("Stofzuigen", "1w", lastCompleted: new DateTimeOffset(2026, 12, 6, 10, 0, 0, TimeSpan.Zero));
        var windows = w.Task("Ramen lappen", "quarter");
        w.Occurrences.FirstPlanned[bathroom.Id] = new DateTimeOffset(2026, 9, 13, 22, 0, 0, TimeSpan.Zero);
        w.Occurrences.Next[bathroom.Id] = new UpcomingOccurrence("aaaaaaaaaaaaaaaaaaaaaaaa", new DateTimeOffset(2026, 12, 6, 23, 0, 0, TimeSpan.Zero), "bbbbbbbbbbbbbbbbbbbbbbbb");

        var list = await w.ListAsync();

        list.Today.Should().Be(new DateOnly(2026, 12, 7));
        list.Items.Select(i => i.TaskId).Should().Equal(bathroom.Id, vacuum.Id, windows.Id);
        list.Items[0].Should().BeEquivalentTo(new
        {
            TaskName = "Badkamer schoonmaken",
            RoomName = "Badkamer",
            IntervalLabel = "1x per week",
            PeriodDays = 7,
            DaysSince = 91,
            Ratio = 13.0,
            State = DueState.Overdue,
            LastCompletedAt = (DateTimeOffset?)null,
            InitialDueDate = new DateOnly(2026, 9, 14),
            NextOccurrence = new DueNextOccurrence("aaaaaaaaaaaaaaaaaaaaaaaa", new DateOnly(2026, 12, 7), "bbbbbbbbbbbbbbbbbbbbbbbb"),
        });
        list.Items[1].Should().BeEquivalentTo(new { DaysSince = 1, State = DueState.Ok, NextOccurrence = (DueNextOccurrence?)null });
        list.Items[2].Should().BeEquivalentTo(new { DaysSince = 0, PeriodDays = 91, State = DueState.Ok });
        list.Summary.Should().Be(new DueSummary(0, 1));
        list.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task A_never_completed_task_stays_ok_until_its_first_planned_date_and_is_due_on_it()
    {
        var w = new World();
        var task = w.Task("Voorraad tellen", "1w");
        w.Occurrences.FirstPlanned[task.Id] = new DateTimeOffset(2026, 12, 10, 23, 0, 0, TimeSpan.Zero); // Friday 11 December, Amsterdam

        (await w.ListAsync()).Items.Single().Should().BeEquivalentTo(new { State = DueState.Ok, DaysSince = 0, InitialDueDate = new DateOnly(2026, 12, 11) });

        w.Clock.Now = new DateTimeOffset(2026, 12, 11, 8, 0, 0, TimeSpan.Zero);
        (await w.ListAsync()).Items.Single().Should().BeEquivalentTo(new { State = DueState.Due, DaysSince = 7, Ratio = 1.0 });
    }

    [Fact]
    public async Task A_task_that_was_never_planned_is_due_one_interval_after_its_creation_day()
    {
        var w = new World();
        var windows = w.Task("Ramen lappen", "quarter", createdAt: new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero));

        w.Clock.Now = new DateTimeOffset(2026, 12, 13, 6, 0, 0, TimeSpan.Zero);
        (await w.ListAsync()).Items.Single().Should().BeEquivalentTo(new { State = DueState.Ok, DaysSince = 0 });

        w.Clock.Now = new DateTimeOffset(2026, 12, 14, 6, 0, 0, TimeSpan.Zero);
        var item = (await w.ListAsync()).Items.Single();
        item.Should().BeEquivalentTo(new { TaskId = windows.Id, DaysSince = 91, Ratio = 1.0, State = DueState.Due, InitialDueDate = new DateOnly(2026, 12, 14) });
    }

    [Fact]
    public async Task A_deactivated_task_and_a_task_with_an_unknown_interval_are_left_out()
    {
        var w = new World();
        var kept = w.Task("Stofzuigen", "1w");
        w.Task("Inactief", "1w", active: false);
        w.Task("Zonder interval", "gone");

        (await w.ListAsync()).Items.Select(i => i.TaskId).Should().Equal(kept.Id);
    }

    [Fact]
    public async Task A_completion_restarts_the_clock_and_a_task_in_a_missing_room_has_no_room_name()
    {
        var w = new World();
        var task = w.Task("Badkamer schoonmaken", "1w", roomId: "cccccccccccccccccccccccc");

        (await w.ListAsync()).Items.Single().Should().BeEquivalentTo(new { RoomName = (string?)null, RoomId = "cccccccccccccccccccccccc", State = DueState.Overdue });

        w.Tasks.TaskStore.Items[0] = task with { LastCompletedAt = Today };
        (await w.ListAsync()).Items.Single().Should().BeEquivalentTo(new { DaysSince = 0, State = DueState.Ok, LastCompletedAt = Today });
    }

    [Fact]
    public async Task Ties_are_broken_by_days_since_and_then_by_task_id()
    {
        var w = new World();
        var weekly = w.Task("A", "1w", lastCompleted: new DateTimeOffset(2026, 11, 30, 10, 0, 0, TimeSpan.Zero)); // 7 / 7
        var biweekly = w.Task("B", "2wk", lastCompleted: new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero)); // 14 / 14
        var daily = w.Task("C", "daily", lastCompleted: new DateTimeOffset(2026, 12, 6, 10, 0, 0, TimeSpan.Zero)); // 1 / 1

        (await w.ListAsync()).Items.Select(i => i.TaskId).Should().Equal(biweekly.Id, weekly.Id, daily.Id);
    }

    [Fact]
    public async Task The_list_is_paged_in_ranking_order_and_the_summary_counts_the_whole_list()
    {
        var w = new World();
        var ids = Enumerable.Range(1, 5)
            .Select(n => w.Task("Taak " + n, "1w", lastCompleted: Today.AddDays(-7 * n)).Id)
            .ToList();

        var first = await w.ListAsync(limit: 2);
        first.Items.Select(i => i.TaskId).Should().Equal(ids[4], ids[3]);
        first.NextCursor.Should().NotBeNull();
        first.Summary.Should().Be(new DueSummary(1, 4));
        w.Occurrences.NextFrom.Should().Be(new DateTimeOffset(2026, 12, 6, 23, 0, 0, TimeSpan.Zero), "the next open occurrence is looked up from the start of today");

        var second = await w.ListAsync(limit: 2, cursor: first.NextCursor);
        second.Items.Select(i => i.TaskId).Should().Equal(ids[2], ids[1]);

        var last = await w.ListAsync(limit: 2, cursor: second.NextCursor);
        last.Items.Select(i => i.TaskId).Should().Equal(ids[0]);
        last.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task A_cursor_of_a_task_that_has_since_dropped_out_still_continues_from_its_position()
    {
        var w = new World();
        var a = w.Task("Taak 1", "1w", lastCompleted: Today.AddDays(-30));
        var b = w.Task("Taak 2", "1w", lastCompleted: Today.AddDays(-20));
        var c = w.Task("Taak 3", "1w", lastCompleted: Today.AddDays(-10));
        var cursor = (await w.ListAsync(limit: 1)).NextCursor;
        w.Tasks.TaskStore.Items.RemoveAll(t => t.Id == a.Id);

        (await w.ListAsync(limit: 5, cursor: cursor)).Items.Select(i => i.TaskId).Should().Equal(b.Id, c.Id);
    }

    [Fact]
    public async Task More_active_tasks_than_one_read_page_are_all_ranked()
    {
        var w = new World();
        for (var n = 0; n < 205; n++)
        {
            w.Task("Taak " + n.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "1w");
        }

        var list = await w.ListAsync(limit: 200);

        list.Items.Should().HaveCount(200);
        list.NextCursor.Should().NotBeNull();
        list.Summary.Should().Be(new DueSummary(0, 205));
        w.Occurrences.AskedAbout.Should().Equal(200, 5);
        (await w.ListAsync(limit: 200, cursor: list.NextCursor)).Items.Should().HaveCount(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-1)]
    public async Task A_limit_outside_1_to_200_is_a_validation_error_on_limit(int limit)
    {
        var w = new World();

        var result = await w.Service.GetDueAsync(limit, null, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("")]
    [InlineData("W10")]
    public async Task A_cursor_this_application_did_not_produce_is_a_validation_error_on_cursor(string cursor)
    {
        var w = new World();

        var result = await w.Service.GetDueAsync(null, cursor, Ct);

        result.AsT1.Errors["cursor"].Should().Equal("invalid_cursor");
    }

    [Fact]
    public async Task A_missing_settings_document_is_settings_missing()
    {
        var w = new World();
        var service = new DueService(w.Tasks.TaskStore, w.Tasks.RoomStore, new FakeSettingsStore(null), w.Occurrences, w.Clock);

        (await service.GetDueAsync(null, null, Ct)).IsT2.Should().BeTrue();
        (await service.GetSummaryAsync(Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_port_is_a_port_error_not_an_exception()
    {
        var w = new World();
        w.Task("Stofzuigen", "1w");

        w.Occurrences.Failure = new PortError("occurrences down");
        (await w.Service.GetDueAsync(null, null, Ct)).AsT3.Message.Should().Be("occurrences down");
        w.Occurrences.Failure = null;

        w.Tasks.RoomStore.Failure = new PortError("rooms down");
        (await w.Service.GetDueAsync(null, null, Ct)).AsT3.Message.Should().Be("rooms down");
        w.Tasks.RoomStore.Failure = null;

        w.Tasks.TaskStore.Failure = new PortError("tasks down");
        (await w.Service.GetDueAsync(null, null, Ct)).AsT3.Message.Should().Be("tasks down");
        (await w.Service.GetSummaryAsync(Ct)).AsT2.Message.Should().Be("tasks down");
    }

    [Fact]
    public async Task The_summary_counts_due_and_overdue_over_all_active_tasks()
    {
        var w = new World();
        w.Task("Overdue", "1w", lastCompleted: Today.AddDays(-14));
        w.Task("Due", "1w", lastCompleted: Today.AddDays(-7));
        w.Task("Due too", "1w", lastCompleted: Today.AddDays(-10));
        w.Task("Ok", "1w", lastCompleted: Today.AddDays(-3));
        w.Task("Inactive overdue", "1w", lastCompleted: Today.AddDays(-30), active: false);

        (await w.Service.GetSummaryAsync(Ct)).AsT0.Should().Be(new DueSummary(2, 1));
    }
}
