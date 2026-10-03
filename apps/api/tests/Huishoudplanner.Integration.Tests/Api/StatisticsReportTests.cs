using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The fixed dataset of <c>apps/server/test/stats.test.ts</c>. Anchor Monday 2026-09-14; cycle 0 = 14 Sep to 11 Oct, cycle 1 = 12 Oct to 8 Nov.
///   A "Badkamer schoonmaken" (Badkamer, 1w, 30 min): every Monday, Persoon 1
///   B "Keuken dweilen"       (Keuken, 2wk, 20 min): Thursday of weeks 1 and 3, Persoon 2
///   C "Ramen lappen"         (Woonkamer, 4wk, 45 min): Saturday of week 2, unassigned
/// Actions in cycle 0:
///   A 14 Sep done by P1 · A 21 Sep done by P2 · A 28 Sep skipped · A 5 Oct left open
///   B 17 Sep done by P2 · B 1 Oct done by P2 (on 2 Oct)
///   C moved from 26 to 27 Sep, then done by P1 (claims it: assignee becomes P1)
/// Cycle 1: A 12 Oct done by P1; the rest still to come. Statistics are asked on Wednesday 14 Oct 2026.
/// The occurrences are stored directly (the occurrence actions are slice 3.2); the rest of the dataset is what generation would produce.
/// </summary>
public sealed class StatisticsDataset : IAsyncLifetime
{
    private readonly MongoContainerFixture mongo;

    public StatisticsDataset(MongoContainerFixture mongo) => this.mongo = mongo;

    public StatisticsHarness H { get; private set; } = null!;

    public string P1 { get; private set; } = "";

    public string P2 { get; private set; } = "";

    public string TaskA { get; private set; } = "";

    public string TaskB { get; private set; } = "";

    public string TaskC { get; private set; } = "";

    public string Woonkamer { get; private set; } = "";

    public string Cycle1 { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        H = new StatisticsHarness(mongo);
        // The clients and the seeds start with the first request.
        (await H.SendAsync(HttpMethod.Get, "/api/v2/health")).Status.Should().Be(HttpStatusCode.OK);
        (P1, P2) = (await H.PersonAsync("Persoon 1"), await H.PersonAsync("Persoon 2"));
        var (badkamer, keuken) = (await H.InsertRoomAsync("Badkamer"), await H.InsertRoomAsync("Keuken"));
        Woonkamer = await H.InsertRoomAsync("Woonkamer");
        TaskA = await H.InsertTaskAsync("Badkamer schoonmaken", badkamer, "1w", 30);
        TaskB = await H.InsertTaskAsync("Keuken dweilen", keuken, "2wk", 20);
        TaskC = await H.InsertTaskAsync("Ramen lappen", Woonkamer, "4wk", 45);
        var cycle0 = await H.InsertCycleAsync(0, "2026-09-14", "2026-10-11");
        Cycle1 = await H.InsertCycleAsync(1, "2026-10-12", "2026-11-08");

        Task Add(string cycle, string task, string name, int minutes, string room, string day, string? assignee, string status, string? completedAt = null, string? completedBy = null, string? planned = null) =>
            H.InsertOccurrenceAsync(cycle, task, day, assignee, status, completedAt, completedBy, planned, minutes, name, room, skipReason: status == "skipped" ? "ziek" : null);
        const string A = "Badkamer schoonmaken", B = "Keuken dweilen", C = "Ramen lappen";

        // cycle 0
        await Add(cycle0, TaskA, A, 30, badkamer, "2026-09-14", P1, "done", "2026-09-14T18:00:00Z", P1);
        await Add(cycle0, TaskA, A, 30, badkamer, "2026-09-21", P1, "done", "2026-09-21T18:00:00Z", P2);
        await Add(cycle0, TaskA, A, 30, badkamer, "2026-09-28", P1, "skipped");
        await Add(cycle0, TaskA, A, 30, badkamer, "2026-10-05", P1, "open");
        await Add(cycle0, TaskB, B, 20, keuken, "2026-09-17", P2, "done", "2026-09-17T18:00:00Z", P2);
        await Add(cycle0, TaskB, B, 20, keuken, "2026-10-01", P2, "done", "2026-10-02T09:00:00Z", P2);
        await Add(cycle0, TaskC, C, 45, Woonkamer, "2026-09-27", P1, "done", "2026-09-27T10:00:00Z", P1, planned: "2026-09-26");

        // cycle 1
        await Add(Cycle1, TaskA, A, 30, badkamer, "2026-10-12", P1, "done", "2026-10-12T18:00:00Z", P1);
        foreach (var day in new[] { "2026-10-19", "2026-10-26", "2026-11-02" })
        {
            await Add(Cycle1, TaskA, A, 30, badkamer, day, P1, "open");
        }

        await Add(Cycle1, TaskB, B, 20, keuken, "2026-10-15", P2, "open");
        await Add(Cycle1, TaskB, B, 20, keuken, "2026-10-29", P2, "open");
        await Add(Cycle1, TaskC, C, 45, Woonkamer, "2026-10-24", null, "open");

        H.Clock.Set("2026-10-14T08:00:00.000Z");
    }

