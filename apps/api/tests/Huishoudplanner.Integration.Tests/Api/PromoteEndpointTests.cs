using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the <c>GET /api/promote-suggestions</c> scenarios of apps/server/test/promote.test.ts to <c>GET /api/v2/promote-suggestions</c> on
/// the real host and a real MongoDB replica set, with the moves made through the occurrence endpoints of slice 3.2 (<c>PATCH reschedule</c> is
/// <c>POST .../reschedule</c>, <c>assign</c> is <c>POST .../assignment</c>). Anchor Monday 2026-09-14; the slot is week index 1, Tuesday, for the
/// first person: cycle 0 plans it on 22 Sep, cycle 1 on 20 Oct, cycle 2 on 17 Nov. The <c>apply</c> and <c>dismiss</c> routes (slice 5.3b) are ported
/// scenario by scenario: 422 <c>invalid_plan</c> with <c>assignee_unavailable</c> and nothing changes, the slot update audited with <c>promotedFrom</c> and the
/// suggestion that disappears, the immediate synchronisation with a system origin, 409 / 404 / 400 for a plan that is not the active one, a missing slot
/// and a bad body, and the dismissal with its <c>settings update</c> audit entry (the dismissal scenario of the GET describe now uses the route).
/// Not ported: nothing. New: the open policy of the read, the empty list without moves, that a cycle still to come is ignored, that the read audits
/// nothing, the apply to another person, the replacing and the idempotent dismissal, field-keyed 400s, the missing profile and that a refusal leaves
/// plan, occurrences and audit alone.
/// </summary>
public sealed class PromoteEndpointTests(MongoContainerFixture mongo)
{
    private const string Anchor = "2026-09-14";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Arranged : IDisposable
    {
        public Arranged(GenerationHarness h, string planId, string taskId, string p1, string p2)
        {
            H = h;
            PlanId = planId;
            TaskId = taskId;
            P1 = p1;
            P2 = p2;
        }

        public GenerationHarness H { get; }

        public string PlanId { get; }

        public string TaskId { get; }

        public string P1 { get; }

        public string P2 { get; }

        public void Dispose() => H.Dispose();

        public object ApplyBody(int toWeekday = 3, string? toAssigneeId = null, int weekIndex = 1) => toAssigneeId is null
            ? new { planId = PlanId, taskId = TaskId, weekIndex, weekday = 2, toWeekday }
            : new { planId = PlanId, taskId = TaskId, weekIndex, weekday = 2, toWeekday, toAssigneeId };

        public Task<(HttpStatusCode Status, JsonElement Body)> ApplyAsync(object body, bool withProfile = true) =>
            H.SendAsync(HttpMethod.Post, "/api/v2/promote-suggestions/apply", body, withProfile);

        public object Dismissal(string lastEvidenceId, int toWeekday = 3) =>
            new { planId = PlanId, taskId = TaskId, weekIndex = 1, weekday = 2, toWeekday, toAssigneeId = (string?)null, lastEvidenceId };

        public Task<(HttpStatusCode Status, JsonElement Body)> DismissAsync(object body, bool withProfile = true) =>
            H.SendAsync(HttpMethod.Post, "/api/v2/promote-suggestions/dismiss", body, withProfile);

        public async Task<List<string>> DaysAsync() => GenerationHarness.DaysOf(await H.OccurrencesOfAsync(TaskId));

        public async Task<long> AuditCountAsync() =>
            await H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken);

        public async Task<BsonDocument> StoredPlanAsync() =>
            await H.Database.GetCollection<BsonDocument>("cyclePlans").Find(new BsonDocument("_id", ObjectId.Parse(PlanId))).SingleAsync(TestContext.Current.CancellationToken);

        public async Task<JsonElement> GetAsync()
        {
            var (status, body) = await H.SendAsync(HttpMethod.Get, "/api/v2/promote-suggestions", withProfile: false);
            status.Should().Be(HttpStatusCode.OK, body.ToString());
            return body.GetProperty("items");
        }

