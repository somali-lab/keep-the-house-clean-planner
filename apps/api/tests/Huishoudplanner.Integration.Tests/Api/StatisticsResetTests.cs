using System.Net;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Statistics;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports <c>apps/server/test/stats-reset.test.ts</c> against the real host and a real MongoDB replica set, with the occurrence actions arranged
/// as stored documents (slice 3.2) and the points ledger as stored entries (slice 4.1). The rebuild of the badge awards after a reset is
/// deferred to the badges slice, which owns the awards.
/// </summary>
public sealed class StatisticsResetTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FilterDefinition<BsonDocument> All = FilterDefinition<BsonDocument>.Empty;

    private sealed record World(StatisticsHarness H, string Person, string Room, string Task, string Cycle0, string Cycle1) : IDisposable
    {
        public void Dispose() => H.Dispose();
    }

    private async Task<World> ArrangeAsync(string now = StatisticsHarness.MondayMorning, string? lastCompletedAt = null)
    {
        var h = new StatisticsHarness(mongo);
        (await h.SendAsync(HttpMethod.Get, "/api/v2/health")).Status.Should().Be(HttpStatusCode.OK);
        var person = await h.PersonAsync("Persoon 1");
        var room = await h.InsertRoomAsync("Keuken");
        var task = await h.InsertTaskAsync("Aanrecht", room, "1w", 15, lastCompletedAt: lastCompletedAt);
        var cycle0 = await h.InsertCycleAsync(0, "2026-09-14", "2026-10-11");
        var cycle1 = await h.InsertCycleAsync(1, "2026-10-12", "2026-11-08");
        h.Clock.Set(now);
        return new World(h, person, room, task, cycle0, cycle1);
    }

    private static Task<long> Count(IMongoCollection<BsonDocument> collection) => collection.CountDocumentsAsync(All, cancellationToken: Ct);

    private static string DayOf(BsonDocument occurrence) => GenerationHarness.DayOf(occurrence);

    [Fact]
    public async Task Reset_startsExecutionStatisticsOver_whilePreservingPeopleRoomsTasksAndThePlan()
    {
        using var w = await ArrangeAsync("2026-09-16T08:00:00Z", lastCompletedAt: "2026-09-16T08:00:00Z");
        var h = w.H;
        var monday = await h.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person, minutes: 15, name: "Aanrecht", roomId: w.Room, pointsSnapshot: 15);
        var wednesday = await h.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-16", w.Person, "done", "2026-09-16T08:00:00Z", w.Person, minutes: 15, name: "Aanrecht", roomId: w.Room, pointsSnapshot: 15);
        var friday = await h.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-18", w.Person, "open", minutes: 15, name: "Aanrecht", roomId: w.Room);
        await h.InsertPointEntryAsync("execution", w.Person, "2026-09-14", 15);
        await h.InsertPointEntryAsync("execution", w.Person, "2026-09-16", 15);
        await h.InsertPointEntryAsync("bonus_week_done", w.Person, "2026-09-20", 10);
        await h.InsertPointEntryAsync("redemption", w.Person, "2026-09-15", -5);
        var taskVersionBefore = await h.Tasks.VersionAsync(ObjectId.Parse(w.Task));
        var settingsVersionBefore = await h.Settings.VersionAsync(All);
        var before = (await Count(h.Users), await Count(h.Rooms), await Count(h.Tasks), await Count(h.Database.GetCollection<BsonDocument>("cyclePlans")));

        var (status, body) = await h.SendAsync(HttpMethod.Delete, "/api/v2/stats", h.Admin);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (body.GetProperty("deletedOccurrences").GetInt32(), body.GetProperty("deletedRecorded").GetInt32(), body.GetProperty("resetOccurrences").GetInt32(), body.GetProperty("resetTasks").GetInt32(), body.GetProperty("deletedPastCycles").GetInt32())
            .Should().Be((1, 0, 1, 1, 0));
        (body.GetProperty("removedPointEntries").GetInt32(), body.GetProperty("removedRedemptions").GetInt32()).Should().Be((4, 1));

        (await Count(h.Users), await Count(h.Rooms), await Count(h.Tasks), await Count(h.Database.GetCollection<BsonDocument>("cyclePlans"))).Should().Be(before);
        (await h.Tasks.Find(All).SingleAsync(Ct))["lastCompletedAt"].IsBsonNull.Should().BeTrue();
        var remaining = await h.Occurrences.Find(All).ToListAsync(Ct);
        remaining.Select(o => o["_id"].AsObjectId.ToString()).Should().BeEquivalentTo([wednesday, friday]);
        remaining.Should().OnlyContain(o => o["status"] == "open");
        remaining.Select(o => o["_id"].AsObjectId.ToString()).Should().NotContain(monday);
        var reopened = remaining.Single(o => o["_id"].AsObjectId.ToString() == wednesday);
        reopened["statusBeforeCompletion"].IsBsonNull.Should().BeTrue();
        reopened["completedAt"].IsBsonNull.Should().BeTrue();
        reopened["completedBy"].IsBsonNull.Should().BeTrue();
        reopened["skipReason"].IsBsonNull.Should().BeTrue();
        reopened["pointsSnapshot"].IsBsonNull.Should().BeTrue();
        reopened["updatedAt"].ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        (await Count(h.PointEntries)).Should().Be(0);
        (await h.GetAsync("/api/v2/stats/completion?cycles=4&groupBy=task")).GetProperty("rows").GetArrayLength().Should().Be(0);
        (await h.Settings.Find(All).SingleAsync(Ct))["bonusFloor"].AsString.Should().Be("2026-09-16");
        (await h.Tasks.VersionAsync(ObjectId.Parse(w.Task))).Should().BeGreaterThan(taskVersionBefore, "the reset cleared lastCompletedAt of the task");
        (await h.Settings.VersionAsync(All)).Should().BeGreaterThan(settingsVersionBefore, "the reset moved the bonus floor of the settings");
    }

    [Fact]
    public async Task Reset_recordsOneAuditEntryOnTheSettings_withTheCountsAndTheFloor()
    {
        using var w = await ArrangeAsync("2026-09-16T08:00:00Z");
        await w.H.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person);
        await w.H.InsertPointEntryAsync("execution", w.Person, "2026-09-14", 15);
        var auditBefore = await Count(w.H.AuditLog);

        var (status, _) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats", w.H.Admin);

        status.Should().Be(HttpStatusCode.OK);
        (await Count(w.H.AuditLog)).Should().Be(auditBefore + 1, "one entry for the reset, none per removed occurrence or ledger entry");
        var entry = (await w.H.AuditAsync("settings", "reset")).Should().ContainSingle().Subject;
        entry["entityId"].AsObjectId.Should().Be(ObjectId.Parse("000000000000000000000001"));
        entry["actorId"].AsObjectId.ToString().Should().Be(w.H.Admin.Id);
        entry["source"].AsString.Should().Be("ui");
        entry["before"].AsBsonDocument.ToJson().Should().Be(new BsonDocument("statistics", "bestaande uitvoeringsgeschiedenis").ToJson());
        entry["after"].AsBsonDocument.ToJson().Should().Be(new BsonDocument { { "statistics", "opnieuw gestart" }, { "bonusFloor", "2026-09-16" } }.ToJson());
        var meta = entry["meta"].AsBsonDocument;
        meta.Names.Should().Equal("deletedOccurrences", "deletedRecorded", "resetOccurrences", "resetTasks", "deletedPastCycles", "removedPointEntries", "removedRedemptions", "resetId", "scoped");
        (meta["deletedOccurrences"].ToInt32(), meta["removedPointEntries"].ToInt32(), meta["scoped"].AsBoolean).Should().Be((1, 1, false));
        meta["resetId"].AsString.Should().HaveLength(36);
    }

    [Fact]
    public async Task Reset_deletesRecordedExtraWork_insteadOfReopeningIt()
    {
        using var w = await ArrangeAsync(lastCompletedAt: "2026-09-14T06:00:00Z");
        var h = w.H;
        var planned = await h.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-15", null, "open", name: "Aanrecht", roomId: w.Room, origin: "adhoc");
        await h.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T06:00:00Z", w.Person, name: "Aanrecht", roomId: w.Room, origin: "adhoc", recorded: true);
        await h.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T06:00:00Z", w.Person, name: "Aanrecht", roomId: w.Room, origin: "adhoc", recorded: true);

        var (status, body) = await h.SendAsync(HttpMethod.Delete, "/api/v2/stats", h.Admin);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        // Recorded work is counted apart from the occurrences before the boundary, in the result and in the audit entry.
        (body.GetProperty("deletedOccurrences").GetInt32(), body.GetProperty("deletedRecorded").GetInt32(), body.GetProperty("resetTasks").GetInt32()).Should().Be((0, 2, 1));
        var audit = (await h.AuditAsync("settings", "reset")).Should().ContainSingle().Subject;
        (audit["meta"]["deletedOccurrences"].ToInt32(), audit["meta"]["deletedRecorded"].ToInt32(), audit["meta"]["scoped"].AsBoolean).Should().Be((0, 2, false));
        var remaining = await h.Occurrences.Find(All).ToListAsync(Ct);
        remaining.Should().OnlyContain(o => o["status"] == "open" && !o.Contains("recordedDone"));
        // The planned extra is reopened as before; only the recorded work disappears.
        remaining.Select(o => o["_id"].AsObjectId.ToString()).Should().Equal(planned);
        (await h.Tasks.Find(All).SingleAsync(Ct))["lastCompletedAt"].IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_withBefore_purgesOnlyDataStrictlyOlderThanThatDay_andLeavesRecentCompletionsUntouched()
    {
        using var w = await ArrangeAsync("2026-10-14T08:00:00Z", lastCompletedAt: "2026-10-14T08:00:00Z");
        var h = w.H;
        foreach (var day in new[] { "2026-09-16", "2026-09-23", "2026-09-30", "2026-10-07" })
        {
            await h.InsertOccurrenceAsync(w.Cycle0, w.Task, day, w.Person, day == "2026-09-16" ? "done" : "open", day == "2026-09-16" ? "2026-09-16T08:00:00Z" : null, day == "2026-09-16" ? w.Person : null, name: "Aanrecht", roomId: w.Room, pointsSnapshot: day == "2026-09-16" ? 15 : null);
        }

        var recent = await h.InsertOccurrenceAsync(w.Cycle1, w.Task, "2026-10-14", w.Person, "done", "2026-10-14T08:00:00Z", w.Person, name: "Aanrecht", roomId: w.Room, pointsSnapshot: 15);
        foreach (var day in new[] { "2026-10-21", "2026-10-28", "2026-11-04" })
        {
            await h.InsertOccurrenceAsync(w.Cycle1, w.Task, day, w.Person, "open", name: "Aanrecht", roomId: w.Room);
        }

        await h.InsertPointEntryAsync("execution", w.Person, "2026-09-16", 15);
        await h.InsertPointEntryAsync("bonus_cycle_done", w.Person, "2026-10-11", 20);
        await h.InsertPointEntryAsync("redemption", w.Person, "2026-10-05", -5);
        await h.InsertPointEntryAsync("redemption", w.Person, "2026-10-14", -3);
        await h.InsertPointEntryAsync("execution", w.Person, "2026-10-14", 15);

        var (status, body) = await h.SendAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-10-12", h.Admin);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (body.GetProperty("deletedOccurrences").GetInt32(), body.GetProperty("resetOccurrences").GetInt32(), body.GetProperty("resetTasks").GetInt32(), body.GetProperty("deletedPastCycles").GetInt32()).Should().Be((4, 0, 0, 1));
        (body.GetProperty("removedPointEntries").GetInt32(), body.GetProperty("removedRedemptions").GetInt32()).Should().Be((3, 1), "the entries and the redemption dated before the boundary go, the later ones stay");
        var remaining = await h.Occurrences.Find(All).ToListAsync(Ct);
        remaining.Should().OnlyContain(o => string.CompareOrdinal(DayOf(o), "2026-10-12") >= 0);
        remaining.Single(o => o["_id"].AsObjectId.ToString() == recent)["status"].AsString.Should().Be("done");
        (await h.Tasks.Find(All).SingleAsync(Ct))["lastCompletedAt"].IsBsonNull.Should().BeFalse();
        (await h.Cycles.Find(All).ToListAsync(Ct)).Select(c => c["index"].ToInt32()).Should().Equal(1);
        (await h.PointEntries.Find(All).ToListAsync(Ct)).Select(e => e["kind"].AsString).Order().Should().Equal("execution", "redemption");
        (await h.AuditAsync("settings", "reset")).Should().ContainSingle().Which["meta"]["scoped"].AsBoolean.Should().BeTrue();
        (await h.Settings.Find(All).SingleAsync(Ct))["bonusFloor"].AsString.Should().Be("2026-10-12");
    }

    [Fact]
    public async Task Reset_movesTheBonusFloorOnlyForward_andRecordsOnlyTheResetThatRemovedSomething()
    {
        using var w = await ArrangeAsync("2026-10-14T08:00:00Z");
        await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-10-12", w.H.Admin);

        var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-10-01", w.H.Admin);

        status.Should().Be(HttpStatusCode.OK);
        body.EnumerateObject().Should().OnlyContain(p => p.Value.GetInt32() == 0, "the first purge already removed everything older");
        (await w.H.Settings.Find(All).SingleAsync(Ct))["bonusFloor"].AsString.Should().Be("2026-10-12");
        (await w.H.AuditAsync("settings", "reset")).Should().ContainSingle("the second reset removed nothing");
    }

    [Fact]
    public async Task Reset_thatRemovesNothing_changesNothing_notEvenTheBonusFloor_andWritesNoAuditEntry()
    {
        using var w = await ArrangeAsync();
        var settingsBefore = (await w.H.Settings.Find(All).SingleAsync(Ct)).ToJson();
        var auditBefore = await Count(w.H.AuditLog);

        var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-09-01", w.H.Admin);

        status.Should().Be(HttpStatusCode.OK);
        body.EnumerateObject().Select(p => p.Name).Should().Equal("deletedOccurrences", "deletedRecorded", "resetOccurrences", "resetTasks", "deletedPastCycles", "removedPointEntries", "removedRedemptions");
        body.EnumerateObject().Should().OnlyContain(p => p.Value.GetInt32() == 0);
        (await w.H.Settings.Find(All).SingleAsync(Ct)).ToJson().Should().Be(settingsBefore, "the bonus floor and updatedAt stay as they were");
        (await Count(w.H.AuditLog)).Should().Be(auditBefore);
        (await w.H.AuditAsync("settings", "reset")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_withBeforeInTheFuture_isRefused_andChangesAndAuditsNothing()
    {
        using var w = await ArrangeAsync();
        await w.H.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person);
        var auditBefore = await Count(w.H.AuditLog);

        var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-09-15", w.H.Admin);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:before_in_future");
        (await Count(w.H.Occurrences)).Should().Be(1);
        (await Count(w.H.AuditLog)).Should().Be(auditBefore);
        (await w.H.Settings.Find(All).SingleAsync(Ct)).Contains("bonusFloor").Should().BeFalse();
    }

    [Theory]
    [InlineData("before=2026-9-14")]
    [InlineData("before=yesterday")]
    [InlineData("before=2026-02-30")]
    public async Task Reset_withAMalformedBefore_isAValidationError_keyedByBefore(string query)
    {
        using var w = await ArrangeAsync();

        var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats?" + query, w.H.Admin);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal("before");
    }

    [Fact]
    public async Task Reset_requiresAnActiveProfile()
    {
        using var w = await ArrangeAsync();

        var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats");

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:profile_required");
    }

    [Fact]
    public async Task Reset_isForAdministratorsOnly_andDeletesNothingOtherwise()
    {
        using var w = await ArrangeAsync();
        await w.H.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person);

        foreach (var role in new[] { w.H.Member, w.H.Planner })
        {
            var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats", role);

            status.Should().Be(HttpStatusCode.Forbidden);
            body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:permission_denied");
        }

        (await w.H.Occurrences.Find(All).SingleAsync(Ct))["status"].AsString.Should().Be("done");
        (await w.H.AuditAsync("settings", "reset")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_onAnEmptyHistory_answersTheZeroCounts_andWritesNoAuditEntry()
    {
        using var w = await ArrangeAsync();
        await w.H.Cycles.DeleteManyAsync(All, Ct);

        var (status, body) = await w.H.SendAsync(HttpMethod.Delete, "/api/v2/stats", w.H.Admin);

        status.Should().Be(HttpStatusCode.OK);
        body.EnumerateObject().Should().OnlyContain(p => p.Value.GetInt32() == 0);
        (await w.H.AuditAsync("settings", "reset")).Should().BeEmpty();
        (await w.H.Settings.Find(All).SingleAsync(Ct)).Contains("bonusFloor").Should().BeFalse();
    }

    // ---- the Mongo adapter: transaction behaviour

    private static StatisticsResetPlan PlanOf(string today) =>
        new(StatisticsHarness.Midnight(today).ToUniversalTime(), DayKeys.Parse(today), 0, true, null, DayKeys.Parse(today), DateTimeOffset.UtcNow);

    [Fact]
    public async Task ResetStore_outsideATransaction_writesNothing_andAnswersAPortError()
    {
        using var w = await ArrangeAsync();
        await w.H.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person);
        await using var scope = w.H.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ForResettingStatistics>();

        var result = await store.ResetAsync(PlanOf("2026-09-15"), Ct);

        result.AsT1.Message.Should().StartWith("statistics.no_transaction");
        (await w.H.Occurrences.Find(All).SingleAsync(Ct))["status"].AsString.Should().Be("done");
    }

    [Fact]
    public async Task ResetStore_whenTheTransactionAborts_leavesEveryCollectionAsItWas()
    {
        using var w = await ArrangeAsync(lastCompletedAt: "2026-09-14T08:00:00Z");
        await w.H.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person, pointsSnapshot: 15);
        await w.H.InsertOccurrenceAsync(w.Cycle0, w.Task, "2026-09-14", w.Person, "done", "2026-09-14T08:00:00Z", w.Person, recorded: true, origin: "adhoc");
        await w.H.InsertPointEntryAsync("execution", w.Person, "2026-09-14", 15);
        await w.H.InsertPointEntryAsync("redemption", w.Person, "2026-09-14", -5);
        await using var scope = w.H.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ForResettingStatistics>();
        var transactions = scope.ServiceProvider.GetRequiredService<ForRunningTransactions>();

        var ran = await transactions.RunAsync(
            async ct =>
            {
                var result = await store.ResetAsync(PlanOf("2026-09-15"), ct);
                return TransactionOutcome.Abort(result);
            },
            Ct);

        // A restart from the 15th: both occurrences are older (so the recorded one is already gone), the task is reopened, the ledger goes.
        ran.AsT0.AsT0.Should().Be(new StatisticsResetResult(2, 0, 0, 1, 0, 2, 1));
        (await Count(w.H.Occurrences)).Should().Be(2);
        (await w.H.Occurrences.Find(new BsonDocument("recordedDone", true)).CountDocumentsAsync(Ct)).Should().Be(1);
        (await Count(w.H.Cycles)).Should().Be(2);
        (await Count(w.H.PointEntries)).Should().Be(2);
        (await w.H.Tasks.Find(All).SingleAsync(Ct))["lastCompletedAt"].IsBsonNull.Should().BeFalse();
        (await w.H.Settings.Find(All).SingleAsync(Ct)).Contains("bonusFloor").Should().BeFalse();
    }
}