    public ValueTask DisposeAsync()
    {
        H.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Ports the report scenarios of <c>apps/server/test/stats.test.ts</c> against the real host and a real MongoDB replica set.</summary>
public sealed class StatisticsReportTests(StatisticsDataset data) : IClassFixture<StatisticsDataset>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int[] Minutes(JsonElement users, string id)
    {
        var user = users.EnumerateArray().Single(u => u.GetProperty("userId").GetString() == id);
        return [user.GetProperty("plannedMinutes").GetInt32(), user.GetProperty("doneMinutes").GetInt32()];
    }

    private static List<string> Strings(JsonElement array, string property) => [.. array.EnumerateArray().Select(e => e.GetProperty(property).GetString()!)];

    // ---- workload

    [Fact]
    public async Task Workload_reportsPlannedByAssigneeAndDoneByCompletedBy_perWeekAndCycle()
    {
        var cycles = (await data.H.GetAsync("/api/v2/stats/workload?cycles=2")).GetProperty("cycles");

        cycles.EnumerateArray().Select(c => (c.GetProperty("index").GetInt32(), c.GetProperty("startDate").GetString(), c.GetProperty("endDate").GetString()))
            .Should().Equal((0, "2026-09-14", "2026-10-11"), (1, "2026-10-12", "2026-11-08"));
        var (c0, c1) = (cycles[0], cycles[1]);
        Strings(c0.GetProperty("weeks"), "startDate").Should().Equal("2026-09-14", "2026-09-21", "2026-09-28", "2026-10-05");
        c0.GetProperty("weeks").EnumerateArray().Select(w => Minutes(w.GetProperty("users"), data.P1)).Should().BeEquivalentTo(new int[][] { [30, 30], [75, 45], [30, 0], [30, 0] }, o => o.WithStrictOrdering());
        c0.GetProperty("weeks").EnumerateArray().Select(w => Minutes(w.GetProperty("users"), data.P2)).Should().BeEquivalentTo(new int[][] { [20, 20], [0, 30], [20, 20], [0, 0] }, o => o.WithStrictOrdering());
        Minutes(c0.GetProperty("users"), data.P1).Should().Equal(165, 75);
        Minutes(c0.GetProperty("users"), data.P2).Should().Equal(40, 70);
        c0.GetProperty("unassignedPlannedMinutes").GetInt32().Should().Be(0);

        c1.GetProperty("weeks").EnumerateArray().Select(w => Minutes(w.GetProperty("users"), data.P1)).Should().BeEquivalentTo(new int[][] { [30, 30], [30, 0], [30, 0], [30, 0] }, o => o.WithStrictOrdering());
        Minutes(c1.GetProperty("users"), data.P1).Should().Equal(120, 30);
        Minutes(c1.GetProperty("users"), data.P2).Should().Equal(40, 0);
        c1.GetProperty("unassignedPlannedMinutes").GetInt32().Should().Be(45);
        c1.GetProperty("weeks").EnumerateArray().Select(w => w.GetProperty("unassignedPlannedMinutes").GetInt32()).Should().Equal(0, 45, 0, 0);
    }

    [Fact]
    public async Task Workload_withoutAPeriod_defaultsToFourCycles_andLimitsToTheRequestedNumberOfRecentCycles()
    {
        (await data.H.GetAsync("/api/v2/stats/workload")).GetProperty("cycles").EnumerateArray().Select(c => c.GetProperty("index").GetInt32()).Should().Equal(0, 1);
        (await data.H.GetAsync("/api/v2/stats/workload?cycles=1")).GetProperty("cycles").EnumerateArray().Select(c => c.GetProperty("index").GetInt32()).Should().Equal(1);
    }

    [Fact]
    public async Task Workload_limitsToThisWeekOrTheLastThreeCalendarWeeks()
    {
        var current = (await data.H.GetAsync("/api/v2/stats/workload?weeks=1")).GetProperty("cycles");
        current.GetArrayLength().Should().Be(1);
        Strings(current[0].GetProperty("weeks"), "startDate").Should().Equal("2026-10-12");

        var recent = (await data.H.GetAsync("/api/v2/stats/workload?weeks=3")).GetProperty("cycles");
        recent.EnumerateArray().SelectMany(c => Strings(c.GetProperty("weeks"), "startDate")).Should().Equal("2026-09-28", "2026-10-05", "2026-10-12");
    }

    [Fact]
    public async Task Workload_doesNotChangeHistory_whenATaskDurationIsHalved()
    {
        var before = (await data.H.GetAsync("/api/v2/stats/workload?cycles=2")).GetRawText();

        await data.H.Tasks.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(data.TaskA)), new BsonDocument("$set", new BsonDocument("durationMinutes", 15)), cancellationToken: Ct);

