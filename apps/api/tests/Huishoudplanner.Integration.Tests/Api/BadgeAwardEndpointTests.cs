#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using static Huishoudplanner.Integration.Tests.Api.BadgeDefinitionEndpointTests;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>badges.test.ts</c>, the awards (ADR-0014): derived from the audited executions at the moment the threshold was crossed, never twice, revoked and
/// moved with the data, evaluated after every execution sync, after a change of a badge and as the last step of the points reconciliation, and rebuilt
/// by a statistics reset. Awards that were damaged or drifted are written straight into the database, which no use case does, to prove the reconciliation
/// repairs them. Wednesday 16 September 2026 is in the week of Monday 14 September (cycle 0).
/// </summary>
public sealed class BadgeAwardEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static async Task<List<BsonDocument>> AwardAuditAsync(BadgeHarness h) => await h.AuditAsync("badgeAward");

    private static async Task Completed(BadgeHarness h, string now, string occurrence, object? body = null, UserIdentity? actor = null)
    {
        var response = await h.CompleteAtAsync(now, occurrence, body, actor);
        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
    }

    private static async Task<HttpStatusCode> RecomputeAsync(BadgeHarness h) =>
        (await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", null, h.P1)).Status;

    // ---- awards from executions

    [Fact]
    public async Task ABadge_isAwardedAtTheMomentTheThresholdWasCrossed_auditedOnce_andNeverAgain()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toiletjuffrouw", rule = Executions([h.Toilet], 2) }), "id");
        var first = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        var second = await h.OccurrenceAsync("2026-09-23", h.Toilet);
        var third = await h.OccurrenceAsync("2026-09-30", h.Toilet);

        await Completed(h, "2026-09-16T08:00:00.000Z", first);
        (await h.HoldersAsync(badge)).Should().BeEmpty();
        (await AwardAuditAsync(h)).Should().BeEmpty();

        h.Capture.Clear();
        await Completed(h, "2026-09-17T09:30:00.000Z", second);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-17T09:30:00.000Z" });
        var audit = (await AwardAuditAsync(h)).Should().ContainSingle().Subject;
        (audit["action"].AsString, audit["source"].AsString, audit["meta"]["reason"].AsString).Should().Be(("create", "ui", "complete"));
        audit["after"]["badgeId"].AsObjectId.ToString().Should().Be(badge);
        audit["after"]["personId"].AsObjectId.ToString().Should().Be(h.P1.Id);
        audit["after"]["awardedAt"].ToUniversalTime().Should().Be(new DateTime(2026, 9, 17, 9, 30, 0, DateTimeKind.Utc));
        audit["actorId"].AsObjectId.ToString().Should().Be(h.P1.Id);

        // A third execution only moves the progress: the award keeps the moment it was earned, and nothing more is written.
        h.Capture.Clear();
        await Completed(h, "2026-09-18T09:30:00.000Z", third);
        h.Capture.Writes().Should().NotContain(w => w.Collection == "badgeAwards");
        (await AwardAuditAsync(h)).Should().HaveCount(1);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-17T09:30:00.000Z" });

        var awards = await h.GetAsync("/api/v2/badges/awards");
        var item = awards.Body.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        (Str(item, "badgeId"), Str(item, "personId"), item.GetProperty("awardedAt").GetDateTimeOffset()).Should()
            .Be((badge, h.P1.Id, new DateTimeOffset(2026, 9, 17, 9, 30, 0, TimeSpan.Zero)));
        Str(item, "id").Should().HaveLength(24);
        var ofPerson = await h.GetAsync($"/api/v2/badges/awards?personId={h.P2.Id}");
        ofPerson.Body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TheAwardsList_isPagedOldestFirst_andRefusesABadQuery()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        await h.AddBadgeAsync(new { name = "Alles", rule = Executions([], 1) });
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await Completed(h, "2026-09-17T08:00:00.000Z", await h.OccurrenceAsync("2026-09-17", h.Toilet), actor: h.P2);

        var first = await h.GetAsync("/api/v2/badges/awards?limit=1");
        var second = await h.GetAsync($"/api/v2/badges/awards?limit=1&cursor={Uri.EscapeDataString(Str(first.Body, "nextCursor"))}");

        first.Body.GetProperty("items")[0].GetProperty("personId").GetString().Should().Be(h.P1.Id);
        second.Body.GetProperty("items")[0].GetProperty("personId").GetString().Should().Be(h.P2.Id);
        second.Body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        (await h.GetAsync("/api/v2/badges/awards?personId=nope")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/v2/badges/awards?limit=0")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/v2/badges/awards?cursor=garbage")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheProgressOfAPerson_isShown_alsoWhenTheBadgeIsNotEarnedYet()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toiletjuffrouw", rule = Executions([h.Toilet], 3) }), "id");
        var inactive = Str(await h.AddBadgeAsync(new { name = "Inactief", rule = Executions([], 1), active = false }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await Completed(h, "2026-09-16T08:05:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Vacuum));

        var progress = await h.GetAsync($"/api/v2/badges/progress?personId={h.P1.Id}");

        Str(progress.Body, "personId").Should().Be(h.P1.Id);
        var item = progress.Body.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        (Str(item, "badgeId"), item.GetProperty("current").GetInt32(), item.GetProperty("threshold").GetInt32(), item.GetProperty("awardedAt").ValueKind).Should()
            .Be((badge, 1, 3, JsonValueKind.Null));
        progress.Body.GetProperty("items").EnumerateArray().Select(i => Str(i, "badgeId")).Should().NotContain(inactive);
        (await h.GetAsync("/api/v2/badges/progress")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/v2/badges/progress?personId=nope")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Minutes_areAddedUpFromTheDurationTheOccurrencesHad_aLaterChangeOfTheTaskNeverRewritesWhatWasDone()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Dweilkampioen", rule = Minutes([h.Mop], 60) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Mop));
        (await h.HoldersAsync(badge)).Should().BeEmpty();

        // The occurrences keep the 20 minutes they were planned with, so two of them are 40, not 240.
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{h.Mop}", new { durationMinutes = 120 }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        await Completed(h, "2026-09-17T08:00:00.000Z", await h.OccurrenceAsync("2026-09-23", h.Mop));
        (await h.HoldersAsync(badge)).Should().BeEmpty();
        await Completed(h, "2026-09-18T08:00:00.000Z", await h.OccurrenceAsync("2026-09-30", h.Mop));

        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-18T08:00:00.000Z" });
    }

    [Fact]
    public async Task TheAward_isRevokedWhenTheWorkIsUndoneBelowTheThreshold_andAwardedAgainAtTheNewMoment()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toiletjuffrouw", rule = Executions([h.Toilet], 2) }), "id");
        var first = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        var second = await h.OccurrenceAsync("2026-09-23", h.Toilet);
        await Completed(h, "2026-09-16T08:00:00.000Z", first);
        await Completed(h, "2026-09-17T08:00:00.000Z", second);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-17T08:00:00.000Z" });

        var undone = await h.UncompleteAtAsync("2026-09-17T09:00:00.000Z", second);

        undone.Status.Should().Be(HttpStatusCode.OK, undone.Body.ToString());
        (await h.HoldersAsync(badge)).Should().BeEmpty();
        (await AwardAuditAsync(h)).Select(e => (e["action"].AsString, e["meta"]["reason"].AsString)).Should().Equal(("create", "complete"), ("delete", "uncomplete"));
        await Completed(h, "2026-09-19T08:00:00.000Z", second);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-19T08:00:00.000Z" });
    }

    [Fact]
    public async Task WorkThatEarnsNoPoints_isAwardedAndRevoked_wherTheLedgerDoesNotSayWhoHeldIt()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Plantenvriend", rule = Executions([h.Free], 1) }), "id");
        var id = await h.OccurrenceAsync("2026-09-16", h.Free);
        await Completed(h, "2026-09-16T08:00:00.000Z", id);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:00:00.000Z" });

        await h.UncompleteAtAsync("2026-09-16T09:00:00.000Z", id);

        (await h.HoldersAsync(badge)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAdministratorCorrectionOfWhoDidTheWork_movesTheAward_andADeletionRemovesIt()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toiletjuffrouw", rule = Executions([h.Toilet], 1) }), "id");
        var id = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        await Completed(h, "2026-09-16T08:00:00.000Z", id);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:00:00.000Z" });

        var moved = await h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/completion", new { date = "2026-09-16", completedAt = "2026-09-16T08:30:00.000Z", completedBy = h.P2.Id }, h.P1);

        moved.Status.Should().Be(HttpStatusCode.OK, moved.Body.ToString());
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p2"] = "2026-09-16T08:30:00.000Z" });
        (await h.SendAsync(HttpMethod.Delete, $"/api/v2/occurrences/{id}", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(badge)).Should().BeEmpty();
    }

    [Fact]
    public async Task ThePersonWhoDidTheWorkIsCredited_onBehalfOfTheAssigneeOrAfterTakingItOver()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toiletjuffrouw", rule = Executions([h.Toilet], 1) }), "id");
        var onBehalf = await h.OccurrenceAsync("2026-09-17", h.Toilet); // assigned to person 2
        var takenOver = await h.OccurrenceAsync("2026-09-24", h.Toilet); // assigned to person 2

        // Person 1 checks it off for the assignee: person 2 did it.
        await Completed(h, "2026-09-17T08:00:00.000Z", onBehalf, new { completedBy = h.P2.Id });
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p2"] = "2026-09-17T08:00:00.000Z" });

        // Person 1 takes the work over: person 1 did it, and gets the badge.
        await Completed(h, "2026-09-18T08:00:00.000Z", takenOver, new { takeOver = true });
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-18T08:00:00.000Z", ["p2"] = "2026-09-17T08:00:00.000Z" });
    }

    [Fact]
    public async Task RecordedExtraWork_counts_andAOneOffTaskOnlyForARuleThatCoversEveryTask()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var allTasks = Str(await h.AddBadgeAsync(new { name = "Alles", rule = Executions([], 2) }), "id");
        var toiletOnly = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 2) }), "id");
        var minutesAll = Str(await h.AddBadgeAsync(new { name = "Minuten", rule = Minutes([], 40) }), "id");
        h.Clock.Set("2026-09-16T08:00:00.000Z");

        var oneOff = await h.SendAsync(HttpMethod.Post, "/api/v2/occurrences/one-off", new { name = "Gordijnen ophangen", durationMinutes = 40, date = "2026-09-16", done = true, requestId = "badge-one-off-request-0001" }, h.P1);
        oneOff.Status.Should().Be(HttpStatusCode.Created, oneOff.Body.ToString());
        // 40 minutes in one go, but one execution of one task.
        (await h.HoldersAsync(allTasks)).Should().BeEmpty();
        (await h.HoldersAsync(toiletOnly)).Should().BeEmpty();
        (await h.HoldersAsync(minutesAll)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:00:00.000Z" });

        h.Clock.Set("2026-09-16T08:30:00.000Z");
        var extra = await h.SendAsync(HttpMethod.Post, "/api/v2/occurrences", new { taskId = h.Toilet, date = "2026-09-16", done = true, requestId = "badge-extra-request-00001" }, h.P1);
        extra.Status.Should().Be(HttpStatusCode.Created, extra.Body.ToString());
        (await h.HoldersAsync(allTasks)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:30:00.000Z" });
        // Only one toilet execution so far: the one-off task did not count for it.
        (await h.HoldersAsync(toiletOnly)).Should().BeEmpty();

        h.Clock.Set("2026-09-16T09:00:00.000Z");
        var again = await h.SendAsync(HttpMethod.Post, "/api/v2/occurrences", new { taskId = h.Toilet, date = "2026-09-16", done = true, requestId = "badge-extra-request-00002" }, h.P1);
        again.Status.Should().Be(HttpStatusCode.Created, again.Body.ToString());
        (await h.HoldersAsync(toiletOnly)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T09:00:00.000Z" });
    }

    [Fact]
    public async Task WorkNobodyCanBeCreditedFor_andWorkThatIsNotDone_earnNothing()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Alles", rule = Executions([], 1) }), "id");
        var open = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        (await h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{open}/skip", new { reason = "geen tijd" }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(badge)).Should().BeEmpty();

        // Done work that was never credited (old data without a person) earns nothing, like in the points ledger.
        var orphan = await h.OccurrenceAsync("2026-09-18", h.Toilet); // unassigned
        await h.Occurrences.UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(orphan)),
            new BsonDocument("$set", new BsonDocument { { "status", "done" }, { "completedBy", BsonNull.Value }, { "completedAt", BsonNull.Value } }),
            cancellationToken: Ct);
        (await RecomputeAsync(h)).Should().Be(HttpStatusCode.OK);

        (await h.HoldersAsync(badge)).Should().BeEmpty();
    }

    // ---- awards and the badge definition

    [Fact]
    public async Task ARuleChange_evaluatesAgain_aDeactivationOrDeletionRevokes_andReactivatingKeepsTheMoment()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 5) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await Completed(h, "2026-09-17T08:00:00.000Z", await h.OccurrenceAsync("2026-09-23", h.Toilet));
        (await h.HoldersAsync(badge)).Should().BeEmpty();

        // Lowering the threshold awards from the audited data, at the moment it was crossed, not now.
        h.Clock.Set("2026-10-01T10:00:00.000Z");
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{badge}", new { rule = Executions([h.Toilet], 2) }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-17T08:00:00.000Z" });
        var summary = (await AwardAuditAsync(h)).Last();
        summary["action"].AsString.Should().Be("recompute");
        summary["entityId"].AsObjectId.ToString().Should().Be("000000000000000000000003");
        (summary["meta"]["trigger"].AsString, summary["meta"]["created"].AsInt32, summary["meta"]["updated"].AsInt32, summary["meta"]["removed"].AsInt32, summary["meta"]["changesTotal"].AsInt32)
            .Should().Be(("badge", 1, 0, 0, 1));

        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{badge}", new { active = false }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(badge)).Should().BeEmpty();
        (await h.GetAsync("/api/v2/badges/awards")).Body.GetProperty("items").GetArrayLength().Should().Be(0);
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{badge}", new { active = true }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-17T08:00:00.000Z" });

        // A change that does not touch the rule writes no award at all.
        var rename = await h.CapturedAsync(HttpMethod.Patch, $"/api/v2/badges/{badge}", new { name = "Toiletkoningin" }, h.P1);
        rename.Writes.Should().NotContain(w => w.Collection == "badgeAwards");

        (await h.SendAsync(HttpMethod.Delete, $"/api/v2/badges/{badge}", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.AwardCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheMomentOfAnAward_moves_whenEarlierWorkIsAdded()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 2) }), "id");
        await Completed(h, "2026-09-17T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await Completed(h, "2026-09-18T08:00:00.000Z", await h.OccurrenceAsync("2026-09-23", h.Toilet));
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-18T08:00:00.000Z" });

        // An administrator corrects a third completion to a day before the others: the threshold was crossed earlier.
        var late = await h.OccurrenceAsync("2026-09-30", h.Toilet);
        await Completed(h, "2026-09-19T08:00:00.000Z", late);
        var corrected = await h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{late}/completion", new { date = "2026-09-16", completedAt = "2026-09-16T07:00:00.000Z", completedBy = h.P1.Id }, h.P1);
        corrected.Status.Should().Be(HttpStatusCode.OK, corrected.Body.ToString());

        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-17T08:00:00.000Z" });
        var update = (await AwardAuditAsync(h)).Where(e => e["action"].AsString == "update").Should().ContainSingle().Subject;
        BadgeHarness.Iso(update["before"]["awardedAt"]).Should().Be("2026-09-18T08:00:00.000Z");
        BadgeHarness.Iso(update["after"]["awardedAt"]).Should().Be("2026-09-17T08:00:00.000Z");
        update["meta"]["reason"].AsString.Should().Be("correction");
    }

    // ---- recomputation

    [Fact]
    public async Task Recomputing_isIdempotent_writesAndAuditsNothing_andAnAwardExistsOncePerBadgeAndPerson()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");
        var before = await h.Awards.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
        var auditBefore = (await AwardAuditAsync(h)).Count;

        for (var run = 0; run < 2; run++)
        {
            var capture = await h.CapturedAsync(HttpMethod.Post, "/api/v2/points/recompute", null, h.P1);
            capture.Status.Should().Be(HttpStatusCode.OK, capture.Body.ToString());
            capture.Writes.Should().NotContain(w => w.Collection == "badgeAwards");
        }

        (await h.Awards.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).Should().Equal(before);
        (await AwardAuditAsync(h)).Should().HaveCount(auditBefore);

        // The key is unique: a second award of the same badge to the same person cannot exist.
        var duplicate = before[0].DeepClone().AsBsonDocument;
        duplicate["_id"] = ObjectId.GenerateNewId();
        var insert = async () => await h.Awards.InsertOneAsync(duplicate, cancellationToken: Ct);
        (await insert.Should().ThrowAsync<MongoWriteException>()).Which.WriteError.Category.Should().Be(ServerErrorCategory.DuplicateKey);
    }

    [Fact]
    public async Task Recomputing_repairsDriftInOneSummary_aLostAwardComesBackAtTheSameMoment_anAwardWithoutDataGoes()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        var award = await h.Awards.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        await h.Awards.DeleteOneAsync(new BsonDocument("_id", award["_id"]), Ct);
        var stray = award.DeepClone().AsBsonDocument;
        stray["_id"] = ObjectId.GenerateNewId();
        stray["key"] = $"badge:{badge}:{h.P2.Id}";
        stray["personId"] = ObjectId.Parse(h.P2.Id);
        await h.Awards.InsertOneAsync(stray, cancellationToken: Ct);
        var before = (await AwardAuditAsync(h)).Count;

        (await RecomputeAsync(h)).Should().Be(HttpStatusCode.OK);

        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:00:00.000Z" });
        var summary = (await AwardAuditAsync(h)).Skip(before).Should().ContainSingle().Subject;
        summary["action"].AsString.Should().Be("recompute");
        (summary["meta"]["trigger"].AsString, summary["meta"]["created"].AsInt32, summary["meta"]["removed"].AsInt32, summary["meta"]["changesTotal"].AsInt32).Should().Be(("admin", 1, 1, 2));
        summary["meta"]["changes"].AsBsonArray.Select(c => c["change"].AsString).Order().Should().Equal("created", "removed");
    }

    [Fact]
    public async Task TheBadgeStep_isPartOfTheNightlyRun()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await h.Awards.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);

        using (var scope = h.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<INightlyService>().RunAsync(AuditActor.System, "nightly-badges", Ct);
            run.IsT0.Should().BeTrue();
        }

        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");
        var summary = (await AwardAuditAsync(h)).Last();
        (summary["meta"]["trigger"].AsString, summary["source"].AsString).Should().Be(("nightly", "system"));
    }

    [Fact]
    public async Task TheBadgeStep_isPartOfTheReconciliationAtStartup()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await h.Awards.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);

        using var restarted = h.Restart();
        (await restarted.GetAsync("/api/v2/health", Ct)).EnsureSuccessStatusCode();

        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");
        (await AwardAuditAsync(h)).Last()["meta"]["trigger"].AsString.Should().Be("startup");
    }

    // ---- on-time weeks

    private async Task<BadgeHarness> WeekHarnessAsync(bool withBonuses)
    {
        var h = await BadgeHarness.StartAsync(mongo, moveToWednesday: false);
        if (withBonuses)
        {
            var response = await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", """{ "periodBonuses": { "weekDone": 5, "weekOnTime": 3, "cycleDone": 0, "cycleOnTime": 0 } }""", h.P1);
            response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        }

        // Both people do everything planned for them in the week on time; the unassigned work stays open.
        foreach (var task in new[] { h.Toilet, h.Mop, h.Vacuum, h.Free })
        {
            await Completed(h, "2026-09-16T07:00:00.000Z", await h.OccurrenceAsync("2026-09-16", task), null, h.P1);
            await Completed(h, "2026-09-17T07:00:00.000Z", await h.OccurrenceAsync("2026-09-17", task), null, h.P2);
        }

        return h;
    }

    [Fact]
    public async Task OnTimeWeeks_areAwardedFromTheBonusesOnceTheWeekIsFinal_atTheLastDayOfThatWeek()
    {
        await using var h = await WeekHarnessAsync(withBonuses: true);
        var badge = Str(await h.AddBadgeAsync(new { name = "Alles op tijd", rule = new { type = "onTimeWeeks", threshold = 1 } }), "id");
        // The week is still running: nothing is paid yet, so nothing is earned.
        (await h.HoldersAsync(badge)).Should().BeEmpty();

        h.Clock.Set("2026-09-21T01:00:00.000Z"); // Monday 03:00 local time
        (await RecomputeAsync(h)).Should().Be(HttpStatusCode.OK);

        var held = await h.HoldersAsync(badge);
        // Both people had planned work and did all of it on time.
        held.Keys.Order().Should().Equal("p1", "p2");
        // The bonus of the week of 14 September is dated on Sunday 20 September, local midnight.
        held["p1"].Should().Be("2026-09-19T22:00:00.000Z");
        var progress = await h.GetAsync($"/api/v2/badges/progress?personId={h.P1.Id}");
        var item = progress.Body.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        (Str(item, "badgeId"), item.GetProperty("current").GetInt32(), item.GetProperty("threshold").GetInt32(), item.GetProperty("awardedAt").GetDateTimeOffset())
            .Should().Be((badge, 1, 1, new DateTimeOffset(2026, 9, 19, 22, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task OnTimeWeeks_areNeverAwarded_whileNoBonusesAreConfigured()
    {
        await using var h = await WeekHarnessAsync(withBonuses: false);
        var badge = Str(await h.AddBadgeAsync(new { name = "Alles op tijd", rule = new { type = "onTimeWeeks", threshold = 1 } }), "id");
        h.Clock.Set("2026-09-21T01:00:00.000Z");

        (await RecomputeAsync(h)).Should().Be(HttpStatusCode.OK);

        (await h.HoldersAsync(badge)).Should().BeEmpty();
    }

    // ---- the statistics reset

    [Fact]
    public async Task AStatisticsReset_rebuildsTheAwardsFromWhatRemains()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");

        // Purging before today keeps the work of today: the award stays and nothing is written.
        h.Clock.Set("2026-09-16T12:00:00.000Z");
        var purge = await h.CapturedAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-09-16", null, h.P1);
        purge.Status.Should().Be(HttpStatusCode.OK, purge.Body.ToString());
        purge.Writes.Should().NotContain(w => w.Collection == "badgeAwards");
        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");

        // Starting over reopens everything: the badge is lost with the history it was earned from.
        (await h.SendAsync(HttpMethod.Delete, "/api/v2/stats", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(badge)).Should().BeEmpty();
        var summary = (await AwardAuditAsync(h)).Last();
        (summary["action"].AsString, summary["meta"]["trigger"].AsString, summary["meta"]["removed"].AsInt32).Should().Be(("recompute", "reset", 1));
        // The definition stays.
        (await h.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task APurge_removesOnlyTheAwardsThatDependedOnThePurgedDays()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 2) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await Completed(h, "2026-09-23T08:00:00.000Z", await h.OccurrenceAsync("2026-09-23", h.Toilet));
        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");

        h.Clock.Set("2026-09-23T12:00:00.000Z");
        (await h.SendAsync(HttpMethod.Delete, "/api/v2/stats?before=2026-09-20", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        (await h.HoldersAsync(badge)).Should().BeEmpty();
    }

    // ---- what is read

    [Fact]
    public async Task ACheckOffOfATaskNoRuleCovers_readsNoExecutions_andStillAwardsTheBadgeThatIsCovered()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Dweilen", rule = Executions([h.Mop], 1) }), "id");
        var toilet = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        var mop = await h.OccurrenceAsync("2026-09-16", h.Mop);

        // The toilet is not covered by the badge on the mop: nothing is read.
        h.Reads.Start("occurrences", "durationMinutesSnapshot", "completedAt");
        await Completed(h, "2026-09-16T08:00:00.000Z", toilet);
        h.Reads.Count.Should().Be(0);
        (await h.HoldersAsync(badge)).Should().BeEmpty();

        h.Reads.Start("occurrences", "durationMinutesSnapshot", "completedAt");
        await Completed(h, "2026-09-16T09:00:00.000Z", mop);
        h.Reads.Count.Should().Be(1);
        (await h.HoldersAsync(badge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T09:00:00.000Z" });
    }

    [Fact]
    public async Task ABadgeThatDoesNotCountExecutions_readsNone_neitherAtACheckOffNorForTheProgress()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        await h.AddBadgeAsync(new { name = "Op tijd", rule = new { type = "onTimeWeeks", threshold = 2 } });
        var id = await h.OccurrenceAsync("2026-09-16", h.Toilet);

        h.Reads.Start("occurrences", "durationMinutesSnapshot", "completedAt");
        await Completed(h, "2026-09-16T08:00:00.000Z", id);
        (await h.GetAsync($"/api/v2/badges/progress?personId={h.P1.Id}")).Status.Should().Be(HttpStatusCode.OK);

        h.Reads.Count.Should().Be(0);
    }

    [Fact]
    public async Task OnlyTheCoveringBadgesAreEvaluated_stillRevokingAndAwardingCorrectly()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var mopBadge = Str(await h.AddBadgeAsync(new { name = "Dweilen", rule = Executions([h.Mop], 1) }), "id");
        var toiletBadge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        var mop = await h.OccurrenceAsync("2026-09-16", h.Mop);
        var toilet = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        await Completed(h, "2026-09-16T08:00:00.000Z", mop);
        await Completed(h, "2026-09-16T09:00:00.000Z", toilet);
        (await h.HoldersAsync(mopBadge)).Keys.Should().Equal("p1");
        (await h.HoldersAsync(toiletBadge)).Keys.Should().Equal("p1");

        await h.UncompleteAtAsync("2026-09-16T10:00:00.000Z", toilet);

        (await h.HoldersAsync(toiletBadge)).Should().BeEmpty();
        (await h.HoldersAsync(mopBadge)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:00:00.000Z" });
    }

    [Fact]
    public async Task TheExecutionsOfFewPeople_areTheSameAsThoseOfEverybody_byTheCreditRule()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet)); // person 1 did their own work
        await Completed(h, "2026-09-17T08:00:00.000Z", await h.OccurrenceAsync("2026-09-17", h.Mop), new { completedBy = h.P1.Id }, h.P2); // done for person 1 by person 2
        await Completed(h, "2026-09-17T09:00:00.000Z", await h.OccurrenceAsync("2026-09-17", h.Toilet), new { takeOver = true }, h.P1); // taken over by person 1
        // Old data without completedBy is credited to the assignee.
        var old = await h.OccurrenceAsync("2026-09-18", h.Free);
        await h.Occurrences.UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(old)),
            new BsonDocument("$set", new BsonDocument { { "status", "done" }, { "completedBy", BsonNull.Value }, { "assigneeId", ObjectId.Parse(h.P2.Id) } }),
            cancellationToken: Ct);
        var evidence = h.Services.GetRequiredService<Huishoudplanner.Domain.Ports.Driven.ForReadingBadgeEvidence>();

        var everybody = (await evidence.FindCreditedExecutionsAsync(null, Ct)).AsT0;

        everybody.Count.Should().BeGreaterThanOrEqualTo(4);
        foreach (var people in new[] { new[] { h.P1.Id }, [h.P2.Id], [h.P1.Id, h.P2.Id] })
        {
            var subset = (await evidence.FindCreditedExecutionsAsync(people, Ct)).AsT0;
            subset.Select(e => $"{e.Execution.Id}:{e.PersonId}").Order().Should().Equal(everybody.Where(e => people.Contains(e.PersonId)).Select(e => $"{e.Execution.Id}:{e.PersonId}").Order());
        }

        (await evidence.FindCreditedExecutionsAsync([ObjectId.GenerateNewId().ToString()], Ct)).AsT0.Should().BeEmpty();
    }

    [Fact]
    public async Task TheOccurrences_haveIndexesOnThePersonAndTheStatus()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var keys = (await (await h.Occurrences.Indexes.ListAsync(Ct)).ToListAsync(Ct)).Select(index => index["key"].ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = false })).ToList();

        keys.Should().Contain(k => k.Replace(" ", string.Empty, StringComparison.Ordinal) == "{\"completedBy\":1,\"status\":1}");
        keys.Should().Contain(k => k.Replace(" ", string.Empty, StringComparison.Ordinal) == "{\"assigneeId\":1,\"status\":1}");
    }

    // ---- history

    [Fact]
    public async Task TheBadgeName_isInTheAwardEntriesAndInTheSummary_alsoAfterTheBadgeWasDeleted()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toiletjuffrouw", rule = Executions([h.Toilet], 1) }), "id");
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        var create = (await AwardAuditAsync(h)).Single(e => e["action"].AsString == "create");
        (create["meta"]["reason"].AsString, create["meta"]["badgeName"].AsString).Should().Be(("complete", "Toiletjuffrouw"));

        (await h.SendAsync(HttpMethod.Delete, $"/api/v2/badges/{badge}", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        var summary = (await AwardAuditAsync(h)).Last();
        (summary["action"].AsString, summary["meta"]["trigger"].AsString, summary["meta"]["removed"].AsInt32).Should().Be(("recompute", "badge", 1));
        var change = summary["meta"]["changes"].AsBsonArray.Single();
        (change["change"].AsString, change["badgeName"].AsString).Should().Be(("removed", "Toiletjuffrouw"));
    }

    // ---- failures of the badge evaluation

    /// <summary>A badge whose rule cannot be read makes every evaluation fail.</summary>
    private static async Task BreakBadgesAsync(BadgeHarness h)
    {
        var now = new BsonDateTime(new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        await h.Badges.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "name", "Kapot" }, { "description", "" },
                { "rule", new BsonDocument { { "type", "executions" }, { "threshold", 1 } } }, { "active", true }, { "createdAt", now }, { "updatedAt", now },
            },
            cancellationToken: Ct);
    }

    [Fact]
    public async Task ABadgeChangeAStatisticsResetAndACheckOff_neverFail_whoseOwnWriteIsCommitted()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var open = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        await BreakBadgesAsync(h);

        var created = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Nieuw", rule = Executions([], 1) }, h.P1);
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        var id = Str(created.Body, "id");
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", new { rule = Executions([h.Mop], 2) }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Delete, $"/api/v2/badges/{id}", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var completed = await h.CompleteAtAsync("2026-09-16T08:00:00.000Z", open);
        completed.Status.Should().Be(HttpStatusCode.OK, completed.Body.ToString());
        (await h.SendAsync(HttpMethod.Delete, "/api/v2/stats", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReconcilingThePoints_recordsThePointsSummaryFirst_thenReportsTheFailureOfTheBadgeStep()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        await Completed(h, "2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));
        await BreakBadgesAsync(h);
        var now = new BsonDateTime(new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        await h.Database.GetCollection<BsonDocument>("pointEntries").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", $"execution:{ObjectId.GenerateNewId()}" }, { "kind", "execution" }, { "personId", ObjectId.Parse(h.P1.Id) }, { "amount", 2 },
                { "date", now }, { "weekStart", now }, { "occurrenceId", BsonNull.Value }, { "taskId", BsonNull.Value }, { "titleSnapshot", "Verdwaald" },
                { "source", "live" }, { "createdAt", now }, { "updatedAt", now },
            },
            cancellationToken: Ct);

        var response = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", null, h.P1);

        response.Status.Should().Be(HttpStatusCode.InternalServerError);
        var summaries = await h.AuditLog.Find(new BsonDocument { { "entity", "points" }, { "action", "recompute" } }).ToListAsync(Ct);
        summaries.Should().ContainSingle().Which["meta"]["removed"].AsInt32.Should().Be(1);
    }

    [Fact]
    public async Task ACheckOff_stillEvaluatesTheBadges_whenThereAreNoSettings()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = Str(await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 1) }), "id");
        var id = await h.OccurrenceAsync("2026-09-16", h.Toilet);
        await Completed(h, "2026-09-16T08:00:00.000Z", id);
        await h.Awards.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await h.Database.GetCollection<BsonDocument>("settings").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);

        using (var scope = h.Services.CreateScope())
        {
            var sync = await scope.ServiceProvider.GetRequiredService<IExecutionPointsService>().SyncAsync(AuditActor.System, id, Huishoudplanner.Domain.Points.PointsSyncReason.Correction, Ct);
            sync.IsT0.Should().BeTrue();
        }

        (await h.HoldersAsync(badge)).Keys.Should().Equal("p1");
    }
}
