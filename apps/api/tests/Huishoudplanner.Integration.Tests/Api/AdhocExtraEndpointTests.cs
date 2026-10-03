using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>adhoc-occurrences.test.ts</c> on <c>POST /api/v2/occurrences</c> and <c>POST /api/v2/occurrences/{id}/retraction</c> (Node: <c>/retract</c>), on the real
/// host and a real replica set: the planned extra, the extra recorded as done now, the idempotent creation (a replay, a conflict, a key that comes back after a retract
/// and real races on one key) and the retract rules. Every scenario makes tasks of its own and sets the clock itself, so no scenario depends on another;
/// the generated occurrence of Badkamer schoonmaken on Wednesday 16 September is used by one scenario only. Deferred to phase 4: the points ledger effects.
/// </summary>
public sealed class AdhocExtraEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private const string Url = "/api/v2/occurrences";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static string Code(string code) => $"urn:huishoudplanner:problem:{code}";

    private static ObjectId Oid(string id) => ObjectId.Parse(id);



    private static string NextKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string UniqueName(string name) => $"{name} {Guid.NewGuid():N}";

    private async Task<string> NewTaskAsync(string name, int minutes = 30, string? defaultAssignee = null, int? points = null) =>
        await h.NewTaskAsync(UniqueName(name), "1w", minutes, defaultAssignee, points);

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(object body, Huishoudplanner.Domain.Identity.UserIdentity? actor = null) =>
        h.SendAsync(HttpMethod.Post, Url, body, actor ?? h.P1);

    private Task<(HttpStatusCode Status, JsonElement Body)> Retract(string id, Huishoudplanner.Domain.Identity.UserIdentity? actor = null) =>
        h.SendAsync(HttpMethod.Post, $"{Url}/{id}/retraction", null, actor ?? h.P1);

    private async Task<List<BsonDocument>> OccurrencesOfAsync(string taskId) =>
        await h.Occurrences.Find(new BsonDocument("taskId", Oid(taskId))).ToListAsync(Ct);

    // ---- planned extra

    [Fact]
    public async Task Plan_usesTheDefaultAssigneeAndIsAuditedAsACreateFromTheUi()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var task = await NewTaskAsync("Ramen lappen", 60, h.P2.Id);

        var response = await Post(new { taskId = task, date = "2026-09-19" });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        var o = response.Body;
        o.GetProperty("taskId").GetString().Should().Be(task);
        (o.GetProperty("date").GetString(), o.GetProperty("plannedDate").GetString(), o.GetProperty("status").GetString(), o.GetProperty("origin").GetString()).Should().Be(("2026-09-19", "2026-09-19", "open", "adhoc"));
        (o.GetProperty("assigneeId").GetString(), o.GetProperty("recordedDone").GetBoolean(), o.GetProperty("requestId").ValueKind, o.GetProperty("planId").ValueKind).Should().Be((h.P2.Id, false, JsonValueKind.Null, JsonValueKind.Null));
        (o.GetProperty("roomNameSnapshot").GetString(), o.GetProperty("durationMinutesSnapshot").GetInt32(), o.GetProperty("isOverdue").GetBoolean(), o.GetProperty("movedFrom").ValueKind).Should().Be(("Badkamer", 60, false, JsonValueKind.Null));
        o.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var id = o.GetProperty("id").GetString()!;
        var entry = (await h.AuditOfAsync("occurrence", id, "create")).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("ui");
        entry["actorId"].Should().Be(Oid(h.P1.Id));
        entry["meta"].AsBsonDocument.ToJson().Should().Be("{ \"origin\" : \"adhoc\", \"kind\" : \"extra\", \"recordedDone\" : false, \"requestId\" : null }");
        entry["after"]["recordedDone"].AsBoolean.Should().BeFalse();
        entry["after"]["requestId"].IsBsonNull.Should().BeTrue();
        entry["before"].AsBsonDocument.ElementCount.Should().Be(0);
    }

    [Fact]
    public async Task Plan_acceptsAnExplicitAnyone()
    {
        var task = await NewTaskAsync("Ramen lappen", 60, h.P2.Id);

        var response = await Post(new { taskId = task, date = "2026-09-20", assigneeId = (string?)null });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        response.Body.GetProperty("assigneeId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Plan_warnsWhenTheTaskIsAlreadyPlannedThatDayButStillCreatesTheSecondOccurrence()
    {
        var task = await NewTaskAsync("Ramen lappen");
        (await Post(new { taskId = task, date = "2026-09-19" })).Status.Should().Be(HttpStatusCode.Created);

        var second = await Post(new { taskId = task, date = "2026-09-19" });

        second.Status.Should().Be(HttpStatusCode.Created, second.Body.ToString());
        var warning = second.Body.GetProperty("warnings").EnumerateArray().Should().ContainSingle().Subject;
        warning.GetProperty("code").GetString().Should().Be("task_already_planned");
        (warning.GetProperty("details").GetProperty("taskId").GetString(), warning.GetProperty("details").GetProperty("date").GetString()).Should().Be((task, "2026-09-19"));
        (await OccurrencesOfAsync(task)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Plan_onlyWithinGeneratedCycles()
    {
        var task = await NewTaskAsync("Ramen lappen");
        var audit = await h.AuditCountAsync();

        var response = await Post(new { taskId = task, date = "2026-12-01" });

        response.Status.Should().Be(HttpStatusCode.Conflict);
        Type(response.Body).Should().Be(Code("cycle_not_generated"));
        response.Body.GetProperty("date").GetString().Should().Be("2026-12-01");
        (await h.AuditCountAsync()).Should().Be(audit);
    }

    [Fact]
    public async Task Plan_aPlannedExtraThatIsCompletedLaterStillUncompletesBackToOpen()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var task = await NewTaskAsync("Ramen lappen");
        var created = await Post(new { taskId = task, date = "2026-09-21", assigneeId = h.P1.Id });
        var id = created.Body.GetProperty("id").GetString()!;

        (await h.SendAsync(HttpMethod.Post, $"{Url}/{id}/complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var open = await h.SendAsync(HttpMethod.Post, $"{Url}/{id}/uncomplete", null, h.P1);

        open.Status.Should().Be(HttpStatusCode.OK, open.Body.ToString());
        (open.Body.GetProperty("status").GetString(), open.Body.GetProperty("recordedDone").GetBoolean(), open.Body.GetProperty("completedAt").ValueKind).Should().Be(("open", false, JsonValueKind.Null));
        var retract = await Retract(id);
        retract.Status.Should().Be(HttpStatusCode.Conflict);
        Type(retract.Body).Should().Be(Code("not_retractable"));
    }

    [Fact]
    public async Task Plan_validatesTaskAssigneeDateRequestKeyBodyAndProfile()
    {
        var task = await NewTaskAsync("Ramen lappen");
        var unknown = "0123456789abcdef01234567";
        var audit = await h.AuditCountAsync();

        var unknownTask = await Post(new { taskId = unknown, date = "2026-09-22" });
        var unknownUser = await Post(new { taskId = task, date = "2026-09-22", assigneeId = unknown });
        var badDate = await Post(new { taskId = task, date = "22-09-2026" });
        var noDate = await Post(new { taskId = task });
        var badKey = await Post(new { taskId = task, date = "2026-09-22", requestId = "short" });
        var badId = await Post(new { taskId = "nope", date = "2026-09-22" });
        var badType = await Post(new { taskId = task, date = "2026-09-22", done = "yes" });
        var malformed = await h.SendAsync(HttpMethod.Post, Url, "{ not json", h.P1);
        var anonymous = await h.SendAsync(HttpMethod.Post, Url, new { taskId = task, date = "2026-09-22" }, null);

        unknownTask.Status.Should().Be(HttpStatusCode.BadRequest);
        unknownTask.Body.GetProperty("errors").GetProperty("taskId")[0].GetString().Should().Be("unknown_task");
        unknownUser.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("unknown_user");
        badDate.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("invalid_day_key");
        noDate.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("is required");
        badKey.Body.GetProperty("errors").GetProperty("requestId")[0].GetString().Should().Be("invalid_request_key");
        badId.Body.GetProperty("errors").GetProperty("taskId")[0].GetString().Should().Be("invalid_object_id");
        badType.Body.GetProperty("errors").GetProperty("done")[0].GetString().Should().Be("must be true or false");
        malformed.Status.Should().Be(HttpStatusCode.BadRequest);
        malformed.Body.GetProperty("errors").TryGetProperty("body", out _).Should().BeTrue();
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(anonymous.Body).Should().Be(Code("profile_required"));
        (await h.AuditCountAsync()).Should().Be(audit);
    }

    [Fact]
    public async Task Plan_anInactiveTaskIsRefused()
    {
        var task = await NewTaskAsync("Tijdelijke taak");
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{task}", new { active = false }, h.Planner)).Status.Should().Be(HttpStatusCode.OK);

        var response = await Post(new { taskId = task, date = "2026-09-22" });

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.GetProperty("errors").GetProperty("taskId")[0].GetString().Should().Be("inactive_task");
    }

    // ---- done now

    [Fact]
    public async Task Record_threeSameDayExtrasNextToTheGeneratedOneLastCompletedAtFollowsTheNewestAndRetractRestoresIt()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var generated = await h.IdOfAsync(h.Weekly, "2026-09-16");
        (await h.SendAsync(HttpMethod.Post, $"{Url}/{generated}/complete", null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        var ids = new List<string>();
        foreach (var (instant, key) in new[] { ("2026-09-16T09:00:00.000Z", "extra-execution-key-0101"), ("2026-09-16T10:00:00.000Z", "extra-execution-key-0102"), ("2026-09-16T11:00:00.000Z", "extra-execution-key-0103") })
        {
            h.Clock.Set(instant);
            var created = await Post(new { taskId = h.Weekly, date = "2026-09-16", done = true, requestId = key });
            created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
            ids.Add(created.Body.GetProperty("id").GetString()!);
        }

        var all = await h.Occurrences.Find(new BsonDocument { { "taskId", Oid(h.Weekly) }, { "date", new BsonDateTime(DateTime.Parse("2026-09-15T22:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal)) } }).ToListAsync(Ct);
        all.Should().HaveCount(4);
        all.Should().OnlyContain(o => o["status"].AsString == "done");
        all.Count(o => o["origin"].AsString == "adhoc").Should().Be(3);
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc));

        // Retracting the newest one restores the previous completion; a second retract is gone.
        var audit = await h.AuditCountAsync();
        var retracted = await Retract(ids[2]);
        retracted.Status.Should().Be(HttpStatusCode.OK, retracted.Body.ToString());
        retracted.Body.GetProperty("retracted").GetBoolean().Should().BeTrue();
        retracted.Body.GetProperty("id").GetString().Should().Be(ids[2]);
        var entry = (await h.AuditOfAsync("occurrence", ids[2], "delete")).Should().ContainSingle().Subject;
        entry["meta"].AsBsonDocument.ToJson().Should().Be("{ \"reason\" : \"retract\" }");
        entry["before"]["status"].AsString.Should().Be("done");
        entry["before"]["recordedDone"].AsBoolean.Should().BeTrue();
        entry["after"].AsBsonDocument.ElementCount.Should().Be(0);
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc));
        (await OccurrencesOfAsync(h.Weekly)).Count(o => o["date"].ToUniversalTime() == new DateTime(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc)).Should().Be(3);

        audit = await h.AuditCountAsync();
        var again = await Retract(ids[2]);
        again.Status.Should().Be(HttpStatusCode.NotFound);
        (await h.AuditCountAsync()).Should().Be(audit);

        // Uncomplete would leave an open record behind, so recorded work must be retracted instead.
        var blocked = await h.SendAsync(HttpMethod.Post, $"{Url}/{ids[1]}/uncomplete", null, h.P1);
        blocked.Status.Should().Be(HttpStatusCode.Conflict);
        Type(blocked.Body).Should().Be(Code("retract_required"));
        (await h.AuditCountAsync()).Should().Be(audit);

        (await Retract(ids[1])).Status.Should().Be(HttpStatusCode.OK);
        (await Retract(ids[0])).Status.Should().Be(HttpStatusCode.OK);
        // Only the generated completion remains.
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Record_oneDoneDocumentForTheActorAuditedWithItsFinalFieldsAndTheDueClockRestarts()
    {
        h.Clock.Set("2026-09-16T12:00:00.000Z");
        var task = await NewTaskAsync("Ramen lappen", 60, h.P2.Id, points: 45);
        var key = NextKey("extra-execution-key");

        var response = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        var o = response.Body;
        (o.GetProperty("status").GetString(), o.GetProperty("origin").GetString(), o.GetProperty("recordedDone").GetBoolean(), o.GetProperty("requestId").GetString()).Should().Be(("done", "adhoc", true, key));
        (o.GetProperty("statusBeforeCompletion").ValueKind, o.GetProperty("completedBy").GetString(), o.GetProperty("assigneeId").GetString(), o.GetProperty("pointsSnapshot").GetInt32()).Should().Be((JsonValueKind.Null, h.P1.Id, h.P1.Id, 45));
        o.GetProperty("completedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        o.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var id = o.GetProperty("id").GetString()!;
        var entry = (await h.AuditOfAsync("occurrence", id, "create")).Should().ContainSingle().Subject;
        entry["after"]["status"].AsString.Should().Be("done");
        entry["after"]["recordedDone"].AsBoolean.Should().BeTrue();
        entry["after"]["requestId"].AsString.Should().Be(key);
        entry["meta"].AsBsonDocument.ToJson().Should().Be($"{{ \"origin\" : \"adhoc\", \"kind\" : \"extra\", \"recordedDone\" : true, \"requestId\" : \"{key}\" }}");
        (await h.LastCompletedAtAsync(task)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));
        (await h.AuditOfAsync("task", task, "update")).Should().Contain(e => e["meta"]["occurrenceId"] == Oid(id));
        var due = await h.SendAsync(HttpMethod.Get, "/api/v2/due?limit=200", null, null);
        due.Body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("taskId").GetString() == task).GetProperty("daysSince").GetInt32().Should().Be(0);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Record_forAnExplicitPerson()
    {
        h.Clock.Set("2026-09-16T12:30:00.000Z");
        var task = await NewTaskAsync("Ramen lappen");

        var response = await Post(new { taskId = task, date = "2026-09-16", done = true, assigneeId = h.P2.Id });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        (response.Body.GetProperty("completedBy").GetString(), response.Body.GetProperty("assigneeId").GetString()).Should().Be((h.P2.Id, h.P2.Id));
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Record_requiresTodayAndAPersonAndWritesNothingOtherwise()
    {
        h.Clock.Set("2026-09-16T13:00:00.000Z");
        var task = await NewTaskAsync("Ramen lappen");
        var audit = await h.AuditCountAsync();
        var count = await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var future = await Post(new { taskId = task, date = "2026-09-17", done = true });
        var past = await Post(new { taskId = task, date = "2026-09-15", done = true });
        var nobody = await Post(new { taskId = task, date = "2026-09-16", done = true, assigneeId = (string?)null });

        future.Status.Should().Be(HttpStatusCode.BadRequest);
        future.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("done_requires_today");
        past.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("done_requires_today");
        nobody.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("done_requires_person");
        (await h.AuditCountAsync()).Should().Be(audit);
        (await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(count);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Record_warnsAboutAPlannedOccurrenceAndLeavesItAloneAndAReplayDoesNotWarnAgain()
    {
        h.Clock.Set("2026-09-16T13:30:00.000Z");
        var task = await NewTaskAsync("Planten water geven");
        var key = NextKey("extra-execution-key");
        (await Post(new { taskId = task, date = "2026-09-16" })).Status.Should().Be(HttpStatusCode.Created);

        var response = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        response.Body.GetProperty("warnings").EnumerateArray().Single().GetProperty("code").GetString().Should().Be("task_already_planned");
        (await OccurrencesOfAsync(task)).Count(o => o["status"].AsString == "open").Should().Be(1);
        var replay = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });
        replay.Status.Should().Be(HttpStatusCode.OK);
        replay.Body.GetProperty("warnings").GetArrayLength().Should().Be(0);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    // ---- idempotent creation

    [Fact]
    public async Task Idempotent_aRepeatedRequestReplaysTheStoredRecordWithOneDocumentOneEntryAndNothingWritten()
    {
        h.Clock.Set("2026-09-16T14:00:00.000Z");
        var task = await NewTaskAsync("Keukenkastjes");
        var key = NextKey("extra-execution-key");
        var first = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });
        first.Status.Should().Be(HttpStatusCode.Created, first.Body.ToString());
        var id = first.Body.GetProperty("id").GetString()!;
        var stored = await h.StoredAsync(id);
        var audit = await h.AuditCountAsync();
        var taskDoc = await h.Tasks.Find(new BsonDocument("_id", Oid(task))).SingleAsync(Ct);

        h.Clock.Set("2026-09-16T14:05:00.000Z");
        var replay = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });

        replay.Status.Should().Be(HttpStatusCode.OK, replay.Body.ToString());
        replay.Body.GetProperty("id").GetString().Should().Be(id);
        replay.Body.GetProperty("completedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero));
        (await h.AuditCountAsync()).Should().Be(audit);
        (await h.StoredAsync(id)).ToJson().Should().Be(stored.ToJson());
        (await h.Tasks.Find(new BsonDocument("_id", Oid(task))).SingleAsync(Ct)).ToJson().Should().Be(taskDoc.ToJson());
        (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
        (await h.AuditOfAsync("occurrence", id, "create")).Should().ContainSingle();
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Idempotent_aReusedKeyForADifferentRequestIsRefusedWithoutWriting()
    {
        h.Clock.Set("2026-09-16T15:00:00.000Z");
        var task = await NewTaskAsync("Spiegels poetsen");
        var other = await NewTaskAsync("Stofzuigen trap");
        var key = NextKey("extra-execution-key");
        (await Post(new { taskId = task, date = "2026-09-16", requestId = key })).Status.Should().Be(HttpStatusCode.Created);
        var audit = await h.AuditCountAsync();

        var differentDate = await Post(new { taskId = task, date = "2026-09-17", requestId = key });
        var differentTask = await Post(new { taskId = other, date = "2026-09-16", requestId = key });
        var differentDone = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });

        foreach (var response in new[] { differentDate, differentTask, differentDone })
        {
            response.Status.Should().Be(HttpStatusCode.Conflict, response.Body.ToString());
            Type(response.Body).Should().Be(Code("idempotency_key_conflict"));
        }

        (await h.AuditCountAsync()).Should().Be(audit);
        (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Idempotent_theRecordIsCreatedAgainWhenTheKeyIsReusedAfterARetract()
    {
        h.Clock.Set("2026-09-16T16:00:00.000Z");
        var task = await NewTaskAsync("Deurmatten uitkloppen");
        var key = NextKey("extra-execution-key");
        var first = (await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key })).Body.GetProperty("id").GetString()!;
        (await Retract(first)).Status.Should().Be(HttpStatusCode.OK);

        var again = await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key });

        again.Status.Should().Be(HttpStatusCode.Created, again.Body.ToString());
        again.Body.GetProperty("id").GetString().Should().NotBe(first);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    // ---- real races on one key

    [Fact]
    public async Task Race_concurrentIdenticalRequestsCreateExactlyOneRecordAndTheOthersReplayIt()
    {
        h.Clock.Set("2026-09-16T17:00:00.000Z");
        var task = await NewTaskAsync("Race identiek");
        for (var round = 0; round < 5; round++)
        {
            var key = NextKey("extra-race-same");
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(new { taskId = task, date = "2026-09-16", done = true, requestId = key })));

            responses.Count(r => r.Status == HttpStatusCode.Created).Should().Be(1, string.Join(" | ", responses.Select(r => r.Status + " " + r.Body)));
            responses.Count(r => r.Status == HttpStatusCode.OK).Should().Be(7, string.Join(" | ", responses.Select(r => r.Status + " " + r.Body)));
            var ids = responses.Select(r => r.Body.GetProperty("id").GetString()).Distinct().ToList();
            ids.Should().ContainSingle();
            (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
            (await h.AuditOfAsync("occurrence", ids[0], "create")).Should().ContainSingle();
        }

        // One completion per round, none lost or doubled: the task's clock sits on the shared moment.
        (await h.LastCompletedAtAsync(task)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 17, 0, 0, DateTimeKind.Utc));
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Race_concurrentRequestsWithOneKeyForDifferentRequestsLetExactlyOneWinAndRefuseTheOthers()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var task = await NewTaskAsync("Race verschillend");
        for (var round = 0; round < 5; round++)
        {
            var key = NextKey("extra-race-diff");
            var days = new[] { "2026-09-18", "2026-09-19", "2026-09-20", "2026-09-21" };
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Post(new { taskId = task, date = days[i % days.Length], requestId = key })));

            var created = responses.Where(r => r.Status == HttpStatusCode.Created).ToList();
            created.Should().ContainSingle(string.Join(" | ", responses.Select(r => r.Status + " " + r.Body)));
            var winnerDay = created[0].Body.GetProperty("date").GetString();
            foreach (var (response, index) in responses.Select((r, i) => (r, i)))
            {
                if (response.Status == HttpStatusCode.Created)
                {
                    continue;
                }

                if (days[index % days.Length] == winnerDay)
                {
                    response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
                    response.Body.GetProperty("id").GetString().Should().Be(created[0].Body.GetProperty("id").GetString());
                }
                else
                {
                    response.Status.Should().Be(HttpStatusCode.Conflict, response.Body.ToString());
                    Type(response.Body).Should().Be(Code("idempotency_key_conflict"));
                }
            }

            (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
        }
    }

    // ---- retract

    [Fact]
    public async Task Retract_refusesWorkThatWasNotRecordedAsDoneAnUnknownIdAMalformedIdAndNoProfile()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var generated = await h.IdOfAsync(h.Twice, "2026-09-24");
        (await h.SendAsync(HttpMethod.Post, $"{Url}/{generated}/complete", null, h.P2)).Status.Should().Be(HttpStatusCode.OK);
        var audit = await h.AuditCountAsync();

        var refused = await Retract(generated);
        var unknown = await Retract("0123456789abcdef01234567");
        var malformed = await Retract("nope");
        var anonymous = await h.SendAsync(HttpMethod.Post, $"{Url}/{generated}/retraction", null, null);

        refused.Status.Should().Be(HttpStatusCode.Conflict);
        Type(refused.Body).Should().Be(Code("not_retractable"));
        unknown.Status.Should().Be(HttpStatusCode.NotFound);
        malformed.Status.Should().Be(HttpStatusCode.BadRequest);
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.AuditCountAsync()).Should().Be(audit);
        (await h.StoredAsync(generated))["status"].AsString.Should().Be("done");
    }

    [Fact]
    public async Task Retract_onlyWorkOfTodayARecordOfAnEarlierDayIsRefusedAndStays()
    {
        h.Clock.Set("2026-09-16T16:00:00.000Z");
        var task = await NewTaskAsync("Plinten afnemen");
        var recorded = (await Post(new { taskId = task, date = "2026-09-16", done = true, requestId = NextKey("extra-execution-key") })).Body.GetProperty("id").GetString()!;

        // The next day it is history: retracting is an undo of today's work, and older completions need an administrator.
        h.Clock.Set("2026-09-17T08:00:00.000Z");
        var audit = await h.AuditCountAsync();
        var refused = await Retract(recorded, h.P2);

        refused.Status.Should().Be(HttpStatusCode.Conflict);
        Type(refused.Body).Should().Be(Code("retract_not_today"));
        (await h.AuditCountAsync()).Should().Be(audit);
        (await OccurrencesOfAsync(task)).Should().ContainSingle(o => o["status"].AsString == "done");
        (await h.LastCompletedAtAsync(task)).IsBsonNull.Should().BeFalse();
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Retract_anotherProfileMayRetractTodaysWork()
    {
        h.Clock.Set("2026-09-16T16:30:00.000Z");
        var task = await NewTaskAsync("Plinten afnemen");
        var recorded = (await Post(new { taskId = task, date = "2026-09-16", done = true })).Body.GetProperty("id").GetString()!;

        var response = await Retract(recorded, h.P2);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (await h.AuditOfAsync("occurrence", recorded, "delete")).Single()["actorId"].Should().Be(Oid(h.P2.Id));
        (await h.LastCompletedAtAsync(task)).IsBsonNull.Should().BeTrue();
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }
}
