using System.Net;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The effects of a task change on occurrences, deferred from slice 2.1: the room snapshot of upcoming occurrences on a room change
/// (tasks.test.ts "moves future open work to the new room while completed history keeps its old room") and the four scenarios of
/// interval-change.test.ts (a change of interval, duration and name writes only the task, existing occurrences keep their snapshots, the template
/// slots stay, the next generation snapshots the new values). Real HTTP pipeline and real MongoDB replica set.
/// </summary>
public sealed class TaskOccurrenceEffectsTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- room change

    [Fact]
    public async Task A_room_change_moves_future_open_work_to_the_new_room_while_completed_history_keeps_its_old_room()
    {
        using var h = new GenerationHarness(mongo);
        var badkamer = await h.SeedRoomAsync("Badkamer");
        var keuken = await h.SeedRoomAsync("Keuken");
        var task = await h.NewTaskAsync("Kast opruimen", badkamer, "1w", 20);
        var plan = await h.ActivePlanIdAsync();
        await h.PutSlotsAsync(plan, (task, 0, 1, null), (task, 0, 4, null), (task, 1, 1, null)); // 14 Sep (today), 17 Sep, 21 Sep
        var occurrences = await h.OccurrencesOfAsync(task);
        var byDay = occurrences.ToDictionary(GenerationHarness.DayOf, o => o["_id"].AsObjectId);
        var done = byDay["2026-09-17"];
        await h.Occurrences.UpdateOneAsync(new BsonDocument("_id", done), new BsonDocument("$set", new BsonDocument("status", "done")), cancellationToken: Ct);
        // Tuesday 15 September: the 14th is before today, the 17th is done, the 21st is open and upcoming.
        h.Clock.Set("2026-09-15T08:00:00.000Z");
        var occurrenceAudit = (await h.AuditAsync("occurrence")).Count;

        var response = await h.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{task}", new { roomId = keuken });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        var after = (await h.OccurrencesOfAsync(task)).ToDictionary(GenerationHarness.DayOf);
        after["2026-09-14"]["roomNameSnapshot"].AsString.Should().Be("Badkamer", "work before today keeps its room");
        after["2026-09-17"]["roomNameSnapshot"].AsString.Should().Be("Badkamer", "completed history keeps its room");
        after["2026-09-21"]["roomNameSnapshot"].AsString.Should().Be("Keuken");
        after["2026-09-21"]["roomIdSnapshot"].AsObjectId.ToString().Should().Be(keuken);
        after["2026-09-21"]["updatedAt"].Should().Be(occurrences.Single(o => GenerationHarness.DayOf(o) == "2026-09-21")["updatedAt"], "a snapshot refresh keeps updatedAt");
        (await h.AuditAsync("occurrence")).Should().HaveCount(occurrenceAudit, "the refresh is not audited, as in the Node server");
        (await h.AuditAsync("task", "update")).Should().ContainSingle(e => e["after"]["roomId"].AsObjectId.ToString() == keuken);
    }

    // ---- interval, duration and name change (interval-change.test.ts)

    private sealed record Arranged(GenerationHarness H, string Task, string PlanId, string Person) : IDisposable
    {
        public void Dispose() => H.Dispose();
    }

    private async Task<Arranged> ArrangeIntervalChangeAsync()
    {
        var h = new GenerationHarness(mongo);
        var room = await h.SeedRoomAsync("Badkamer");
        var person = await h.SeedPersonAsync("Persoon A");
        var task = await h.NewTaskAsync("Badkamer schoonmaken", room, "1w", 30);
        var plan = await h.ActivePlanIdAsync();
        await h.PutSlotsAsync(plan, [.. Enumerable.Range(0, 4).Select(week => (task, week, 1, (string?)person))]);
        await h.GenerateUpcomingAsync();
        return new Arranged(h, task, plan, person);
    }

    private static List<(string Id, string Date, string PlannedDate, int Duration, string Name, string UpdatedAt)> Snapshot(IEnumerable<BsonDocument> docs) =>
        [.. docs.Select(d => (d["_id"].AsObjectId.ToString(), d["date"].ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture), d["plannedDate"].ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture), d["durationMinutesSnapshot"].ToInt32(), d["taskNameSnapshot"].AsString, d["updatedAt"].ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)))];

    [Fact]
    public async Task Changing_interval_duration_and_name_writes_only_the_task_never_the_existing_occurrences()
    {
        using var a = await ArrangeIntervalChangeAsync();
        var before = Snapshot(await a.H.OccurrencesOfAsync(a.Task));
        before.Should().HaveCount(8);
        before.Should().OnlyContain(o => o.Duration == 30 && o.Name == "Badkamer schoonmaken");
        a.H.Clock.Set("2026-09-16T08:00:00.000Z");
        var occurrenceAudit = (await a.H.AuditAsync("occurrence")).Count;
        var cycleAudit = (await a.H.AuditAsync("cycle")).Count;

        var response = await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{a.Task}", new { durationMinutes = 45, intervalKey = "2wk", name = "Badkamer grondig" });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        Snapshot(await a.H.OccurrencesOfAsync(a.Task)).Should().Equal(before);
        (await a.H.AuditAsync("occurrence")).Should().HaveCount(occurrenceAudit);
        (await a.H.AuditAsync("cycle")).Should().HaveCount(cycleAudit);
        (await a.H.AuditAsync("task", "update")).Should().ContainSingle();
    }

    [Fact]
    public async Task Generation_does_not_touch_existing_occurrences_after_the_change_when_it_runs_again_for_the_same_cycles()
    {
        using var a = await ArrangeIntervalChangeAsync();
        await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{a.Task}", new { durationMinutes = 45, intervalKey = "2wk", name = "Badkamer grondig" });
        a.H.Clock.Set("2026-09-16T08:00:00.000Z");
        var before = Snapshot(await a.H.OccurrencesOfAsync(a.Task));

        var run = await a.H.GenerateUpcomingAsync();

        run.Generated.Select(g => g.Inserted).Should().Equal(0, 0);
        Snapshot(await a.H.OccurrencesOfAsync(a.Task)).Should().Equal(before);
    }

    [Fact]
    public async Task The_template_slots_stay_as_they_are_because_an_interval_change_only_affects_validation()
    {
        using var a = await ArrangeIntervalChangeAsync();
        await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{a.Task}", new { durationMinutes = 45, intervalKey = "2wk", name = "Badkamer grondig" });

        var plan = await a.H.SendAsync(HttpMethod.Get, $"/api/v2/cycle-plans/{a.PlanId}", withProfile: false);

        plan.Body.GetProperty("slots").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task The_next_generation_snapshots_the_new_duration_and_name_while_older_cycles_keep_the_old_ones()
    {
        using var a = await ArrangeIntervalChangeAsync();
        await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{a.Task}", new { durationMinutes = 45, intervalKey = "2wk", name = "Badkamer grondig" });
        a.H.Clock.Set("2026-11-09T06:00:00.000Z"); // the start of cycle 2

        await a.H.GenerateUpcomingAsync();

        var all = await a.H.OccurrencesOfAsync(a.Task);
        var cycle2 = all.Where(o => string.CompareOrdinal(GenerationHarness.DayOf(o), "2026-11-09") >= 0 && string.CompareOrdinal(GenerationHarness.DayOf(o), "2026-12-06") <= 0).ToList();
        cycle2.Should().HaveCount(4);
        cycle2.Should().OnlyContain(o => o["durationMinutesSnapshot"].ToInt32() == 45 && o["taskNameSnapshot"].AsString == "Badkamer grondig");
        var older = all.Where(o => string.CompareOrdinal(GenerationHarness.DayOf(o), "2026-11-09") < 0).ToList();
        older.Should().HaveCount(8);
        older.Should().OnlyContain(o => o["durationMinutesSnapshot"].ToInt32() == 30 && o["taskNameSnapshot"].AsString == "Badkamer schoonmaken");
    }
}
