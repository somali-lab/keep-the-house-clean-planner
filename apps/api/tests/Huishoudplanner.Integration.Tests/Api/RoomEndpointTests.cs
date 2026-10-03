using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
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
/// Ports apps/server/test/rooms.test.ts (list, create, validate, rename and deactivate, unknown room, delete and
/// <c>room_in_use</c>) and adds the v2 behaviour: role policies, paging, no-op and rollback, Node-shaped documents.
/// Real HTTP pipeline and real MongoDB replica set; one database per test.
/// </summary>
public sealed class RoomEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 30, 0, TimeSpan.Zero);

    private static readonly string[] SeededNames = ["Keuken", "Badkamer", "Toilet", "Woonkamer", "Slaapkamer", "Hal", "Hele huis"];

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory users = new();
    private readonly UserIdentity admin;
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    private readonly IMongoDatabase database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RoomEndpointTests(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        admin = users.Add(Role.Admin);
        mongoClient = new MongoClient(mongo.ConnectionString);
        database = mongoClient.GetDatabase(databaseName);
        factory = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(users)
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

    private IMongoCollection<BsonDocument> Rooms => database.GetCollection<BsonDocument>("rooms");

    private IMongoCollection<BsonDocument> Tasks => database.GetCollection<BsonDocument>("tasks");

    private IMongoCollection<BsonDocument> AuditLog => database.GetCollection<BsonDocument>("auditLog");

    /// <summary>The seed of the Node server (<c>SEED_ROOMS</c>, sort order 10 to 70), inserted the way the Node server stores it.</summary>
    private async Task<Dictionary<string, ObjectId>> SeedRoomsAsync()
    {
        var ids = new Dictionary<string, ObjectId>();
        var at = new BsonDateTime(Now.UtcDateTime.AddDays(-30));
        for (var i = 0; i < SeededNames.Length; i++)
        {
            var id = ObjectId.GenerateNewId();
            ids[SeededNames[i]] = id;
            await Rooms.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", id },
                    { "name", SeededNames[i] },
                    { "sortOrder", (i + 1) * 10 },
                    { "active", true },
                    { "virtual", SeededNames[i] == "Hele huis" },
                    { "createdAt", at },
                    { "updatedAt", at },
                },
                cancellationToken: Ct);
        }

        return ids;
    }

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? profileId = null, bool web = true, bool noProfile = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (!noProfile)
        {
            request.Headers.Add("X-Profile-Id", profileId ?? admin.Id);
        }

        if (web)
        {
            request.Headers.Add("X-Client", "web");
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

    private Task<(HttpResponseMessage Response, JsonElement Body)> Get(string url) => Send(new HttpRequestMessage(HttpMethod.Get, url));

    private static string[] Names(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];

    private async Task<List<BsonDocument>> RoomAuditEntries() =>
        await AuditLog.Find(new BsonDocument("entity", "room")).Sort(Builders<BsonDocument>.Sort.Ascending("at").Ascending("_id")).ToListAsync(Ct);

    private static void Same(BsonDocument actual, BsonDocument expected) => actual.ToJson().Should().Be(expected.ToJson());

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    // ---- 'lists seeded rooms in sort order, including the virtual room'

    [Fact]
    public async Task List_returnsTheSeededRoomsInSortOrder_includingTheVirtualRoom_withoutAProfile()
    {
        await SeedRoomsAsync();

        var (response, body) = await Get("/api/v2/rooms");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Names(body).Should().Equal(SeededNames);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var last = body.GetProperty("items").EnumerateArray().Last();
        last.GetProperty("name").GetString().Should().Be("Hele huis");
        last.GetProperty("virtual").GetBoolean().Should().BeTrue();
        last.GetProperty("id").GetString().Should().MatchRegex("^[0-9a-f]{24}$");
        last.GetProperty("createdAt").GetDateTimeOffset().Should().Be(Now.AddDays(-30));
    }

    [Fact]
    public async Task List_inAnEmptyHouse_isAnEmptyPage()
    {
        var (response, body) = await Get("/api/v2/rooms");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_readsDocumentsTheWayTheNodeServerStoresThem()
    {
        // JavaScript numbers can land in MongoDB as doubles; a document may also lack the flags of an older installation.
        await Rooms.InsertOneAsync(
            new BsonDocument { { "name", "Oud" }, { "sortOrder", 15.0 }, { "active", true }, { "virtual", false }, { "createdAt", new BsonDateTime(Now.UtcDateTime) }, { "updatedAt", new BsonDateTime(Now.UtcDateTime) }, { "extra", "ignored" } },
            cancellationToken: Ct);

        var (_, body) = await Get("/api/v2/rooms");

        body.GetProperty("items")[0].GetProperty("sortOrder").GetInt32().Should().Be(15);
    }

    [Fact]
    public async Task List_filtersOnActive()
    {
        var ids = await SeedRoomsAsync();
        await Rooms.UpdateOneAsync(new BsonDocument("_id", ids["Hal"]), new BsonDocument("$set", new BsonDocument("active", false)), cancellationToken: Ct);

        Names((await Get("/api/v2/rooms?active=true")).Body).Should().NotContain("Hal").And.HaveCount(6);
        Names((await Get("/api/v2/rooms?active=false")).Body).Should().Equal("Hal");
    }

    [Fact]
    public async Task List_pagesWithLimitAndCursor_inStableOrderEvenForEqualSortOrders()
    {
        await SeedRoomsAsync();
        await Rooms.InsertManyAsync(
            [
                new BsonDocument { { "name", "Zolder" }, { "sortOrder", 70 }, { "active", true }, { "virtual", false }, { "createdAt", new BsonDateTime(Now.UtcDateTime) }, { "updatedAt", new BsonDateTime(Now.UtcDateTime) } },
                new BsonDocument { { "name", "Bijkeuken" }, { "sortOrder", 70 }, { "active", true }, { "virtual", false }, { "createdAt", new BsonDateTime(Now.UtcDateTime) }, { "updatedAt", new BsonDateTime(Now.UtcDateTime) } },
            ],
            cancellationToken: Ct);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/v2/rooms?limit=3" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var (response, body) = await Get(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            seen.AddRange(Names(body));
            cursor = body.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().Equal("Keuken", "Badkamer", "Toilet", "Woonkamer", "Slaapkamer", "Hal", "Bijkeuken", "Hele huis", "Zolder");
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task List_withABadLimitOrCursor_is400ValidationError(string query, string field)
    {
        var (response, body) = await Get("/api/v2/rooms?" + query);

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    // ---- 'creates a room at the end of the list and audits it'

    [Fact]
    public async Task Create_putsTheRoomAtTheEndOfTheList_andAuditsIt()
    {
        await SeedRoomsAsync();

        var (response, body) = await Send(Request(HttpMethod.Post, "/api/v2/rooms", new { name = "Zolder" }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body.GetProperty("name").GetString().Should().Be("Zolder");
        body.GetProperty("sortOrder").GetInt32().Should().Be(80);
        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("virtual").GetBoolean().Should().BeFalse();
        body.GetProperty("createdAt").GetDateTimeOffset().Should().Be(Now);
        body.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(Now);
        var id = body.GetProperty("id").GetString()!;

        var stored = await Rooms.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);
        stored["sortOrder"].BsonType.Should().Be(BsonType.Int32);
        stored.Names.Should().Equal("_id", "name", "sortOrder", "active", "virtual", "createdAt", "updatedAt");

        var entries = await RoomAuditEntries();
        var entry = entries.Should().ContainSingle().Subject;
        entry["action"].AsString.Should().Be("create");
        entry["source"].AsString.Should().Be("ui");
        entry["entityId"].AsObjectId.ToString().Should().Be(id);
        entry["actorId"].AsObjectId.ToString().Should().Be(admin.Id);
        entry["before"].AsBsonDocument.ElementCount.Should().Be(0);
        Same(entry["after"].AsBsonDocument, new BsonDocument { { "name", "Zolder" }, { "sortOrder", 80 }, { "active", true }, { "virtual", false } });
    }

    [Fact]
    public async Task Create_inAnEmptyHouse_startsAtTen_andUsesTheGivenOrderFlagsAndTrimmedName()
    {
        var first = await Send(Request(HttpMethod.Post, "/api/v2/rooms", new { name = "  Hele huis ", @virtual = true }, web: false));
        var second = await Send(Request(HttpMethod.Post, "/api/v2/rooms", new { name = "Keuken", sortOrder = 5 }));

        first.Body.GetProperty("sortOrder").GetInt32().Should().Be(10);
        first.Body.GetProperty("name").GetString().Should().Be("Hele huis");
        first.Body.GetProperty("virtual").GetBoolean().Should().BeTrue();
        second.Body.GetProperty("sortOrder").GetInt32().Should().Be(5);
        (await RoomAuditEntries()).Select(e => e["source"].AsString).Should().Equal("api", "ui");
    }

    // ---- 'validates input'

    [Theory]
    [InlineData("""{"name":""}""", "name")]
    [InlineData("""{"name":"   "}""", "name")]
    [InlineData("""{}""", "name")]
    [InlineData("""{"name":5}""", "name")]
    [InlineData("""{"name":"X","sortOrder":1.5}""", "sortOrder")]
    [InlineData("""{"name":"X","sortOrder":"3"}""", "sortOrder")]
    [InlineData("""{"name":"X","sortOrder":3000000000}""", "sortOrder")]
    [InlineData("""{"name":"X","virtual":null}""", "virtual")]
    [InlineData("""{"name":"X","virtual":"yes"}""", "virtual")]
    [InlineData("""not json""", "body")]
    [InlineData("""[]""", "body")]
    [InlineData("", "body")]
    public async Task Create_withInvalidInput_is400ValidationErrorOnTheField_andWritesNothing(string json, string field)
    {
        var request = Request(HttpMethod.Post, "/api/v2/rooms");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var (response, body) = await Send(request);

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue($"the errors should name '{field}': {body}");
        (await Rooms.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
        (await RoomAuditEntries()).Should().BeEmpty();
    }

    // ---- 'renames and deactivates with audited before/after'

    [Fact]
    public async Task Patch_renamesAndDeactivates_withTheChangedFieldsAuditedBeforeAndAfter()
    {
        var ids = await SeedRoomsAsync();
        var hal = ids["Hal"].ToString();

        var (response, body) = await Send(Request(HttpMethod.Patch, $"/api/v2/rooms/{hal}", new { name = "Gang", active = false }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("name").GetString().Should().Be("Gang");
        body.GetProperty("active").GetBoolean().Should().BeFalse();
        body.GetProperty("sortOrder").GetInt32().Should().Be(60);
        body.GetProperty("createdAt").GetDateTimeOffset().Should().Be(Now.AddDays(-30));
        body.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(Now);

        var entry = (await RoomAuditEntries()).Should().ContainSingle().Subject;
        entry["action"].AsString.Should().Be("update");
        Same(entry["before"].AsBsonDocument, new BsonDocument { { "name", "Hal" }, { "active", true } });
        Same(entry["after"].AsBsonDocument, new BsonDocument { { "name", "Gang" }, { "active", false } });

        Names((await Get("/api/v2/rooms?active=true")).Body).Should().NotContain("Gang");
    }

    [Fact]
    public async Task Patch_thatChangesNothing_writesAndAuditsNothing_andReturnsTheRoom()
    {
        var ids = await SeedRoomsAsync();
        var hal = ids["Hal"].ToString();

        var same = await Send(Request(HttpMethod.Patch, $"/api/v2/rooms/{hal}", new { name = " Hal ", active = true, sortOrder = 60 }));
        var empty = await Send(Request(HttpMethod.Patch, $"/api/v2/rooms/{hal}", new { }));

        same.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        empty.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        same.Body.GetProperty("name").GetString().Should().Be("Hal");
        same.Body.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(Now.AddDays(-30));
        (await RoomAuditEntries()).Should().BeEmpty();
        var stored = await Rooms.Find(new BsonDocument("_id", ids["Hal"])).SingleAsync(Ct);
        stored["updatedAt"].ToUniversalTime().Should().Be(Now.UtcDateTime.AddDays(-30));
    }

    [Theory]
    [InlineData("""{"name":""}""", "name")]
    [InlineData("""{"name":null}""", "name")]
    [InlineData("""{"active":"no"}""", "active")]
    [InlineData("""{"sortOrder":2.5}""", "sortOrder")]
    [InlineData("""{"virtual":1}""", "virtual")]
    [InlineData("""nope""", "body")]
    public async Task Patch_withInvalidInput_is400ValidationErrorOnTheField_andChangesNothing(string json, string field)
    {
        var ids = await SeedRoomsAsync();
        var request = Request(HttpMethod.Patch, $"/api/v2/rooms/{ids["Hal"]}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var (response, body) = await Send(request);

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        (await RoomAuditEntries()).Should().BeEmpty();
    }

    // ---- 'returns 404 for an unknown room'

    [Fact]
    public async Task Patch_ofAnUnknownRoom_is404NotFound()
    {
        var (response, body) = await Send(Request(HttpMethod.Patch, "/api/v2/rooms/0123456789abcdef01234567", new { name = "X" }));

        ShouldBeProblem(response, body, HttpStatusCode.NotFound, "not_found");
        (await RoomAuditEntries()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Write_withAMalformedId_is400ValidationErrorOnId(string method)
    {
        var (response, body) = await Send(Request(new HttpMethod(method), "/api/v2/rooms/not-an-id", method == "PATCH" ? new { name = "X" } : null));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").TryGetProperty("id", out _).Should().BeTrue();
    }

    // ---- 'deletes an empty room and refuses while active or inactive tasks still use it'

    [Fact]
    public async Task Delete_removesAnEmptyRoom_andAuditsTheDeletedFields()
    {
        var created = await Send(Request(HttpMethod.Post, "/api/v2/rooms", new { name = "Lege kamer" }));
        var id = created.Body.GetProperty("id").GetString()!;

        var (response, body) = await Send(Request(HttpMethod.Delete, $"/api/v2/rooms/{id}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        (await Rooms.CountDocumentsAsync(new BsonDocument("_id", ObjectId.Parse(id)), cancellationToken: Ct)).Should().Be(0);
        var entries = await RoomAuditEntries();
        entries.Select(e => e["action"].AsString).Should().Equal("create", "delete");
        Same(entries[1]["before"].AsBsonDocument, new BsonDocument { { "name", "Lege kamer" }, { "sortOrder", 10 }, { "active", true }, { "virtual", false } });
        entries[1]["after"].AsBsonDocument.ElementCount.Should().Be(0);
    }

    [Fact]
    public async Task Delete_isRefusedWithRoomInUse_whileAnInactiveTaskStillUsesTheRoom()
    {
        var created = await Send(Request(HttpMethod.Post, "/api/v2/rooms", new { name = "Gebruikte kamer" }));
        var id = created.Body.GetProperty("id").GetString()!;
        // The task domain does not exist yet: a task is a document of the tasks collection, here an inactive one.
        await Tasks.InsertOneAsync(new BsonDocument { { "name", "Klus" }, { "roomId", ObjectId.Parse(id) }, { "intervalKey", "1w" }, { "durationMinutes", 5 }, { "active", false } }, cancellationToken: Ct);

        var (response, body) = await Send(Request(HttpMethod.Delete, $"/api/v2/rooms/{id}"));

        ShouldBeProblem(response, body, HttpStatusCode.Conflict, "room_in_use");
        body.GetProperty("taskCount").GetInt32().Should().Be(1);
        body.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
        (await Rooms.CountDocumentsAsync(new BsonDocument("_id", ObjectId.Parse(id)), cancellationToken: Ct)).Should().Be(1);
        (await RoomAuditEntries()).Select(e => e["action"].AsString).Should().Equal("create");
    }

    [Fact]
    public async Task Delete_countsOnlyTheTasksOfThatRoom_activeAndInactive()
    {
        var ids = await SeedRoomsAsync();
        await Tasks.InsertManyAsync(
            [
                new BsonDocument { { "name", "A" }, { "roomId", ids["Keuken"] }, { "active", true } },
                new BsonDocument { { "name", "B" }, { "roomId", ids["Keuken"] }, { "active", false } },
                new BsonDocument { { "name", "C" }, { "roomId", ids["Keuken"] }, { "active", true } },
                new BsonDocument { { "name", "D" }, { "roomId", ids["Toilet"] }, { "active", true } },
            ],
            cancellationToken: Ct);

        var (_, kitchen) = await Send(Request(HttpMethod.Delete, $"/api/v2/rooms/{ids["Keuken"]}"));
        var (_, hall) = await Send(Request(HttpMethod.Delete, $"/api/v2/rooms/{ids["Hal"]}"));

        kitchen.GetProperty("taskCount").GetInt32().Should().Be(3);
        hall.GetProperty("deleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Delete_ofAnUnknownRoom_is404NotFound()
    {
        var (response, body) = await Send(Request(HttpMethod.Delete, "/api/v2/rooms/0123456789abcdef01234567"));

        ShouldBeProblem(response, body, HttpStatusCode.NotFound, "not_found");
    }

    // ---- roles and profile (Node: requireAdmin on POST, PATCH and DELETE)

    [Theory]
    [InlineData(Role.Member, "POST")]
    [InlineData(Role.Member, "PATCH")]
    [InlineData(Role.Member, "DELETE")]
    [InlineData(Role.Planner, "POST")]
    [InlineData(Role.Planner, "PATCH")]
    [InlineData(Role.Planner, "DELETE")]
    public async Task Writes_needAnAdministrator_otherRolesGet403PermissionDenied_andNothingChanges(Role role, string method)
    {
        var ids = await SeedRoomsAsync();
        var profile = users.Add(role);
        var url = method == "POST" ? "/api/v2/rooms" : $"/api/v2/rooms/{ids["Hal"]}";

        var (response, body) = await Send(Request(new HttpMethod(method), url, method == "DELETE" ? null : new { name = "Nieuw" }, profile.Id));

        ShouldBeProblem(response, body, HttpStatusCode.Forbidden, "permission_denied");
        (await Rooms.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(7);
        (await RoomAuditEntries()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Writes_withoutAProfile_are400ProfileRequired(string method)
    {
        var ids = await SeedRoomsAsync();
        var url = method == "POST" ? "/api/v2/rooms" : $"/api/v2/rooms/{ids["Hal"]}";

        var (response, body) = await Send(Request(new HttpMethod(method), url, method == "DELETE" ? null : new { name = "Nieuw" }, noProfile: true));

        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public void EveryRoomWrite_requiresTheAdminPolicy_andTheReadRequiresNone()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v2/rooms", StringComparison.Ordinal) == true)
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
                policies.Should().BeEquivalentTo([AuthorizationPolicies.AdminPolicy], $"{string.Join(",", methods)} {endpoint.RoutePattern.RawText} needs an administrator");
            }
        }
    }

    // ---- transactions: the room and its audit entry commit or fail together

    [Fact]
    public async Task Create_whenTheAuditEntryCannotBeWritten_answers500_andLeavesNoRoomBehind()
    {
        using var failing = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(users)
            .WithPort<ForRecordingAudit>(new FailingAudit());
        using var failingClient = failing.CreateClient();
        using var request = Request(HttpMethod.Post, "/api/v2/rooms", new { name = "Zolder" });

        var response = await failingClient.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("audit down");
        (await Rooms.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    private sealed class FailingAudit : ForRecordingAudit
    {
        public Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult<OneOf<Success, PortError>>(new PortError("audit down"));
    }
}
