using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Optimistic concurrency on every entity write of API v2 (ADR-0022, slice 6.7a): one contract, run against each entity that has a PATCH, PUT or
/// DELETE. A read answers the <c>ETag</c> (and the entity carries the same number as <c>version</c>); a write without <c>If-Match</c> is 428, with a
/// stale one 412 (with the current ETag, nothing written), with the current one it succeeds, raises the version by one and answers the new ETag;
/// a write that changes nothing keeps the version and the audit log. Real HTTP pipeline and real MongoDB replica set.
/// </summary>
public sealed class EntityConcurrencyTests : IDisposable
{
    private const string Tasks = "tasks";
    private const string Rooms = "rooms";
    private const string Users = "users";
    private const string UserNotifications = "user-browser-notifications";
    private const string Plans = "cycle-plans";
    private const string PlanSlots = "cycle-plan-slots";
    private const string Badges = "badges";
    private const string Settings = "settings";

    private static readonly string[] EightOClock = ["08:00"];
    private static readonly string[] WeakOrOdd = ["1", "W/\"1\"", "*", "\"1\", \"2\"", "\"abc\"", "\"\"", "\"-1\"", "\"99999999999\""];

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory directory = new();
    private readonly UserIdentity admin;
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    private readonly IMongoDatabase database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public EntityConcurrencyTests(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        admin = directory.Add(Role.Admin);
        mongoClient = new MongoClient(mongo.ConnectionString);
        database = mongoClient.GetDatabase(databaseName);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithPort<ForFindingUsers>(directory);
        client = factory.CreateRawClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    /// <summary>Every entity with an editable resource; the second of the pair names the case.</summary>
    public static TheoryData<string> Entities => [Tasks, Rooms, Users, UserNotifications, Plans, PlanSlots, Badges, Settings];

    /// <summary>The entities that also have a DELETE.</summary>
    public static TheoryData<string> Deletable => [Tasks, Rooms, Plans, Badges];

    /// <summary>The entities that have a POST that creates them.</summary>
    public static TheoryData<string> Creatable => [Tasks, Rooms, Users, Plans, Badges];

    // ---- the cases

    /// <summary>What a case knows about its entity: how to make one, where to read it, and a write that changes it and one that does not.</summary>
    private sealed record Case(
        string Id,
        string ReadPath,
        string WritePath,
        HttpMethod WriteMethod,
        object ChangeBody,
        object NoOpBody,
        Func<JsonElement, JsonElement> EntityOf,
        string AuditEntity,
        string AuditEntityId);

    private async Task<Case> Prepare(string name)
    {
        JsonElement Same(JsonElement body) => body;
        switch (name)
        {
            case Tasks:
            {
                var room = await Post<JsonElement>("/api/v2/rooms", new { name = "Keuken" });
                var task = await Post<JsonElement>("/api/v2/tasks", new { name = "Afwas", roomId = room.GetProperty("id").GetString(), intervalKey = "1w", durationMinutes = 20 });
                var id = task.GetProperty("id").GetString()!;
                return new Case(id, $"/api/v2/tasks/{id}", $"/api/v2/tasks/{id}", HttpMethod.Patch, new { name = "Afwas en opruimen" }, new { name = "Afwas" }, Same, "task", id);
            }

            case Rooms:
            {
                var room = await Post<JsonElement>("/api/v2/rooms", new { name = "Keuken" });
                var id = room.GetProperty("id").GetString()!;
                return new Case(id, $"/api/v2/rooms/{id}", $"/api/v2/rooms/{id}", HttpMethod.Patch, new { name = "Woonkamer" }, new { name = "Keuken" }, Same, "room", id);
            }

            case Users:
            case UserNotifications:
            {
                var user = await Post<JsonElement>("/api/v2/users", new { name = "Anna", color = "#16a34a" });
                var id = user.GetProperty("id").GetString()!;
                return name == Users
                    ? new Case(id, $"/api/v2/users/{id}", $"/api/v2/users/{id}", HttpMethod.Patch, new { name = "Anna B" }, new { name = "Anna" }, Same, "user", id)
                    : new Case(
                        id, $"/api/v2/users/{id}", $"/api/v2/users/{id}/browser-notifications", HttpMethod.Put,
                        new { enabled = true, times = EightOClock }, new { enabled = false, times = Array.Empty<string>() }, Same, "user", id);
            }

            case Plans:
            {
                var plan = await Post<JsonElement>("/api/v2/cycle-plans", new { name = "Plan A" });
                var id = plan.GetProperty("id").GetString()!;
                return new Case(id, $"/api/v2/cycle-plans/{id}", $"/api/v2/cycle-plans/{id}", HttpMethod.Patch, new { name = "Plan B" }, new { name = "Plan A" }, Same, "cyclePlan", id);
            }

            case PlanSlots:
            {
                var plan = await Post<JsonElement>("/api/v2/cycle-plans", new { name = "Plan A" });
                var id = plan.GetProperty("id").GetString()!;
                var room = await Post<JsonElement>("/api/v2/rooms", new { name = "Keuken" });
                var task = await Post<JsonElement>("/api/v2/tasks", new { name = "Afwas", roomId = room.GetProperty("id").GetString(), intervalKey = "1w", durationMinutes = 20 });
                var slot = new { taskId = task.GetProperty("id").GetString(), weekIndex = 0, weekday = 1, assigneeId = (string?)null };
                return new Case(
                    id, $"/api/v2/cycle-plans/{id}", $"/api/v2/cycle-plans/{id}/slots", HttpMethod.Put, new { slots = new[] { slot } }, new { slots = Array.Empty<object>() },
                    body => body.TryGetProperty("plan", out var plan2) ? plan2 : body, "cyclePlan", id);
            }

            case Badges:
            {
                var badge = await Post<JsonElement>("/api/v2/badges", new { name = "Afwasser", rule = new { type = "executions", taskIds = Array.Empty<string>(), threshold = 3 } });
                var id = badge.GetProperty("id").GetString()!;
                return new Case(id, $"/api/v2/badges/{id}", $"/api/v2/badges/{id}", HttpMethod.Patch, new { name = "Afwaskampioen" }, new { name = "Afwasser" }, Same, "badge", id);
            }

            case Settings:
                return new Case(
                    "settings", "/api/v2/settings", "/api/v2/settings", HttpMethod.Patch, new { promoteThreshold = 7 }, new { promoteThreshold = 2 }, Same, "settings", SettingsIdForAudit);

            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private const string SettingsIdForAudit = "000000000000000000000001";

    // ---- helpers

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Profile-Id", admin.Id);
        request.Headers.Add("X-Client", "web");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Send(HttpRequestMessage request)
    {
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<T> Post<T>(string url, object body)
    {
        var (response, json) = await Send(Request(HttpMethod.Post, url, body));
        response.StatusCode.Should().Be(HttpStatusCode.Created, json.ToString());
        return (T)(object)json;
    }

    private async Task<(string ETag, int Version)> Read(Case c)
    {
        var (response, body) = await Send(Request(HttpMethod.Get, c.ReadPath));
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var etag = response.Headers.ETag!.ToString();
        var version = body.GetProperty("version").GetInt32();
        etag.Should().Be($"\"{version}\"", "the ETag is the strong validator of the version member");
        return (etag, version);
    }

    private static string ETagOf(HttpResponseMessage response) => response.Headers.ETag?.ToString() ?? "(no ETag)";

    private async Task<long> AuditCount(Case c) =>
        await database.GetCollection<BsonDocument>("auditLog").CountDocumentsAsync(new BsonDocument("entity", c.AuditEntity), cancellationToken: Ct);

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    // ---- reads and creates

    [Theory]
    [MemberData(nameof(Creatable))]
    public async Task Create_answersTheETagOfVersion1(string entity)
    {
        var c = await Prepare(entity);

        var (etag, version) = await Read(c);

        version.Should().Be(1, "an entity this application creates starts at version 1");
        etag.Should().Be("\"1\"");
    }

    [Fact]
    public async Task Create_answersTheETagInTheResponseHeader()
    {
        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/rooms", new { name = "Keuken" }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        ETagOf(response).Should().Be("\"1\"");
        body.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Read_answersTheETagAndTheVersionMember(string entity)
    {
        var c = await Prepare(entity);

        var (etag, version) = await Read(c);

        etag.Should().Be($"\"{version}\"");
    }

    [Fact]
    public async Task Read_ofAnUnknownEntity_is404_andOfAMalformedId400()
    {
        (await Send(Request(HttpMethod.Get, "/api/v2/tasks/ffffffffffffffffffffffff"))).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(Request(HttpMethod.Get, "/api/v2/rooms/ffffffffffffffffffffffff"))).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(Request(HttpMethod.Get, "/api/v2/users/ffffffffffffffffffffffff"))).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(Request(HttpMethod.Get, "/api/v2/badges/ffffffffffffffffffffffff"))).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(Request(HttpMethod.Get, "/api/v2/tasks/nope"))).Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListItems_carryTheirVersion()
    {
        var c = await Prepare(Tasks);

        var (response, body) = await Send(Request(HttpMethod.Get, "/api/v2/tasks"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("id").GetString() == c.Id).GetProperty("version").GetInt32().Should().Be(1);
    }

    // ---- 428 and 400

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Write_withoutIfMatch_is428_andWritesNothing(string entity)
    {
        var c = await Prepare(entity);
        var audit = await AuditCount(c);

        var (response, body) = await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody));

        ShouldBeProblem(response, body, HttpStatusCode.PreconditionRequired, "precondition_required");
        (await Read(c)).Version.Should().Be(1);
        (await AuditCount(c)).Should().Be(audit);
    }