        (await data.H.GetAsync("/api/v2/stats/workload?cycles=2")).GetRawText().Should().Be(before);
        await data.H.Tasks.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(data.TaskA)), new BsonDocument("$set", new BsonDocument("durationMinutes", 30)), cancellationToken: Ct);
    }

    // ---- completion

    [Fact]
    public async Task Completion_byTask_isDoneOverDoneSkippedAndStillOpenInThePast_worstFirst()
    {
        var body = await data.H.GetAsync("/api/v2/stats/completion?cycles=2&groupBy=task");

        body.GetProperty("groupBy").GetString().Should().Be("task");
        var rows = body.GetProperty("rows");
        Strings(rows, "key").Should().Equal(data.TaskA, data.TaskB, data.TaskC);
        Strings(rows, "name").Should().Equal("Badkamer schoonmaken", "Keuken dweilen", "Ramen lappen");
        rows.EnumerateArray().Select(r => (r.GetProperty("done").GetInt32(), r.GetProperty("skipped").GetInt32(), r.GetProperty("missed").GetInt32(), r.GetProperty("rate").GetDouble()))
            .Should().Equal((3, 1, 1, 0.6), (2, 0, 0, 1.0), (1, 0, 0, 1.0));
    }

    [Fact]
    public async Task Completion_byRoom_usesTheRoomOfTheTask()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/completion?cycles=2&groupBy=room")).GetProperty("rows");

        rows.EnumerateArray().Select(r => (r.GetProperty("name").GetString(), r.GetProperty("done").GetInt32(), r.GetProperty("skipped").GetInt32(), r.GetProperty("missed").GetInt32(), r.GetProperty("rate").GetDouble()))
            .Should().Equal(("Badkamer", 3, 1, 1, 0.6), ("Keuken", 2, 0, 0, 1.0), ("Woonkamer", 1, 0, 0, 1.0));
    }

    [Fact]
    public async Task Completion_byUser_groupsByAssignee()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/completion?cycles=2&groupBy=user")).GetProperty("rows");

        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("key").GetString().Should().Be(data.P1);
        rows[0].GetProperty("name").GetString().Should().Be("Persoon 1");
        (rows[0].GetProperty("done").GetInt32(), rows[0].GetProperty("skipped").GetInt32(), rows[0].GetProperty("missed").GetInt32()).Should().Be((4, 1, 1));
        rows[0].GetProperty("rate").GetDouble().Should().BeApproximately(4 / 6.0, 1e-10);
        rows[1].GetProperty("key").GetString().Should().Be(data.P2);
        (rows[1].GetProperty("done").GetInt32(), rows[1].GetProperty("rate").GetDouble()).Should().Be((2, 1.0));
    }

    [Fact]
    public async Task Completion_appliesTheWeekPeriod()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/completion?weeks=1&groupBy=task")).GetProperty("rows");

        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("key").GetString().Should().Be(data.TaskA);
        rows[0].GetProperty("done").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("/api/v2/stats/completion?cycles=2", "groupBy")]
    [InlineData("/api/v2/stats/completion?groupBy=planet", "groupBy")]
    [InlineData("/api/v2/stats/workload?cycles=0", "cycles")]
    [InlineData("/api/v2/stats/workload?cycles=27", "cycles")]
    [InlineData("/api/v2/stats/workload?cycles=abc", "cycles")]
    [InlineData("/api/v2/stats/workload?weeks=4", "weeks")]
    [InlineData("/api/v2/stats/workload?weeks=0", "weeks")]
    [InlineData("/api/v2/stats/intervals?weeks=x", "weeks")]
    [InlineData("/api/v2/stats/deviations?cycles=-1", "cycles")]
    public async Task Reports_validateTheQuery_withAFieldKeyedError(string url, string field)
    {
        var (status, body) = await data.H.SendAsync(HttpMethod.Get, url);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
    }

    [Fact]
    public async Task Reports_needNoProfile()
    {
        foreach (var url in new[] { "workload", "completion?groupBy=task", "intervals", "deviations" })
        {
            (await data.H.SendAsync(HttpMethod.Get, "/api/v2/stats/" + url)).Status.Should().Be(HttpStatusCode.OK, url);
        }
    }

    // ---- intervals

    [Fact]
    public async Task Intervals_compareAverageDaysBetweenCompletionsWithThePeriod_mostWishfulFirst()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/intervals?cycles=2")).GetProperty("rows");

        rows.EnumerateArray().Select(r => (r.GetProperty("name").GetString(), r.GetProperty("periodDays").GetInt32(), r.GetProperty("completions").GetInt32(), r.GetProperty("averageDays").ValueKind == JsonValueKind.Null ? (double?)null : r.GetProperty("averageDays").GetDouble()))
            .Should().Equal(("Badkamer schoonmaken", 7, 3, (double?)14), ("Keuken dweilen", 14, 2, 15), ("Ramen lappen", 28, 1, null));
        rows[0].GetProperty("intervalKey").GetString().Should().Be("1w");
        rows[0].GetProperty("deviation").GetDouble().Should().Be(2);
        rows[1].GetProperty("deviation").GetDouble().Should().BeApproximately(15 / 14.0, 1e-10);
        rows[2].GetProperty("deviation").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- deviations

    [Fact]
    public async Task Deviations_separatePlanChangesFromEarlyOrLateCompletion()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/deviations?cycles=2")).GetProperty("rows");

        Strings(rows, "taskId").Should().Equal(data.TaskB, data.TaskC, data.TaskA);
        Strings(rows, "name").Should().Equal("Keuken dweilen", "Ramen lappen", "Badkamer schoonmaken");
        rows.EnumerateArray().Select(r => (r.GetProperty("completions").GetInt32(), r.GetProperty("averagePlanningShiftDays").GetDouble(), r.GetProperty("averageCompletionDelayDays").GetDouble(), r.GetProperty("early").GetInt32(), r.GetProperty("onTime").GetInt32(), r.GetProperty("late").GetInt32()))
            .Should().Equal((2, 0.0, 0.5, 0, 1, 1), (1, 1.0, 0.0, 0, 1, 0), (3, 0.0, 0.0, 0, 3, 0));
    }
}

