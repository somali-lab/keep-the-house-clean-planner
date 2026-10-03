using System.Net;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the generation scenarios of apps/server/test/generation.test.ts against the real host and a real MongoDB replica set: idempotency
/// that the unique slot index enforces, the audit entries with the run id, snapshots, vacation, inactive tasks and the past, DST, intervals,
/// the repair of stale occurrences (anchor and slot changes) and the synchronisation when the slots of the active plan are saved.
/// Ported in slice 6.3a (Jobs/JobSchedulerTests, Jobs/SchedulerHostTests, Api/JobEndpointTests): the three <c>scheduler</c> tests and the HTTP trigger
/// <c>POST /jobs/generation</c> (the use case behind it is called directly here). Deferred to slice 2.4: the <c>activate</c> block
/// (activation preview, token, mid-cycle activation). Deferred to slice 3.2: the occurrence query of "shows a newly assigned active-plan task"
/// (the stored occurrences are asserted instead).
/// </summary>
public sealed class GenerationTests(MongoContainerFixture mongo)
{
    private static readonly int[] AllWeekdays = [1, 2, 3, 4, 5, 6, 0];
    private static readonly int[] MonWedFri = [1, 3, 5];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string, int, int, string?)[] EveryWeek(string task, int weekday, string? assignee = null) =>
        [.. Enumerable.Range(0, 4).Select(week => (task, week, weekday, assignee))];

    private sealed record Arranged(GenerationHarness H, string Room, string PlanId) : IDisposable
    {
        public void Dispose() => H.Dispose();
    }

    private async Task<Arranged> ArrangeAsync(string now = GenerationHarness.MondayMorning)
    {
        var h = new GenerationHarness(mongo, now);
        var room = await h.SeedRoomAsync("Badkamer");
        var plan = await h.ActivePlanIdAsync();
        return new Arranged(h, room, plan);
    }

    [Fact]
    public async Task A_second_run_inserts_and_audits_nothing_because_the_unique_slot_index_keeps_the_occurrences_unique()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w");
        await a.H.StoreSlotsAsync(a.PlanId, EveryWeek(weekly, 3));

        var first = await a.H.GenerateUpcomingAsync();

        first.Generated.Select(g => (g.CycleIndex, g.Inserted)).Should().Equal((0, 4), (1, 4));
        (await a.H.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(8);
        (await a.H.Cycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
        var audited = (await a.H.AuditAsync("occurrence", "create")).Count;
        var auditTotal = await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var second = await a.H.GenerateUpcomingAsync();

        second.Generated.Select(g => (g.Inserted, g.Skipped)).Should().Equal((0, 4), (0, 4));
        (await a.H.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(8);
        (await a.H.AuditAsync("occurrence", "create")).Should().HaveCount(audited);
        (await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(auditTotal, "a no-op run audits nothing");
    }

    [Fact]
    public async Task An_ad_hoc_occurrence_on_a_slot_day_no_longer_blocks_the_generated_one()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w");
        await a.H.PutSlotsAsync(a.PlanId, EveryWeek(weekly, 3));
        var generated = (await a.H.OccurrencesOfAsync(weekly)).Single(o => GenerationHarness.DayOf(o) == "2026-09-16");
        var adhoc = new BsonDocument(generated) { ["_id"] = ObjectId.GenerateNewId(), ["origin"] = "adhoc", ["planId"] = BsonNull.Value };
        await a.H.Occurrences.InsertOneAsync(adhoc, cancellationToken: Ct);
        await a.H.Occurrences.DeleteOneAsync(new BsonDocument("_id", generated["_id"]), Ct);

        await a.H.GenerateUpcomingAsync();

        var onTheDay = (await a.H.OccurrencesOfAsync(weekly)).Where(o => GenerationHarness.DayOf(o) == "2026-09-16").Select(o => o["origin"].AsString).Order().ToList();
        onTheDay.Should().Equal("adhoc", "generated");
        var count = await a.H.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var again = await a.H.GenerateUpcomingAsync();

        again.Removed.Should().Be(0);
        again.Generated.Select(g => g.Inserted).Should().Equal(0, 0);
        (await a.H.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(count);
    }

    [Fact]
    public async Task Each_generated_occurrence_and_cycle_is_audited_with_the_run_id_and_the_system_as_actor()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w");
        await a.H.StoreSlotsAsync(a.PlanId, (weekly, 1, 1, null));

        var run = await a.H.GenerateUpcomingAsync();

        var created = await a.H.AuditAsync("occurrence", "create");
        created.Should().HaveCount(2);
        created.Should().OnlyContain(e => e["meta"]["runId"].AsString == run.RunId && e["source"].AsString == "system" && e["actorId"].AsObjectId == ObjectId.Empty);
        var after = created[0]["after"].AsBsonDocument;
        after["status"].AsString.Should().Be("open");
        after["origin"].AsString.Should().Be("generated");
        after["taskNameSnapshot"].AsString.Should().Be("Badkamer");
        after["roomNameSnapshot"].AsString.Should().Be("Badkamer");
        after["statusBeforeCompletion"].IsBsonNull.Should().BeTrue();
        after.Names.Should().NotContain(["_id", "createdAt", "updatedAt"]);
        created[0]["meta"]["cycleIndex"].ToInt32().Should().Be(0);
        var cycles = await a.H.AuditAsync("cycle", "create");
        cycles.Should().HaveCount(2);
        cycles[0]["after"]["startDate"].AsString.Should().Be("2026-09-14");
        cycles[0]["after"]["planId"].AsObjectId.ToString().Should().Be(a.PlanId);
        cycles[0]["meta"]["runId"].AsString.Should().Be(run.RunId);
    }

    [Fact]
    public async Task Snapshots_hold_name_duration_room_assignee_and_plan_and_the_planned_date_equals_the_date()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w", 30);
        var p2 = await a.H.SeedPersonAsync("Persoon B");
        await a.H.PutSlotsAsync(a.PlanId, (weekly, 2, 5, p2));

        await a.H.GenerateUpcomingAsync();

        var occurrence = (await a.H.OccurrencesOfAsync(weekly)).First();
        occurrence["taskNameSnapshot"].AsString.Should().Be("Badkamer");
        occurrence["roomNameSnapshot"].AsString.Should().Be("Badkamer");
        occurrence["roomIdSnapshot"].Should().Be(ObjectId.Parse(a.Room));
        occurrence["durationMinutesSnapshot"].ToInt32().Should().Be(30);
        occurrence["assigneeId"].Should().Be(ObjectId.Parse(p2));
        occurrence["planId"].Should().Be(ObjectId.Parse(a.PlanId));
        occurrence["status"].AsString.Should().Be("open");
        occurrence["plannedDate"].Should().Be(occurrence["date"]);
        GenerationHarness.DayOf(occurrence).Should().Be("2026-10-02");
    }

    [Fact]
    public async Task Vacation_days_stay_empty()
    {
        using var a = await ArrangeAsync();
        var daily = await a.H.NewTaskAsync("Afwas", a.Room, "daily", 15);
        await a.H.PutSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 2).SelectMany(week => AllWeekdays.Select(day => (daily, week, day, (string?)null)))]);
        var settings = await a.H.SendAsync(
            HttpMethod.Patch,
            "/api/v2/settings",
            new { vacationRanges = new[] { new { from = "2026-09-17", to = "2026-09-20" }, new { from = "2026-10-12", to = "2026-10-18" } } },
            asAdmin: true);
        settings.Status.Should().Be(HttpStatusCode.OK, settings.Body.ToString());

        await a.H.GenerateUpcomingAsync();

        // The slot save above already generated everything: a repair removes what the new vacation ranges now exclude.
        var days = GenerationHarness.DaysOf(await a.H.OccurrencesOfAsync());
        foreach (var vacation in new[] { "2026-09-17", "2026-09-18", "2026-09-19", "2026-09-20", "2026-10-12", "2026-10-18" })
        {
            days.Should().NotContain(vacation);
        }

        days.Should().Contain(["2026-09-16", "2026-09-21", "2026-10-19"]);
        days.Should().HaveCount(28 - 4 - 7);
    }

    [Fact]
    public async Task Inactive_tasks_and_days_before_today_are_skipped()
    {
        using var a = await ArrangeAsync("2026-09-16T08:00:00.000Z"); // Wednesday
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w");
        var old = await a.H.NewTaskAsync("Oud", a.Room, "1w");
        await a.H.StoreSlotsAsync(a.PlanId, (weekly, 0, 1, null), (weekly, 0, 3, null), (old, 0, 4, null));
        (await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{old}", new { active = false })).Status.Should().Be(HttpStatusCode.OK);

        await a.H.GenerateUpcomingAsync();

        var cycle0 = await a.H.OccurrencesOfAsync(extra: Builders<BsonDocument>.Filter.Lt("date", new BsonDateTime(new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc))));
        GenerationHarness.DaysOf(cycle0).Should().Equal("2026-09-16");
    }

    [Fact]
    public async Task Local_dates_are_correct_across_the_end_of_daylight_saving_time()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w");
        await a.H.PutSlotsAsync(a.PlanId, (weekly, 1, 0, null), (weekly, 2, 1, null)); // cycle 1: Sunday 25 Oct (DST ends), Monday 26 Oct

        await a.H.GenerateUpcomingAsync();

        var cycle1 = await a.H.OccurrencesOfAsync(extra: Builders<BsonDocument>.Filter.Gte("date", new BsonDateTime(new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc))));
        cycle1.Select(o => o["date"].ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)).Should().Equal("2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z");
        GenerationHarness.DaysOf(cycle1).Should().Equal("2026-10-25", "2026-10-26");
    }

    [Fact]
    public async Task A_task_with_eight_slots_yields_eight_occurrences_per_cycle()
    {
        using var a = await ArrangeAsync();
        var twice = await a.H.NewTaskAsync("Wastafel", a.Room, "2w");
        await a.H.StoreSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).SelectMany(week => new[] { (twice, week, 2, (string?)null), (twice, week, 5, (string?)null) })]);

        var run = await a.H.GenerateUpcomingAsync();

        run.Generated.Select(g => g.Inserted).Should().Equal(8, 8);
    }

    [Fact]
    public async Task Future_occurrences_generated_before_the_anchor_changed_are_repaired()
    {
        using var a = await ArrangeAsync("2026-09-20T08:00:00.000Z");
        var water = await a.H.NewTaskAsync("Waterbak", a.Room, "1w", 3);
        var p1 = await a.H.SeedPersonAsync("Persoon A");
        var p2 = await a.H.SeedPersonAsync("Persoon B");
        await a.H.StoreSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).Select(week => (water, week, 3, (string?)p1))]);
        await a.H.GenerateUpcomingAsync();
        (await a.H.SendAsync(HttpMethod.Patch, "/api/v2/settings", new { cycleAnchorDate = "2026-09-21" }, asAdmin: true)).Status.Should().Be(HttpStatusCode.OK);
        await a.H.StoreSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).SelectMany(week => MonWedFri.Select(weekday => (water, week, weekday, (string?)p2)))]);

        var run = await a.H.GenerateUpcomingAsync();

        run.Removed.Should().Be(4);
        var occurrences = await a.H.OccurrencesOfAsync(water);
        GenerationHarness.DaysOf(occurrences).Should().Equal(
            "2026-09-21", "2026-09-23", "2026-09-25", "2026-09-28", "2026-09-30", "2026-10-02", "2026-10-05", "2026-10-07", "2026-10-09",
            "2026-10-12", "2026-10-14", "2026-10-16", "2026-10-21", "2026-10-28", "2026-11-04");
        occurrences.Where(o => string.CompareOrdinal(GenerationHarness.DayOf(o), "2026-10-18") <= 0).Should().OnlyContain(o => o["assigneeId"] == ObjectId.Parse(p2));
        occurrences.Where(o => string.CompareOrdinal(GenerationHarness.DayOf(o), "2026-10-18") > 0).Should().OnlyContain(o => o["assigneeId"] == ObjectId.Parse(p1));
        var cycles = await a.H.Cycles.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("index")).ToListAsync(Ct);
        cycles.Select(c => (c["index"].ToInt32(), c["startDate"].AsString, c["endDate"].AsString)).Should().Equal(
            (-1, "2026-08-24", "2026-09-20"), (0, "2026-09-21", "2026-10-18"), (1, "2026-10-19", "2026-11-15"));
    }

    [Fact]
    public async Task Stale_upcoming_occurrences_are_repaired_and_the_removals_audited_with_the_nightly_reason()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w", 30);
        var p1 = await a.H.SeedPersonAsync("Persoon A");
        var p2 = await a.H.SeedPersonAsync("Persoon B");
        await a.H.StoreSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).Select(week => (weekly, week, 1, (string?)p1))]);
        await a.H.GenerateUpcomingAsync();
        await a.H.StoreSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).Select(week => (weekly, week, 4, (string?)p2))]);

        var run = await a.H.GenerateUpcomingAsync();

        run.Removed.Should().Be(8);
        var occurrences = await a.H.OccurrencesOfAsync(weekly);
        GenerationHarness.DaysOf(occurrences).Should().Equal("2026-09-17", "2026-09-24", "2026-10-01", "2026-10-08", "2026-10-15", "2026-10-22", "2026-10-29", "2026-11-05");
        occurrences.Should().OnlyContain(o => o["assigneeId"] == ObjectId.Parse(p2));
        var removals = await a.H.AuditAsync("occurrence", "delete", new BsonDocument("meta.reason", "nightly_reconciliation"));
        removals.Should().HaveCount(8);
        removals[0]["before"]["taskNameSnapshot"].AsString.Should().Be("Badkamer");
        removals.Should().OnlyContain(e => e["meta"]["runId"].AsString == run.RunId && e["meta"]["planId"].AsObjectId.ToString() == a.PlanId);
    }

    [Fact]
    public async Task Saving_the_slots_of_the_active_plan_synchronises_the_upcoming_occurrences_at_once_and_reports_it()
    {
        using var a = await ArrangeAsync();
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w", 30);
        var p1 = await a.H.SeedPersonAsync("Persoon A");
        var p2 = await a.H.SeedPersonAsync("Persoon B");
        var first = await a.H.PutSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).Select(week => (weekly, week, 1, (string?)p1))]);
        first.GetProperty("synchronized").GetProperty("removed").GetInt32().Should().Be(0);
        first.GetProperty("synchronized").GetProperty("generated").EnumerateArray().Select(g => g.GetProperty("inserted").GetInt32()).Should().Equal(4, 4);
        await a.H.GenerateUpcomingAsync();
        var auditBefore = (await a.H.AuditAsync("occurrence", "delete")).Count;

        var saved = await a.H.PutSlotsAsync(a.PlanId, [.. Enumerable.Range(0, 4).Select(week => (weekly, week, 4, (string?)p2))]);

        var synchronized = saved.GetProperty("synchronized");
        synchronized.GetProperty("removed").GetInt32().Should().Be(8);
        var generated = synchronized.GetProperty("generated").EnumerateArray().ToList();
        generated.Select(g => (g.GetProperty("cycleIndex").GetInt32(), g.GetProperty("inserted").GetInt32(), g.GetProperty("skipped").GetInt32())).Should().Equal((0, 4, 0), (1, 4, 0));
        generated.Should().OnlyContain(g => g.GetProperty("planId").GetString() == a.PlanId && g.GetProperty("cycleId").GetString()!.Length == 24);
        var occurrences = await a.H.OccurrencesOfAsync(weekly);
        GenerationHarness.DaysOf(occurrences).Should().Equal("2026-09-17", "2026-09-24", "2026-10-01", "2026-10-08", "2026-10-15", "2026-10-22", "2026-10-29", "2026-11-05");
        occurrences.Should().OnlyContain(o => o["assigneeId"] == ObjectId.Parse(p2));
        var removals = (await a.H.AuditAsync("occurrence", "delete", new BsonDocument("meta.reason", "plan_update")));
        removals.Should().HaveCount(8);
        removals.Should().OnlyContain(e => e["source"].AsString == "system" && e["actorId"].AsObjectId.ToString() == a.H.Planner.Id);
        (await a.H.AuditAsync("occurrence", "delete")).Should().HaveCount(auditBefore + 8);
    }

    [Fact]
    public async Task A_newly_assigned_task_of_the_active_plan_exists_on_its_date_for_its_person()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen zemen", a.Room, "4wk", 20);
        var p1 = await a.H.SeedPersonAsync("Persoon A");
        await a.H.GenerateUpcomingAsync();

        await a.H.PutSlotsAsync(a.PlanId, (task, 0, 4, p1));

        var persisted = (await a.H.OccurrencesOfAsync(task)).Where(o => GenerationHarness.DayOf(o) == "2026-09-17").ToList();
        persisted.Should().ContainSingle();
        persisted[0]["assigneeId"].Should().Be(ObjectId.Parse(p1));
        persisted[0]["status"].AsString.Should().Be("open");
    }

    [Fact]
    public async Task Slots_saved_in_a_draft_plan_publish_nothing()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen zemen", a.Room, "4wk", 20);
        var p1 = await a.H.SeedPersonAsync("Persoon A");
        await a.H.GenerateUpcomingAsync();
        var created = await a.H.SendAsync(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "Concept" });
        created.Status.Should().Be(HttpStatusCode.Created);
        created.Body.GetProperty("active").GetBoolean().Should().BeFalse();

        var saved = await a.H.PutSlotsAsync(created.Body.GetProperty("id").GetString()!, (task, 0, 4, p1));

        saved.GetProperty("synchronized").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        (await a.H.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Without_an_active_plan_the_cycles_exist_but_no_occurrences_are_generated()
    {
        using var a = await ArrangeAsync();
        await a.H.Database.GetCollection<BsonDocument>("cyclePlans").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("active", false)), cancellationToken: Ct);

        var run = await a.H.GenerateUpcomingAsync();

        run.Generated.Should().OnlyContain(g => g.PlanId == null && g.Inserted == 0);
        (await a.H.Cycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
        (await a.H.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }
}