    [Theory]
    [MemberData(nameof(Deletable))]
    public async Task Delete_withoutIfMatch_is428_andDeletesNothing(string entity)
    {
        var c = await Prepare(entity);

        var (response, body) = await Send(Request(HttpMethod.Delete, c.WritePath));

        ShouldBeProblem(response, body, HttpStatusCode.PreconditionRequired, "precondition_required");
        (await Read(c)).Version.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Write_withAMalformedIfMatch_is400_onTheHeader(string entity)
    {
        var c = await Prepare(entity);

        foreach (var header in WeakOrOdd)
        {
            var (response, body) = await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, header));

            ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
            body.GetProperty("errors").TryGetProperty("If-Match", out _).Should().BeTrue($"the header {header} is named in the errors");
        }

        (await Read(c)).Version.Should().Be(1);
    }

    [Fact]
    public async Task Write_withoutIfMatch_isAnsweredAfterAuthorization_soAnUnauthorisedCallerLearnsNothingAboutTheHeader()
    {
        var c = await Prepare(Rooms);
        using var anonymous = new HttpRequestMessage(HttpMethod.Patch, c.WritePath) { Content = JsonContent.Create(c.ChangeBody) };
        var member = directory.Add(Role.Member);
        using var forbidden = new HttpRequestMessage(HttpMethod.Patch, c.WritePath) { Content = JsonContent.Create(c.ChangeBody) };
        forbidden.Headers.Add("X-Profile-Id", member.Id);

        var withoutProfile = await client.SendAsync(anonymous, Ct);
        var withoutRole = await client.SendAsync(forbidden, Ct);

        withoutProfile.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await withoutProfile.Content.ReadAsStringAsync(Ct)).Should().Contain("profile_required");
        withoutRole.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 412

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Write_withAStaleIfMatch_is412_withTheCurrentETag_andWritesNothing(string entity)
    {
        var c = await Prepare(entity);
        var (first, _) = await Read(c);
        (await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, first))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var audit = await AuditCount(c);

        var (response, body) = await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, first));

        ShouldBeProblem(response, body, HttpStatusCode.PreconditionFailed, "precondition_failed");
        ETagOf(response).Should().Be("\"2\"", "the answer carries the current ETag");
        (await Read(c)).Version.Should().Be(2);
        (await AuditCount(c)).Should().Be(audit);
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task NoOpWrite_withAStaleIfMatch_isStill412(string entity)
    {
        var c = await Prepare(entity);
        var (first, _) = await Read(c);
        (await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, first))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var (response, body) = await Send(Request(c.WriteMethod, c.WritePath, c.NoOpBody, first));

        ShouldBeProblem(response, body, HttpStatusCode.PreconditionFailed, "precondition_failed");
    }

    // ---- success

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Write_withTheCurrentIfMatch_succeeds_raisesTheVersionByOne_andAnswersTheNewETag(string entity)
    {
        var c = await Prepare(entity);
        var (etag, _) = await Read(c);

        var (response, body) = await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, etag));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        ETagOf(response).Should().Be("\"2\"");
        c.EntityOf(body).GetProperty("version").GetInt32().Should().Be(2);
        (await Read(c)).ETag.Should().Be("\"2\"");
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task Write_theNextWriteNeedsTheNewETag(string entity)
    {
        var c = await Prepare(entity);
        var (etag, _) = await Read(c);
        var (first, _) = await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, etag));
        var next = ETagOf(first);

        var (back, _) = await Send(Request(c.WriteMethod, c.WritePath, c.NoOpBody, next));

        back.StatusCode.Should().Be(HttpStatusCode.OK, "the ETag of the answer is the current one");
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task NoOpWrite_withTheCurrentIfMatch_is200_keepsTheVersion_andWritesNoAuditEntry(string entity)
    {
        var c = await Prepare(entity);
        var (etag, _) = await Read(c);
        var audit = await AuditCount(c);

        var (response, body) = await Send(Request(c.WriteMethod, c.WritePath, c.NoOpBody, etag));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        ETagOf(response).Should().Be(etag);
        c.EntityOf(body).GetProperty("version").GetInt32().Should().Be(1);
        (await Read(c)).Version.Should().Be(1);
        (await AuditCount(c)).Should().Be(audit, "a no-op audits nothing");
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task TheVersion_isMetadata_andNeverAppearsInTheAuditEntry(string entity)
    {
        var c = await Prepare(entity);
        var (etag, _) = await Read(c);
        (await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, etag))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await database.GetCollection<BsonDocument>("auditLog")
            .Find(new BsonDocument { { "entity", c.AuditEntity }, { "action", "update" } }).ToListAsync(Ct);

        entries.Should().NotBeEmpty();
        entries.Select(e => e.ToJson()).Should().OnlyContain(json => !json.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    // ---- delete

    [Theory]
    [MemberData(nameof(Deletable))]
    public async Task Delete_withAStaleIfMatch_is412_andDeletesNothing(string entity)
    {
        var c = await Prepare(entity);
        var (first, _) = await Read(c);
        (await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, first))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var (response, body) = await Send(Request(HttpMethod.Delete, c.WritePath, null, first));

        ShouldBeProblem(response, body, HttpStatusCode.PreconditionFailed, "precondition_failed");
        ETagOf(response).Should().Be("\"2\"");
        (await Read(c)).Version.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(Deletable))]
    public async Task Delete_withTheCurrentIfMatch_deletesTheEntity(string entity)
    {
        var c = await Prepare(entity);
        var (etag, _) = await Read(c);

        var (response, body) = await Send(Request(HttpMethod.Delete, c.WritePath, null, etag));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await Send(Request(HttpMethod.Get, c.ReadPath))).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ofAnUnknownTask_is404_beforeTheVersionIsLookedAt()
    {
        var (response, _) = await Send(Request(HttpMethod.Delete, "/api/v2/tasks/ffffffffffffffffffffffff", null, "\"7\""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ofATask_keepsItsCascadeInOneTransaction_whenTheVersionIsStale()
    {
        var c = await Prepare(PlanSlots);
        var (etag, _) = await Read(c);
        (await Send(Request(c.WriteMethod, c.WritePath, c.ChangeBody, etag))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = (await Send(Request(HttpMethod.Get, c.ReadPath))).Body;
        var taskId = plan.GetProperty("slots").EnumerateArray().Single().GetProperty("taskId").GetString()!;

        var (response, _) = await Send(Request(HttpMethod.Delete, $"/api/v2/tasks/{taskId}", null, "\"99\""));

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await Send(Request(HttpMethod.Get, c.ReadPath))).Body.GetProperty("slots").GetArrayLength().Should().Be(1, "a refused delete leaves the plans alone");
        (await Send(Request(HttpMethod.Get, $"/api/v2/tasks/{taskId}"))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- what is not an entity write

    [Fact]
    public async Task IntentEndpoints_takeNoIfMatch()
    {
        var room = await Post<JsonElement>("/api/v2/rooms", new { name = "Keuken" });
        var roomId = room.GetProperty("id").GetString();
        await Post<JsonElement>("/api/v2/tasks", new { name = "Afwas", roomId, intervalKey = "1w", durationMinutes = 20 });

        var (response, body) = await Send(Request(HttpMethod.Post, $"/api/v2/rooms/{roomId}/tasks/bulk", new { op = "deactivate" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("updated").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ABulkChange_raisesTheVersionOfEveryTaskItChanges()
    {
        var room = await Post<JsonElement>("/api/v2/rooms", new { name = "Keuken" });
        var roomId = room.GetProperty("id").GetString();
        var task = await Post<JsonElement>("/api/v2/tasks", new { name = "Afwas", roomId, intervalKey = "1w", durationMinutes = 20 });
        var id = task.GetProperty("id").GetString();
        (await Send(Request(HttpMethod.Post, $"/api/v2/rooms/{roomId}/tasks/bulk", new { op = "deactivate" }))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var (response, body) = await Send(Request(HttpMethod.Get, $"/api/v2/tasks/{id}"));

        body.GetProperty("version").GetInt32().Should().Be(2);
        ETagOf(response).Should().Be("\"2\"");
    }

    [Fact]
    public async Task ADocumentWithoutAVersion_readsAsVersion0_andItsFirstWriteMakesItVersion1()
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(DateTime.UtcNow.AddDays(-30));
        await database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", "Oud" }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);

        var read = await Send(Request(HttpMethod.Get, $"/api/v2/rooms/{id}"));
        var (stale, _) = await Send(Request(HttpMethod.Patch, $"/api/v2/rooms/{id}", new { name = "Nieuw" }, "\"1\""));
        var (response, body) = await Send(Request(HttpMethod.Patch, $"/api/v2/rooms/{id}", new { name = "Nieuw" }, ETagOf(read.Response)));

        ETagOf(read.Response).Should().Be("\"0\"");
        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        ETagOf(stale).Should().Be("\"0\"");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("version").GetInt32().Should().Be(1);
    }

    // ---- concurrency and rollback

    [Fact]
    public async Task TwoWritersWithTheSameETag_exactlyOneWins()
    {
        var c = await Prepare(Tasks);
        var (etag, _) = await Read(c);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            var (response, _) = await Send(Request(HttpMethod.Patch, c.WritePath, new { notes = $"writer {i}" }, etag));
            return response.StatusCode;
        }));

        attempts.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        attempts.Count(s => s == HttpStatusCode.PreconditionFailed).Should().Be(5);
        (await Read(c)).Version.Should().Be(2, "only the winner wrote");
        (await database.GetCollection<BsonDocument>("auditLog").CountDocumentsAsync(
            new BsonDocument { { "entity", "task" }, { "action", "update" } }, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task TwoSettingsWritersWithTheSameETag_exactlyOneWins()
    {
        var c = await Prepare(Settings);
        var (etag, _) = await Read(c);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            var (response, _) = await Send(Request(HttpMethod.Patch, c.WritePath, new { promoteThreshold = 10 + i }, etag));
            return response.StatusCode;
        }));

        attempts.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        attempts.Count(s => s == HttpStatusCode.PreconditionFailed).Should().Be(3);
    }

    [Fact]
    public async Task AWriteWhoseAuditEntryFails_rollsBack_andLeavesTheVersionAlone()
    {
        var c = await Prepare(Tasks);
        var (etag, _) = await Read(c);
        using var failing = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<ForRecordingAudit>(new FailingAudit());
        using var failingClient = failing.CreateRawClient();

        var response = await failingClient.SendAsync(Request(HttpMethod.Patch, c.WritePath, c.ChangeBody, etag), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await Read(c)).ETag.Should().Be(etag, "the version was raised inside the transaction that rolled back");
        (await Send(Request(HttpMethod.Patch, c.WritePath, c.ChangeBody, etag))).Response.StatusCode.Should().Be(HttpStatusCode.OK, "the same ETag still works");
    }

    private sealed class FailingAudit : ForRecordingAudit
    {
        public Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult<OneOf<Success, PortError>>(new PortError("audit down"));
    }
}
