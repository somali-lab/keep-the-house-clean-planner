#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>DELETE /api/v2/tasks/{id}</c> (slice 6.6) on the real host and a real replica set. Ports the delete scenario of <c>tasks.test.ts</c> (the task is gone and
/// its previous values are audited) and the <c>deleting a task (ADR-0014)</c> scenario of <c>badges.test.ts</c> (the rules that name the task stop naming it, a rule
/// left without tasks is switched off, the awards follow, each change is a <c>task_deleted</c> badge update, the badges stay editable and the export imports
/// again), and adds what v2 specifies for the plans (the removed slots are audited as a plan update with <c>task_delete</c>) and the one transaction. The household
/// is the one of <see cref="BadgeHarness"/>: an active plan with the slots of four tasks and generated occurrences. Not ported: the Node behaviour that removes the
/// task from the plans before it knows the task exists (a 404 left the plans changed); here an unknown task changes nothing.
/// </summary>
public sealed class TaskDeleteEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static object Executions(string[] tasks, int threshold) => new { type = "executions", taskIds = tasks, threshold };

    private static string ReasonOf(BsonDocument entry) => entry["meta"]["reason"].AsString;

    private static async Task<string> ActivePlanAsync(BadgeHarness h) =>
        Str((await h.GetAsync("/api/v2/cycle-plans/active")).Body, "id");

    [Fact]
    public async Task AnAdministrator_deletesATask_andItsPreviousValuesAreAudited()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var result = await h.SendAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Free}", null, h.P1);

        result.Status.Should().Be(HttpStatusCode.OK, result.Body.ToString());
        result.Body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        (await h.GetAsync("/api/v2/tasks?limit=200")).Body.GetProperty("items").EnumerateArray().Select(t => Str(t, "id")).Should().NotContain(h.Free);
        (await h.Database.GetCollection<BsonDocument>("tasks").CountDocumentsAsync(new BsonDocument("_id", ObjectId.Parse(h.Free)), cancellationToken: Ct)).Should().Be(0);
        var entry = (await h.AuditAsync("task")).Single(e => e["action"].AsString == "delete");
        (entry["entityId"].AsObjectId.ToString(), entry["source"].AsString, entry["actorId"].AsObjectId.ToString()).Should().Be((h.Free, "ui", h.P1.Id));
        entry["before"]["name"].AsString.Should().Be("Planten water geven");
        entry["before"]["points"].AsInt32.Should().Be(0);
        entry["before"]["durationMinutes"].AsInt32.Should().Be(15);
        entry["before"].AsBsonDocument.Contains("createdAt").Should().BeFalse();
        entry["after"].AsBsonDocument.ElementCount.Should().Be(0);
    }

    [Fact]
    public async Task TheSlotsLeaveTheActivePlan_withOneUpdateEntryThatNamesTheTask_andTheOccurrencesStay()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var plan = await ActivePlanAsync(h);
        var occurrencesBefore = await h.Occurrences.CountDocumentsAsync(new BsonDocument("taskId", ObjectId.Parse(h.Toilet)), cancellationToken: Ct);
        occurrencesBefore.Should().BeGreaterThan(0);
        var allOccurrencesBefore = await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        var occurrenceEntriesBefore = (await h.AuditAsync("occurrence")).Count;

        var result = await h.SendAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Toilet}", null, h.P1);

        result.Status.Should().Be(HttpStatusCode.OK, result.Body.ToString());
        var slots = (await h.GetAsync($"/api/v2/cycle-plans/{plan}")).Body.GetProperty("slots").EnumerateArray().ToList();
        slots.Should().HaveCount(36, "three tasks of twelve slots remain");
        slots.Select(s => Str(s, "taskId")).Should().NotContain(h.Toilet);
        var planEntries = (await h.AuditAsync("cyclePlan")).Where(e => e["action"].AsString == "update" && e.Contains("meta") && e["meta"].IsBsonDocument && ReasonOf(e) == "task_delete").ToList();
        var entry = planEntries.Should().ContainSingle().Subject;
        (entry["entityId"].AsObjectId.ToString(), entry["meta"]["taskId"].AsObjectId.ToString(), entry["source"].AsString).Should().Be((plan, h.Toilet, "ui"));
        entry["before"]["slots"].AsBsonArray.Should().HaveCount(12);
        entry["before"]["slots"].AsBsonArray.Select(s => s["taskId"].AsObjectId.ToString()).Should().OnlyContain(id => id == h.Toilet);
        entry["after"]["slots"].AsBsonArray.Should().BeEmpty();

        // Occurrences carry their own snapshot: the work stays where it is, open and done, and nothing about it is written.
        (await h.Occurrences.CountDocumentsAsync(new BsonDocument("taskId", ObjectId.Parse(h.Toilet)), cancellationToken: Ct)).Should().Be(occurrencesBefore);
        (await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(allOccurrencesBefore);
        (await h.AuditAsync("occurrence")).Should().HaveCount(occurrenceEntriesBefore);
    }

    [Fact]
    public async Task TheRulesThatNameTheTaskStopNamingIt_aRuleLeftWithoutTasksIsSwitchedOff_andTheAwardsFollow()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var both = await h.AddBadgeAsync(new { name = "Twee taken", rule = Executions([h.Toilet, h.Mop], 5) });
        var only = await h.AddBadgeAsync(new { name = "Eén taak", rule = Executions([h.Toilet], 1) });
        var bothId = Str(both, "id");
        var onlyId = Str(only, "id");
        (await h.CompleteAtAsync(BadgeHarness.Wednesday, await h.OccurrenceAsync("2026-09-16", h.Toilet))).Status.Should().Be(HttpStatusCode.OK);
        (await h.HoldersAsync(onlyId)).Keys.Should().Equal("p1");

        var deleted = await h.SendAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Toilet}", null, h.P1);

        deleted.Status.Should().Be(HttpStatusCode.OK, deleted.Body.ToString());
        var badges = (await h.GetAsync("/api/v2/badges")).Body.GetProperty("items").EnumerateArray().ToList();
        var twoNow = badges.Single(b => Str(b, "id") == bothId);
        var oneNow = badges.Single(b => Str(b, "id") == onlyId);
        twoNow.GetProperty("rule").GetProperty("taskIds").EnumerateArray().Select(t => t.GetString()).Should().Equal(h.Mop);
        twoNow.GetProperty("active").GetBoolean().Should().BeTrue();
        // A rule that named tasks and names none now is switched off: an empty list would count every task.
        oneNow.GetProperty("rule").GetProperty("taskIds").GetArrayLength().Should().Be(0);
        oneNow.GetProperty("active").GetBoolean().Should().BeFalse();
        (await h.HoldersAsync(onlyId)).Should().BeEmpty();
        var updates = (await h.AuditAsync("badge")).Where(e => e["action"].AsString == "update").ToList();
        updates.Should().HaveCount(2);
        updates.Should().OnlyContain(e => ReasonOf(e) == "task_deleted" && e["meta"]["taskId"].AsObjectId.ToString() == h.Toilet);
        (await h.AuditAsync("badgeAward")).Should().Contain(e => e["action"].AsString == "recompute" && e["meta"]["trigger"].AsString == "badge");

        // Both badges can still be saved.
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{bothId}", new { name = "Nog steeds" }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{onlyId}", new { active = true, rule = Executions([h.Mop], 1) }, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        // And the export of the household imports again.
        var export = await h.SendAsync(HttpMethod.Get, "/api/v2/export/json", null, h.P1);
        export.Status.Should().Be(HttpStatusCode.OK);
        await using var target = await BadgeHarness.StartAsync(mongo, moveToWednesday: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/import/json?mode=replace&confirm=true")
        {
            Content = new StringContent(export.Body.GetRawText(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Profile-Id", target.P1.Id);
        request.Headers.Add("X-Client", "web");
        var imported = await target.Client.SendAsync(request, Ct);
        imported.StatusCode.Should().Be(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(Ct));
        (await target.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public async Task EverythingIsWrittenInOneRequest_plansTaskRulesAndAwards_eachWithItsAuditEntry()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        await h.AddBadgeAsync(new { name = "Eén taak", rule = Executions([h.Toilet], 1) });
        (await h.CompleteAtAsync(BadgeHarness.Wednesday, await h.OccurrenceAsync("2026-09-16", h.Toilet))).Status.Should().Be(HttpStatusCode.OK);

        var deleted = await h.CapturedAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Toilet}", null, h.P1);

        deleted.Status.Should().Be(HttpStatusCode.OK, deleted.Body.ToString());
        deleted.Writes.Select(w => w.Collection).Distinct().Should().BeEquivalentTo("cyclePlans", "tasks", "badges", "badgeAwards");
        // plan update, task delete, badge update and the one badgeAward summary of the evaluation that follows
        deleted.AuditInserts.Should().Be(4);
        h.Capture.AbortedTransactions.Should().Be(0);
    }

    [Fact]
    public async Task ATaskThatNoPlanAndNoRuleNames_isJustRemoved_withItsOwnEntryOnly()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var created = await h.SendAsync(HttpMethod.Post, "/api/v2/tasks", new { name = "Los klusje", roomId = h.Room, intervalKey = "1w", durationMinutes = 5 }, h.P1);
        var id = Str(created.Body, "id");

        var deleted = await h.CapturedAsync(HttpMethod.Delete, $"/api/v2/tasks/{id}", null, h.P1);

        deleted.Status.Should().Be(HttpStatusCode.OK, deleted.Body.ToString());
        deleted.Writes.Select(w => w.Collection).Should().Equal("tasks");
        deleted.AuditInserts.Should().Be(1);
    }

    [Fact]
    public async Task AnUnknownTask_is404NotFound_andNothingIsWritten_asIsTheSecondDeleteOfTheSameTask()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var plan = await ActivePlanAsync(h);

        var unknown = await h.CapturedAsync(HttpMethod.Delete, "/api/v2/tasks/0123456789abcdef01234567", null, h.P1);

        unknown.Status.Should().Be(HttpStatusCode.NotFound, unknown.Body.ToString());
        unknown.Body.GetProperty("type").GetString().Should().EndWith(":not_found");
        unknown.Writes.Should().BeEmpty();
        unknown.AuditInserts.Should().Be(0);
        (await h.GetAsync($"/api/v2/cycle-plans/{plan}")).Body.GetProperty("slots").GetArrayLength().Should().Be(48);

        (await h.SendAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Vacuum}", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var again = await h.CapturedAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Vacuum}", null, h.P1);
        again.Status.Should().Be(HttpStatusCode.NotFound);
        again.Writes.Should().BeEmpty();
        again.AuditInserts.Should().Be(0);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0123456789abcdef0123456")]
    [InlineData("0123456789abcdef012345678")]
    public async Task AMalformedId_is400ValidationErrorOnId(string id)
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var result = await h.CapturedAsync(HttpMethod.Delete, $"/api/v2/tasks/{id}", null, h.P1);

        result.Status.Should().Be(HttpStatusCode.BadRequest, result.Body.ToString());
        result.Body.GetProperty("type").GetString().Should().EndWith(":validation_error");
        result.Body.GetProperty("errors").TryGetProperty("id", out _).Should().BeTrue();
        result.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyAPlannerMayDelete_aMemberIsRefused_andARequestWithoutAProfileNeedsOne()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var member = await h.SendAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Toilet}", null, h.P2);
        var anonymous = await h.SendAsync(HttpMethod.Delete, $"/api/v2/tasks/{h.Toilet}", null, null);

        member.Status.Should().Be(HttpStatusCode.Forbidden);
        member.Body.GetProperty("type").GetString().Should().EndWith(":permission_denied");
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        anonymous.Body.GetProperty("type").GetString().Should().EndWith(":profile_required");
        (await h.GetAsync("/api/v2/tasks?limit=200")).Body.GetProperty("items").EnumerateArray().Select(t => Str(t, "id")).Should().Contain(h.Toilet);
    }
}
