using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>one-off-occurrences.test.ts</c> on <c>POST /api/v2/occurrences/one-off</c> on the real host and a real replica set: the planned and the recorded one-off task, the idempotent
/// creation, the retract, and the rule that a one-off task has no central task record (task list, due list). Every scenario sets the clock itself and names its one-off tasks
/// uniquely, so no scenario depends on another. Not ported: the AI proposal input (the proposal reads the task store only, which a one-off task is never part of, see
/// <c>AiService</c>) and the points ledger effects (phase 4). The activation scenario is <see cref="AdhocOneOffActivationTests"/>.
/// </summary>
public sealed class AdhocOneOffEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private const string Url = "/api/v2/occurrences/one-off";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static string Code(string code) => $"urn:huishoudplanner:problem:{code}";

    private static ObjectId Oid(string id) => ObjectId.Parse(id);



    private static string NextKey() => $"one-off-request-key-{Guid.NewGuid():N}";

    private static string Name(string name) => $"{name} {Guid.NewGuid():N}";

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(object body, Huishoudplanner.Domain.Identity.UserIdentity? actor = null) =>
        h.SendAsync(HttpMethod.Post, Url, body, actor ?? h.P1);

    private Task<(HttpStatusCode Status, JsonElement Body)> Retract(string id) =>
        h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/retraction", null, h.P1);

    private Task<(HttpStatusCode Status, JsonElement Body)> Act(string id, string action, object? body = null) =>
        h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/{action}", body, h.P1);

    // ---- planned

    [Fact]
    public async Task Plan_usesTheSnapshotsOnlyIsUnassignedByDefaultAndIsAuditedAsOneOff()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var name = Name("Gordijnen ophangen");
        var tasks = await h.Tasks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var response = await Post(new { name = $"  {name} ", roomId = h.Room, durationMinutes = 40, date = "2026-09-19" });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        var o = response.Body;
        o.GetProperty("taskId").ValueKind.Should().Be(JsonValueKind.Null);
        (o.GetProperty("date").GetString(), o.GetProperty("plannedDate").GetString(), o.GetProperty("assigneeId").ValueKind, o.GetProperty("status").GetString()).Should().Be(("2026-09-19", "2026-09-19", JsonValueKind.Null, "open"));
        (o.GetProperty("origin").GetString(), o.GetProperty("recordedDone").GetBoolean(), o.GetProperty("requestId").ValueKind, o.GetProperty("planId").ValueKind).Should().Be(("adhoc", false, JsonValueKind.Null, JsonValueKind.Null));
        (o.GetProperty("taskNameSnapshot").GetString(), o.GetProperty("roomIdSnapshot").GetString(), o.GetProperty("roomNameSnapshot").GetString(), o.GetProperty("durationMinutesSnapshot").GetInt32()).Should().Be((name, h.Room, "Badkamer", 40));
        o.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var entry = (await h.AuditOfAsync("occurrence", o.GetProperty("id").GetString(), "create")).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("ui");
        entry["meta"].AsBsonDocument.ToJson().Should().Be("{ \"origin\" : \"adhoc\", \"kind\" : \"one_off\", \"recordedDone\" : false, \"requestId\" : null }");
        entry["after"]["taskId"].IsBsonNull.Should().BeTrue();
        entry["after"]["taskNameSnapshot"].AsString.Should().Be(name);
        // No task document is created.
        (await h.Tasks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(tasks);
    }

    [Fact]
    public async Task Plan_aMissingRoomIsStoredAsNullSnapshotsAndAnAssigneeOrAnyoneIsAccepted()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var name = Name("Kast ophalen");

        var named = await Post(new { name, durationMinutes = 25, date = "2026-09-20", assigneeId = h.P2.Id });
        var anyone = await Post(new { name, roomId = (string?)null, durationMinutes = 25, date = "2026-09-20", assigneeId = (string?)null });

        named.Status.Should().Be(HttpStatusCode.Created, named.Body.ToString());
        (named.Body.GetProperty("taskId").ValueKind, named.Body.GetProperty("roomIdSnapshot").ValueKind, named.Body.GetProperty("roomNameSnapshot").ValueKind, named.Body.GetProperty("assigneeId").GetString()).Should().Be((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, h.P2.Id));
        anyone.Status.Should().Be(HttpStatusCode.Created, anyone.Body.ToString());
        (anyone.Body.GetProperty("roomIdSnapshot").ValueKind, anyone.Body.GetProperty("assigneeId").ValueKind).Should().Be((JsonValueKind.Null, JsonValueKind.Null));
    }

    [Fact]
    public async Task Plan_severalIdenticalOneOffTasksOnOneDayCoexistAndNoneWarns()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var body = new { name = Name("Dubbele klus"), durationMinutes = 5, date = "2026-09-21" };

        var first = await Post(body);
        var second = await Post(body);

        first.Status.Should().Be(HttpStatusCode.Created);
        second.Status.Should().Be(HttpStatusCode.Created);
        second.Body.GetProperty("warnings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Plan_validatesInputRoomAssigneeCycleAndProfileWithoutWriting()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var zolder = (await h.SendAsync(HttpMethod.Post, "/api/v2/rooms", new { name = Name("Zolder") }, h.Admin)).Body.GetProperty("id").GetString()!;
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/rooms/{zolder}", new { active = false }, h.Admin)).Status.Should().Be(HttpStatusCode.OK);
        var audit = await h.AuditCountAsync();
        var count = await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        var unknown = "0123456789abcdef01234567";

        var inactive = await Post(new { name = "Klus", roomId = zolder, durationMinutes = 10, date = "2026-09-22" });
        var unknownRoom = await Post(new { name = "Klus", roomId = unknown, durationMinutes = 10, date = "2026-09-22" });
        var unknownUser = await Post(new { name = "Klus", durationMinutes = 10, date = "2026-09-22", assigneeId = unknown });

        inactive.Status.Should().Be(HttpStatusCode.BadRequest);
        inactive.Body.GetProperty("errors").GetProperty("roomId")[0].GetString().Should().Be("inactive_room");
        unknownRoom.Body.GetProperty("errors").GetProperty("roomId")[0].GetString().Should().Be("unknown_room");
        unknownUser.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("unknown_user");

        var bad = new object[]
        {
            new { name = "   ", durationMinutes = 10, date = "2026-09-22" },
            new { name = new string('x', 121), durationMinutes = 10, date = "2026-09-22" },
            new { name = "Klus", durationMinutes = 0, date = "2026-09-22" },
            new { name = "Klus", durationMinutes = 1.5, date = "2026-09-22" },
            new { name = "Klus", durationMinutes = 10, date = "22-09-2026" },
            new { name = "Klus", durationMinutes = 10, date = "2026-09-22", requestId = "short" },
            new { name = "Klus", durationMinutes = 10, date = "2026-09-22", points = 1001 },
            new { name = "Klus", durationMinutes = 10, date = "2026-09-22", points = -1 },
            new { durationMinutes = 10, date = "2026-09-22" },
        };
        foreach (var body in bad)
        {
            (await Post(body)).Status.Should().Be(HttpStatusCode.BadRequest, JsonSerializer.Serialize(body));
        }

        (await Post(new { name = "Klus", durationMinutes = 10, date = "2026-09-22" })).Status.Should().Be(HttpStatusCode.Created);
        var anonymous = await h.SendAsync(HttpMethod.Post, Url, new { name = "Klus", durationMinutes = 10, date = "2026-09-22" }, null);
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(anonymous.Body).Should().Be(Code("profile_required"));

        var notGenerated = await Post(new { name = "Klus", durationMinutes = 10, date = "2026-12-01" });
        notGenerated.Status.Should().Be(HttpStatusCode.Conflict);
        Type(notGenerated.Body).Should().Be(Code("cycle_not_generated"));
        notGenerated.Body.GetProperty("date").GetString().Should().Be("2026-12-01");

        // Only the one valid request above wrote anything: one occurrence, one entry.
        (await h.AuditCountAsync()).Should().Be(audit + 1);
        (await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(count + 1);
    }

    // ---- points (ADR-0011)

    [Fact]
    public async Task Points_theChosenValueIsKeptAndRecordedWorkSnapshotsItElseOnePointPerMinute()
    {
        h.Clock.Set("2026-09-16T11:30:00.000Z");

        var planned = await Post(new { name = Name("Puntenklus"), durationMinutes = 90, date = "2026-09-22", points = 12 });
        var recorded = await Post(new { name = Name("Puntenklus"), durationMinutes = 90, date = "2026-09-16", done = true, points = 12 });
        var byDuration = await Post(new { name = Name("Puntenklus"), durationMinutes = 90, date = "2026-09-16", done = true });
        var zero = await Post(new { name = Name("Puntenklus"), durationMinutes = 90, date = "2026-09-16", done = true, points = 0 });

        (planned.Body.GetProperty("pointsOverride").GetInt32(), planned.Body.GetProperty("pointsSnapshot").ValueKind).Should().Be((12, JsonValueKind.Null));
        (recorded.Body.GetProperty("pointsOverride").GetInt32(), recorded.Body.GetProperty("pointsSnapshot").GetInt32()).Should().Be((12, 12));
        (byDuration.Body.GetProperty("pointsOverride").ValueKind, byDuration.Body.GetProperty("pointsSnapshot").GetInt32()).Should().Be((JsonValueKind.Null, 90));
        (zero.Body.GetProperty("pointsOverride").GetInt32(), zero.Body.GetProperty("pointsSnapshot").GetInt32()).Should().Be((0, 0));
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    // ---- done now

    [Fact]
    public async Task Record_oneDoneDocumentForTheActorWithoutTouchingAnyTask()
    {
        h.Clock.Set("2026-09-16T12:00:00.000Z");
        var key = NextKey();
        var name = Name("Zolder opruimen");
        var taskAudit = (await h.AuditOfAsync("task")).Count;

        var response = await Post(new { name, roomId = h.Room, durationMinutes = 90, date = "2026-09-16", done = true, requestId = key });

        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        var o = response.Body;
        (o.GetProperty("taskId").ValueKind, o.GetProperty("status").GetString(), o.GetProperty("recordedDone").GetBoolean(), o.GetProperty("requestId").GetString()).Should().Be((JsonValueKind.Null, "done", true, key));
        (o.GetProperty("statusBeforeCompletion").ValueKind, o.GetProperty("completedBy").GetString(), o.GetProperty("assigneeId").GetString()).Should().Be((JsonValueKind.Null, h.P1.Id, h.P1.Id));
        o.GetProperty("completedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var entry = (await h.AuditOfAsync("occurrence", o.GetProperty("id").GetString(), "create")).Should().ContainSingle().Subject;
        entry["meta"].AsBsonDocument.ToJson().Should().Be($"{{ \"origin\" : \"adhoc\", \"kind\" : \"one_off\", \"recordedDone\" : true, \"requestId\" : \"{key}\" }}");
        entry["after"]["status"].AsString.Should().Be("done");
        entry["after"]["recordedDone"].AsBoolean.Should().BeTrue();
        (await h.AuditOfAsync("task")).Count.Should().Be(taskAudit);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Record_forAnExplicitPersonAndItRequiresTodayAndAPerson()
    {
        h.Clock.Set("2026-09-16T12:30:00.000Z");

        var explicitPerson = await Post(new { name = Name("Boekenkast"), durationMinutes = 15, date = "2026-09-16", done = true, assigneeId = h.P2.Id });
        var audit = await h.AuditCountAsync();
        var future = await Post(new { name = "Morgen", durationMinutes = 15, date = "2026-09-17", done = true });
        var nobody = await Post(new { name = "Niemand", durationMinutes = 15, date = "2026-09-16", done = true, assigneeId = (string?)null });

        (explicitPerson.Body.GetProperty("completedBy").GetString(), explicitPerson.Body.GetProperty("assigneeId").GetString()).Should().Be((h.P2.Id, h.P2.Id));
        future.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("done_requires_today");
        nobody.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("done_requires_person");
        (await h.AuditCountAsync()).Should().Be(audit);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    // ---- idempotent creation

    [Fact]
    public async Task Idempotent_aRepeatedRequestReplaysTheStoredRecordWithOneDocumentOneEntryAndNothingWritten()
    {
        h.Clock.Set("2026-09-16T14:00:00.000Z");
        var key = NextKey();
        var body = new { name = Name("Fiets repareren"), durationMinutes = 30, date = "2026-09-16", done = true, requestId = key };
        var first = await Post(body);
        first.Status.Should().Be(HttpStatusCode.Created, first.Body.ToString());
        var id = first.Body.GetProperty("id").GetString()!;
        var stored = await h.StoredAsync(id);
        var audit = await h.AuditCountAsync();

        var replay = await Post(body);

        replay.Status.Should().Be(HttpStatusCode.OK, replay.Body.ToString());
        replay.Body.GetProperty("id").GetString().Should().Be(id);
        (await h.AuditCountAsync()).Should().Be(audit);
        (await h.StoredAsync(id)).ToJson().Should().Be(stored.ToJson());
        (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Idempotent_aReusedKeyForADifferentRequestIsRefusedWithoutWriting()
    {
        h.Clock.Set("2026-09-16T15:00:00.000Z");
        var key = NextKey();
        var body = new { name = Name("Schuur leegmaken"), durationMinutes = 30, date = "2026-09-16", requestId = key };
        (await Post(body)).Status.Should().Be(HttpStatusCode.Created);
        var audit = await h.AuditCountAsync();

        var differentName = await Post(new { name = "Andere klus", durationMinutes = 30, date = "2026-09-16", requestId = key });
        var differentDate = await Post(new { body.name, durationMinutes = 30, date = "2026-09-17", requestId = key });
        var differentDone = await Post(new { body.name, durationMinutes = 30, date = "2026-09-16", done = true, requestId = key });
        var asExtra = await h.SendAsync(HttpMethod.Post, "/api/v2/occurrences", new { taskId = h.Weekly, date = "2026-09-16", requestId = key }, h.P1);

        foreach (var response in new[] { differentName, differentDate, differentDone, asExtra })
        {
            response.Status.Should().Be(HttpStatusCode.Conflict, response.Body.ToString());
            Type(response.Body).Should().Be(Code("idempotency_key_conflict"));
        }

        (await h.AuditCountAsync()).Should().Be(audit);
        (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Race_concurrentIdenticalOneOffRequestsCreateExactlyOneRecord()
    {
        h.Clock.Set("2026-09-16T17:00:00.000Z");
        for (var round = 0; round < 5; round++)
        {
            var key = NextKey();
            var body = new { name = Name("Race eenmalig"), durationMinutes = 20, date = "2026-09-16", done = true, requestId = key };

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(body)));

            responses.Count(r => r.Status == HttpStatusCode.Created).Should().Be(1, string.Join(" | ", responses.Select(r => r.Status + " " + r.Body)));
            responses.Count(r => r.Status == HttpStatusCode.OK).Should().Be(7, string.Join(" | ", responses.Select(r => r.Status + " " + r.Body)));
            var ids = responses.Select(r => r.Body.GetProperty("id").GetString()).Distinct().ToList();
            ids.Should().ContainSingle();
            (await h.Occurrences.CountDocumentsAsync(new BsonDocument("requestId", key), cancellationToken: Ct)).Should().Be(1);
            (await h.AuditOfAsync("occurrence", ids[0], "create")).Should().ContainSingle();
        }

        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    // ---- retract

    [Fact]
    public async Task Retract_deletesRecordedOneOffWorkWithAnAuditedRetractASecondRetractIsGoneAndUncompleteIsRefused()
    {
        h.Clock.Set("2026-09-16T16:00:00.000Z");
        var name = Name("Tuinhuis verven");
        var created = (await Post(new { name, durationMinutes = 60, date = "2026-09-16", done = true, requestId = NextKey() })).Body.GetProperty("id").GetString()!;
        var audit = await h.AuditCountAsync();

        var blocked = await Act(created, "uncomplete");
        blocked.Status.Should().Be(HttpStatusCode.Conflict);
        Type(blocked.Body).Should().Be(Code("retract_required"));
        (await h.AuditCountAsync()).Should().Be(audit);

        var response = await Retract(created);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        response.Body.GetProperty("retracted").GetBoolean().Should().BeTrue();
        response.Body.GetProperty("id").GetString().Should().Be(created);
        var entry = (await h.AuditOfAsync("occurrence", created, "delete")).Should().ContainSingle().Subject;
        entry["meta"].AsBsonDocument.ToJson().Should().Be("{ \"reason\" : \"retract\" }");
        entry["before"]["taskId"].IsBsonNull.Should().BeTrue();
        entry["before"]["taskNameSnapshot"].AsString.Should().Be(name);
        (await h.Occurrences.CountDocumentsAsync(new BsonDocument("_id", Oid(created)), cancellationToken: Ct)).Should().Be(0);

        audit = await h.AuditCountAsync();
        var again = await Retract(created);
        again.Status.Should().Be(HttpStatusCode.NotFound);
        (await h.AuditCountAsync()).Should().Be(audit);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Retract_aPlannedOneOffTaskCanBeCompletedAndSkippedLikeAnyOccurrenceAndRetractRefusesIt()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var planned = (await Post(new { name = Name("Planklus"), durationMinutes = 10, date = "2026-09-23" })).Body.GetProperty("id").GetString()!;

        var done = await Act(planned, "complete");
        done.Status.Should().Be(HttpStatusCode.OK, done.Body.ToString());
        (done.Body.GetProperty("taskId").ValueKind, done.Body.GetProperty("status").GetString(), done.Body.GetProperty("recordedDone").GetBoolean(), done.Body.GetProperty("pointsSnapshot").GetInt32()).Should().Be((JsonValueKind.Null, "done", false, 10));
        (await Act(planned, "uncomplete")).Status.Should().Be(HttpStatusCode.OK);
        var refused = await Retract(planned);
        refused.Status.Should().Be(HttpStatusCode.Conflict);
        Type(refused.Body).Should().Be(Code("not_retractable"));
        (await Act(planned, "skip")).Status.Should().Be(HttpStatusCode.OK);
    }

    // ---- no central task record

    [Fact]
    public async Task ANoTaskRecord_aOneOffTaskNeverAppearsInTheTaskListOrTheDueList()
    {
        h.Clock.Set(OccurrenceHarness.Wednesday);
        var name = Name("Unieke eenmalige klus");
        (await Post(new { name, roomId = h.Room, durationMinutes = 45, date = "2026-09-18" })).Status.Should().Be(HttpStatusCode.Created);
        (await Post(new { name, roomId = h.Room, durationMinutes = 45, date = "2026-09-16", done = true })).Status.Should().Be(HttpStatusCode.Created);

        var tasks = await h.SendAsync(HttpMethod.Get, "/api/v2/tasks?limit=200", null, null);
        var due = await h.SendAsync(HttpMethod.Get, "/api/v2/due?limit=200", null, null);

        tasks.Status.Should().Be(HttpStatusCode.OK);
        tasks.Body.ToString().Should().NotContain(name);
        due.Status.Should().Be(HttpStatusCode.OK);
        due.Body.ToString().Should().NotContain(name);
        due.Body.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("taskId").ValueKind == JsonValueKind.String);
        // It does show on the day it sits on, like any occurrence.
        var list = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-18&to=2026-09-18", null, null);
        list.Body.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("taskNameSnapshot").GetString() == name && i.GetProperty("taskId").ValueKind == JsonValueKind.Null);
    }
}
