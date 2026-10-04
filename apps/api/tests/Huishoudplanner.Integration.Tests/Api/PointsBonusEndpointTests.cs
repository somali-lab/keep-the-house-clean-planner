using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// ADR-0012, <c>points-bonuses.test.ts</c> on the real host and a real MongoDB replica set: week and cycle bonuses are derived ledger entries that
/// only the reconciliation writes. Monday 2026-09-14 is the first day of cycle 0 (14 Sep to 11 Oct); every cycle has one task of 30 minutes on
/// Monday (person 1), Tuesday (person 2) and Wednesday (nobody) of its first week. The state is arranged through the occurrence endpoints.
/// Deferred to the import slice (7.x): the two <c>transfer</c> scenarios and the import schedule check of <c>writing the bonuses</c>; the badge
/// side effects of bonuses are slice 4.5.
/// </summary>
public sealed class PointsBonusEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] WeekBonuses =
    [
        "bonus_week_done p1 2026-09-14 5",
        "bonus_week_done p2 2026-09-14 5",
        "bonus_week_ontime p1 2026-09-14 3",
        "bonus_week_ontime p2 2026-09-14 3",
    ];

    private static string[] Sorted(string[] entries) => [.. entries.Order(StringComparer.Ordinal)];

    private static IEnumerable<string> Of(string person, IEnumerable<string>? from = null) =>
        (from ?? WeekBonuses).Where(entry => entry.Contains($" {person} ", StringComparison.Ordinal));

    // ---- finalisation by the nightly job

    [Fact]
    public async Task Nightly_writesNothingOnSunday2300Local_createsTheBonusesOnMonday0300WithOneAuditEntry_andNeverAgain()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();

        // Sunday 20 September, 23:00 local time (21:00Z): the week has not ended, so the ledger is not touched.
        await h.NightlyAtAsync("2026-09-20T21:00:00.000Z");
        var sunday = await h.FingerprintAsync();
        await h.NightlyAtAsync("2026-09-20T21:00:00.000Z");
        (await h.FingerprintAsync()).Should().Be(sunday);
        (await h.BonusesAsync()).Should().BeEmpty();
        (await h.RecomputeAuditAsync()).Should().BeEmpty();

        // Monday 21 September, 03:00 local time (01:00Z): the week that ended at midnight is finalised.
        await h.NightlyAtAsync("2026-09-21T01:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);

        var audit = (await h.RecomputeAuditAsync()).Should().ContainSingle().Subject;
        (audit["entity"].AsString, audit["action"].AsString, audit["source"].AsString).Should().Be(("points", "recompute", "system"));
        var meta = audit["meta"].AsBsonDocument;
        (meta["trigger"].AsString, meta["created"].AsInt32, meta["updated"].AsInt32, meta["removed"].AsInt32).Should().Be(("nightly", 0, 0, 0));
        (meta["bonusesCreated"].AsInt32, meta["bonusesRemoved"].AsInt32, meta["bonusChangesTotal"].AsInt32, meta["bonusChangesTruncated"].AsBoolean).Should().Be((4, 0, 4, false));
        var changes = meta["bonusChanges"].AsBsonArray.Select(c => c.AsBsonDocument).ToList();
        // The people inside the summary are hexadecimal strings, as the Node server wrote them.
        changes.Should().Contain(c => c["key"].AsString == $"bonus_week_done:{h.P1.Id}:2026-09-14" && c["personId"].IsString && c["personId"].AsString == h.P1.Id && c["amount"].AsInt32 == 5 && c["change"].AsString == "created");
        changes.Should().Contain(c => c["key"].AsString == $"bonus_week_ontime:{h.P2.Id}:2026-09-14" && c["personId"].AsString == h.P2.Id && c["amount"].AsInt32 == 3 && c["change"].AsString == "created");

        var entry = (await h.Ledger.Find(new BsonDocument { { "kind", "bonus_week_done" }, { "personId", ObjectId.Parse(h.P1.Id) } }).SingleAsync(Ct));
        entry["key"].AsString.Should().Be($"bonus_week_done:{h.P1.Id}:2026-09-14");
        (entry["occurrenceId"], entry["taskId"], entry["titleSnapshot"].AsString, entry["source"].AsString).Should().Be((BsonNull.Value, BsonNull.Value, string.Empty, "recompute"));
        // Dated on the last day of the period, so a balance range that includes that day includes the bonus.
        (BonusHarness.DayOf(entry["date"]), BonusHarness.DayOf(entry["weekStart"]), BonusHarness.DayOf(entry["periodStart"])).Should().Be(("2026-09-20", "2026-09-14", "2026-09-14"));

        // A second run, and a restart, change nothing: a bonus is never awarded twice.
        var settled = await h.FingerprintAsync();
        await h.NightlyAtAsync("2026-09-21T01:00:00.000Z");
        (await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z", PointsRecomputeTrigger.Startup)).ChangedAnything.Should().BeFalse();
        (await h.FingerprintAsync()).Should().Be(settled);
        (await h.RecomputeAuditAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Nightly_aRestartOfTheHostAfterTheWeekEndedCreatesTheBonusesOnceThroughTheStartupReconcile()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        h.Clock.Set("2026-09-21T01:00:00.000Z");
        using var restarted = ApiFactory.ForMongo(mongo, h.Database.DatabaseNamespace.DatabaseName)
            .WithPort<Huishoudplanner.Domain.Ports.Driven.ForFindingUsers>(new FakeUserDirectory())
            .WithPort<TimeProvider>(h.Clock);
        using var client = restarted.CreateClient();
        using var response = await client.GetAsync("/api/v2/health", Ct);

        (await h.BonusesAsync()).Should().Equal(WeekBonuses);
        (await h.RecomputeAuditAsync()).Should().ContainSingle().Which["meta"]["trigger"].AsString.Should().Be("startup");
    }

    [Fact]
    public async Task Reconcile_paysNobodyForSkippedWork_andUnassignedOpenWorkBlocksNobody()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        await h.CompleteAsync("2026-09-14T07:00:00.000Z", monday, h.P1);
        await h.PostAsync("2026-09-15T07:00:00.000Z", tuesday, "skip", new { }, h.P2);

        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");

        (await h.BonusesAsync()).Should().Equal("bonus_week_done p1 2026-09-14 5", "bonus_week_ontime p1 2026-09-14 3");
    }

    [Fact]
    public async Task Reconcile_doesNothingWhileTheAmountsAreZero_whichIsTheDefault()
    {
        await using var h = await BonusHarness.StartAsync(mongo, amounts: false);
        await h.DoTheWeekAsync();
        var before = await h.RecomputeAuditAsync();

        var result = await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");

        (result.BonusesCreated, result.BonusesRemoved, result.BonusChangesTotal).Should().Be((0, 0, 0));
        (await h.BonusesAsync()).Should().BeEmpty();
        (await h.RecomputeAuditAsync()).Should().HaveCount(before.Count, "a run that changes nothing audits nothing");
    }

    [Fact]
    public async Task Recompute_theEndpointAnswersWithTheRealBonusCountsAndTheirChanges()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        h.Clock.Set("2026-09-21T01:00:00.000Z");

        var response = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", null, h.Admin);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        var body = response.Body;
        (body.GetProperty("trigger").GetString(), body.GetProperty("bonusesCreated").GetInt32(), body.GetProperty("bonusesRemoved").GetInt32(), body.GetProperty("bonusChangesTotal").GetInt32(), body.GetProperty("bonusChangesTruncated").GetBoolean())
            .Should().Be(("admin", 4, 0, 4, false));
        var changes = body.GetProperty("bonusChanges").EnumerateArray().ToList();
        changes.Should().HaveCount(4).And.OnlyContain(c => c.GetProperty("change").GetString() == "created");
        changes.Select(c => c.GetProperty("key").GetString()).Should().BeInAscendingOrder(StringComparer.Ordinal);
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);

        var again = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", null, h.Admin);
        again.Body.GetProperty("bonusChangesTotal").GetInt32().Should().Be(0);
    }

    // ---- corrections after finalisation

    private static async Task<BonusHarness> FinalisedAsync(MongoContainerFixture mongo)
    {
        var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);
        return h;
    }

    [Fact]
    public async Task Correction_anUncompleteRemovesTheBonuses_andALateCheckOffAddsOnlyDone()
    {
        await using var h = await FinalisedAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");
        await h.PostAsync("2026-09-22T08:00:00.000Z", monday, "uncomplete");
        // Check-offs and corrections never write a bonus: the ledger follows at the next run.
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);

        var removed = await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");
        (removed.BonusesCreated, removed.BonusesRemoved, removed.BonusChangesTotal).Should().Be((0, 2, 2));
        removed.BonusChanges.Select(c => (c.Change, c.PersonId, c.Amount)).Should().Equal(("removed", h.P1.Id, 5), ("removed", h.P1.Id, 3));
        (await h.BonusesAsync()).Should().Equal(Of("p2"));

        // Completed again, after the end of the week: done, but not on time.
        await h.CompleteAsync("2026-09-23T08:00:00.000Z", monday, h.P1);
        var late = await h.ReconcileAtAsync("2026-09-23T09:00:00.000Z");
        (late.BonusesCreated, late.BonusesRemoved).Should().Be((1, 0));
        (await h.BonusesAsync()).Should().Equal(["bonus_week_done p1 2026-09-14 5", .. Of("p2")]);
        (await h.RecomputeAuditAsync()).Should().HaveCount(3);
    }

    [Fact]
    public async Task Correction_removesTheOnTimeBonusWhenAnAdministratorMovesTheCompletionPastTheCutOff()
    {
        await using var h = await FinalisedAsync(mongo);
        var tuesday = await h.OccurrenceAsync("2026-09-15");

        await h.PostAsync("2026-09-22T08:00:00.000Z", tuesday, "completion", new { date = "2026-09-15", completedAt = "2026-09-21T10:00:00.000Z", completedBy = h.P2.Id }, h.Admin);
        await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");

        (await h.BonusesAsync()).Should().Equal(WeekBonuses.Where(entry => entry != "bonus_week_ontime p2 2026-09-14 3"));
    }

    [Fact]
    public async Task Correction_removesTheBonusesWhenAnAdministratorDeletesTheCompletion()
    {
        await using var h = await FinalisedAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");

        await h.AtAsync("2026-09-22T08:00:00.000Z", HttpMethod.Delete, $"/api/v2/occurrences/{monday}", null, h.Admin);
        // The set of person 1 is now empty, so nothing is earned.
        var result = await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");

        result.BonusesRemoved.Should().Be(2);
        (await h.BonusesAsync()).Should().Equal(Of("p2"));
    }

    [Fact]
    public async Task Owner_keepsPersonOnesFinalisedBonusWhenSheTakesOverPersonTwosOverdueItem_andPersonTwoGainsNothingFromIt()
    {
        await using var h = await FinalisedAsync(mongo);
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        // Person 2 uncompletes Tuesday after the week ended: the item is open and overdue, and blocks person 2.
        await h.PostAsync("2026-09-22T08:00:00.000Z", tuesday, "uncomplete", null, h.P2);
        await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(Of("p1"));

        // Person 1 takes it over and finishes it: the week of person 1 is not penalised, person 2 still has not done it.
        var before = (await h.OccurrenceAuditAsync(tuesday)).Count;
        await h.CompleteAsync("2026-09-23T08:00:00.000Z", tuesday, h.P1, new { takeOver = true });
        var stored = await h.StoredAsync(tuesday);
        (stored["periodOwnerId"].AsObjectId.ToString(), stored["assigneeId"].AsObjectId.ToString()).Should().Be((h.P2.Id, h.P1.Id));
        // The freeze is part of the same audited update: one more entry, no separate one.
        var audit = await h.OccurrenceAuditAsync(tuesday);
        audit.Should().HaveCount(before + 1);
        audit[^1]["after"]["periodOwnerId"].AsObjectId.ToString().Should().Be(h.P2.Id);
        var result = await h.ReconcileAtAsync("2026-09-23T09:00:00.000Z");
        (result.BonusesCreated, result.BonusesRemoved).Should().Be((0, 0));
        (await h.BonusesAsync()).Should().Equal(Of("p1"));
    }

    [Fact]
    public async Task Owner_aCompletionOnBehalfOfTheOwnerCountsForTheOwner_doneButLate()
    {
        await using var h = await FinalisedAsync(mongo);
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        await h.PostAsync("2026-09-22T08:00:00.000Z", tuesday, "uncomplete", null, h.P2);

        // Person 1 checks it off for person 2.
        await h.CompleteAsync("2026-09-23T08:00:00.000Z", tuesday, h.P1, new { completedBy = h.P2.Id });
        await h.ReconcileAtAsync("2026-09-23T09:00:00.000Z");

        (await h.BonusesAsync()).Should().Equal(Sorted([.. Of("p1"), "bonus_week_done p2 2026-09-14 5"]));
    }

    [Fact]
    public async Task Owner_claimingUnassignedOverdueWorkChangesNobodysBonusAndNeverBlocksTheClaimer()
    {
        await using var h = await FinalisedAsync(mongo);
        var wednesday = await h.OccurrenceAsync("2026-09-16");

        await h.PostAsync("2026-09-22T08:00:00.000Z", wednesday, "claim", null, h.P2);
        var stored = await h.StoredAsync(wednesday);
        stored["periodOwnerId"].IsBsonNull.Should().BeTrue("a frozen null means it was unassigned");
        await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);

        await h.CompleteAsync("2026-09-23T08:00:00.000Z", wednesday, h.P2);
        await h.ReconcileAtAsync("2026-09-23T09:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);
    }

    [Fact]
    public async Task Owner_doesNotMoveReassignedOpenOverdueWorkIntoTheNewAssigneesEndedWeek()
    {
        await using var h = await FinalisedAsync(mongo);
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        await h.PostAsync("2026-09-22T08:00:00.000Z", tuesday, "uncomplete", null, h.P2);
        await h.PostAsync("2026-09-22T08:30:00.000Z", tuesday, "assignment", new { assigneeId = h.P1.Id });
        (await h.StoredAsync(tuesday))["periodOwnerId"].AsObjectId.ToString().Should().Be(h.P2.Id);

        await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");

        // Person 2 still owns the missed item of that week; person 1 keeps the bonuses, and has no extra item in the ended week.
        (await h.BonusesAsync()).Should().Equal(Of("p1"));
        // Assigning the person who already has it freezes and writes nothing.
        var (stored, audits) = (await h.StoredAsync(tuesday), (await h.FingerprintAsync()).Audits);
        h.Clock.Set("2026-09-22T09:30:00.000Z");
        var again = await h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{tuesday}/assignment", new { assigneeId = h.P1.Id }, h.P1);
        again.Status.Should().Be(HttpStatusCode.OK, again.Body.ToString());
        (await h.StoredAsync(tuesday)).Should().BeEquivalentTo(stored);
        (await h.FingerprintAsync()).Audits.Should().Be(audits);
    }

    [Fact]
    public async Task Owner_freezesNothingWhileThePlannedWeekIsStillRunning()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        var tuesday = await h.OccurrenceAsync("2026-09-15");

        await h.PostAsync("2026-09-15T08:00:00.000Z", tuesday, "assignment", new { assigneeId = h.P1.Id });

        (await h.StoredAsync(tuesday)).Contains("periodOwnerId").Should().BeFalse();
    }

    [Fact]
    public async Task Recorded_workNeverBlocksOrIsLate_alsoWhenAnAdministratorCorrectsItsDateToBeforeItsCompletion()
    {
        await using var h = await FinalisedAsync(mongo);
        // Person 1 records extra work on 23 September, then an administrator moves it into the ended week.
        var recorded = await h.AtAsync("2026-09-23T08:00:00.000Z", HttpMethod.Post, "/api/v2/occurrences", new { taskId = h.TaskId, date = "2026-09-23", done = true, assigneeId = h.P1.Id });
        var id = recorded.GetProperty("id").GetString()!;
        await h.PostAsync("2026-09-23T09:00:00.000Z", id, "completion", new { date = "2026-09-16", completedAt = "2026-09-23T08:00:00.000Z", completedBy = h.P1.Id }, h.Admin);

        var result = await h.ReconcileAtAsync("2026-09-23T10:00:00.000Z");

        (result.BonusesCreated, result.BonusesRemoved).Should().Be((0, 0));
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);
    }

    [Fact]
    public async Task Reconcile_leavesTheBonusesOfAPersonWhoseOccurrenceCannotBeReadAndCountsItAsSkipped()
    {
        await using var h = await FinalisedAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        var baseDocument = await h.StoredAsync(monday);
        baseDocument["_id"] = ObjectId.GenerateNewId();
        baseDocument["origin"] = "adhoc";
        baseDocument["planId"] = BsonNull.Value;
        baseDocument["status"] = "open";
        baseDocument["completedAt"] = BsonNull.Value;
        baseDocument["completedBy"] = BsonNull.Value;
        baseDocument["plannedDate"] = "not-a-date";
        baseDocument["requestId"] = BsonNull.Value;
        await h.Occurrences.InsertOneAsync(baseDocument, cancellationToken: Ct);
        // Person 1 would lose the bonuses (their Monday task is undone) and so would person 2; only person 2 can be evaluated.
        await h.PostAsync("2026-09-22T08:00:00.000Z", monday, "uncomplete");
        await h.PostAsync("2026-09-22T08:00:00.000Z", tuesday, "uncomplete", null, h.P2);

        var result = await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");

        result.Skipped.Should().Be(1);
        (await h.BonusesAsync()).Should().Equal(Of("p1"));
    }

    // ---- the bonus schedule

    [Fact]
    public async Task Schedule_leavesThePeriodsThatEndedBeforeTheAmountsWereEnabledOrChangedAsTheyWere()
    {
        await using var h = await BonusHarness.StartAsync(mongo, amounts: false);
        await h.DoTheWeekAsync();
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");
        (await h.BonusesAsync()).Should().BeEmpty();

        // Enabling bonuses never awards the past.
        await h.AtAsync("2026-09-22T08:00:00.000Z", HttpMethod.Patch, "/api/v2/settings", $$"""{ "periodBonuses": {{BonusHarness.Amounts}} }""", h.Admin);
        var enabled = await h.ReconcileAtAsync("2026-09-22T09:00:00.000Z");
        (enabled.BonusesCreated, enabled.BonusesRemoved).Should().Be((0, 0));
        (await h.BonusesAsync()).Should().BeEmpty();

        // The week of 12 October ends after the row, so it is paid with the amounts in force on its last day.
        var monday = await h.OccurrenceAsync("2026-10-12");
        var tuesday = await h.OccurrenceAsync("2026-10-13");
        await h.CompleteAsync("2026-10-12T07:00:00.000Z", monday, h.P1);
        await h.CompleteAsync("2026-10-13T07:00:00.000Z", tuesday, h.P2);
        await h.ReconcileAtAsync("2026-10-19T01:00:00.000Z");
        // Cycle 0 ended on 11 October, after the row, so its cycle bonuses include the week that was done before the amounts were set.
        var afterEnabling = await h.BonusesAsync();
        afterEnabling.Should().Equal(
            "bonus_cycle_done p1 2026-09-14 20",
            "bonus_cycle_done p2 2026-09-14 20",
            "bonus_cycle_ontime p1 2026-09-14 10",
            "bonus_cycle_ontime p2 2026-09-14 10",
            "bonus_week_done p1 2026-10-12 5",
            "bonus_week_done p2 2026-10-12 5",
            "bonus_week_ontime p1 2026-10-12 3",
            "bonus_week_ontime p2 2026-10-12 3");

        // Changing the amounts again never alters the periods that ended: only later ones use them.
        await h.AtAsync("2026-10-20T08:00:00.000Z", HttpMethod.Patch, "/api/v2/settings", """{ "periodBonuses": { "weekDone": 10, "weekOnTime": 6, "cycleDone": 40, "cycleOnTime": 20 } }""", h.Admin);
        var changed = await h.ReconcileAtAsync("2026-10-20T09:00:00.000Z");
        (changed.BonusesCreated, changed.BonusesRemoved).Should().Be((0, 0));
        (await h.BonusesAsync()).Should().Equal(afterEnabling);
        // A full rebuild or a run on another day gives the same ledger.
        (await h.Ledger.CountDocumentsAsync(new BsonDocument("kind", new BsonDocument("$in", new BsonArray(BonusHarness.BonusKindNames))), cancellationToken: Ct)).Should().Be(8);
    }

    [Fact]
    public async Task Schedule_paysTheCycleBonusesOnceTheCycleHasEnded_andRedrawsThemWhenTheAnchorMoves()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        // Cycle 0 ends on Sunday 11 October.
        await h.ReconcileAtAsync("2026-10-11T21:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);
        await h.ReconcileAtAsync("2026-10-12T01:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(Sorted([.. WeekBonuses, "bonus_cycle_done p1 2026-09-14 20", "bonus_cycle_done p2 2026-09-14 20", "bonus_cycle_ontime p1 2026-09-14 10", "bonus_cycle_ontime p2 2026-09-14 10"]));

        // The anchor moves one week back: the same work now sits in the cycle that starts on 7 September.
        await h.AtAsync("2026-10-12T02:00:00.000Z", HttpMethod.Patch, "/api/v2/settings", """{ "cycleAnchorDate": "2026-09-07" }""", h.Admin);
        var result = await h.ReconcileAtAsync("2026-10-12T03:00:00.000Z");

        (result.BonusesCreated, result.BonusesRemoved, result.BonusChangesTotal).Should().Be((4, 4, 8));
        result.BonusChanges.Take(4).Should().OnlyContain(c => c.Change == "removed" && c.Key.Contains("2026-09-14", StringComparison.Ordinal));
        result.BonusChanges.Skip(4).Should().OnlyContain(c => c.Change == "created" && c.Key.Contains("2026-09-07", StringComparison.Ordinal));
        (await h.BonusesAsync()).Should().Equal(Sorted([.. WeekBonuses, "bonus_cycle_done p1 2026-09-07 20", "bonus_cycle_done p2 2026-09-07 20", "bonus_cycle_ontime p1 2026-09-07 10", "bonus_cycle_ontime p2 2026-09-07 10"]));
    }

    // ---- reading

    [Fact]
    public async Task Reading_countsOnlyExecutionsInExecutions_reportsTheBonusesInBonusPoints_andIncludesTheLastDayOfAPeriod()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");
        async Task<JsonElement> Balance(string query)
        {
            var response = await h.SendAsync(HttpMethod.Get, $"/api/v2/points/balances{query}", null, null);
            return response.Body.GetProperty("balances").EnumerateArray().First(b => b.GetProperty("personId").GetString() == h.P1.Id);
        }

        static (long Points, long Earned, int Executions, long Bonus) Row(JsonElement b) =>
            (b.GetProperty("points").GetInt64(), b.GetProperty("earned").GetInt64(), b.GetProperty("executions").GetInt32(), b.GetProperty("bonusPoints").GetInt64());

        Row(await Balance(string.Empty)).Should().Be((30 + 8, 30 + 8, 1, 8));
        // The bonus is dated on the last day of the week: a range that stops before it does not include it.
        Row(await Balance("?from=2026-09-14&to=2026-09-19")).Should().Be((30, 30, 1, 0));
        Row(await Balance("?from=2026-09-14&to=2026-09-20")).Should().Be((38, 38, 1, 8));

        var entries = (await h.SendAsync(HttpMethod.Get, $"/api/v2/points/entries?personId={h.P1.Id}&from=2026-09-14&to=2026-09-20", null, null)).Body.GetProperty("items").EnumerateArray().ToList();
        var bonus = entries.First(e => e.GetProperty("kind").GetString() == "bonus_week_ontime");
        (bonus.GetProperty("amount").GetInt32(), bonus.GetProperty("date").GetString(), bonus.GetProperty("weekStart").GetString(), bonus.GetProperty("periodStart").GetString())
            .Should().Be((3, "2026-09-20", "2026-09-14", "2026-09-14"));
        (bonus.GetProperty("occurrenceId").ValueKind, bonus.GetProperty("taskId").ValueKind, bonus.GetProperty("titleSnapshot").GetString(), bonus.GetProperty("source").GetString())
            .Should().Be((JsonValueKind.Null, JsonValueKind.Null, string.Empty, "recompute"));
        entries.First(e => e.GetProperty("kind").GetString() == "execution").GetProperty("periodStart").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- statistics reset

    [Fact]
    public async Task Reset_deletesTheBonusesOfThePeriodsThatEndedBeforeTheBoundary_withTheExecutions()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");

        var res = await h.AtAsync("2026-09-21T01:30:00.000Z", HttpMethod.Delete, "/api/v2/stats?before=2026-09-21", null, h.Admin);

        // Two executions and four bonuses.
        res.GetProperty("removedPointEntries").GetInt32().Should().Be(6);
        (await h.BonusesAsync()).Should().BeEmpty();
        // A restart finds nothing to award again.
        var restart = await h.ReconcileAtAsync("2026-09-21T01:45:00.000Z", PointsRecomputeTrigger.Startup);
        (restart.ChangedAnything, restart.BonusesCreated).Should().Be((false, 0));
        (await h.BonusesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_keepsTheBonusesOfAPeriodThatStraddlesTheBoundaryUntilTheNextRunEvaluatesWhatRemains()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");

        var res = await h.AtAsync("2026-09-21T01:30:00.000Z", HttpMethod.Delete, "/api/v2/stats?before=2026-09-17", null, h.Admin);

        res.GetProperty("removedPointEntries").GetInt32().Should().Be(2);
        (await h.BonusesAsync()).Should().Equal(WeekBonuses);
        // Both tasks were purged, so no person has a set in that week any more.
        var result = await h.ReconcileAtAsync("2026-09-21T02:00:00.000Z");
        result.BonusesRemoved.Should().Be(4);
        (await h.BonusesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_removesEveryBonusWhenTheStatisticsStartOver()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");

        var res = await h.AtAsync("2026-09-21T01:30:00.000Z", HttpMethod.Delete, "/api/v2/stats", null, h.Admin);

        res.GetProperty("removedPointEntries").GetInt32().Should().Be(6);
        (await h.BonusesAsync()).Should().BeEmpty();
    }

    // ---- the statistics reset floor

    [Fact]
    public async Task Floor_keepsAPurgedWeekFromPayingOutOnWhatRemains_forAnItemThatWasDraggedForwardOutOfIt()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        await h.CompleteAsync("2026-09-15T07:00:00.000Z", tuesday, h.P2);
        // Person 1 never did Monday's task; it was dragged to 22 September and finished there.
        await h.PostAsync("2026-09-21T08:00:00.000Z", monday, "reschedule", new { date = "2026-09-22" });
        await h.CompleteAsync("2026-09-22T08:00:00.000Z", monday, h.P1);

        await h.AtAsync("2026-09-22T09:00:00.000Z", HttpMethod.Delete, "/api/v2/stats?before=2026-09-21", null, h.Admin);
        (await h.Settings.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["bonusFloor"].AsString.Should().Be("2026-09-21");
        var reset = (await h.AuditLog.Find(new BsonDocument { { "entity", "settings" }, { "action", "reset" } }).ToListAsync(Ct))[^1];
        reset["after"]["bonusFloor"].AsString.Should().Be("2026-09-21");

        // Only the dragged item survived the purge. The week it was planned in began before the boundary, so it pays nothing.
        await h.ReconcileAtAsync("2026-09-22T10:00:00.000Z");
        (await h.BonusesAsync()).Should().BeEmpty();

        // Without the floor the same history would award the purged week.
        await h.Settings.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$unset", new BsonDocument("bonusFloor", string.Empty)), cancellationToken: Ct);
        await h.ReconcileAtAsync("2026-09-22T11:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal("bonus_week_done p1 2026-09-14 5");
    }

    [Fact]
    public async Task Floor_isAppliedWhenTheStatisticsStartOver_andOnlyMovesForward()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        await h.CompleteAsync("2026-09-15T07:00:00.000Z", tuesday, h.P2);
        // Person 1 dragged Monday's task forward to 25 September instead of doing it.
        await h.PostAsync("2026-09-21T08:00:00.000Z", monday, "reschedule", new { date = "2026-09-25" });
        await h.ReconcileAtAsync("2026-09-21T09:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal(Of("p2"));

        // Starting over deletes the past, reopens the rest and sets the floor to today.
        await h.AtAsync("2026-09-22T08:00:00.000Z", HttpMethod.Delete, "/api/v2/stats", null, h.Admin);
        async Task<string> Floor() => (await h.Settings.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["bonusFloor"].AsString;
        (await Floor()).Should().Be("2026-09-22");
        // The dragged item is finished late; the week it was planned in began before the floor.
        await h.CompleteAsync("2026-09-25T08:00:00.000Z", monday, h.P1);
        await h.ReconcileAtAsync("2026-09-25T09:00:00.000Z");
        (await h.BonusesAsync()).Should().BeEmpty();

        // Without the floor the surviving item would pay out the week that was cleared.
        await h.Settings.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$unset", new BsonDocument("bonusFloor", string.Empty)), cancellationToken: Ct);
        await h.ReconcileAtAsync("2026-09-25T10:00:00.000Z");
        (await h.BonusesAsync()).Should().Equal("bonus_week_done p1 2026-09-14 5");

        // A later purge before an earlier day never moves the floor back.
        await h.Settings.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("bonusFloor", "2026-09-22")), cancellationToken: Ct);
        await h.AtAsync("2026-09-26T08:00:00.000Z", HttpMethod.Delete, "/api/v2/stats?before=2026-09-10", null, h.Admin);
        (await Floor()).Should().Be("2026-09-22");
    }

    [Fact]
    public async Task Floor_isMovedByAResetThatRemovesNothing_soLateWorkInAWeekThatBeganBeforeTodayEarnsNoBonus()
    {
        await using var h = await BonusHarness.StartAsync(mongo);
        var monday = await h.OccurrenceAsync("2026-09-14");
        var tuesday = await h.OccurrenceAsync("2026-09-15");
        // Monday's task is dragged to Thursday, so on Tuesday morning nothing lies before today and nothing is done: the reset removes nothing.
        await h.PostAsync("2026-09-14T08:00:00.000Z", monday, "reschedule", new { date = "2026-09-17" });

        var reset = await h.AtAsync("2026-09-15T06:00:00.000Z", HttpMethod.Delete, "/api/v2/stats", null, h.Admin);

        reset.EnumerateObject().Should().OnlyContain(p => p.Value.GetInt32() == 0);
        (await h.Settings.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["bonusFloor"].AsString.Should().Be("2026-09-15");
        await h.CompleteAsync("2026-09-15T07:00:00.000Z", tuesday, h.P2);
        await h.CompleteAsync("2026-09-17T07:00:00.000Z", monday, h.P1);

        // The week began on 14 September, before the floor, so it pays nothing.
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");
        (await h.BonusesAsync()).Should().BeEmpty();

        // Without the floor the same history would pay the week.
        await h.Settings.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$unset", new BsonDocument("bonusFloor", string.Empty)), cancellationToken: Ct);
        await h.ReconcileAtAsync("2026-09-21T02:00:00.000Z");
        (await h.BonusesAsync()).Should().Contain("bonus_week_done p1 2026-09-14 5");
    }

    // ---- writing the bonuses

    [Fact]
    public async Task Writing_aCycleAnchorThatIsNoMondaySkipsTheBonusStepWhileTheExecutionPartCommits()
    {
        // The Node server audited what steps 1 to 3 had changed and then rethrew; here the bonus step is a transaction of its own and is skipped.
        await using var h = await BonusHarness.StartAsync(mongo);
        await h.DoTheWeekAsync();
        var now = DateTime.Parse("2026-09-21T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        // Drift for step 3 to repair: an execution entry without an occurrence.
        await h.Ledger.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", "execution:" + ObjectId.GenerateNewId() }, { "kind", "execution" }, { "personId", ObjectId.Parse(h.P1.Id) },
                { "amount", 2 }, { "date", now }, { "weekStart", now }, { "periodStart", BsonNull.Value }, { "occurrenceId", BsonNull.Value }, { "taskId", BsonNull.Value },
                { "titleSnapshot", "Verdwaald" }, { "source", "live" }, { "createdAt", now }, { "updatedAt", now },
            },
            cancellationToken: Ct);
        // An anchor that is not a Monday makes the cycle calculation of step 4 impossible.
        await h.Settings.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("cycleAnchorDate", "2026-09-15")), cancellationToken: Ct);
        h.Clock.Set("2026-09-21T01:00:00.000Z");


        var response = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", null, h.Admin);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (response.Body.GetProperty("removed").GetInt32(), response.Body.GetProperty("bonusesCreated").GetInt32()).Should().Be((1, 0));
        (await h.RecomputeAuditAsync()).Should().ContainSingle().Which["meta"]["step"].AsString.Should().Be("executions");
        (await h.Ledger.CountDocumentsAsync(new BsonDocument("titleSnapshot", "Verdwaald"), cancellationToken: Ct)).Should().Be(0, "the drift of the execution part is repaired");
        (await h.BonusesAsync()).Should().BeEmpty();
    }
}
