using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// ADR-0011, <c>points-reconcile.test.ts</c> on the real host and a real replica set: the reconciliation makes the ledger match the occurrences,
/// idempotently, at startup, in the nightly run and on an administrator's request (<c>POST /api/v2/points/recompute</c>). Old and drifted data is
/// written straight into the database, which no use case does, to prove the reconciliation repairs it. Monday 2026-09-14 is the first day of cycle 0.
/// The bonus step is in <c>PointsBonusEndpointTests</c>; the badge step and the import are later slices.
/// </summary>
public sealed class PointsReconcileEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ObjectId LedgerId = ObjectId.Parse("000000000000000000000002");

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static async Task<List<BsonDocument>> Summaries(PointsHarness h) => await h.PointsAuditAsync("recompute");

    private static async Task<string> Snapshot(PointsHarness h) =>
        string.Join("\n", (await h.EntriesAsync()).Select(e => e.ToJson()));

    private static async Task<JsonElement> Recompute(PointsHarness h, ObjectId? actor = null)
    {
        var response = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", actor ?? h.Admin);
        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        return response.Body;
    }

    private static int Count(JsonElement result, string name) => result.GetProperty(name).GetInt32();

    // ---- retroactive points

    [Fact]
    public async Task Recompute_awardsHistoricalExecutionsPerTheAdrWritesOneSummaryAndASecondRunWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: null); // a task from before points existed
        // Done with completedBy: credited to that person, not the assignee. Done without: the assignee. Done without anybody: unattributed.
        var monday = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P2, h.P1);
        var tuesday = await h.InsertDoneOccurrenceAsync("2026-09-15", task, null, h.P2);
        var wednesday = await h.InsertDoneOccurrenceAsync("2026-09-16", task, null);
        // The task of this occurrence no longer exists, and a one-off task has none: the duration rule applies.
        var missingTask = ObjectId.GenerateNewId();
        var orphanTask = await h.InsertDoneOccurrenceAsync("2026-09-14", missingTask, h.P1, duration: 25, name: "Oud");
        var oneOff = await h.InsertDoneOccurrenceAsync("2026-09-14", null, null, h.P2, duration: 95, name: "Zolder vegen");

        var result = await Recompute(h);

        (result.GetProperty("trigger").GetString(), Count(result, "tasksDefaulted"), Count(result, "snapshotsSet"), Count(result, "created")).Should().Be(("admin", 1, 5, 4));
        (Count(result, "updated"), Count(result, "removed"), Count(result, "unattributed"), Count(result, "skipped"), Count(result, "correctionsTotal")).Should().Be((0, 0, 1, 0, 0));
        result.GetProperty("corrections").GetArrayLength().Should().Be(0);
        (result.GetProperty("correctionsTruncated").GetBoolean(), Count(result, "bonusesCreated"), Count(result, "bonusesRemoved")).Should().Be((false, 0, 0));

        var mondayEntry = (await h.EntryOfAsync(monday))!;
        mondayEntry["personId"].Should().Be(h.P2);
        (mondayEntry["amount"].AsInt32, mondayEntry["source"].AsString, mondayEntry["titleSnapshot"].AsString).Should().Be((30, "backfill", "Stofzuigen"));
        mondayEntry["date"].Should().Be(PointsHarness.Midnight("2026-09-14"));
        mondayEntry["taskId"].Should().Be(task);
        (await h.EntryOfAsync(tuesday))!["personId"].Should().Be(h.P2);
        (await h.EntryOfAsync(wednesday)).Should().BeNull();
        var orphanEntry = (await h.EntryOfAsync(orphanTask))!;
        (orphanEntry["personId"], orphanEntry["amount"].AsInt32, orphanEntry["taskId"]).Should().Be((h.P1, 25, missingTask));
        var oneOffEntry = (await h.EntryOfAsync(oneOff))!;
        (oneOffEntry["personId"], oneOffEntry["amount"].AsInt32, oneOffEntry["taskId"], oneOffEntry["titleSnapshot"].AsString).Should().Be((h.P2, 95, BsonNull.Value, "Zolder vegen"));
        (await h.StoredOccurrenceAsync(wednesday))["pointsSnapshot"].AsInt32.Should().Be(30);
        (await h.StoredTaskAsync(task))["points"].AsInt32.Should().Be(30);

        var summary = (await Summaries(h)).Should().ContainSingle().Subject;
        summary["entityId"].Should().Be(LedgerId);
        (summary["source"].AsString, summary["actorId"]).Should().Be(("ui", h.Admin));
        var meta = summary["meta"].AsBsonDocument;
        (meta["trigger"].AsString, meta["tasksDefaulted"].AsInt32, meta["snapshotsSet"].AsInt32, meta["created"].AsInt32, meta["unattributed"].AsInt32).Should().Be(("admin", 1, 5, 4, 1));
        meta.Names.Should().BeEquivalentTo(
            "trigger", "step", "tasksDefaulted", "snapshotsSet", "created", "updated", "removed", "unattributed", "skipped", "corrections", "correctionsTotal",
            "correctionsTruncated", "bonusesCreated", "bonusesRemoved", "bonusChanges", "bonusChangesTotal", "bonusChangesTruncated");
        // The per-entry audit entries of a backfill are not written; the summary stands for all of them.
        (await h.PointsAuditAsync("create")).Should().BeEmpty();

        var (audits, entries) = (await h.AuditCountAsync(), await Snapshot(h));
        var again = await Recompute(h);
        (Count(again, "tasksDefaulted"), Count(again, "snapshotsSet"), Count(again, "created"), Count(again, "updated"), Count(again, "removed")).Should().Be((0, 0, 0, 0, 0));
        (await h.AuditCountAsync(), await Snapshot(h)).Should().Be((audits, entries));
    }

    [Fact]
    public async Task Recompute_neverRewritesASnapshotFromATaskValueThatChangedAfterwards()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 9);
        var done = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1, snapshot: 30);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 30, "Stofzuigen", done, task: task);

        var result = await Recompute(h);

        (Count(result, "created"), Count(result, "updated"), Count(result, "removed"), Count(result, "snapshotsSet")).Should().Be((0, 0, 0, 0));
        (await h.EntryOfAsync(done))!["amount"].AsInt32.Should().Be(30);
    }

    [Fact]
    public async Task Recompute_healsDriftBetweenTheLedgerAndTheOccurrencesAndListsEveryCorrection()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 30);
        var monday = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1, snapshot: 30);
        var tuesday = await h.InsertDoneOccurrenceAsync("2026-09-15", task, h.P2, snapshot: 30);
        var wednesday = await h.InsertDoneOccurrenceAsync("2026-09-16", task, h.P1, snapshot: 30);
        // Drift: a wrong person and amount, a lost entry (tuesday has none) and an entry without an occurrence.
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-14", 9, "Stofzuigen", monday, task: task);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-16", 30, "Stofzuigen", wednesday, task: task);
        var stray = ObjectId.GenerateNewId();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 4, "Verdwaald", stray);

        var result = await Recompute(h);

        (Count(result, "created"), Count(result, "updated"), Count(result, "removed"), Count(result, "snapshotsSet"), Count(result, "tasksDefaulted")).Should().Be((1, 1, 1, 0, 0));
        Count(result, "correctionsTotal").Should().Be(2);
        var corrections = result.GetProperty("corrections").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);
        var changed = corrections["execution:" + monday];
        (changed.GetProperty("from").GetProperty("personId").GetString(), changed.GetProperty("from").GetProperty("amount").GetInt32()).Should().Be((h.P2.ToString(), 9));
        (changed.GetProperty("to").GetProperty("personId").GetString(), changed.GetProperty("to").GetProperty("amount").GetInt32()).Should().Be((h.P1.ToString(), 30));
        var removed = corrections["execution:" + stray];
        (removed.GetProperty("from").GetProperty("amount").GetInt32(), removed.GetProperty("to").ValueKind).Should().Be((4, JsonValueKind.Null));

        (await h.EntryOfAsync(stray)).Should().BeNull();
        var healed = (await h.EntryOfAsync(monday))!;
        (healed["personId"], healed["amount"].AsInt32, healed["source"].AsString).Should().Be((h.P1, 30, "recompute"));
        (await h.EntryOfAsync(tuesday))!["source"].AsString.Should().Be("backfill");
        (await h.EntryOfAsync(wednesday))!["source"].AsString.Should().Be("live", "an entry that already matches is left alone");
        (await Summaries(h)).Should().ContainSingle();

        var (audits, entries) = (await h.AuditCountAsync(), await Snapshot(h));
        await Recompute(h);
        (await h.AuditCountAsync(), await Snapshot(h)).Should().Be((audits, entries));
    }

    [Fact]
    public async Task Recompute_neverTouchesABookedRedemption()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var redemption = await h.InsertOtherEntryAsync(h.P1, "2026-09-18", -5, "redemption");

        var result = await Recompute(h);

        (Count(result, "created"), Count(result, "updated"), Count(result, "removed")).Should().Be((0, 0, 0));
        (await h.EntriesAsync()).Select(e => e["_id"].AsObjectId).Should().BeEquivalentTo([redemption]);
        (await Summaries(h)).Should().BeEmpty();
    }

    [Fact]
    public async Task Recompute_withoutSettingsThereIsNothingToReconcileAndNothingIsWritten()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await h.Database.GetCollection<BsonDocument>("settings").DeleteManyAsync(new BsonDocument(), Ct);
        await h.InsertDoneOccurrenceAsync("2026-09-14", null, h.P1);
        var audits = await h.AuditCountAsync();

        var result = await Recompute(h);

        (Count(result, "created"), Count(result, "snapshotsSet")).Should().Be((0, 0));
        (await h.AuditCountAsync(), await h.Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be((audits, 0));
    }

    // ---- robustness

    [Fact]
    public async Task Recompute_skipsAndCountsAnUnreadableOccurrenceInsteadOfFailingAndKeepsItsEntry()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 30);
        var monday = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1);
        // An old row with no date at all that already has an entry.
        var broken = await h.InsertDoneOccurrenceAsync("2026-09-15", task, h.P1, snapshot: 2, brokenDate: true);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 2, "Oud", broken);
        var tuesday = await h.InsertDoneOccurrenceAsync("2026-09-16", task, h.P1);

        var result = await Recompute(h);

        (Count(result, "skipped"), Count(result, "created"), Count(result, "removed"), Count(result, "snapshotsSet")).Should().Be((1, 2, 0, 2));
        (await h.EntryOfAsync(broken)).Should().NotBeNull();
        (await h.EntryOfAsync(monday)).Should().NotBeNull();
        (await h.EntryOfAsync(tuesday)).Should().NotBeNull();
        (await Summaries(h)).Single()["meta"]["skipped"].AsInt32.Should().Be(1);
    }

    [Fact]
    public async Task Recompute_anExecutionEntryThatCannotBeMappedIsLeftAloneAndCountedOnEveryRunInsteadOfFailingOnItsKey()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 30);
        var broken = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1, snapshot: 30);
        var fine = await h.InsertDoneOccurrenceAsync("2026-09-15", task, h.P1, snapshot: 30);
        // The person of this entry is a string, not an id (old or hand-edited data), and another entry has no key at all.
        await h.Ledger.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "key", "execution:" + broken }, { "kind", "execution" }, { "personId", "nobody" }, { "amount", 30 }, { "date", PointsHarness.Midnight("2026-09-14") } }, cancellationToken: Ct);
        await h.Ledger.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "execution" }, { "personId", h.P1 }, { "amount", 1 }, { "date", PointsHarness.Midnight("2026-09-14") } }, cancellationToken: Ct);

        var first = await Recompute(h);
        var second = await Recompute(h);

        (Count(first, "skipped"), Count(first, "created"), Count(first, "removed")).Should().Be((2, 1, 0));
        (Count(second, "skipped"), Count(second, "created"), Count(second, "removed")).Should().Be((2, 0, 0));
        (await h.EntryOfAsync(fine)).Should().NotBeNull();
        (await h.Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be(3);
    }

    [Fact]
    public async Task Recompute_listsAtMost100CorrectionsWithTheTotalAndATruncationFlagInTheAnswerAndTheAuditEntry()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        for (var i = 0; i < 101; i++)
        {
            await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", i + 1, "Verdwaald");
        }

        var result = await Recompute(h);

        (Count(result, "removed"), Count(result, "correctionsTotal"), result.GetProperty("correctionsTruncated").GetBoolean()).Should().Be((101, 101, true));
        result.GetProperty("corrections").GetArrayLength().Should().Be(100);
        var meta = (await Summaries(h)).Single()["meta"].AsBsonDocument;
        meta["corrections"].AsBsonArray.Count.Should().Be(100);
        (meta["correctionsTotal"].AsInt32, meta["correctionsTruncated"].AsBoolean).Should().Be((101, true));

        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 1, "Verdwaald");
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 2, "Verdwaald");
        var small = await Recompute(h);
        (Count(small, "correctionsTotal"), small.GetProperty("correctionsTruncated").GetBoolean(), small.GetProperty("corrections").GetArrayLength()).Should().Be((2, false, 2));
    }

    [Fact]
    public async Task Recompute_twoRunsAtOnceNeverOverlap_everyEntryIsCreatedOnceAndOneSummaryIsWritten()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 30);
        foreach (var day in new[] { "2026-09-14", "2026-09-15", "2026-09-16" })
        {
            await h.InsertDoneOccurrenceAsync(day, task, h.P1);
        }

        var runs = await Task.WhenAll(
            h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", h.Admin),
            h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", h.Admin));

        runs.Should().OnlyContain(r => r.Status == HttpStatusCode.OK);
        runs.Sum(r => r.Body.GetProperty("created").GetInt32()).Should().Be(3);
        (await h.EntriesAsync()).Should().HaveCount(3);
        (await Summaries(h)).Should().ContainSingle();
    }

    // ---- who may recompute

    [Fact]
    public async Task Recompute_isRefusedForAMemberAndWithoutAProfileAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 30);
        await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1);
        var audits = await h.AuditCountAsync();

        var member = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", h.P2);
        var anonymous = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", null);

        member.Status.Should().Be(HttpStatusCode.Forbidden);
        Type(member.Body).Should().Be("urn:huishoudplanner:problem:permission_denied");
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(anonymous.Body).Should().Be("urn:huishoudplanner:problem:profile_required");
        (await h.AuditCountAsync(), await h.Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be((audits, 0));
    }

    // ---- at startup

    [Fact]
    public async Task Startup_awardsAllExistingHistoryItsPointsWithOneSystemSummary_andEveryLaterStartWritesNothing()
    {
        ObjectId task = default, monday = default, tuesday = default;
        await using var h = await PointsHarness.StartAsync(mongo, async self =>
        {
            task = await self.InsertTaskAsync("Stofzuigen", 30, points: null);
            monday = await self.InsertDoneOccurrenceAsync("2026-09-14", task, self.P1);
            tuesday = await self.InsertDoneOccurrenceAsync("2026-09-15", task, null, self.P2);
        });

        (await h.EntryOfAsync(monday))!["personId"].Should().Be(h.P1);
        (await h.EntryOfAsync(tuesday))!["personId"].Should().Be(h.P2);
        (await h.StoredOccurrenceAsync(monday))["pointsSnapshot"].AsInt32.Should().Be(30);
        (await h.StoredTaskAsync(task))["points"].AsInt32.Should().Be(30);
        var summary = (await Summaries(h)).Should().ContainSingle().Subject;
        (summary["actorId"], summary["source"].AsString, summary["entityId"]).Should().Be((ObjectId.Parse(AuditActor.SystemActorId), "system", LedgerId));
        var meta = summary["meta"].AsBsonDocument;
        (meta["trigger"].AsString, meta["tasksDefaulted"].AsInt32, meta["snapshotsSet"].AsInt32, meta["created"].AsInt32).Should().Be(("startup", 1, 2, 2));

        // Every later start writes nothing.
        var (audits, entries) = (await h.AuditCountAsync(), await Snapshot(h));
        using var second = h.Restart();
        (await second.GetAsync("/api/v2/points/balances", Ct)).EnsureSuccessStatusCode();
        (await h.AuditCountAsync(), await Snapshot(h)).Should().Be((audits, entries));
    }

    [Fact]
    public async Task Startup_aFailingReconciliationIsLoggedAndNeverKeepsTheApplicationFromStarting()
    {
        ObjectId occurrence = default;
        await using var h = await PointsHarness.StartAsync(mongo, async self =>
        {
            occurrence = await self.InsertDoneOccurrenceAsync("2026-09-14", null, self.P1, snapshot: 5);
            // An entry of a kind this application does not know holds the key of the entry the reconciliation has to insert: the insert fails.
            await self.Ledger.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "key", "execution:" + occurrence }, { "kind", "kind-from-the-future" }, { "personId", self.P1 }, { "amount", 1 },
                    { "date", PointsHarness.Midnight("2026-09-14") }, { "weekStart", PointsHarness.Midnight("2026-09-14") }, { "occurrenceId", BsonNull.Value },
                    { "taskId", BsonNull.Value }, { "titleSnapshot", string.Empty }, { "source", "live" },
                    { "createdAt", new BsonDateTime(DateTime.UtcNow) }, { "updatedAt", new BsonDateTime(DateTime.UtcNow) },
                },
                cancellationToken: Ct);
        });

        var balances = await h.GetAsync("/api/v2/points/balances");

        balances.Status.Should().Be(HttpStatusCode.OK);
        h.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.StartsWith("Points reconciliation failed (startup)", StringComparison.Ordinal));
        (await Summaries(h)).Should().BeEmpty();
        (await h.EntriesAsync()).Should().ContainSingle("the failed run rolled back");
    }

    // ---- the nightly run

    [Fact]
    public async Task Nightly_generatesThenReconcilesWithTheNightlyTriggerAndTheSystemAsActor()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: null);
        var monday = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1);
        await using var scope = h.Scope();
        var nightly = scope.ServiceProvider.GetRequiredService<INightlyService>();

        var run = (await nightly.RunAsync(AuditActor.System, "run-1", Ct)).AsT0;

        run.Generation.RunId.Should().Be("run-1");
        run.Points.Should().NotBeNull();
        (run.Points!.Created, run.Points.SnapshotsSet, run.Points.TasksDefaulted).Should().Be((1, 1, 1));
        (await h.EntryOfAsync(monday))!["personId"].Should().Be(h.P1);
        var summary = (await Summaries(h)).Should().ContainSingle().Subject;
        (summary["meta"]["trigger"].AsString, summary["source"].AsString).Should().Be(("nightly", "system"));
    }

    [Fact]
    public async Task Nightly_repairsDriftWithinADayAndASecondRunWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var task = await h.InsertTaskAsync("Stofzuigen", 30, points: 30);
        var monday = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1, snapshot: 30);
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-14", 30, "Stofzuigen", monday, task: task); // the entry went to the wrong person after a crash
        await using var scope = h.Scope();
        var nightly = scope.ServiceProvider.GetRequiredService<INightlyService>();

        var first = (await nightly.RunAsync(AuditActor.System, "run-1", Ct)).AsT0;
        var (audits, entries) = (await h.AuditCountAsync(), await Snapshot(h));
        var second = (await nightly.RunAsync(AuditActor.System, "run-2", Ct)).AsT0;

        (first.Points!.Updated, first.Points.Corrections.Count).Should().Be((1, 1));
        (await h.EntryOfAsync(monday))!["personId"].Should().Be(h.P1);
        (second.Points!.Created, second.Points.Updated, second.Points.Removed).Should().Be((0, 0, 0));
        // Generation of the same cycles twice is idempotent too, so the second run adds nothing to the log.
        (await h.AuditCountAsync(), await Snapshot(h)).Should().Be((audits, entries));
    }
}
