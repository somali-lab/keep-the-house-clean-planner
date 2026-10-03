using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/tasks.test.ts (create with defaults and audit, validation, references, settings intervals, update and its
/// audit, assign entries, list filters, bulk deactivate and reassign) and the task side of interval-change.test.ts, and adds the v2
/// behaviour: planner policy, paging, no-op and rollback, Node-shaped documents. Not ported (deferred): the room snapshot of upcoming
/// occurrences on a room change (needs occurrences, slice 3.1), the effect of an interval change on occurrences and generation
/// (3.1) and DELETE (arrives with the plans and badges it cascades into). Real HTTP pipeline and real MongoDB replica set.
/// </summary>
public sealed class TaskEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 30, 0, TimeSpan.Zero);

    private static readonly string[] NatTags = ["nat"];
    private static readonly string[] ATags = ["a"];

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory directory = new();
    private readonly UserIdentity planner;
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    private readonly IMongoDatabase database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TaskEndpointTests(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        planner = directory.Add(Role.Planner);
        mongoClient = new MongoClient(mongo.ConnectionString);
        database = mongoClient.GetDatabase(databaseName);
        factory = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<TimeProvider>(new FixedClock(Now));
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- helpers

    private IMongoCollection<BsonDocument> Tasks => database.GetCollection<BsonDocument>("tasks");

    private IMongoCollection<BsonDocument> Rooms => database.GetCollection<BsonDocument>("rooms");

    private IMongoCollection<BsonDocument> AuditLog => database.GetCollection<BsonDocument>("auditLog");

    private async Task<string> SeedRoomAsync(string name, bool active = true)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(Now.UtcDateTime.AddDays(-30));
        await Rooms.InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "sortOrder", 10 }, { "active", active }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }

    private async Task<string> SeedPersonAsync(string name, bool active = true)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(Now.UtcDateTime.AddDays(-30));
        await database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "color", "#336699" }, { "active", active }, { "role", "member" }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? profileId = null, bool web = true, bool noProfile = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (!noProfile)
        {
            request.Headers.Add("X-Profile-Id", profileId ?? planner.Id);
        }

        if (web)
        {
            request.Headers.Add("X-Client", "web");
        }

        if (body is not null)
        {
            request.Content = body is string raw
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }

        return request;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Send(HttpRequestMessage request)
    {
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpResponseMessage Response, JsonElement Body)> Get(string url) => Send(new HttpRequestMessage(HttpMethod.Get, url));

    private Task<(HttpResponseMessage Response, JsonElement Body)> Post(string url, object body) => Send(Request(HttpMethod.Post, url, body));

    private Task<(HttpResponseMessage Response, JsonElement Body)> Patch(string url, object body) => Send(Request(HttpMethod.Patch, url, body));

    private async Task<JsonElement> NewTask(string roomId, string name = "Badkamer schoonmaken", object? extra = null)
    {
        var payload = new Dictionary<string, object?> { ["name"] = name, ["roomId"] = roomId, ["intervalKey"] = "1w", ["durationMinutes"] = 30 };
        foreach (var property in extra?.GetType().GetProperties() ?? [])
        {
            payload[property.Name] = property.GetValue(extra);
        }

        var (response, body) = await Post("/api/v2/tasks", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created, body.ToString());
        return body;
    }

    private async Task<List<BsonDocument>> TaskAudit(string? action = null)
    {
        var filter = new BsonDocument("entity", "task");
        if (action is not null)
        {
            filter.Add("action", action);
        }

        return await AuditLog.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("at").Ascending("_id")).ToListAsync(Ct);
    }

    private static void Same(BsonDocument actual, BsonDocument expected) => actual.ToJson().Should().Be(expected.ToJson());

    private static string[] ErrorFields(JsonElement problem) => [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

    private static string[] Messages(JsonElement problem, string field) =>
        [.. problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(m => m.GetString()!)];

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    // ---- POST /tasks: 'creates a task with defaults and audits every field'

    [Fact]
    public async Task Create_storesATaskWithDefaults_answers201_andAuditsEveryField()
    {
        var room = await SeedRoomAsync("Badkamer");

        var (response, body) = await Post("/api/v2/tasks", new { name = "Badkamer schoonmaken", roomId = room, intervalKey = "1w", durationMinutes = 30, tags = NatTags });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body.GetProperty("id").GetString().Should().MatchRegex("^[0-9a-f]{24}$");
        body.GetProperty("defaultAssigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("lastCompletedAt").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("points").GetInt32().Should().Be(30);
        body.GetProperty("createdAt").GetDateTimeOffset().Should().Be(Now);
        var entries = await TaskAudit();
        entries.Should().ContainSingle();
        entries[0]["action"].AsString.Should().Be("create");
        entries[0]["source"].AsString.Should().Be("ui");
        entries[0]["entityId"].AsObjectId.ToString().Should().Be(body.GetProperty("id").GetString());
        entries[0]["actorId"].AsObjectId.ToString().Should().Be(planner.Id);
        entries[0]["before"].AsBsonDocument.ElementCount.Should().Be(0);
        Same(
            entries[0]["after"].AsBsonDocument,
            new BsonDocument
            {
                { "name", "Badkamer schoonmaken" },
                { "roomId", ObjectId.Parse(room) },
                { "intervalKey", "1w" },
                { "durationMinutes", 30 },
                { "points", 30 },
                { "defaultAssigneeId", BsonNull.Value },
                { "notes", "" },
                { "tags", new BsonArray { "nat" } },
                { "active", true },
                { "lastCompletedAt", BsonNull.Value },
            });
    }

    [Fact]
    public async Task Create_writesTheDocumentTheNodeServerWrites()
    {
        var room = await SeedRoomAsync("Badkamer");
        var person = await SeedPersonAsync("Sven");

        var body = await NewTask(room, extra: new { defaultAssigneeId = person, notes = "n", tags = ATags });

        var stored = await Tasks.Find(new BsonDocument("_id", ObjectId.Parse(body.GetProperty("id").GetString()))).SingleAsync(Ct);
        stored.Names.Should().Equal("_id", "name", "roomId", "intervalKey", "durationMinutes", "points", "defaultAssigneeId", "notes", "tags", "active", "lastCompletedAt", "createdAt", "updatedAt");
        stored["roomId"].IsObjectId.Should().BeTrue();
        stored["defaultAssigneeId"].AsObjectId.ToString().Should().Be(person);
        stored["durationMinutes"].IsInt32.Should().BeTrue();
        stored["createdAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(45, 45)]
    [InlineData(5000, 1000)]
    public async Task Create_withoutPoints_appliesTheServerDefault(int minutes, int expected)
    {
        var room = await SeedRoomAsync("Badkamer");

        var task = await NewTask(room, extra: new { durationMinutes = minutes });

        task.GetProperty("points").GetInt32().Should().Be(expected);
        (await TaskAudit())[0]["after"]["points"].ToInt32().Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(1000)]
    public async Task Create_withExplicitPoints_keepsThem(int points)
    {
        var room = await SeedRoomAsync("Badkamer");

        var task = await NewTask(room, extra: new { points });

        task.GetProperty("points").GetInt32().Should().Be(points);
    }

    [Theory]
    [InlineData("missing duration", """{"name":"X","roomId":"ROOM","intervalKey":"1w"}""", new[] { "durationMinutes" })]
    [InlineData("zero duration", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":0}""", new[] { "durationMinutes" })]
    [InlineData("fractional duration", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":12.5}""", new[] { "durationMinutes" })]
    [InlineData("missing name, room and interval", """{"durationMinutes":10}""", new[] { "name", "roomId", "intervalKey" })]
    [InlineData("blank name", """{"name":"  ","roomId":"ROOM","intervalKey":"1w","durationMinutes":10}""", new[] { "name" })]
    [InlineData("points above the maximum", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"points":1001}""", new[] { "points" })]
    [InlineData("negative points", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"points":-1}""", new[] { "points" })]
    [InlineData("fractional points", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"points":2.5}""", new[] { "points" })]
    [InlineData("a wrong type", """{"name":5,"roomId":"ROOM","intervalKey":"1w","durationMinutes":"10"}""", new[] { "name", "durationMinutes" })]
    [InlineData("explicit null name", """{"name":null,"roomId":"ROOM","intervalKey":"1w","durationMinutes":10}""", new[] { "name" })]
    [InlineData("a bad room id", """{"name":"X","roomId":"nope","intervalKey":"1w","durationMinutes":10}""", new[] { "roomId" })]
    [InlineData("a bad assignee id", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"defaultAssigneeId":"nope"}""", new[] { "defaultAssigneeId" })]
    [InlineData("a tag that is not a string, and a blank tag", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"tags":[1," "]}""", new[] { "tags.0", "tags.1" })]
    [InlineData("tags that are not an array", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"tags":"nat"}""", new[] { "tags" })]
    [InlineData("null notes", """{"name":"X","roomId":"ROOM","intervalKey":"1w","durationMinutes":10,"notes":null}""", new[] { "notes" })]
    public async Task Create_rejectsInvalidBodiesWith400_namingTheFields(string label, string json, string[] fields)
    {
        var room = await SeedRoomAsync("Badkamer");

        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/tasks", json.Replace("ROOM", room, StringComparison.Ordinal)));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().BeEquivalentTo(fields, label);
        (await Tasks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
        (await TaskAudit()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Create_rejectsAMalformedOrEmptyBody_with400OnBody(string json)
    {
        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/tasks", json));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().Equal("body");
    }

    [Fact]
    public async Task Create_rejectsUnknownRoomIntervalAndAssignee_naming_allThree_andWritesNothing()
    {
        var (response, body) = await Post("/api/v2/tasks", new
        {
            name = "X",
            roomId = "0123456789abcdef01234567",
            intervalKey = "fortnightly",
            durationMinutes = 10,
            defaultAssigneeId = "0123456789abcdef01234568",
        });

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        Messages(body, "roomId").Should().Equal("unknown_room");
        Messages(body, "intervalKey").Should().Equal("unknown_interval");
        Messages(body, "defaultAssigneeId").Should().Equal("unknown_user");
        (await Tasks.CountDocumentsAsync(new BsonDocument("name", "X"), cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Create_inAnInactiveRoom_orWithAnInactiveAssignee_isRefused()
    {
        var room = await SeedRoomAsync("Zolder", active: false);
        var gone = await SeedPersonAsync("Oud", active: false);

        var (response, body) = await Post("/api/v2/tasks", new { name = "X", roomId = room, intervalKey = "1w", durationMinutes = 10, defaultAssigneeId = gone });

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        Messages(body, "roomId").Should().Equal("inactive_room");
        Messages(body, "defaultAssigneeId").Should().Equal("inactive_user");
    }

    [Fact]
    public async Task Create_acceptsAnIntervalAddedViaSettings_withoutCodeChanges()
    {
        var room = await SeedRoomAsync("Slaapkamer");
        var admin = directory.Add(Role.Admin);
        var intervals = HouseholdLimits.Current.Defaults.Intervals
            .Select(i => new { key = i.Key, label = i.Label, perCycle = i.PerCycle, periodDays = i.PeriodDays })
            .Append(new { key = "year", label = "1x per jaar", perCycle = (int?)null, periodDays = 365 })
            .ToList();
        var settings = await Send(Request(HttpMethod.Patch, "/api/v2/settings", new { intervals }, admin.Id));
        settings.Response.StatusCode.Should().Be(HttpStatusCode.OK, settings.Body.ToString());

        var task = await NewTask(room, "Matras keren", new { intervalKey = "year", durationMinutes = 15 });

        task.GetProperty("intervalKey").GetString().Should().Be("year");
    }

    [Fact]
    public async Task AnIntervalThatATaskUses_cannotBeRemovedFromSettings_untilTheTaskMoves()
    {
        var room = await SeedRoomAsync("Slaapkamer");
        var admin = directory.Add(Role.Admin);
        var task = await NewTask(room, extra: new { intervalKey = "quarter" });
        var kept = HouseholdLimits.Current.Defaults.Intervals.Where(i => i.Key != "quarter")
            .Select(i => new { key = i.Key, label = i.Label, perCycle = i.PerCycle, periodDays = i.PeriodDays }).ToList();

        var blocked = await Send(Request(HttpMethod.Patch, "/api/v2/settings", new { intervals = kept }, admin.Id));
        ShouldBeProblem(blocked.Response, blocked.Body, HttpStatusCode.Conflict, "interval_in_use");

        (await Patch($"/api/v2/tasks/{task.GetProperty("id").GetString()}", new { intervalKey = "1w" })).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var allowed = await Send(Request(HttpMethod.Patch, "/api/v2/settings", new { intervals = kept }, admin.Id));
        allowed.Response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ARoomThatATaskUses_cannotBeDeleted_whileTheTaskIsInactiveToo()
    {
        var room = await SeedRoomAsync("Slaapkamer");
        var admin = directory.Add(Role.Admin);
        var task = await NewTask(room);
        await Patch($"/api/v2/tasks/{task.GetProperty("id").GetString()}", new { active = false });

        var (response, body) = await Send(Request(HttpMethod.Delete, $"/api/v2/rooms/{room}", profileId: admin.Id));

        ShouldBeProblem(response, body, HttpStatusCode.Conflict, "room_in_use");
        body.GetProperty("taskCount").GetInt32().Should().Be(1);
    }

    // ---- PATCH /tasks/:id

    [Fact]
    public async Task Patch_auditsOldAndNewValuesOfNameIntervalDurationAndRoom()
    {
        var badkamer = await SeedRoomAsync("Badkamer");
        var keuken = await SeedRoomAsync("Keuken");
        var task = await NewTask(badkamer);
        var id = task.GetProperty("id").GetString()!;

        var (response, body) = await Patch($"/api/v2/tasks/{id}", new { name = "Badkamer grondig", intervalKey = "2wk", durationMinutes = 45, roomId = keuken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("name").GetString().Should().Be("Badkamer grondig");
        body.GetProperty("roomId").GetString().Should().Be(keuken);
        var entries = await TaskAudit("update");
        entries.Should().ContainSingle();
        Same(entries[0]["before"].AsBsonDocument, new BsonDocument { { "name", "Badkamer schoonmaken" }, { "roomId", ObjectId.Parse(badkamer) }, { "intervalKey", "1w" }, { "durationMinutes", 30 } });
        Same(entries[0]["after"].AsBsonDocument, new BsonDocument { { "name", "Badkamer grondig" }, { "roomId", ObjectId.Parse(keuken) }, { "intervalKey", "2wk" }, { "durationMinutes", 45 } });
        var stored = await Tasks.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);
        stored["roomId"].AsObjectId.ToString().Should().Be(keuken);
        stored["points"].ToInt32().Should().Be(30, "points do not follow the duration");
    }

    [Fact]
    public async Task Patch_ofTheDefaultAssignee_isLoggedAsAssign_andNotAsUpdate()
    {
        var room = await SeedRoomAsync("Badkamer");
        var person = await SeedPersonAsync("Sven");
        var id = (await NewTask(room)).GetProperty("id").GetString()!;

        var (response, body) = await Patch($"/api/v2/tasks/{id}", new { defaultAssigneeId = person });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("defaultAssigneeId").GetString().Should().Be(person);
        var assign = await TaskAudit("assign");
        assign.Should().ContainSingle();
        Same(assign[0]["before"].AsBsonDocument, new BsonDocument("defaultAssigneeId", BsonNull.Value));
        Same(assign[0]["after"].AsBsonDocument, new BsonDocument("defaultAssigneeId", ObjectId.Parse(person)));
        (await TaskAudit("update")).Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_ofDurationAndAssignee_writesSeparateUpdateAndAssignEntries()
    {
        var room = await SeedRoomAsync("Badkamer");
        var person = await SeedPersonAsync("Sven");
        var id = (await NewTask(room)).GetProperty("id").GetString()!;

        await Patch($"/api/v2/tasks/{id}", new { durationMinutes = 20, defaultAssigneeId = person });

        var update = await TaskAudit("update");
        update.Should().ContainSingle();
        Same(update[0]["after"].AsBsonDocument, new BsonDocument("durationMinutes", 20));
        (await TaskAudit("assign")).Should().ContainSingle();
    }

    [Fact]
    public async Task Patch_withNullAssignee_clearsIt()
    {
        var room = await SeedRoomAsync("Badkamer");
        var person = await SeedPersonAsync("Sven");
        var id = (await NewTask(room, extra: new { defaultAssigneeId = person })).GetProperty("id").GetString()!;

        var (response, body) = await Patch($"/api/v2/tasks/{id}", """{"defaultAssigneeId":null}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("defaultAssigneeId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Patch_thatChangesNothing_writesNothing_andAuditsNothing()
    {
        var room = await SeedRoomAsync("Badkamer");
        var task = await NewTask(room);
        var id = task.GetProperty("id").GetString()!;
        var auditBefore = await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        var storedBefore = await Tasks.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);

        var (response, body) = await Patch($"/api/v2/tasks/{id}", new { name = "Badkamer schoonmaken", durationMinutes = 30, active = true, tags = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("name").GetString().Should().Be("Badkamer schoonmaken");
        (await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(auditBefore);
        (await Tasks.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct)).ToJson().Should().Be(storedBefore.ToJson());
    }

    [Fact]
    public async Task Patch_validatesDurationAndReferences_andAnUnknownTaskIs404()
    {
        var room = await SeedRoomAsync("Badkamer");
        var id = (await NewTask(room)).GetProperty("id").GetString()!;

        var duration = await Patch($"/api/v2/tasks/{id}", new { durationMinutes = 0 });
        var interval = await Patch($"/api/v2/tasks/{id}", new { intervalKey = "nope" });
        var unknown = await Patch("/api/v2/tasks/0123456789abcdef01234567", new { name = "X" });
        var badId = await Patch("/api/v2/tasks/nope", new { name = "X" });
        var inactiveRoom = await Patch($"/api/v2/tasks/{id}", new { roomId = await SeedRoomAsync("Zolder", active: false) });

        ShouldBeProblem(duration.Response, duration.Body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(duration.Body).Should().Equal("durationMinutes");
        ShouldBeProblem(interval.Response, interval.Body, HttpStatusCode.BadRequest, "validation_error");
        Messages(interval.Body, "intervalKey").Should().Equal("unknown_interval");
        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(badId.Response, badId.Body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(badId.Body).Should().Equal("id");
        Messages(inactiveRoom.Body, "roomId").Should().Equal("inactive_room");
    }

    [Fact]
    public async Task Patch_deactivates_andTheTaskLeavesTheActiveList()
    {
        var room = await SeedRoomAsync("Badkamer");
        var id = (await NewTask(room)).GetProperty("id").GetString()!;

        var (response, body) = await Patch($"/api/v2/tasks/{id}", new { active = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("active").GetBoolean().Should().BeFalse();
        (await Get($"/api/v2/tasks?roomId={room}&active=true")).Body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Patch_writesOnlyTheTaskAndItsAuditEntry_neverOtherCollections()
    {
        // The task side of interval-change.test.ts: a change of interval and duration touches the task only.
        var room = await SeedRoomAsync("Badkamer");
        var id = (await NewTask(room)).GetProperty("id").GetString()!;
        var before = await CollectionCounts();

        var (response, _) = await Patch($"/api/v2/tasks/{id}", new { durationMinutes = 45, intervalKey = "2wk", name = "Badkamer grondig" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await CollectionCounts();
        after.Where(c => before.GetValueOrDefault(c.Key) != c.Value).Select(c => c.Key).Should().BeEquivalentTo(["auditLog"]);
    }

    private async Task<Dictionary<string, long>> CollectionCounts()
    {
        var counts = new Dictionary<string, long>();
        foreach (var name in await (await database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
        {
            counts[name] = await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        }

        return counts;
    }

    // ---- GET /tasks

    [Fact]
    public async Task List_filtersByRoomAndActive_orderedByName_withoutAProfile()
    {
        var room = await SeedRoomAsync("Berging");
        var a = await NewTask(room, "Berging opruimen");
        var b = await NewTask(room, "Berging vegen");
        await NewTask(await SeedRoomAsync("Keuken"), "Afwas");
        await Patch($"/api/v2/tasks/{b.GetProperty("id").GetString()}", new { active = false });

        var inRoom = await Get($"/api/v2/tasks?roomId={room}");
        var activeOnly = await Get($"/api/v2/tasks?roomId={room}&active=true");
        var inactive = await Get("/api/v2/tasks?active=false");
        var all = await Get("/api/v2/tasks");

        Names(inRoom.Body).Should().Equal("Berging opruimen", "Berging vegen");
        activeOnly.Body.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should().Equal(a.GetProperty("id").GetString());
        Names(inactive.Body).Should().Equal("Berging vegen");
        Names(all.Body).Should().Equal("Afwas", "Berging opruimen", "Berging vegen");
        all.Body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static string[] Names(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];

    [Theory]
    [InlineData("active=yes", "active")]
    [InlineData("limit=abc", "limit")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("roomId=nope", "roomId")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task List_rejectsBadQueryValues_with400NamingTheField(string query, string field)
    {
        var (response, body) = await Get($"/api/v2/tasks?{query}");

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().Contain(field);
    }

    [Fact]
    public async Task List_pagesWithLimitAndCursor_withoutGapsOrRepeats()
    {
        var room = await SeedRoomAsync("Keuken");
        foreach (var name in new[] { "E", "C", "A", "D", "B" })
        {
            await NewTask(room, name);
        }

        var first = (await Get("/api/v2/tasks?limit=2")).Body;
        var second = (await Get($"/api/v2/tasks?limit=2&cursor={Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!)}")).Body;
        var third = (await Get($"/api/v2/tasks?limit=2&cursor={Uri.EscapeDataString(second.GetProperty("nextCursor").GetString()!)}")).Body;

        Names(first).Should().Equal("A", "B");
        Names(second).Should().Equal("C", "D");
        Names(third).Should().Equal("E");
        third.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_readsDocumentsTheWayTheNodeServerStoresThem()
    {
        // A task from before points existed, numbers that landed as doubles, no notes and tags, no timestamps beyond createdAt.
        var room = ObjectId.Parse(await SeedRoomAsync("Badkamer"));
        await Tasks.InsertOneAsync(
            new BsonDocument
            {
                { "name", "Oud" }, { "roomId", room }, { "intervalKey", "1w" }, { "durationMinutes", 25.0 }, { "defaultAssigneeId", BsonNull.Value },
                { "active", true }, { "lastCompletedAt", new BsonDateTime(Now.UtcDateTime.AddDays(-1)) }, { "createdAt", new BsonDateTime(Now.UtcDateTime) },
                { "extra", "ignored" },
            },
            cancellationToken: Ct);

        var task = (await Get("/api/v2/tasks")).Body.GetProperty("items")[0];

        task.GetProperty("durationMinutes").GetInt32().Should().Be(25);
        task.GetProperty("points").GetInt32().Should().Be(25, "a task without points reads as the default for its duration");
        task.GetProperty("notes").GetString().Should().BeEmpty();
        task.GetProperty("tags").GetArrayLength().Should().Be(0);
        task.GetProperty("lastCompletedAt").GetDateTimeOffset().Should().Be(Now.AddDays(-1));
        task.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(Now);
    }

    // ---- POST /rooms/:id/tasks/bulk

    private async Task<(string Room, List<string> Ids)> RoomWithTasks(string name, int count)
    {
        var room = await SeedRoomAsync(name);
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            ids.Add((await NewTask(room, $"{name} {i}")).GetProperty("id").GetString()!);
        }

        return (room, ids);
    }

    [Fact]
    public async Task Bulk_deactivate_deactivatesAllTasksOfTheRoom_withOneAuditEntryPerTask()
    {
        var (room, ids) = await RoomWithTasks("Schuur", 3);
        var (otherRoom, _) = await RoomWithTasks("Tuin", 1);

        var (response, body) = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "deactivate" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("updated").GetInt32().Should().Be(3);
        var entries = await TaskAudit("update");
        entries.Should().HaveCount(3);
        entries.Select(e => e["entityId"].AsObjectId.ToString()).Should().BeEquivalentTo(ids);
        entries.Should().OnlyContain(e => e["before"]["active"].AsBoolean && !e["after"]["active"].AsBoolean);
        (await Get($"/api/v2/tasks?roomId={room}&active=true")).Body.GetProperty("items").GetArrayLength().Should().Be(0);
        (await Get($"/api/v2/tasks?roomId={otherRoom}&active=true")).Body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Bulk_reassign_givesAllActiveTasksTheAssignee_loggedAsAssignPerTask()
    {
        var (room, _) = await RoomWithTasks("Tuin", 2);
        var person = await SeedPersonAsync("Sven");

        var (response, body) = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "reassign", defaultAssigneeId = person });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("updated").GetInt32().Should().Be(2);
        var entries = await TaskAudit("assign");
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e["after"]["defaultAssigneeId"].AsObjectId.ToString() == person);

        var again = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "reassign", defaultAssigneeId = person });
        again.Body.GetProperty("updated").GetInt32().Should().Be(0);
        (await TaskAudit("assign")).Should().HaveCount(2, "a bulk change that changes nothing audits nothing");
    }

    [Fact]
    public async Task Bulk_reassign_toNull_clearsTheAssignee()
    {
        var (room, _) = await RoomWithTasks("Tuin", 1);
        var person = await SeedPersonAsync("Sven");
        await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "reassign", defaultAssigneeId = person });

        var (_, body) = await Send(Request(HttpMethod.Post, $"/api/v2/rooms/{room}/tasks/bulk", """{"op":"reassign","defaultAssigneeId":null}"""));

        body.GetProperty("updated").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Bulk_validatesOpAssigneeAndRoom()
    {
        var (room, _) = await RoomWithTasks("Garage", 1);

        var op = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "explode" });
        var noOp = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { });
        var noAssignee = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "reassign" });
        var unknownAssignee = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "reassign", defaultAssigneeId = "0123456789abcdef01234567" });
        var inactive = await Post($"/api/v2/rooms/{room}/tasks/bulk", new { op = "reassign", defaultAssigneeId = await SeedPersonAsync("Oud", active: false) });
        var unknownRoom = await Post("/api/v2/rooms/0123456789abcdef01234567/tasks/bulk", new { op = "deactivate" });
        var badRoom = await Post("/api/v2/rooms/nope/tasks/bulk", new { op = "deactivate" });

        ErrorFields(op.Body).Should().Equal("op");
        ErrorFields(noOp.Body).Should().Equal("op");
        ErrorFields(noAssignee.Body).Should().Equal("defaultAssigneeId");
        Messages(unknownAssignee.Body, "defaultAssigneeId").Should().Equal("unknown_user");
        Messages(inactive.Body, "defaultAssigneeId").Should().Equal("inactive_user");
        ShouldBeProblem(unknownRoom.Response, unknownRoom.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(badRoom.Response, badRoom.Body, HttpStatusCode.BadRequest, "validation_error");
        (await TaskAudit("update")).Should().BeEmpty();
    }

    // ---- roles and profile (Node: requirePlanner on POST, PATCH and the bulk route; reads are open)

    [Theory]
    [InlineData(Role.Member, "POST")]
    [InlineData(Role.Member, "PATCH")]
    [InlineData(Role.Member, "BULK")]
    public async Task Writes_needAPlanner_aMemberGets403PermissionDenied_andNothingChanges(Role role, string kind)
    {
        var room = await SeedRoomAsync("Badkamer");
        var id = (await NewTask(room)).GetProperty("id").GetString()!;
        var profile = directory.Add(role);
        var auditBefore = await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var request = kind switch
        {
            "POST" => Request(HttpMethod.Post, "/api/v2/tasks", new { name = "N", roomId = room, intervalKey = "1w", durationMinutes = 5 }, profile.Id),
            "PATCH" => Request(HttpMethod.Patch, $"/api/v2/tasks/{id}", new { name = "N" }, profile.Id),
            _ => Request(HttpMethod.Post, $"/api/v2/rooms/{room}/tasks/bulk", new { op = "deactivate" }, profile.Id),
        };
        var (response, body) = await Send(request);

        ShouldBeProblem(response, body, HttpStatusCode.Forbidden, "permission_denied");
        (await Tasks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
        (await Tasks.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["active"].AsBoolean.Should().BeTrue();
        (await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(auditBefore);
    }

    [Fact]
    public async Task Writes_areAllowedForAnAdministrator_too()
    {
        var room = await SeedRoomAsync("Badkamer");
        var admin = directory.Add(Role.Admin);

        var (response, _) = await Send(Request(HttpMethod.Post, "/api/v2/tasks", new { name = "N", roomId = room, intervalKey = "1w", durationMinutes = 5 }, admin.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("BULK")]
    public async Task Writes_withoutAProfile_are400ProfileRequired(string kind)
    {
        var url = kind switch
        {
            "POST" => "/api/v2/tasks",
            "PATCH" => "/api/v2/tasks/0123456789abcdef01234567",
            _ => "/api/v2/rooms/0123456789abcdef01234567/tasks/bulk",
        };

        var (response, body) = await Send(Request(kind == "PATCH" ? HttpMethod.Patch : HttpMethod.Post, url, new { name = "N" }, noProfile: true));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public void EveryTaskWrite_requiresThePlannerPolicy_andTheReadRequiresNone()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } p && (p.StartsWith("/api/v2/tasks", StringComparison.Ordinal) || p.EndsWith("/tasks/bulk", StringComparison.Ordinal)))
            .ToList();

        endpoints.Should().HaveCount(4);
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            if (methods.Contains("GET"))
            {
                policies.Should().BeEmpty();
            }
            else
            {
                policies.Should().BeEquivalentTo([AuthorizationPolicies.PlannerPolicy], $"{string.Join(",", methods)} {endpoint.RoutePattern.RawText} needs a planner");
            }
        }
    }

    // ---- transactions: the task and its audit entry commit or fail together

    [Fact]
    public async Task Create_whenTheAuditEntryCannotBeWritten_answers500_andLeavesNoTaskBehind()
    {
        var room = await SeedRoomAsync("Badkamer");
        using var failing = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<ForRecordingAudit>(new FailingAudit());
        using var failingClient = failing.CreateClient();
        using var request = Request(HttpMethod.Post, "/api/v2/tasks", new { name = "N", roomId = room, intervalKey = "1w", durationMinutes = 5 });

        var response = await failingClient.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("audit down");
        (await Tasks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Bulk_whenAnAuditEntryCannotBeWritten_answers500_andChangesNoTask()
    {
        var (room, _) = await RoomWithTasks("Schuur", 2);
        using var failing = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<ForRecordingAudit>(new FailingAudit());
        using var failingClient = failing.CreateClient();
        using var request = Request(HttpMethod.Post, $"/api/v2/rooms/{room}/tasks/bulk", new { op = "deactivate" });

        var response = await failingClient.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await Tasks.CountDocumentsAsync(new BsonDocument("active", true), cancellationToken: Ct)).Should().Be(2);
    }

    private sealed class FailingAudit : ForRecordingAudit
    {
        public Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult<OneOf<Success, PortError>>(new PortError("audit down"));
    }
}
