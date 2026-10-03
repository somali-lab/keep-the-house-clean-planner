using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/due-api.test.ts to <c>GET /api/v2/due</c> on the real host and a real MongoDB replica set: a never-completed task stays
/// ok until its first planned date, three skipped cycles make a task overdue while the grid looks tidy, the ranking, a quarterly task due exactly
/// at its period, deactivation, the restart and restore of the clock by an extra execution, and the nightly summary. Where the Node test used the
/// occurrence actions of slice 3.2 and 3.3 (skip, complete, extra execution and its retract) the state is arranged by direct writes to the
/// documents those actions would leave (<c>occurrences.status</c>, <c>tasks.lastCompletedAt</c>); the due list reads only those fields. Not ported:
/// the <c>/export/pdf/due</c> block (the PDF export is slice 6.4) and the nightly job call (<c>POST /jobs/generation</c> is slice 6.3; the summary
/// it reports is asserted through the same <see cref="IDueService"/>). New: paging, field-keyed query errors, the open policy and the reader's rules.
/// </summary>
public sealed class DueEndpointTests(MongoContainerFixture mongo)
{
    // Anchor Monday 2026-09-14. Cycle starts: 14 Sep, 12 Oct, 9 Nov, 7 Dec.
    private static readonly string[] CycleStarts = ["2026-09-14", "2026-10-12", "2026-11-09", "2026-12-07"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Arranged(GenerationHarness H, string Badkamer, string Stofzuigen, string Ramen, string Person, JsonElement SlotWarnings) : IDisposable
    {
        public void Dispose() => H.Dispose();
    }

    /// <summary>Three bathroom cleans skipped for three cycles, vacuuming done on the last Sunday of cycle 2, then the nightly run of 7 December.</summary>
    private async Task<Arranged> ArrangeAsync()
    {
        var h = new GenerationHarness(mongo, $"{CycleStarts[0]}T06:00:00.000Z");
        var room = await h.SeedRoomAsync("Badkamer");
        var person = await h.SeedPersonAsync("Sven");
        var badkamer = await h.NewTaskAsync("Badkamer schoonmaken", room, "1w", 20);
        var stofzuigen = await h.NewTaskAsync("Stofzuigen", room, "1w", 20);
        var ramen = await h.NewTaskAsync("Ramen lappen", room, "quarter", 20);
        var plan = await h.ActivePlanIdAsync();
        var put = await h.PutSlotsAsync(
            plan,
            [.. Enumerable.Range(0, 4).SelectMany(w => new (string, int, int, string?)[] { (badkamer, w, 1, person), (stofzuigen, w, 2, null) })]);

        foreach (var start in CycleStarts.Take(3))
        {
            h.Clock.Set($"{start}T06:00:00.000Z");
            await h.GenerateUpcomingAsync();
            await h.Occurrences.UpdateManyAsync(
                new BsonDocument { { "taskId", ObjectId.Parse(badkamer) }, { "status", "open" }, { "date", InCycle(start) } },
                new BsonDocument("$set", new BsonDocument { { "status", "skipped" }, { "skipReason", "geen zin" } }),
                cancellationToken: Ct);
        }

        h.Clock.Set("2026-12-06T10:00:00.000Z");
        var lastVacuum = (await h.OccurrencesOfAsync(stofzuigen, new BsonDocument("date", InCycle(CycleStarts[2])))).Last();
        await h.Occurrences.UpdateOneAsync(
            new BsonDocument("_id", lastVacuum["_id"]),
            new BsonDocument("$set", new BsonDocument { { "status", "done" }, { "completedAt", new BsonDateTime(h.Clock.GetUtcNow().UtcDateTime) } }),
            cancellationToken: Ct);
        await SetLastCompletedAsync(h, stofzuigen, h.Clock.GetUtcNow());

        h.Clock.Set($"{CycleStarts[3]}T06:00:00.000Z");
        await h.GenerateUpcomingAsync();
        return new Arranged(h, badkamer, stofzuigen, ramen, person, put.GetProperty("warnings"));
    }

    private static BsonDocument InCycle(string start) => new()
    {
        { "$gte", new BsonDateTime(Amsterdam(start)) },
        { "$lt", new BsonDateTime(Amsterdam(DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture).AddDays(28).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))) },
    };

    private static DateTime Amsterdam(string day) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(day, System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"));

    private static async Task SetLastCompletedAsync(GenerationHarness h, string taskId, DateTimeOffset? at) =>
        await h.Tasks.UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(taskId)),
            new BsonDocument("$set", new BsonDocument("lastCompletedAt", at is { } instant ? new BsonDateTime(instant.UtcDateTime) : BsonNull.Value)),
            cancellationToken: Ct);

    private static async Task<JsonElement[]> DueItemsAsync(GenerationHarness h)
    {
        var (status, body) = await h.SendAsync(HttpMethod.Get, "/api/v2/due", withProfile: false);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private static JsonElement Find(JsonElement[] items, string taskId) => items.Single(i => i.GetProperty("taskId").GetString() == taskId);

    [Fact]
    public async Task A_never_completed_task_stays_ok_until_its_first_planned_date()
    {
        using var h = new GenerationHarness(mongo, "2026-09-20T08:00:00.000Z");
        var room = await h.SeedRoomAsync("Badkamer");
        var person = await h.SeedPersonAsync("Sven");
        var task = await h.NewTaskAsync("Voorraad tellen", room, "1w", 10);
        var plan = await h.ActivePlanIdAsync();
        (await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", new { cycleAnchorDate = "2026-09-21" }, asAdmin: true)).Status.Should().Be(HttpStatusCode.OK);
        await h.PutSlotsAsync(plan, [.. Enumerable.Range(0, 4).Select(w => (task, w, 5, (string?)person))]);
        await h.GenerateUpcomingAsync();

        var before = Find(await DueItemsAsync(h), task);
        before.GetProperty("state").GetString().Should().Be("ok");
        before.GetProperty("nextOccurrence").GetProperty("date").GetString().Should().Be("2026-09-25");

        h.Clock.Set("2026-09-25T08:00:00.000Z");
        var onDate = Find(await DueItemsAsync(h), task);
        onDate.GetProperty("state").GetString().Should().Be("due");
        onDate.GetProperty("nextOccurrence").GetProperty("date").GetString().Should().Be("2026-09-25");
    }

    [Fact]
    public async Task A_task_skipped_for_three_cycles_is_overdue_while_the_grid_looks_tidy()
    {
        using var a = await ArrangeAsync();
        a.SlotWarnings.EnumerateArray().Where(w => w.TryGetProperty("taskId", out var id) && id.GetString() == a.Badkamer).Should().BeEmpty();

        var first = (await DueItemsAsync(a.H))[0];

        first.GetProperty("taskId").GetString().Should().Be(a.Badkamer);
        first.GetProperty("taskName").GetString().Should().Be("Badkamer schoonmaken");
        first.GetProperty("roomName").GetString().Should().Be("Badkamer");
        first.GetProperty("intervalLabel").GetString().Should().Be("1x per week");
        first.GetProperty("periodDays").GetInt32().Should().Be(7);
        first.GetProperty("daysSince").GetInt32().Should().Be(91);
        first.GetProperty("ratio").GetDouble().Should().Be(13);
        first.GetProperty("state").GetString().Should().Be("overdue");
        first.GetProperty("lastCompletedAt").ValueKind.Should().Be(JsonValueKind.Null);
        first.GetProperty("initialDueDate").GetString().Should().Be("2026-09-14");
        var next = first.GetProperty("nextOccurrence");
        next.GetProperty("date").GetString().Should().Be("2026-12-07", "the grid still has it planned today");
        next.GetProperty("assigneeId").GetString().Should().Be(a.Person);
    }

    [Fact]
    public async Task All_active_tasks_are_ranked_and_a_recently_done_task_stays_ok()
    {
        using var a = await ArrangeAsync();

        var list = await DueItemsAsync(a.H);

        list.Select(i => i.GetProperty("taskId").GetString()).Should().Equal(a.Badkamer, a.Stofzuigen, a.Ramen);
        Find(list, a.Stofzuigen).GetProperty("daysSince").GetInt32().Should().Be(1);
        Find(list, a.Stofzuigen).GetProperty("state").GetString().Should().Be("ok");
        var ramen = Find(list, a.Ramen);
        ramen.GetProperty("daysSince").GetInt32().Should().Be(0);
        ramen.GetProperty("periodDays").GetInt32().Should().Be(91);
        ramen.GetProperty("state").GetString().Should().Be("ok");
        ramen.GetProperty("nextOccurrence").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_quarterly_task_is_due_exactly_at_its_period_counted_from_its_creation_date()
    {
        using var a = await ArrangeAsync();

        a.H.Clock.Set("2026-12-14T06:00:00.000Z"); // 91 days after 14 Sep
        var ramen = Find(await DueItemsAsync(a.H), a.Ramen);

        ramen.GetProperty("daysSince").GetInt32().Should().Be(91);
        ramen.GetProperty("ratio").GetDouble().Should().Be(1);
        ramen.GetProperty("state").GetString().Should().Be("due");
    }

    [Fact]
    public async Task A_deactivated_task_drops_out_of_the_list_and_returns_when_it_is_activated_again()
    {
        using var a = await ArrangeAsync();

        (await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{a.Ramen}", new { active = false })).Status.Should().Be(HttpStatusCode.OK);
        (await DueItemsAsync(a.H)).Select(i => i.GetProperty("taskId").GetString()).Should().NotContain(a.Ramen);

        (await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{a.Ramen}", new { active = true })).Status.Should().Be(HttpStatusCode.OK);
        (await DueItemsAsync(a.H)).Select(i => i.GetProperty("taskId").GetString()).Should().Contain(a.Ramen);
    }

    [Fact]
    public async Task A_recorded_completion_restarts_the_clock_and_clearing_it_restores_the_overdue_state()
    {
        using var a = await ArrangeAsync();
        var first = (await DueItemsAsync(a.H))[0];
        first.GetProperty("taskId").GetString().Should().Be(a.Badkamer);
        first.GetProperty("state").GetString().Should().Be("overdue");

        // What an extra execution recorded as done leaves on the task, and what retracting it restores (slice 3.3 writes both).
        await SetLastCompletedAsync(a.H, a.Badkamer, a.H.Clock.GetUtcNow());
        var restarted = Find(await DueItemsAsync(a.H), a.Badkamer);
        restarted.GetProperty("daysSince").GetInt32().Should().Be(0);
        restarted.GetProperty("state").GetString().Should().Be("ok");
        restarted.GetProperty("lastCompletedAt").GetDateTimeOffset().Should().Be(a.H.Clock.GetUtcNow());

        await SetLastCompletedAsync(a.H, a.Badkamer, null);
        var restored = (await DueItemsAsync(a.H))[0];
        restored.GetProperty("taskId").GetString().Should().Be(a.Badkamer);
        restored.GetProperty("state").GetString().Should().Be("overdue");
        restored.GetProperty("lastCompletedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_summary_is_what_the_nightly_job_reports()
    {
        using var a = await ArrangeAsync();

        var (_, body) = await a.H.SendAsync(HttpMethod.Get, "/api/v2/due", withProfile: false);
        body.GetProperty("summary").GetProperty("due").GetInt32().Should().Be(0);
        body.GetProperty("summary").GetProperty("overdue").GetInt32().Should().Be(1);
        body.GetProperty("today").GetString().Should().Be("2026-12-07");

        using var scope = a.H.Services.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IDueService>().GetSummaryAsync(Ct);
        summary.AsT0.Should().Be(new DueSummary(0, 1));
    }

    [Fact]
    public async Task The_list_needs_no_profile_and_pages_with_limit_and_cursor_without_gaps_or_repeats()
    {
        using var a = await ArrangeAsync();

        var (status, first) = await a.H.SendAsync(HttpMethod.Get, "/api/v2/due?limit=2", withProfile: false);
        status.Should().Be(HttpStatusCode.OK);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var (_, second) = await a.H.SendAsync(HttpMethod.Get, $"/api/v2/due?limit=2&cursor={Uri.EscapeDataString(cursor)}", withProfile: false);

        first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("taskId").GetString()).Should().Equal(a.Badkamer, a.Stofzuigen);
        second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("taskId").GetString()).Should().Equal(a.Ramen);
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        second.GetProperty("summary").GetProperty("overdue").GetInt32().Should().Be(1, "the summary counts the whole list, not the page");
    }

    [Theory]
    [InlineData("limit=abc", "limit")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=-1", "limit")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task A_bad_query_value_is_a_400_validation_error_naming_the_field(string query, string field)
    {
        using var h = new GenerationHarness(mongo);

        var (status, body) = await h.SendAsync(HttpMethod.Get, $"/api/v2/due?{query}", withProfile: false);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
    }

    [Fact]
    public async Task An_empty_installation_answers_an_empty_list()
    {
        using var h = new GenerationHarness(mongo);

        var (status, body) = await h.SendAsync(HttpMethod.Get, "/api/v2/due", withProfile: false);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("summary").GetProperty("overdue").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task The_initial_due_date_is_the_earliest_generated_planned_day_even_when_that_occurrence_is_done_or_moved()
    {
        using var h = new GenerationHarness(mongo);
        var room = await h.SeedRoomAsync("Keuken");
        var task = await h.NewTaskAsync("Afwas", room, "1w");
        await h.PutSlotsAsync(await h.ActivePlanIdAsync(), (task, 0, 3, null), (task, 1, 3, null));
        var occurrences = await h.OccurrencesOfAsync(task);
        var earliest = occurrences[0];
        await h.Occurrences.UpdateOneAsync(
            new BsonDocument("_id", earliest["_id"]),
            new BsonDocument("$set", new BsonDocument { { "status", "done" }, { "date", new BsonDateTime(earliest["date"].ToUniversalTime().AddDays(2)) } }),
            cancellationToken: Ct);

        var item = Find(await DueItemsAsync(h), task);

        item.GetProperty("initialDueDate").GetString().Should().Be(GenerationHarness.DayOf(earliest));
    }

    [Fact]
    public async Task The_next_occurrence_is_the_first_open_one_from_today_and_ignores_done_skipped_past_and_one_off_work()
    {
        using var h = new GenerationHarness(mongo);
        var room = await h.SeedRoomAsync("Keuken");
        var task = await h.NewTaskAsync("Afwas", room, "daily");
        var other = await h.NewTaskAsync("Stofzuigen", room, "1w");
        await h.PutSlotsAsync(await h.ActivePlanIdAsync(), (task, 0, 1, null), (task, 0, 2, null), (task, 0, 3, null), (task, 0, 4, null));
        var days = await h.OccurrencesOfAsync(task); // Mon 14 .. Thu 17 September
        h.Clock.Set("2026-09-15T08:00:00.000Z");
        await SetStatusAsync(h, days[1], "done");
        await SetStatusAsync(h, days[2], "skipped");
        var oneOff = new BsonDocument(days[3]) { ["_id"] = ObjectId.GenerateNewId(), ["taskId"] = BsonNull.Value, ["origin"] = "adhoc", ["date"] = new BsonDateTime(days[3]["date"].ToUniversalTime().AddHours(1)) };
        await h.Occurrences.InsertOneAsync(oneOff, cancellationToken: Ct);

        var items = await DueItemsAsync(h);

        Find(items, task).GetProperty("nextOccurrence").GetProperty("id").GetString().Should().Be(days[3]["_id"].AsObjectId.ToString());
        Find(items, other).GetProperty("nextOccurrence").ValueKind.Should().Be(JsonValueKind.Null);

        await SetStatusAsync(h, days[3], "done");
        Find(await DueItemsAsync(h), task).GetProperty("nextOccurrence").GetProperty("date").GetString().Should().Be("2026-10-12", "only the next cycle has an open occurrence left");
    }

    private static async Task SetStatusAsync(GenerationHarness h, BsonDocument occurrence, string status) =>
        await h.Occurrences.UpdateOneAsync(new BsonDocument("_id", occurrence["_id"]), new BsonDocument("$set", new BsonDocument("status", status)), cancellationToken: Ct);
}