        /// <summary>Drags the occurrence planned in <paramref name="cycle"/> to <paramref name="date"/>, optionally to another person; returns its id.</summary>
        public async Task<string> MoveAsync(int cycle, string date, string? assigneeId = null)
        {
            var planned = DateOnly.Parse(Anchor, System.Globalization.CultureInfo.InvariantCulture).AddDays((cycle * 28) + 7 + 1)
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var occurrence = (await H.OccurrencesOfAsync(TaskId)).Single(o => PlannedDay(o) == planned);
            var id = occurrence["_id"].AsObjectId.ToString();
            var moved = await H.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/reschedule", new { date });
            moved.Status.Should().Be(HttpStatusCode.OK, moved.Body.ToString());
            if (assigneeId is not null)
            {
                var assigned = await H.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/assignment", new { assigneeId });
                assigned.Status.Should().Be(HttpStatusCode.OK, assigned.Body.ToString());
            }

            return id;
        }

        private static string PlannedDay(BsonDocument occurrence) =>
            TimeZoneInfo.ConvertTime(new DateTimeOffset(occurrence["plannedDate"].ToUniversalTime(), TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"))
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Arranged> ArrangeAsync(int[]? p1Unavailable = null)
    {
        var h = new GenerationHarness(mongo, $"{Anchor}T06:00:00.000Z");
        var room = await h.SeedRoomAsync("Badkamer");
        var p1 = await h.SeedPersonAsync("Persoon 1", p1Unavailable);
        var p2 = await h.SeedPersonAsync("Persoon 2");
        var task = await h.NewTaskAsync("Badkamer schoonmaken", room, "4wk", 30);
        var plan = await h.ActivePlanIdAsync();
        await h.PutSlotsAsync(plan, (task, 1, 2, p1));
        await h.GenerateUpcomingAsync();
        return new Arranged(h, plan, task, p1, p2);
    }

    [Fact]
    public async Task DoesNotSuggestWithoutMovesOrAfterASingleMove()
    {
        using var a = await ArrangeAsync();
        (await a.GetAsync()).GetArrayLength().Should().Be(0);

        await a.MoveAsync(0, "2026-09-23");

        (await a.GetAsync()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SuggestsTheNewWeekdayWhenTwoConsecutiveCyclesWereMovedTheSameWay()
    {
        using var a = await ArrangeAsync();
        var first = await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");

        var items = await a.GetAsync();

        var suggestion = items.EnumerateArray().Should().ContainSingle().Subject;
        suggestion.GetProperty("planId").GetString().Should().Be(a.PlanId);
        suggestion.GetProperty("taskId").GetString().Should().Be(a.TaskId);
        suggestion.GetProperty("taskName").GetString().Should().Be("Badkamer schoonmaken");
        var from = suggestion.GetProperty("fromSlot");
        (from.GetProperty("weekIndex").GetInt32(), from.GetProperty("weekday").GetInt32(), from.GetProperty("assigneeId").GetString()).Should().Be((1, 2, a.P1));
        suggestion.GetProperty("toWeekday").GetInt32().Should().Be(3);
        suggestion.GetProperty("toAssigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        suggestion.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()).Should().Equal(second, first);
    }

    [Fact]
    public async Task DoesNotSuggestWhenTheMovesWentToDifferentWeekdays()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23"); // Wednesday
        await a.MoveAsync(1, "2026-10-22"); // Thursday

        (await a.GetAsync()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task IncludesTheOtherPersonWhenEveryMoveAlsoWentToThem()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-24", a.P2);
        await a.MoveAsync(1, "2026-10-22", a.P2);

        var suggestion = (await a.GetAsync()).EnumerateArray().Should().ContainSingle().Subject;

        (suggestion.GetProperty("toWeekday").GetInt32(), suggestion.GetProperty("toAssigneeId").GetString()).Should().Be((4, a.P2));
    }

    [Fact]
    public async Task KeepsADismissedSuggestionAwayUntilThereIsNewerEvidence()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");
        (await a.GetAsync()).GetArrayLength().Should().Be(1);

        var auditBefore = (await a.H.AuditAsync("settings", "update")).Count;
        var (status, body) = await a.DismissAsync(a.Dismissal(second));
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("dismissed").GetBoolean().Should().BeTrue();
        (await a.H.AuditAsync("settings", "update")).Should().HaveCount(auditBefore + 1);
        (await a.GetAsync()).GetArrayLength().Should().Be(0);

        // A cycle later the same move happens again.
        a.H.Clock.Set("2026-10-12T06:00:00.000Z");
        await a.H.GenerateUpcomingAsync();
        var third = await a.MoveAsync(2, "2026-11-18");

        var evidence = (await a.GetAsync()).EnumerateArray().Single().GetProperty("evidence").EnumerateArray().Select(e => e.GetString()).ToList();
        evidence[0].Should().Be(third);
    }

    [Fact]
    public async Task IgnoresACycleThatIsStillToComeAndUntouched()
    {
        using var a = await ArrangeAsync();
        var first = await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");
        a.H.Clock.Set("2026-10-12T06:00:00.000Z");
        await a.H.GenerateUpcomingAsync(); // cycle 2 now exists, planned for 17 Nov, nobody touched it

        var evidence = (await a.GetAsync()).EnumerateArray().Single().GetProperty("evidence").EnumerateArray().Select(e => e.GetString());

        evidence.Should().Equal(second, first);
    }

    [Fact]
    public async Task AnswersWithoutAProfile_likeTheNodeRoute()
    {
        using var a = await ArrangeAsync();

        var (status, _) = await a.H.SendAsync(HttpMethod.Get, "/api/v2/promote-suggestions", withProfile: false);

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WritesNothingAndAuditsNothing()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23");
        await a.MoveAsync(1, "2026-10-21");
        var before = await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        await a.GetAsync();
        await a.GetAsync();

        (await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(before);
    }

    // ---- POST /api/v2/promote-suggestions/apply (describe 'POST /api/promote-suggestions/apply')

    [Fact]
    public async Task Apply_failsWith422WhenTheNewWeekdayIsUnavailableForTheAssignee_andChangesNothing()
    {
        using var a = await ArrangeAsync(p1Unavailable: [3]);
        await a.MoveAsync(0, "2026-09-23");
        await a.MoveAsync(1, "2026-10-21");
        var planBefore = (await a.StoredPlanAsync()).ToJson();
        var daysBefore = await a.DaysAsync();
        var auditBefore = await a.AuditCountAsync();

        var (status, body) = await a.ApplyAsync(a.ApplyBody());

        status.Should().Be(HttpStatusCode.UnprocessableEntity, body.ToString());
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:invalid_plan");
        var issue = body.GetProperty("issues").EnumerateArray().Should().ContainSingle().Subject;
        issue.GetProperty("code").GetString().Should().Be("assignee_unavailable");
        body.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Array);
        body.GetProperty("summary").GetProperty("weeks").GetArrayLength().Should().Be(4);
        (await a.StoredPlanAsync()).ToJson().Should().Be(planBefore);
        (await a.DaysAsync()).Should().Equal(daysBefore);
        (await a.AuditCountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task Apply_updatesTheSlot_auditsItWithPromotedFrom_andTheSuggestionDisappears()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23");
        await a.MoveAsync(1, "2026-10-21");

        var (status, body) = await a.ApplyAsync(a.ApplyBody());

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var slot = body.GetProperty("plan").GetProperty("slots").EnumerateArray().Should().ContainSingle().Subject;
        (slot.GetProperty("weekIndex").GetInt32(), slot.GetProperty("weekday").GetInt32(), slot.GetProperty("assigneeId").GetString()).Should().Be((1, 3, a.P1));
        body.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Array);
        body.GetProperty("summary").GetProperty("weeks").GetArrayLength().Should().Be(4);
        body.GetProperty("synchronized").ValueKind.Should().Be(JsonValueKind.Object);
        var entry = (await a.H.AuditAsync("cyclePlan", "update", Builders<BsonDocument>.Filter.Eq("meta.toWeekday", 3))).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("ui");
        var promotedFrom = entry["meta"]["promotedFrom"].AsBsonDocument;
        (promotedFrom["weekIndex"].ToInt32(), promotedFrom["weekday"].ToInt32(), promotedFrom["assigneeId"].AsObjectId.ToString()).Should().Be((1, 2, a.P1));
        entry["meta"].AsBsonDocument.Contains("toAssigneeId").Should().BeFalse();
        (await a.GetAsync()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Apply_canMoveTheSlotToAnotherPersonToo()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-24", a.P2);
        await a.MoveAsync(1, "2026-10-22", a.P2);

        var (status, body) = await a.ApplyAsync(a.ApplyBody(4, a.P2));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var slot = body.GetProperty("plan").GetProperty("slots").EnumerateArray().Single();
        (slot.GetProperty("weekday").GetInt32(), slot.GetProperty("assigneeId").GetString()).Should().Be((4, a.P2));
        var entry = (await a.H.AuditAsync("cyclePlan", "update", Builders<BsonDocument>.Filter.Eq("meta.toWeekday", 4))).Should().ContainSingle().Subject;
        entry["meta"]["toAssigneeId"].AsString.Should().Be(a.P2);
    }

    [Fact]
    public async Task Apply_synchronizesTheFutureOccurrencesImmediately_likeASlotSave_recordedWithASystemOrigin()
    {
        using var a = await ArrangeAsync();
        (await a.DaysAsync()).Should().Equal("2026-09-22", "2026-10-20");

        var (status, body) = await a.ApplyAsync(a.ApplyBody());

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await a.DaysAsync()).Should().Equal("2026-09-23", "2026-10-21");
        var deletions = await a.H.AuditAsync("occurrence", "delete", Builders<BsonDocument>.Filter.Eq("meta.reason", "plan_update"));
        deletions.Should().HaveCount(2);
        deletions.Should().OnlyContain(d => d["source"] == "system");
        deletions.Should().OnlyContain(d => d["actorId"].AsObjectId.ToString() == a.H.Planner.Id);
    }

    [Fact]
    public async Task Apply_onlyChangesTheActivePlan_andAnExistingSlot()
    {
        using var a = await ArrangeAsync();
        var auditBefore = await a.AuditCountAsync();

        var wrongPlan = await a.ApplyAsync(new { planId = "0123456789abcdef01234567", taskId = a.TaskId, weekIndex = 1, weekday = 2, toWeekday = 3 });
        var noSlot = await a.ApplyAsync(a.ApplyBody(weekIndex: 2));
        var invalid = await a.ApplyAsync(new { planId = a.PlanId });

        wrongPlan.Status.Should().Be(HttpStatusCode.Conflict);
        wrongPlan.Body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:plan_not_active");
        noSlot.Status.Should().Be(HttpStatusCode.NotFound);
        noSlot.Body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:slot_not_found");
        invalid.Status.Should().Be(HttpStatusCode.BadRequest);
        invalid.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo("taskId", "weekIndex", "weekday", "toWeekday");
        (await a.AuditCountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task Apply_refusesAPlanThatIsNotActive_evenIfItHoldsTheSlot()
    {
        using var a = await ArrangeAsync();
        var (created, plan) = await a.H.SendAsync(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "Zomer", copyFromId = a.PlanId });
        created.Should().Be(HttpStatusCode.Created, plan.ToString());

        var (status, body) = await a.ApplyAsync(new { planId = plan.GetProperty("id").GetString(), taskId = a.TaskId, weekIndex = 1, weekday = 2, toWeekday = 3 });

        status.Should().Be(HttpStatusCode.Conflict, body.ToString());
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:plan_not_active");
    }

    [Fact]
    public async Task Apply_rejectsBadValuesAndBadTypesFieldByField()
    {
        using var a = await ArrangeAsync();

        // The shape is read first (types, missing members), then the values.
        var shape = await a.ApplyAsync(new { planId = "x", taskId = a.TaskId, weekIndex = 4, weekday = 7, toWeekday = "3" });
        var values = await a.ApplyAsync(new { planId = "x", taskId = a.TaskId, weekIndex = 4, weekday = 7, toWeekday = 3, toAssigneeId = "nope" });

        shape.Status.Should().Be(HttpStatusCode.BadRequest, shape.Body.ToString());
        shape.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("toWeekday");
        values.Status.Should().Be(HttpStatusCode.BadRequest, values.Body.ToString());
        values.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("planId", "weekIndex", "weekday", "toAssigneeId");
    }

    [Fact]
    public async Task Apply_rejectsAnEmptyOrMalformedBody()
    {
        using var a = await ArrangeAsync();
        using var empty = new HttpRequestMessage(HttpMethod.Post, "/api/v2/promote-suggestions/apply") { Content = new StringContent("{oops", System.Text.Encoding.UTF8, "application/json") };
        empty.Headers.Add("X-Profile-Id", a.H.Planner.Id);
        empty.Headers.Add("X-Client", "web");

        var response = await a.H.Client.SendAsync(empty, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- POST /api/v2/promote-suggestions/dismiss

    [Fact]
    public async Task Dismiss_storesTheDismissal_andAuditsASettingsUpdate()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");

        var settingsCollection = a.H.Database.GetCollection<BsonDocument>("settings");
        var versionBefore = await settingsCollection.VersionAsync(FilterDefinition<BsonDocument>.Empty);
        var (status, body) = await a.DismissAsync(a.Dismissal(second));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("dismissed").GetBoolean().Should().BeTrue();
        var settings = await a.H.Database.GetCollection<BsonDocument>("settings").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        var stored = settings["dismissedPromotions"].AsBsonArray.Should().ContainSingle().Subject.AsBsonDocument;
        (stored["planId"].AsObjectId.ToString(), stored["lastEvidenceId"].AsObjectId.ToString(), stored["toAssigneeId"].IsBsonNull).Should().Be((a.PlanId, second, true));
        var entries = await a.H.AuditAsync("settings", "update", Builders<BsonDocument>.Filter.Exists("after.dismissedPromotions"));
        entries.Should().ContainSingle().Which["source"].AsString.Should().Be("ui");
        (await settingsCollection.VersionAsync(FilterDefinition<BsonDocument>.Empty)).Should().BeGreaterThan(versionBefore, "the dismissal changed the settings, so a stale ETag of the settings is refused afterwards");
    }

    [Fact]
    public async Task Dismiss_ofTheSameTargetReplacesTheEarlierOne_andDismissingWhatIsStoredAuditsNothing()
    {
        using var a = await ArrangeAsync();
        var first = ObjectId.GenerateNewId().ToString();
        var newer = ObjectId.GenerateNewId().ToString();
        await a.DismissAsync(a.Dismissal(first));
        await a.DismissAsync(a.Dismissal(newer));
        var auditBefore = await a.AuditCountAsync();

        var again = await a.DismissAsync(a.Dismissal(newer));

        again.Status.Should().Be(HttpStatusCode.OK);
        var settings = await a.H.Database.GetCollection<BsonDocument>("settings").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        settings["dismissedPromotions"].AsBsonArray.Select(d => d["lastEvidenceId"].AsObjectId.ToString()).Should().Equal(newer);
        (await a.AuditCountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task Dismiss_rejectsABadBodyFieldByField_andWritesNothing()
    {
        using var a = await ArrangeAsync();
        var auditBefore = await a.AuditCountAsync();

        var shape = await a.DismissAsync(new { planId = a.PlanId, taskId = a.TaskId, weekIndex = 1, weekday = 2, toWeekday = 3, lastEvidenceId = "x" });
        var values = await a.DismissAsync(new { planId = a.PlanId, taskId = a.TaskId, weekIndex = 1, weekday = 2, toWeekday = 9, toAssigneeId = (string?)null, lastEvidenceId = "x" });

        shape.Status.Should().Be(HttpStatusCode.BadRequest, shape.Body.ToString());
        shape.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("toAssigneeId");
        values.Status.Should().Be(HttpStatusCode.BadRequest, values.Body.ToString());
        values.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("toWeekday", "lastEvidenceId");
        (await a.AuditCountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task TheWrites_needAProfile_andAnEmptyBodyIsAValidationError()
    {
        using var a = await ArrangeAsync();

        var apply = await a.ApplyAsync(a.ApplyBody(), withProfile: false);
        var dismiss = await a.DismissAsync(a.Dismissal(ObjectId.GenerateNewId().ToString()), withProfile: false);
        var emptyApply = await a.H.SendAsync(HttpMethod.Post, "/api/v2/promote-suggestions/apply");

        apply.Status.Should().Be(HttpStatusCode.BadRequest);
        apply.Body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:profile_required");
        dismiss.Body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:profile_required");
        emptyApply.Status.Should().Be(HttpStatusCode.BadRequest);
        emptyApply.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain("body");
    }
}
