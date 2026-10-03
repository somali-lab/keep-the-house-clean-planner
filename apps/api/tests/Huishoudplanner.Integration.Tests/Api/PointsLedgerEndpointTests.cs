using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// ADR-0011, <c>points.test.ts</c> on the real host and a real replica set: the ledger is a keyed projection of the executions. The occurrence
/// actions of slice 3.2 make the single <c>execution:&lt;occurrenceId&gt;</c> entry follow, in the transaction of the action. Every scenario has
/// occurrences of its own, and a scenario that changes a task's points puts them back; one harness serves the class. The recorded and one-off
/// executions (slice 3.3), the bonuses, the redemptions, the progress and the badges are later slices.
/// </summary>
public sealed class PointsLedgerEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoCollection<BsonDocument> Ledger => h.Database.GetCollection<BsonDocument>("pointEntries");

    private static ObjectId Oid(string id) => ObjectId.Parse(id);

    private async Task<BsonDocument?> EntryOf(string occurrenceId) =>
        await Ledger.Find(new BsonDocument("key", "execution:" + occurrenceId)).FirstOrDefaultAsync(Ct);

    private async Task<long> LedgerSize() => await Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct);

    private async Task<List<BsonDocument>> PointsAuditOf(string occurrenceId) =>
        await h.AuditLog.Find(new BsonDocument { { "entity", "points" }, { "meta.occurrenceId", Oid(occurrenceId) } }).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string id, string action, object? body, Huishoudplanner.Domain.Identity.UserIdentity actor) =>
        h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/{action}", body, actor);

    private async Task SetPoints(string taskId, int points)
    {
        var patch = await h.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{taskId}", new { points }, h.Planner);
        patch.Status.Should().Be(HttpStatusCode.OK, patch.Body.ToString());
    }

    // ---- a check-off creates exactly one ledger entry

    [Fact]
    public async Task Complete_createsExactlyOneEntryForThePersonWithTheTaskPointsAndAuditsIt()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-14");

        var response = await Post(id, "complete", null, h.P1);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        response.Body.GetProperty("pointsSnapshot").GetInt32().Should().Be(30);
        var entry = (await EntryOf(id))!;
        entry["kind"].AsString.Should().Be("execution");
        entry["personId"].Should().Be(Oid(h.P1.Id));
        entry["amount"].AsInt32.Should().Be(30);
        entry["date"].Should().Be(PointsHarness.Midnight("2026-09-14"));
        entry["weekStart"].Should().Be(PointsHarness.Midnight("2026-09-14"));
        entry["periodStart"].Should().Be(BsonNull.Value);
        entry["occurrenceId"].Should().Be(Oid(id));
        entry["taskId"].Should().Be(Oid(h.Weekly));
        (entry["titleSnapshot"].AsString, entry["source"].AsString).Should().Be(("Badkamer schoonmaken", "live"));
        var audit = (await PointsAuditOf(id)).Should().ContainSingle().Subject;
        audit["action"].AsString.Should().Be("create");
        audit["source"].AsString.Should().Be("ui");
        audit["actorId"].Should().Be(Oid(h.P1.Id));
        audit["entityId"].Should().Be(entry["_id"]);
        audit["meta"].Should().Be(new BsonDocument { { "occurrenceId", Oid(id) }, { "reason", "complete" } });
        audit["after"]["key"].AsString.Should().Be("execution:" + id);
        audit["after"]["personId"].Should().Be(Oid(h.P1.Id));
        audit["after"]["amount"].AsInt32.Should().Be(30);
    }

    [Fact]
    public async Task Complete_aSecondCompleteIsAConflictAndWritesAndAuditsNothing()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-21");
        (await Post(id, "complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var (audits, entries) = (await h.AuditCountAsync(), await LedgerSize());

        var again = await Post(id, "complete", null, h.P1);

        again.Status.Should().Be(HttpStatusCode.Conflict);
        (await h.AuditCountAsync(), await LedgerSize()).Should().Be((audits, entries));
        (await Ledger.CountDocumentsAsync(new BsonDocument("key", "execution:" + id), cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Complete_aCheckOffWritesAtMostThreeAuditEntries_theOccurrenceTheTaskAndThePointsEntry()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-28");

        (await Post(id, "complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        (await h.AuditOfAsync("occurrence", id, "complete")).Should().ContainSingle();
        (await PointsAuditOf(id)).Should().ContainSingle();
        var taskEntries = (await h.AuditOfAsync("task", h.Weekly, "update")).Where(e => e.Contains("meta") && e["meta"].AsBsonDocument.Contains("occurrenceId") && e["meta"]["occurrenceId"] == Oid(id));
        taskEntries.Count().Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Complete_aTaskOfZeroPointsEarnsNoEntryAndAuditsNothingForTheLedger()
    {
        await SetPoints(h.Twice, 0);
        try
        {
            var id = await h.IdOfAsync(h.Twice, "2026-09-23");

            var response = await Post(id, "complete", null, h.P1);

            response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
            response.Body.GetProperty("pointsSnapshot").GetInt32().Should().Be(0);
            (await EntryOf(id)).Should().BeNull();
            (await PointsAuditOf(id)).Should().BeEmpty();
        }
        finally
        {
            await SetPoints(h.Twice, 10);
        }
    }

    [Fact]
    public async Task Complete_creditsThePersonWhoPerformedTheWork_onBehalfTakeOverAndUnassigned()
    {
        var onBehalf = await h.IdOfAsync(h.Weekly, "2026-10-05"); // assigned to P1, who presses and names P2
        (await Post(onBehalf, "complete", new { completedBy = h.P2.Id }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await EntryOf(onBehalf))!["personId"].Should().Be(Oid(h.P2.Id));

        var takeOver = await h.IdOfAsync(h.Twice, "2026-09-24"); // assigned to P2, P1 takes over
        (await Post(takeOver, "complete", new { takeOver = true }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await EntryOf(takeOver))!["personId"].Should().Be(Oid(h.P1.Id));

        var unassigned = await h.IdOfAsync(h.Twice, "2026-09-30"); // assigned to nobody, P2 does it
        (await Post(unassigned, "complete", null, h.P2)).Status.Should().Be(HttpStatusCode.OK);
        (await EntryOf(unassigned))!["personId"].Should().Be(Oid(h.P2.Id));
    }

    // ---- uncomplete removes it, and a later check-off snapshots again

    [Fact]
    public async Task Uncomplete_deletesTheEntryWithItsFieldsInTheAuditAndARecompleteUsesTheCurrentPoints()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-07");
        (await Post(id, "complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await EntryOf(id))!["amount"].AsInt32.Should().Be(10);
        var stored = (await EntryOf(id))!;

        var undo = await Post(id, "uncomplete", null, h.P1);

        undo.Status.Should().Be(HttpStatusCode.OK, undo.Body.ToString());
        undo.Body.GetProperty("pointsSnapshot").ValueKind.Should().Be(JsonValueKind.Null);
        (await EntryOf(id)).Should().BeNull();
        var deleted = (await PointsAuditOf(id)).Last();
        deleted["action"].AsString.Should().Be("delete");
        deleted["entityId"].Should().Be(stored["_id"]);
        deleted["meta"].Should().Be(new BsonDocument { { "occurrenceId", Oid(id) }, { "reason", "uncomplete" } });
        (deleted["before"]["key"].AsString, deleted["before"]["amount"].AsInt32, deleted["before"]["personId"]).Should().Be(("execution:" + id, 10, Oid(h.P1.Id)));

        await SetPoints(h.Twice, 4);
        try
        {
            (await Post(id, "complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
            (await EntryOf(id))!["amount"].AsInt32.Should().Be(4);
        }
        finally
        {
            await SetPoints(h.Twice, 10);
        }
    }

    // ---- administrator corrections

    [Fact]
    public async Task EditCompletion_movesTheEntryToAnotherPersonAndDateInPlaceWithOneAuditedUpdateAndLeavesNoOpsAlone()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-01"); // Thursday, assigned to P2
        (await Post(id, "complete", new { completedBy = h.P2.Id }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var stored = (await EntryOf(id))!;
        stored["personId"].Should().Be(Oid(h.P2.Id));
        var completedAt = (await h.StoredAsync(id))["completedAt"].ToUniversalTime();
        var correction = new { date = "2026-09-29", completedAt = new DateTimeOffset(completedAt, TimeSpan.Zero), completedBy = h.P1.Id };

        var edit = await Post(id, "completion", correction, h.Admin);

        edit.Status.Should().Be(HttpStatusCode.OK, edit.Body.ToString());
        var moved = (await EntryOf(id))!;
        moved["_id"].Should().Be(stored["_id"]);
        moved["personId"].Should().Be(Oid(h.P1.Id));
        moved["amount"].AsInt32.Should().Be(10);
        moved["date"].Should().Be(PointsHarness.Midnight("2026-09-29"));
        moved["weekStart"].Should().Be(PointsHarness.Midnight("2026-09-28"));
        moved["source"].AsString.Should().Be("live");
        var updates = (await PointsAuditOf(id)).Where(e => e["action"].AsString == "update").ToList();
        var audit = updates.Should().ContainSingle().Subject;
        // The diff lists changed fields only, so the title and the amount travel in the meta for the history feed.
        audit["meta"].Should().Be(new BsonDocument { { "occurrenceId", Oid(id) }, { "reason", "correction" }, { "titleSnapshot", "Wastafel" }, { "amount", 10 } });
        audit["before"].Should().Be(new BsonDocument { { "personId", Oid(h.P2.Id) }, { "date", PointsHarness.Midnight("2026-10-01") } });
        audit["after"].Should().Be(new BsonDocument { { "personId", Oid(h.P1.Id) }, { "date", PointsHarness.Midnight("2026-09-29") } });

        // Repeating the same correction changes nothing, so nothing is written or audited.
        var (audits, size, updatedAt) = (await h.AuditCountAsync(), await LedgerSize(), (await EntryOf(id))!["updatedAt"]);
        (await Post(id, "completion", correction, h.Admin)).Status.Should().Be(HttpStatusCode.OK);
        (await h.AuditCountAsync(), await LedgerSize()).Should().Be((audits, size));

        // Only the time of day changes: the occurrence is audited, the ledger is not touched.
        var later = new DateTimeOffset(completedAt, TimeSpan.Zero).AddMinutes(1);
        (await Post(id, "completion", new { correction.date, completedAt = later, correction.completedBy }, h.Admin)).Status.Should().Be(HttpStatusCode.OK);
        (await EntryOf(id))!["updatedAt"].Should().Be(updatedAt);
        (await PointsAuditOf(id)).Where(e => e["action"].AsString == "update").Should().HaveCount(1);
        (await h.AuditCountAsync()).Should().BeGreaterThan(audits, "the occurrence (and the task's lastCompletedAt) are audited");
    }

    [Fact]
    public async Task Delete_removesTheEntryWhenAnAdministratorDeletesTheCompletion()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-08");
        (await Post(id, "complete", null, h.P2)).Status.Should().Be(HttpStatusCode.OK);
        (await EntryOf(id)).Should().NotBeNull();

        var deleted = await h.SendAsync(HttpMethod.Delete, $"/api/v2/occurrences/{id}", null, h.Admin);

        deleted.Status.Should().Be(HttpStatusCode.OK, deleted.Body.ToString());
        (await EntryOf(id)).Should().BeNull();
        var audit = (await PointsAuditOf(id)).Last();
        audit["action"].AsString.Should().Be("delete");
        audit["meta"].Should().Be(new BsonDocument { { "occurrenceId", Oid(id) }, { "reason", "correction" } });
    }

    [Fact]
    public async Task EditCompletion_aMemberIsRefusedAndTheLedgerStaysAsItWas()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-09-17");
        (await Post(id, "complete", new { completedBy = h.P2.Id }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var before = await EntryOf(id);

        var refused = await Post(id, "completion", new { date = "2026-09-17", completedAt = DateTimeOffset.UtcNow, completedBy = h.P1.Id }, h.P2);

        refused.Status.Should().Be(HttpStatusCode.Forbidden);
        (await EntryOf(id))!.ToJson().Should().Be(before!.ToJson());
    }

    // ---- task points never rewrite what was earned

    [Fact]
    public async Task ChangingTheTaskPointsLeavesPastEntriesAndSnapshotsAlone()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-16");
        (await Post(id, "complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var entries = await Ledger.Find(new BsonDocument()).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
        var audits = await h.AuditCountAsync();
        await SetPoints(h.Weekly, 2);
        try
        {
            var after = await Ledger.Find(new BsonDocument()).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
            after.Should().Equal(entries);
            (await EntryOf(id))!["amount"].AsInt32.Should().Be(30);
            (await h.StoredAsync(id))["pointsSnapshot"].AsInt32.Should().Be(30);
            (await h.AuditCountAsync()).Should().Be(audits + 1, "only the task update itself is audited");
        }
        finally
        {
            await SetPoints(h.Weekly, 30);
        }
    }

    // ---- the sync is idempotent, and repairs a lost or orphaned entry

    [Fact]
    public async Task Sync_ofAnEntryThatAlreadyMatchesWritesAndAuditsNothing_andRestoresALostEntry()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-09-16");
        (await Post(id, "complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        await using var scope = h.Services.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<IExecutionPointsService>();
        var (audits, size) = (await h.AuditCountAsync(), await LedgerSize());

        var same = await points.SyncAsync(AuditActor.System, id, PointsSyncReason.Correction, Ct);

        same.AsT0.Should().Be(SyncOutcome.Unchanged);
        (await h.AuditCountAsync(), await LedgerSize()).Should().Be((audits, size));

        // A crash between the occurrence write and the ledger write would leave no entry: the next sync restores it.
        await Ledger.DeleteOneAsync(new BsonDocument("key", "execution:" + id), Ct);
        var restored = await points.SyncAsync(AuditActor.System, id, PointsSyncReason.Correction, Ct);
        restored.AsT0.Should().Be(SyncOutcome.Created);
        (await EntryOf(id))!["amount"].AsInt32.Should().Be(10);
        (await PointsAuditOf(id)).Last()["meta"]["reason"].AsString.Should().Be("correction");
    }

    [Fact]
    public async Task Sync_removesAnOrphanedEntryOfAnOccurrenceThatNoLongerExists()
    {
        var orphan = ObjectId.GenerateNewId();
        await Ledger.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", "execution:" + orphan }, { "kind", "execution" }, { "personId", Oid(h.P1.Id) }, { "amount", 2 },
                { "date", PointsHarness.Midnight("2026-09-16") }, { "weekStart", PointsHarness.Midnight("2026-09-14") }, { "periodStart", BsonNull.Value },
                { "occurrenceId", orphan }, { "taskId", BsonNull.Value }, { "titleSnapshot", "Weg" }, { "source", "live" },
                { "createdAt", new BsonDateTime(DateTime.UtcNow) }, { "updatedAt", new BsonDateTime(DateTime.UtcNow) },
            },
            cancellationToken: Ct);
        await using var scope = h.Services.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<IExecutionPointsService>();

        var outcome = await points.SyncAsync(AuditActor.System, orphan.ToString(), PointsSyncReason.Correction, Ct);

        outcome.AsT0.Should().Be(SyncOutcome.Deleted);
        (await EntryOf(orphan.ToString())).Should().BeNull();
        (await PointsAuditOf(orphan.ToString())).Should().ContainSingle().Which["action"].AsString.Should().Be("delete");
    }

    // ---- the entry and the occurrence commit together

    [Fact]
    public async Task Complete_aLedgerFailureRollsTheWholeCheckOffBack()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-10-12");
        // An unusable entry in the way of the unique key makes the ledger write of the check-off fail: the occurrence must not stay done.
        var blocker = ObjectId.GenerateNewId();
        await Ledger.InsertOneAsync(
            new BsonDocument
            {
                { "_id", blocker }, { "key", "execution:" + id }, { "kind", "weird-kind-from-the-future" }, { "personId", Oid(h.P1.Id) }, { "amount", 1 },
                { "date", PointsHarness.Midnight("2026-09-18") }, { "weekStart", PointsHarness.Midnight("2026-09-14") }, { "occurrenceId", BsonNull.Value },
                { "taskId", BsonNull.Value }, { "titleSnapshot", "" }, { "source", "live" },
                { "createdAt", new BsonDateTime(DateTime.UtcNow) }, { "updatedAt", new BsonDateTime(DateTime.UtcNow) },
            },
            cancellationToken: Ct);
        try
        {
            var audits = await h.AuditCountAsync();

            var response = await Post(id, "complete", null, h.P1);

            response.Status.Should().Be(HttpStatusCode.InternalServerError);
            (await h.StoredAsync(id))["status"].AsString.Should().Be("open");
            (await h.AuditCountAsync()).Should().Be(audits);
        }
        finally
        {
            await Ledger.DeleteOneAsync(new BsonDocument("_id", blocker), Ct);
        }
    }
}