/// <summary>Ports the "statistics with one-off tasks (taskId null)" scenarios of <c>apps/server/test/stats.test.ts</c>; the one-offs are stored directly (slice 3.3 creates them through the API).</summary>
public sealed class StatisticsOneOffTests(StatisticsDataset data) : IClassFixture<StatisticsDataset>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // A fixture is shared by the tests of the class, so the one-offs are added once.
        if (await data.H.Occurrences.CountDocumentsAsync(new BsonDocument("taskId", BsonNull.Value), cancellationToken: Ct) > 0)
        {
            return;
        }

        await data.H.InsertOccurrenceAsync(data.Cycle1, null, "2026-10-14", data.P2, "done", "2026-10-14T08:00:00Z", data.P2, null, 40, "Gordijnen ophangen", data.Woonkamer, recorded: true, origin: "adhoc");
        await data.H.InsertOccurrenceAsync(data.Cycle1, null, "2026-10-14", data.P1, "done", "2026-10-14T08:00:00Z", data.P1, null, 25, "Kast ophalen", null, recorded: true, origin: "adhoc");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task OneOffTasks_countInWorkloadAsPlannedAndDoneMinutes()
    {
        var cycles = (await data.H.GetAsync("/api/v2/stats/workload?cycles=2")).GetProperty("cycles");
        var cycle1 = cycles.EnumerateArray().Single(c => c.GetProperty("index").GetInt32() == 1);

        var users = cycle1.GetProperty("users").EnumerateArray().ToDictionary(u => u.GetProperty("userId").GetString()!);
        (users[data.P1].GetProperty("plannedMinutes").GetInt32(), users[data.P1].GetProperty("doneMinutes").GetInt32()).Should().Be((120 + 25, 30 + 25));
        (users[data.P2].GetProperty("plannedMinutes").GetInt32(), users[data.P2].GetProperty("doneMinutes").GetInt32()).Should().Be((40 + 40, 40));
        cycle1.GetProperty("unassignedPlannedMinutes").GetInt32().Should().Be(45);
    }

    [Fact]
    public async Task OneOffTasks_showAsOneCombinedRowWithoutAKey_whenGroupedByTask()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/completion?cycles=2&groupBy=task")).GetProperty("rows").EnumerateArray().ToList();

        var combined = rows.Where(r => r.GetProperty("key").ValueKind == JsonValueKind.Null).Should().ContainSingle().Subject;
        (combined.GetProperty("name").GetString(), combined.GetProperty("done").GetInt32(), combined.GetProperty("skipped").GetInt32(), combined.GetProperty("missed").GetInt32(), combined.GetProperty("rate").GetDouble()).Should().Be(("", 2, 0, 0, 1.0));
        rows.Where(r => r.GetProperty("key").ValueKind != JsonValueKind.Null).Select(r => r.GetProperty("name").GetString()!).Order().Should().Equal("Badkamer schoonmaken", "Keuken dweilen", "Ramen lappen");
    }

    [Fact]
    public async Task OneOffTasks_groupByTheRoomSnapshot_withOneRowWithoutARoomForRoomlessOnes()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/completion?cycles=2&groupBy=room")).GetProperty("rows").EnumerateArray().ToList();

        rows.Where(r => r.GetProperty("key").ValueKind != JsonValueKind.Null).ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("done").GetInt32())
            .Should().Contain(new Dictionary<string, int> { ["Badkamer"] = 3, ["Keuken"] = 2, ["Woonkamer"] = 2 });
        rows.Single(r => r.GetProperty("key").ValueKind == JsonValueKind.Null).GetProperty("done").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task OneOffTasks_countForThePersonWhoDidThem()
    {
        var rows = (await data.H.GetAsync("/api/v2/stats/completion?cycles=2&groupBy=user")).GetProperty("rows").EnumerateArray().ToList();

        rows.Single(r => r.GetProperty("key").GetString() == data.P1).GetProperty("done").GetInt32().Should().Be(5);
        rows.Single(r => r.GetProperty("key").GetString() == data.P2).GetProperty("done").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task OneOffTasks_areLeftOutOfIntervalAndDeviationStatistics()
    {
        var intervals = (await data.H.GetAsync("/api/v2/stats/intervals?cycles=2")).GetProperty("rows");
        intervals.EnumerateArray().Select(r => (r.GetProperty("name").GetString(), r.GetProperty("completions").GetInt32()))
            .Should().Equal(("Badkamer schoonmaken", 3), ("Keuken dweilen", 2), ("Ramen lappen", 1));

        var deviations = (await data.H.GetAsync("/api/v2/stats/deviations?cycles=2")).GetProperty("rows");
        deviations.GetArrayLength().Should().Be(3);
        deviations.EnumerateArray().Should().OnlyContain(r => r.GetProperty("taskId").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task EveryReport_isAnswered_forAPeriodThatContainsOnlyOneOffWork()
    {
        foreach (var url in new[]
        {
            "/api/v2/stats/workload?weeks=1",
            "/api/v2/stats/completion?weeks=1&groupBy=task",
            "/api/v2/stats/completion?weeks=1&groupBy=room",
            "/api/v2/stats/intervals?weeks=1",
            "/api/v2/stats/deviations?weeks=1",
        })
        {
            (await data.H.SendAsync(HttpMethod.Get, url)).Status.Should().Be(HttpStatusCode.OK, url);
        }
    }
}
