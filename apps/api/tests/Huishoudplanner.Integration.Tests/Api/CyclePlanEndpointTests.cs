using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/cyclePlans.test.ts scenario by scenario (read and create, copy with audit, rename and themes, delete with
/// <c>default_plan</c>, slot saves with warnings, the audit of added/removed/changed slots, 422 <c>invalid_plan</c>, the identical
/// save, body validation) and adds the diff, the two validation endpoints (slice 2.3), roles, paging and the Node-shaped documents.
/// Deferred to slice 2.4: the activation preview and the activation scenarios of the Node file (<c>activation-preview</c>, <c>activate</c>,
/// the preview token and the occurrence replacement); deferred to 3.1: the occurrence synchronisation when slots of the active plan are
/// saved. Real HTTP pipeline and real MongoDB replica set.
/// </summary>
public sealed class CyclePlanEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 30, 0, TimeSpan.Zero);

    private static readonly string[] Themes = ["Keuken", "", "Ramen", ""];
    private static readonly string[] TooFewThemes = ["a", "b"];
    private static readonly string[] NoThemes = ["", "", "", ""];

    private readonly FakeUserDirectory directory = new();
    private readonly UserIdentity planner;
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    private readonly IMongoDatabase database;
    private readonly MongoContainerFixture mongo;

    private string p1 = string.Empty;
    private string p2 = string.Empty;
    private string weekly = string.Empty; // 1w, 30 min
    private string twice = string.Empty; // 2w, 10 min
    private string standaard = string.Empty;
    private bool arranged;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CyclePlanEndpointTests(MongoContainerFixture mongo)
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

    private IMongoCollection<BsonDocument> Plans => database.GetCollection<BsonDocument>("cyclePlans");

    private IMongoCollection<BsonDocument> AuditLog => database.GetCollection<BsonDocument>("auditLog");

    /// <summary>The Node test's beforeAll: two people (the second not available on Tuesday), a weekly and a two-weekly task.</summary>
    private async Task ArrangeAsync()
    {
        if (arranged)
        {
            return;
        }

        arranged = true;
        p1 = await SeedPersonAsync("Persoon A", []);
        p2 = await SeedPersonAsync("Persoon B", [2]);
        var room = await SeedRoomAsync("Badkamer");
        weekly = await NewTaskAsync("Badkamer schoonmaken", room, "1w", 30);
        twice = await NewTaskAsync("Wastafel poetsen", room, "2w", 10);
        standaard = (await Get("/api/v2/cycle-plans/active")).Body.GetProperty("id").GetString()!;
    }

    private async Task<string> SeedRoomAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(Now.UtcDateTime.AddDays(-30));
        await database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }

    private async Task<string> SeedPersonAsync(string name, int[] unavailable, bool active = true)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(Now.UtcDateTime.AddDays(-30));
        await database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "name", name }, { "color", "#336699" }, { "active", active }, { "role", "member" },
                { "unavailableWeekdays", new BsonArray(unavailable) },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 60 }, { "weekend", 120 } } },
                { "maxDailyMinutes", new BsonDocument { { "weekday", 120 }, { "weekend", 240 } } },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
        return id.ToString();
    }

    private async Task<string> NewTaskAsync(string name, string roomId, string intervalKey, int minutes, bool active = true)
    {
        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/tasks", new { name, roomId, intervalKey, durationMinutes = minutes }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("id").GetString()!;
        if (!active)
        {
            await Send(Request(HttpMethod.Patch, $"/api/v2/tasks/{id}", new { active = false }));
        }

        return id;
    }

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? profileId = null, bool noProfile = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (!noProfile)
        {
            request.Headers.Add("X-Profile-Id", profileId ?? planner.Id);
        }

        request.Headers.Add("X-Client", "web");
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

    private async Task<string> NewPlanAsync(string name, string? copyFromId = null)
    {
        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans", copyFromId is null ? new { name } : new { name, copyFromId }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    private static object SlotBody(string task, int week, int weekday, string? assignee) => new { taskId = task, weekIndex = week, weekday, assigneeId = assignee };

    private Task<(HttpResponseMessage Response, JsonElement Body)> PutSlots(string planId, params object[] slots) =>
        Send(Request(HttpMethod.Put, $"/api/v2/cycle-plans/{planId}/slots", new { slots }));

    private async Task<List<BsonDocument>> PlanAudit(string? action = null)
    {
        var filter = new BsonDocument("entity", "cyclePlan");
        if (action is not null)
        {
            filter.Add("action", action);
        }

        return await AuditLog.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("at").Ascending("_id")).ToListAsync(Ct);
    }

    private async Task<long> PlanAuditCount(string action) => (await PlanAudit(action)).Count;

    private static string[] ErrorFields(JsonElement problem) => [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

    private static string[] Codes(JsonElement list) => [.. list.EnumerateArray().Select(e => e.GetProperty("code").GetString()!)];

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    // ---- read and create (describe 'cycle plans: read and create')

    [Fact]
    public async Task StartsWithAnEmptyActivePlanStandaard_audited_asTheSystem()
    {
        await ArrangeAsync();

        var (response, active) = await Get("/api/v2/cycle-plans/active");
        var (_, list) = await Get("/api/v2/cycle-plans");
        var (_, one) = await Get($"/api/v2/cycle-plans/{standaard}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        active.GetProperty("name").GetString().Should().Be("Standaard");
        active.GetProperty("active").GetBoolean().Should().BeTrue();
        active.GetProperty("slots").GetArrayLength().Should().Be(0);
        active.GetProperty("weekThemes").EnumerateArray().Select(e => e.GetString()).Should().Equal("", "", "", "");
        active.GetProperty("source").GetString().Should().Be("manual");
        active.GetProperty("draft").GetBoolean().Should().BeFalse();
        active.GetProperty("proposalId").ValueKind.Should().Be(JsonValueKind.Null);
        active.GetProperty("rationale").ValueKind.Should().Be(JsonValueKind.Null);
        active.GetProperty("createdAt").GetDateTimeOffset().Should().Be(Now);
        list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Standaard");
        list.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        one.GetProperty("id").GetString().Should().Be(standaard);
        var entries = await PlanAudit("create");
        entries.Should().ContainSingle();
        entries[0]["source"].AsString.Should().Be("system");
        entries[0]["actorId"].AsObjectId.ToString().Should().Be("000000000000000000000000");
    }

    [Fact]
    public async Task StartingAgainOnTheSameDatabase_doesNotSeedASecondPlan()
    {
        await ArrangeAsync();
        using var second = ApiFactory.ForMongo(mongo, databaseName).WithPort<ForFindingUsers>(directory);

        using var secondClient = second.CreateClient();

        (await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
        (await PlanAudit("create")).Should().ContainSingle();
    }

    [Fact]
    public async Task ReturnsNotFoundForAnUnknownPlan_andAValidationErrorForAMalformedId()
    {
        await ArrangeAsync();

        var unknown = await Get("/api/v2/cycle-plans/0123456789abcdef01234567");
        var malformed = await Get("/api/v2/cycle-plans/nope");

        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(malformed.Response, malformed.Body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(malformed.Body).Should().Equal("id");
    }

    [Fact]
    public async Task CreatesAnInactiveCopyWithSlotsAndThemes_auditedWithCopiedFrom()
    {
        await ArrangeAsync();
        (await PutSlots(standaard, SlotBody(weekly, 0, 1, p1))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        await Send(Request(HttpMethod.Patch, $"/api/v2/cycle-plans/{standaard}", new { weekThemes = Themes }));

        var (response, copy) = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "Experiment", copyFromId = standaard }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        copy.GetProperty("name").GetString().Should().Be("Experiment");
        copy.GetProperty("active").GetBoolean().Should().BeFalse();
        copy.GetProperty("draft").GetBoolean().Should().BeFalse();
        copy.GetProperty("weekThemes").EnumerateArray().Select(e => e.GetString()).Should().Equal("Keuken", "", "Ramen", "");
        var slot = copy.GetProperty("slots").EnumerateArray().Should().ContainSingle().Subject;
        slot.GetProperty("taskId").GetString().Should().Be(weekly);
        slot.GetProperty("weekIndex").GetInt32().Should().Be(0);
        slot.GetProperty("weekday").GetInt32().Should().Be(1);
        slot.GetProperty("assigneeId").GetString().Should().Be(p1);
        slot.GetProperty("sortOrder").GetInt32().Should().Be(0);
        var entries = (await PlanAudit("create")).Where(e => e["entityId"].AsObjectId.ToString() == copy.GetProperty("id").GetString()).ToList();
        entries.Should().ContainSingle();
        entries[0]["source"].AsString.Should().Be("ui");
        entries[0]["actorId"].AsObjectId.ToString().Should().Be(planner.Id);
        entries[0]["meta"].AsBsonDocument["copiedFrom"].AsObjectId.ToString().Should().Be(standaard);
    }

    [Fact]
    public async Task ReturnsNotFoundWhenCopyingAnUnknownPlan_and400WithoutAName()
    {
        await ArrangeAsync();
        var createsBefore = await PlanAuditCount("create");

        var unknown = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "X", copyFromId = "0123456789abcdef01234567" }));
        var noName = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans", new { }));
        var badSource = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "X", copyFromId = "nope" }));

        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(noName.Response, noName.Body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(noName.Body).Should().Equal("name");
        ErrorFields(badSource.Body).Should().Equal("copyFromId");
        (await PlanAuditCount("create")).Should().Be(createsBefore);
        (await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData("", "body")]
    [InlineData("{ not json", "body")]
    [InlineData("[]", "body")]
    [InlineData("""{"name":5}""", "name")]
    [InlineData("""{"name":null}""", "name")]
    [InlineData("""{"name":"  "}""", "name")]
    [InlineData("""{"name":"X","copyFromId":null}""", "copyFromId")]
    public async Task Create_rejectsBadBodiesWith400_namingTheField(string json, string field)
    {
        await ArrangeAsync();

        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans", json));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().Equal(field);
    }

    [Fact]
    public async Task RenamesAndSetsWeekThemes_withAnAuditedDiff()
    {
        await ArrangeAsync();
        var id = await NewPlanAsync("Leeg");

        var (response, plan) = await Send(Request(HttpMethod.Patch, $"/api/v2/cycle-plans/{id}", new { name = "Zomer", weekThemes = Themes }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        plan.GetProperty("name").GetString().Should().Be("Zomer");
        var entries = await PlanAudit("update");
        entries.Should().ContainSingle();
        entries[0]["before"].ToJson().Should().Be(new BsonDocument { { "name", "Leeg" }, { "weekThemes", new BsonArray { "", "", "", "" } } }.ToJson());
        entries[0]["after"].ToJson().Should().Be(new BsonDocument { { "name", "Zomer" }, { "weekThemes", new BsonArray { "Keuken", "", "Ramen", "" } } }.ToJson());
        var badThemes = await Send(Request(HttpMethod.Patch, $"/api/v2/cycle-plans/{id}", new { weekThemes = TooFewThemes }));
        ShouldBeProblem(badThemes.Response, badThemes.Body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(badThemes.Body).Should().Equal("weekThemes");
        (await PlanAudit("update")).Should().ContainSingle();
    }

    [Fact]
    public async Task APatchThatChangesNothing_writesAndAuditsNothing()
    {
        await ArrangeAsync();
        var id = await NewPlanAsync("Leeg");
        var before = await Plans.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);

        var same = await Send(Request(HttpMethod.Patch, $"/api/v2/cycle-plans/{id}", new { name = " Leeg ", weekThemes = NoThemes }));
        var empty = await Send(Request(HttpMethod.Patch, $"/api/v2/cycle-plans/{id}", new { }));

        same.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        empty.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PlanAudit("update")).Should().BeEmpty();
        (await Plans.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct)).ToJson().Should().Be(before.ToJson());
    }

    [Fact]
    public async Task Patch_ofAnUnknownPlan_is404()
    {
        await ArrangeAsync();

        var (response, body) = await Send(Request(HttpMethod.Patch, "/api/v2/cycle-plans/0123456789abcdef01234567", new { name = "X" }));

        ShouldBeProblem(response, body, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task DeletesANonActiveCopy_butNeverTheOriginalDefaultPlan()
    {
        await ArrangeAsync();
        var blocked = await Send(Request(HttpMethod.Delete, $"/api/v2/cycle-plans/{standaard}"));
        var id = await NewPlanAsync("Tijdelijk");

        var (response, body) = await Send(Request(HttpMethod.Delete, $"/api/v2/cycle-plans/{id}"));

        ShouldBeProblem(blocked.Response, blocked.Body, HttpStatusCode.Conflict, "default_plan");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        var entries = await PlanAudit("delete");
        entries.Should().ContainSingle();
        entries[0]["before"].AsBsonDocument["name"].AsString.Should().Be("Tijdelijk");
        entries[0]["before"].AsBsonDocument["active"].AsBoolean.Should().BeFalse();
        entries[0]["after"].AsBsonDocument.ElementCount.Should().Be(0);
        (await Get($"/api/v2/cycle-plans/{id}")).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteOfTheActivePlan_is409ActivePlan_andTheOldestPlanIsStillTheDefaultWhenInactive()
    {
        await ArrangeAsync();
        var other = await NewPlanAsync("Actief");
        // Activation arrives with slice 2.4; until then the state is set directly.
        await Plans.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("active", false)), cancellationToken: Ct);
        await Plans.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(other)), new BsonDocument("$set", new BsonDocument("active", true)), cancellationToken: Ct);

        var active = await Send(Request(HttpMethod.Delete, $"/api/v2/cycle-plans/{other}"));
        var oldest = await Send(Request(HttpMethod.Delete, $"/api/v2/cycle-plans/{standaard}"));

        ShouldBeProblem(active.Response, active.Body, HttpStatusCode.Conflict, "active_plan");
        ShouldBeProblem(oldest.Response, oldest.Body, HttpStatusCode.Conflict, "default_plan");
        (await PlanAudit("delete")).Should().BeEmpty();
        (await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Delete_ofAnUnknownPlan_is404()
    {
        await ArrangeAsync();

        var (response, body) = await Send(Request(HttpMethod.Delete, "/api/v2/cycle-plans/0123456789abcdef01234567"));

        ShouldBeProblem(response, body, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ListsPlansOldestFirst_inBoundedPages()
    {
        await ArrangeAsync();
        await NewPlanAsync("Tweede");
        await NewPlanAsync("Derde");

        var first = (await Get("/api/v2/cycle-plans?limit=2")).Body;
        var second = (await Get($"/api/v2/cycle-plans?limit=2&cursor={Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!)}")).Body;

        first.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Standaard", "Tweede");
        second.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Derde");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=abc", "limit")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task List_rejectsABadLimitOrCursor_with400(string query, string field)
    {
        await ArrangeAsync();

        var (response, body) = await Get($"/api/v2/cycle-plans?{query}");

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().Equal(field);
    }

    [Fact]
    public async Task Active_is404WhenNoPlanIsActive()
    {
        await ArrangeAsync();
        await Plans.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("active", false)), cancellationToken: Ct);

        var (response, body) = await Get("/api/v2/cycle-plans/active");

        ShouldBeProblem(response, body, HttpStatusCode.NotFound, "not_found");
    }

    // ---- PUT slots (describe 'PUT /api/cycle-plans/:id/slots')

    [Fact]
    public async Task SavesAValidPlanAndReturnsWarningsAndSummary_withAnAuditOfTheAddedSlots()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");

        var (response, body) = await PutSlots(planId, SlotBody(weekly, 0, 1, p1), SlotBody(twice, 0, 3, null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("plan").GetProperty("slots").GetArrayLength().Should().Be(2);
        var warnings = body.GetProperty("warnings").EnumerateArray().ToList();
        warnings.Should().Contain(w => w.GetProperty("code").GetString() == "interval_mismatch" && w.GetProperty("taskId").GetString() == weekly &&
            w.GetProperty("placed").GetInt32() == 1 && w.GetProperty("required").GetInt32() == 4);
        warnings.Should().Contain(w => w.GetProperty("code").GetString() == "interval_mismatch" && w.GetProperty("taskId").GetString() == twice &&
            w.GetProperty("placed").GetInt32() == 1 && w.GetProperty("required").GetInt32() == 8);
        body.GetProperty("summary").GetProperty("tasks").EnumerateArray().Should().Contain(t =>
            t.GetProperty("taskId").GetString() == weekly && t.GetProperty("placed").GetInt32() == 1 && t.GetProperty("required").GetInt32() == 4);
        var entries = (await PlanAudit("update")).Where(e => e["entityId"].AsObjectId.ToString() == planId).ToList();
        entries.Should().ContainSingle();
        entries[0]["source"].AsString.Should().Be("ui");
        entries[0]["before"].AsBsonDocument["slots"].AsBsonArray.Count.Should().Be(0);
        entries[0]["after"].AsBsonDocument["slots"].AsBsonArray.Count.Should().Be(2);
    }

    [Fact]
    public async Task AuditsOnlyAddedRemovedAndChangedSlots()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");
        await PutSlots(planId, SlotBody(weekly, 0, 1, p1), SlotBody(twice, 0, 3, null));

        // changed: assignee p1 -> p2 (Monday is fine for p2); removed: twice on week 0 Wednesday; added: twice on week 1 Friday
        var (response, _) = await PutSlots(planId, SlotBody(weekly, 0, 1, p2), SlotBody(twice, 1, 5, null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await PlanAudit("update")).Last(e => e["entityId"].AsObjectId.ToString() == planId);
        var before = entry["before"].AsBsonDocument["slots"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();
        var after = entry["after"].AsBsonDocument["slots"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();
        before.Select(s => (s["weekIndex"].ToInt32(), s["weekday"].ToInt32())).Should().Equal((0, 3), (0, 1));
        before[1]["assigneeId"].AsObjectId.ToString().Should().Be(p1);
        after.Select(s => (s["weekIndex"].ToInt32(), s["weekday"].ToInt32())).Should().Equal((1, 5), (0, 1));
        after[1]["assigneeId"].AsObjectId.ToString().Should().Be(p2);
    }

    [Fact]
    public async Task Returns422WithDetailsForHardErrors_andLeavesThePlanUntouched()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");
        var before = await Plans.Find(new BsonDocument("_id", ObjectId.Parse(planId))).SingleAsync(Ct);
        var updatesBefore = await PlanAuditCount("update");

        // Persoon B is unavailable on Tuesday
        var (response, body) = await PutSlots(planId, SlotBody(weekly, 2, 2, p2));

        ShouldBeProblem(response, body, HttpStatusCode.UnprocessableEntity, "invalid_plan");
        var issue = body.GetProperty("issues").EnumerateArray().Should().ContainSingle().Subject;
        issue.GetProperty("code").GetString().Should().Be("assignee_unavailable");
        issue.GetProperty("slotIndex").GetInt32().Should().Be(0);
        issue.GetProperty("userId").GetString().Should().Be(p2);
        body.GetProperty("errors").GetProperty("slots[0].assigneeId").EnumerateArray().Select(m => m.GetString()).Should().Equal("assignee_unavailable");
        body.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Array);
        body.GetProperty("summary").GetProperty("weeks").GetArrayLength().Should().Be(4);
        (await PlanAuditCount("update")).Should().Be(updatesBefore);
        (await Plans.Find(new BsonDocument("_id", ObjectId.Parse(planId))).SingleAsync(Ct)).ToJson().Should().Be(before.ToJson());
    }

    [Fact]
    public async Task RejectsTheSameTaskTwiceOnOneDayWith422()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");

        var (response, body) = await PutSlots(planId, SlotBody(twice, 3, 4, p1), SlotBody(twice, 3, 4, p2));

        ShouldBeProblem(response, body, HttpStatusCode.UnprocessableEntity, "invalid_plan");
        Codes(body.GetProperty("issues")).Should().Equal("duplicate_task_day");
    }

    [Fact]
    public async Task RejectsAnUnknownTask_anInactiveTask_andUnknownAndInactivePeopleWith422()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");
        var room = await SeedRoomAsync("Hal");
        var inactiveTask = await NewTaskAsync("Oud", room, "1w", 10, active: false);
        var inactivePerson = await SeedPersonAsync("Oud", [], active: false);

        var (response, body) = await PutSlots(
            planId,
            SlotBody("aaaaaaaaaaaaaaaaaaaaaaaa", 0, 1, null),
            SlotBody(inactiveTask, 0, 2, null),
            SlotBody(weekly, 0, 3, "bbbbbbbbbbbbbbbbbbbbbbbb"),
            SlotBody(weekly, 0, 4, inactivePerson));

        ShouldBeProblem(response, body, HttpStatusCode.UnprocessableEntity, "invalid_plan");
        Codes(body.GetProperty("issues")).Should().Equal("unknown_task", "inactive_task", "unknown_user", "inactive_user");
        ErrorFields(body).Should().BeEquivalentTo("slots[0].taskId", "slots[1].taskId", "slots[2].assigneeId", "slots[3].assigneeId");
    }

    [Fact]
    public async Task AnIdenticalPut_isNotAudited()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");
        await PutSlots(planId, SlotBody(weekly, 0, 1, p1));
        var current = (await Get($"/api/v2/cycle-plans/{planId}")).Body;
        var updatesBefore = await PlanAuditCount("update");
        var stored = await Plans.Find(new BsonDocument("_id", ObjectId.Parse(planId))).SingleAsync(Ct);

        var slots = current.GetProperty("slots").EnumerateArray().Select(s => (object)JsonSerializer.Deserialize<Dictionary<string, object?>>(s.GetRawText())!).ToArray();
        var (response, body) = await PutSlots(planId, slots);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("warnings").GetArrayLength().Should().BeGreaterThan(0);
        (await PlanAuditCount("update")).Should().Be(updatesBefore);
        (await Plans.Find(new BsonDocument("_id", ObjectId.Parse(planId))).SingleAsync(Ct)).ToJson().Should().Be(stored.ToJson());
    }

    [Fact]
    public async Task SortsTheStoredSlots_andKeepsTheDocumentShapeOfTheNodeServer()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");

        await PutSlots(planId, SlotBody(twice, 1, 1, null), SlotBody(weekly, 0, 0, p1), SlotBody(weekly, 0, 1, null));

        var stored = await Plans.Find(new BsonDocument("_id", ObjectId.Parse(planId))).SingleAsync(Ct);
        stored.Names.Should().Equal("_id", "name", "active", "slots", "weekThemes", "draft", "source", "proposalId", "rationale", "discarded", "createdAt", "updatedAt");
        var slots = stored["slots"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();
        slots.Select(s => (s["weekIndex"].ToInt32(), s["weekday"].ToInt32())).Should().Equal((0, 1), (0, 0), (1, 1));
        slots[1].Names.Should().Equal("taskId", "weekIndex", "weekday", "assigneeId", "sortOrder");
        slots[1]["taskId"].IsObjectId.Should().BeTrue();
        slots[1]["assigneeId"].AsObjectId.ToString().Should().Be(p1);
        slots[0]["assigneeId"].IsBsonNull.Should().BeTrue();
        slots[0]["weekIndex"].IsInt32.Should().BeTrue();
        stored["weekThemes"].AsBsonArray.Select(t => t.AsString).Should().Equal("", "", "", "");
        stored["rationale"].IsBsonNull.Should().BeTrue();
        stored["proposalId"].IsBsonNull.Should().BeTrue();
        stored["createdAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task ReadsAPlanThatTheNodeServerWrote()
    {
        await ArrangeAsync();
        var id = ObjectId.GenerateNewId();
        await Plans.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "name", "Uit Node" }, { "active", false },
                { "slots", new BsonArray { new BsonDocument { { "taskId", ObjectId.Parse(weekly) }, { "weekIndex", 1 }, { "weekday", 3 }, { "assigneeId", BsonNull.Value }, { "sortOrder", 0 } } } },
                { "weekThemes", new BsonArray { "a", "b", "c", "d" } }, { "draft", true }, { "source", "ai" }, { "proposalId", "p-1" },
                { "rationale", new BsonArray { "r1", "r2", "r3", "r4" } }, { "discarded", false },
                { "createdAt", new BsonDateTime(Now.UtcDateTime.AddDays(1)) }, { "updatedAt", new BsonDateTime(Now.UtcDateTime.AddDays(1)) },
            },
            cancellationToken: Ct);

        var (response, plan) = await Get($"/api/v2/cycle-plans/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        plan.GetProperty("source").GetString().Should().Be("ai");
        plan.GetProperty("draft").GetBoolean().Should().BeTrue();
        plan.GetProperty("proposalId").GetString().Should().Be("p-1");
        plan.GetProperty("rationale").EnumerateArray().Select(r => r.GetString()).Should().Equal("r1", "r2", "r3", "r4");
        plan.GetProperty("slots")[0].GetProperty("taskId").GetString().Should().Be(weekly);
    }

    [Theory]
    [InlineData("", "body")]
    [InlineData("""{"slots":"x"}""", "slots")]
    [InlineData("""{}""", "slots")]
    [InlineData("""{"slots":[5]}""", "slots[0]")]
    [InlineData("""{"slots":[{"taskId":"nope","weekIndex":0,"weekday":1,"assigneeId":null}]}""", "slots[0].taskId")]
    [InlineData("""{"slots":[{"taskId":"0123456789abcdef01234567","weekIndex":4,"weekday":1,"assigneeId":null}]}""", "slots[0].weekIndex")]
    [InlineData("""{"slots":[{"taskId":"0123456789abcdef01234567","weekIndex":0,"weekday":7,"assigneeId":null}]}""", "slots[0].weekday")]
    [InlineData("""{"slots":[{"taskId":"0123456789abcdef01234567","weekIndex":0.5,"weekday":1,"assigneeId":null}]}""", "slots[0].weekIndex")]
    [InlineData("""{"slots":[{"taskId":"0123456789abcdef01234567","weekIndex":0,"weekday":1,"assigneeId":"x"}]}""", "slots[0].assigneeId")]
    [InlineData("""{"slots":[{"taskId":"0123456789abcdef01234567","weekIndex":0,"weekday":1}]}""", "slots[0].assigneeId")]
    [InlineData("""{"slots":[{"taskId":"0123456789abcdef01234567","weekIndex":0,"weekday":1,"assigneeId":null,"sortOrder":"a"}]}""", "slots[0].sortOrder")]
    public async Task Put_validatesTheBody_with400(string json, string field)
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");

        var (response, body) = await Send(Request(HttpMethod.Put, $"/api/v2/cycle-plans/{planId}/slots", json));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().Equal(field);
    }

    [Fact]
    public async Task Put_validatesThePlanId_with400_andAnUnknownPlanWith404_beforeTheRulesRun()
    {
        await ArrangeAsync();

        var malformed = await Send(Request(HttpMethod.Put, "/api/v2/cycle-plans/nope/slots", new { slots = Array.Empty<object>() }));
        var unknown = await PutSlots("0123456789abcdef01234567", SlotBody(weekly, 2, 2, p2));

        ShouldBeProblem(malformed.Response, malformed.Body, HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Put_acceptsBodiesBeyondTheSmallWriteLimit_aPlanOfAFewHundredSlots()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Groot");
        var room = await SeedRoomAsync("Zolder");
        var slots = new List<object>();
        for (var i = 0; i < 30; i++)
        {
            var task = await NewTaskAsync($"Taak {i}", room, "quarter", 5);
            for (var day = 0; day < 7; day++)
            {
                slots.Add(SlotBody(task, 0, day, null));
            }
        }

        var (response, _) = await Send(Request(HttpMethod.Put, $"/api/v2/cycle-plans/{planId}/slots", new { slots }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Serialize(new { slots }).Length.Should().BeGreaterThan(16 * 1024);
    }

    // ---- diff

    [Fact]
    public async Task Diff_comparesAPlanWithTheActivePlan_withNamesAndWeeklyMinutes()
    {
        await ArrangeAsync();
        await PutSlots(standaard, SlotBody(weekly, 0, 1, p1), SlotBody(twice, 0, 3, null));
        var other = await NewPlanAsync("Experiment", standaard);
        await PutSlots(other, SlotBody(weekly, 0, 1, p2), SlotBody(twice, 1, 5, null));

        var (response, diff) = await Get($"/api/v2/cycle-plans/{other}/diff?against=active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        diff.GetProperty("planId").GetString().Should().Be(other);
        diff.GetProperty("againstPlanId").GetString().Should().Be(standaard);
        diff.GetProperty("unchanged").GetInt32().Should().Be(0);
        diff.GetProperty("added").GetArrayLength().Should().Be(0);
        diff.GetProperty("removed").GetArrayLength().Should().Be(0);
        var moved = diff.GetProperty("moved").EnumerateArray().ToList();
        moved.Select(m => m.GetProperty("taskName").GetString()).Should().Equal("Badkamer schoonmaken", "Wastafel poetsen");
        moved[0].GetProperty("roomName").GetString().Should().Be("Badkamer");
        moved[0].GetProperty("durationMinutes").GetInt32().Should().Be(30);
        moved[0].GetProperty("from").GetProperty("assigneeId").GetString().Should().Be(p1);
        moved[0].GetProperty("to").GetProperty("assigneeId").GetString().Should().Be(p2);
        var before = diff.GetProperty("summary").GetProperty("before")[0].GetProperty("users").EnumerateArray().ToList();
        var after = diff.GetProperty("summary").GetProperty("after")[0].GetProperty("users").EnumerateArray().ToList();
        before.Single(u => u.GetProperty("userId").GetString() == p1).GetProperty("minutes").GetInt32().Should().Be(30);
        after.Single(u => u.GetProperty("userId").GetString() == p2).GetProperty("minutes").GetInt32().Should().Be(30);
        diff.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Diff_ofThePlanAgainstItself_isAllUnchanged_andAnUnknownPlanIs404_andABadAgainstIs400()
    {
        await ArrangeAsync();
        await PutSlots(standaard, SlotBody(weekly, 0, 1, p1));

        var self = (await Get($"/api/v2/cycle-plans/{standaard}/diff")).Body;
        var unknown = await Get("/api/v2/cycle-plans/0123456789abcdef01234567/diff");
        var bad = await Get($"/api/v2/cycle-plans/{standaard}/diff?against=other");

        self.GetProperty("unchanged").GetInt32().Should().Be(1);
        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(bad.Response, bad.Body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(bad.Body).Should().Equal("against");
    }

    [Fact]
    public async Task Diff_withoutAnActivePlan_addsEverythingAndHasNoBase()
    {
        await ArrangeAsync();
        var other = await NewPlanAsync("Experiment");
        await PutSlots(other, SlotBody(weekly, 0, 1, p1));
        await Plans.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("active", false)), cancellationToken: Ct);

        var (_, diff) = await Get($"/api/v2/cycle-plans/{other}/diff");

        diff.GetProperty("againstPlanId").ValueKind.Should().Be(JsonValueKind.Null);
        diff.GetProperty("added").GetArrayLength().Should().Be(1);
    }

    // ---- validation endpoints (plan 4.3)

    [Fact]
    public async Task ValidatingAStoredPlan_returnsWhatTheSaveWouldHaveSaid_andWritesNothing()
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Slots");
        var slots = new[] { SlotBody(weekly, 0, 1, p1), SlotBody(twice, 0, 3, null) };
        var saved = (await PutSlots(planId, slots)).Body;
        var updatesBefore = await PlanAuditCount("update");

        var (response, validation) = await Send(Request(HttpMethod.Post, $"/api/v2/cycle-plans/{planId}/validation"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        validation.GetProperty("valid").GetBoolean().Should().BeTrue();
        validation.GetProperty("errors").EnumerateObject().Should().BeEmpty();
        validation.GetProperty("issues").GetArrayLength().Should().Be(0);
        validation.GetProperty("warnings").ToString().Should().Be(saved.GetProperty("warnings").ToString());
        validation.GetProperty("summary").ToString().Should().Be(saved.GetProperty("summary").ToString());
        (await PlanAuditCount("update")).Should().Be(updatesBefore);
    }

    [Fact]
    public async Task ValidatingADraft_reportsTheErrorsAWouldBeSaveWouldRefuseWith_andSavesNothing()
    {
        await ArrangeAsync();
        var slots = new[] { SlotBody(weekly, 2, 2, p2), SlotBody(twice, 3, 4, p1), SlotBody(twice, 3, 4, p2) };
        var planId = await NewPlanAsync("Slots");
        var save = await PutSlots(planId, slots);
        var plansBefore = await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var (response, validation) = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans/validation", new { slots }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        validation.GetProperty("valid").GetBoolean().Should().BeFalse();
        Codes(validation.GetProperty("issues")).Should().Equal("assignee_unavailable", "duplicate_task_day");
        validation.GetProperty("issues").ToString().Should().Be(save.Body.GetProperty("issues").ToString());
        validation.GetProperty("errors").ToString().Should().Be(save.Body.GetProperty("errors").ToString());
        validation.GetProperty("summary").ToString().Should().Be(save.Body.GetProperty("summary").ToString());
        ErrorFields(validation).Should().BeEquivalentTo("slots[0].assigneeId", "slots[2].taskId");
        (await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(plansBefore);
        (await PlanAuditCount("update")).Should().Be(0);
    }

    [Fact]
    public async Task ValidatingADraft_withNoSlots_isValidWithAFourWeekSummary()
    {
        await ArrangeAsync();

        var (response, validation) = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans/validation", new { slots = Array.Empty<object>() }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        validation.GetProperty("valid").GetBoolean().Should().BeTrue();
        validation.GetProperty("summary").GetProperty("weeks").GetArrayLength().Should().Be(4);
        validation.GetProperty("summary").GetProperty("days").GetArrayLength().Should().Be(28);
        validation.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).Should().OnlyContain(c => c == "interval_mismatch");
    }

    [Fact]
    public async Task ValidatingADraft_refusesMalformedSlotsLikeASave()
    {
        await ArrangeAsync();

        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans/validation", """{"slots":[{"taskId":"x","weekIndex":9,"weekday":1,"assigneeId":null}]}"""));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(body).Should().BeEquivalentTo("slots[0].taskId", "slots[0].weekIndex");
    }

    [Fact]
    public async Task ValidatingAnUnknownOrMalformedStoredPlan_is404Or400()
    {
        await ArrangeAsync();

        var unknown = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans/0123456789abcdef01234567/validation"));
        var malformed = await Send(Request(HttpMethod.Post, "/api/v2/cycle-plans/nope/validation"));

        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(malformed.Response, malformed.Body, HttpStatusCode.BadRequest, "validation_error");
    }

    // ---- roles and profile (Node: requirePlanner on every write; reads are open)

    private HttpRequestMessage WriteRequest(string kind, string planId, string? profileId, bool noProfile = false) => kind switch
    {
        "POST" => Request(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "N" }, profileId, noProfile),
        "PATCH" => Request(HttpMethod.Patch, $"/api/v2/cycle-plans/{planId}", new { name = "N" }, profileId, noProfile),
        "DELETE" => Request(HttpMethod.Delete, $"/api/v2/cycle-plans/{planId}", null, profileId, noProfile),
        "PUT" => Request(HttpMethod.Put, $"/api/v2/cycle-plans/{planId}/slots", new { slots = Array.Empty<object>() }, profileId, noProfile),
        "VALIDATE" => Request(HttpMethod.Post, $"/api/v2/cycle-plans/{planId}/validation", null, profileId, noProfile),
        _ => Request(HttpMethod.Post, "/api/v2/cycle-plans/validation", new { slots = Array.Empty<object>() }, profileId, noProfile),
    };

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("VALIDATE")]
    [InlineData("VALIDATE-DRAFT")]
    public async Task Writes_needAPlanner_aMemberGets403PermissionDenied_andNothingChanges(string kind)
    {
        await ArrangeAsync();
        var planId = await NewPlanAsync("Doel");
        var member = directory.Add(Role.Member);
        var auditBefore = await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var (response, body) = await Send(WriteRequest(kind, planId, member.Id));

        ShouldBeProblem(response, body, HttpStatusCode.Forbidden, "permission_denied");
        (await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
        (await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(auditBefore);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("VALIDATE")]
    [InlineData("VALIDATE-DRAFT")]
    public async Task Writes_withoutAProfile_are400ProfileRequired(string kind)
    {
        var (response, body) = await Send(WriteRequest(kind, "0123456789abcdef01234567", null, noProfile: true));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task ReadsNeedNoProfile_andAnAdministratorMayWriteToo()
    {
        await ArrangeAsync();
        var admin = directory.Add(Role.Admin);

        var list = await Get("/api/v2/cycle-plans");
        var diff = await Get($"/api/v2/cycle-plans/{standaard}/diff");
        var created = await Send(WriteRequest("POST", standaard, admin.Id));

        list.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        diff.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        created.Response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
