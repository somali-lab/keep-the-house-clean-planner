using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/audit-api.test.ts (order, filters, date range, stable paging, validation, occurrence context, clear)
/// onto <c>/api/v2/audit</c>, with entries stored in the shape the Node server writes. Real HTTP pipeline and real MongoDB
/// replica set; one database per test.
/// </summary>
public sealed class AuditEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private static readonly ObjectId SystemActor = ObjectId.Parse("000000000000000000000000");

    private readonly FakeUserDirectory users = new();
    private readonly UserIdentity admin;
    private readonly UserIdentity planner;
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    private readonly IMongoDatabase database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AuditEndpointTests(MongoContainerFixture mongo)
    {
        admin = users.Add(Role.Admin);
        planner = users.Add(Role.Planner);
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

    private IMongoCollection<BsonDocument> AuditLog => database.GetCollection<BsonDocument>("auditLog");

    private async Task<ObjectId> Insert(
        DateTimeOffset at,
        string entity = "room",
        ObjectId? entityId = null,
        string action = "update",
        ObjectId? actorId = null,
        string source = "ui",
        BsonDocument? before = null,
        BsonDocument? after = null,
        BsonDocument? meta = null)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            { "_id", id },
            { "at", new BsonDateTime(at.UtcDateTime) },
            { "actorId", actorId ?? ObjectId.Parse(admin.Id) },
            { "entity", entity },
            { "entityId", entityId ?? ObjectId.GenerateNewId() },
            { "action", action },
            { "before", before ?? new BsonDocument() },
            { "after", after ?? new BsonDocument() },
            { "source", source },
        };
        if (meta is not null)
        {
            document.Add("meta", meta);
        }

        await AuditLog.InsertOneAsync(document, cancellationToken: Ct);
        return id;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? profileId = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (profileId is not null)
        {
            request.Headers.Add("X-Profile-Id", profileId);
            request.Headers.Add("X-Client", "web");
        }

        return request;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Send(HttpRequestMessage request)
    {
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpResponseMessage Response, JsonElement Body)> Get(string query = "") =>
        Send(Request(HttpMethod.Get, "/api/v2/audit" + (query.Length == 0 ? string.Empty : "?" + query)));

    private async Task<JsonElement> List(string query = "")
    {
        var (response, body) = await Get(query);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private static string[] Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    private static string[] Field(JsonElement page, string field) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).GetString()!)];

    // ---- 'returns entries newest first'

    [Fact]
    public async Task List_returnsEntriesNewestFirst_withoutAProfile()
    {
        await Insert(Now.AddDays(-2), "room", action: "create");
        await Insert(Now, "user", after: new BsonDocument("name", "Bram"));
        await Insert(Now.AddDays(-1), "room");

        var page = await List("limit=200");

        Field(page, "at").Select(DateTimeOffset.Parse).Should().BeInDescendingOrder();
        page.GetProperty("items")[0].GetProperty("entity").GetString().Should().Be("user");
        page.GetProperty("items")[0].GetProperty("after").GetProperty("name").GetString().Should().Be("Bram");
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_inAnEmptyLog_isAnEmptyPage()
    {
        var page = await List();

        page.GetProperty("items").GetArrayLength().Should().Be(0);
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_showsAnEntryTheWayTheNodeServerWroteIt()
    {
        var entityId = ObjectId.GenerateNewId();
        var related = ObjectId.GenerateNewId();
        var moment = new DateTime(2026, 9, 18, 7, 30, 15, 250, DateTimeKind.Utc);
        var id = await Insert(
            new DateTimeOffset(moment),
            "task",
            entityId,
            "create",
            source: "ai",
            before: new BsonDocument(),
            after: new BsonDocument
            {
                { "name", "Afwassen" },
                { "minutes", 15 },
                { "weight", 1.5 },
                { "points", 0.0 },
                { "roomId", related },
                { "lastCompletedAt", new BsonDateTime(moment) },
                { "tags", new BsonArray { "a", 2, BsonNull.Value } },
                { "nested", new BsonDocument("deep", true) },
                { "gone", BsonNull.Value },
                { "big", 9_000_000_000L },
            },
            meta: new BsonDocument("reason", "complete"));

        var item = (await List()).GetProperty("items")[0];

        item.GetProperty("id").GetString().Should().Be(id.ToString());
        item.GetProperty("at").GetDateTimeOffset().Should().Be(new DateTimeOffset(moment));
        item.GetProperty("actorId").GetString().Should().Be(admin.Id);
        item.GetProperty("entity").GetString().Should().Be("task");
        item.GetProperty("entityId").GetString().Should().Be(entityId.ToString());
        item.GetProperty("action").GetString().Should().Be("create");
        item.GetProperty("source").GetString().Should().Be("ai");
        item.GetProperty("before").EnumerateObject().Should().BeEmpty();
        item.GetProperty("meta").GetProperty("reason").GetString().Should().Be("complete");
        var after = item.GetProperty("after");
        after.GetProperty("name").GetString().Should().Be("Afwassen");
        after.GetProperty("minutes").GetInt32().Should().Be(15);
        after.GetProperty("weight").GetDouble().Should().Be(1.5);
        after.GetProperty("points").GetInt32().Should().Be(0);
        after.GetProperty("roomId").GetString().Should().Be(related.ToString());
        after.GetProperty("lastCompletedAt").GetString().Should().Be("2026-09-18T07:30:15.250Z");
        after.GetProperty("tags").EnumerateArray().Select(e => e.ValueKind).Should().Equal(JsonValueKind.String, JsonValueKind.Number, JsonValueKind.Null);
        after.GetProperty("nested").GetProperty("deep").GetBoolean().Should().BeTrue();
        after.GetProperty("gone").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("big").GetInt64().Should().Be(9_000_000_000L);
    }

    [Fact]
    public async Task List_showsEntriesOfTheLegacyAiApplyAction()
    {
        await Insert(Now, "cyclePlan", action: "ai-apply", source: "ai");

        (await List()).GetProperty("items")[0].GetProperty("action").GetString().Should().Be("ai-apply");
    }

    // ---- 'filters by entity and entityId (history of one entity)'

    [Fact]
    public async Task List_filtersByEntityAndEntityId_theHistoryOfOneEntity()
    {
        var room = ObjectId.GenerateNewId();
        await Insert(Now.AddDays(-2), "room", room, "create", after: new BsonDocument("name", "Zolder"));
        await Insert(Now.AddDays(-1), "room", room, "update", before: new BsonDocument("name", "Zolder"), after: new BsonDocument("name", "Vliering"));
        await Insert(Now, "room");
        await Insert(Now, "user", room);

        var page = await List($"entity=room&entityId={room}");

        Field(page, "action").Should().Equal("update", "create");
        page.GetProperty("items")[0].GetProperty("before").GetProperty("name").GetString().Should().Be("Zolder");
    }

    [Fact]
    public async Task List_filtersByEntityAlone_andByEntityIdWithUppercaseHex()
    {
        var entityId = ObjectId.GenerateNewId();
        await Insert(Now, "room", entityId);
        await Insert(Now, "user");

        Field(await List("entity=user"), "entity").Should().Equal("user");
        (await List($"entityId={entityId.ToString().ToUpperInvariant()}")).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---- 'filters by actor and source'

    [Fact]
    public async Task List_filtersByActorAndSource()
    {
        var other = ObjectId.Parse(planner.Id);
        await Insert(Now.AddMinutes(-3), "room", actorId: SystemActor, source: "system");
        await Insert(Now.AddMinutes(-2), "room", actorId: other, source: "api");
        await Insert(Now.AddMinutes(-1), "user", actorId: other, source: "ui");

        var byOther = await List($"actorId={planner.Id}");
        Field(byOther, "entity").Should().Equal("user", "room");
        Field(byOther, "source").Should().Equal("ui", "api");

        var system = await List("source=system&limit=200");
        Field(system, "actorId").Should().OnlyContain(a => a == "000000000000000000000000").And.HaveCount(1);

        Field(await List("source=api"), "entity").Should().Equal("room");
        Field(await List($"source=ui&actorId={admin.Id}"), "entity").Should().BeEmpty();
    }

    // ---- 'filters by date range (inclusive)'

    [Fact]
    public async Task List_filtersByDateRange_bothEndsInclusive()
    {
        var day17 = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var day18 = new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
        await Insert(day17, "room", action: "create");
        await Insert(day18, "room");
        await Insert(day18, "user");
        await Insert(day18.AddDays(1), "task");

        Field(await List("from=2026-09-17T00:00:00.000Z&to=2026-09-17T23:59:59.999Z"), "action").Should().Equal("create");
        (await List("from=2026-09-18T08:00:00.000Z&to=2026-09-18T08:00:00.000Z")).GetProperty("items").GetArrayLength().Should().Be(2);
        (await List("from=2026-09-18T08:00:00.001Z&to=2026-09-18T08:00:00.000Z")).GetProperty("items").GetArrayLength().Should().Be(0);
        Field(await List("to=2026-09-17T08:00:00.000Z"), "action").Should().Equal("create");
        (await List("from=2026-09-18T10:00:00%2B02:00&to=2026-09-18T10:00:00%2B02:00")).GetProperty("items").GetArrayLength().Should().Be(2);
    }

    // ---- 'paginates with a stable cursor, even when entries share a timestamp'

    [Fact]
    public async Task List_paginatesWithAStableCursor_evenWhenEntriesShareATimestamp()
    {
        for (var i = 0; i < 8; i++)
        {
            await Insert(i < 5 ? Now : Now.AddMinutes(-i));
        }

        var all = Ids(await List("limit=200"));
        all.Should().HaveCount(8);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await List("limit=3" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            page.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(3);
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 50);

        pages.Should().Be(3);
        seen.Should().Equal(all);
        seen.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task List_hasNoCursorOnTheLastPage_evenWhenThatPageIsFull()
    {
        var room = ObjectId.GenerateNewId();
        await Insert(Now, "room", room, "create");
        await Insert(Now.AddMinutes(1), "room", room);
        await Insert(Now, "user");

        var page = await List($"entity=room&entityId={room}&limit=2");

        page.GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_pagesWithAFilter_andAcceptsACursorOfTheNodeServer()
    {
        for (var i = 0; i < 4; i++)
        {
            await Insert(Now.AddMinutes(-i), "room");
        }

        var first = await List("entity=room&limit=2");
        var last = first.GetProperty("items")[1];
        var nodeCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(last.GetProperty("at").GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "|" + last.GetProperty("id").GetString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        first.GetProperty("nextCursor").GetString().Should().Be(nodeCursor);
        Ids(await List("entity=room&limit=2&cursor=" + nodeCursor)).Should().HaveCount(2).And.NotIntersectWith(Ids(first));
    }

    // ---- 'validates query parameters'

    [Theory]
    [InlineData("entity=banana", "entity")]
    [InlineData("entity=Room", "entity")]
    [InlineData("actorId=nope", "actorId")]
    [InlineData("entityId=0123456789abcdef0123456", "entityId")]
    [InlineData("source=origin", "source")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=500", "limit")]
    [InlineData("limit=abc", "limit")]
    [InlineData("limit=-1", "limit")]
    [InlineData("from=yesterday", "from")]
    [InlineData("to=2026-09-18", "to")]
    [InlineData("from=2026-09-18T08:00:00", "from")]
    [InlineData("cursor=bm9wZQ", "cursor")]
    [InlineData("cursor=%%%", "cursor")]
    public async Task List_withAMalformedQueryValue_is400ValidationError_keyedByTheField(string query, string field)
    {
        var (response, body) = await Get(query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(query);
    }

    [Fact]
    public async Task List_withAMalformedCursor_namesInvalidCursor()
    {
        var (_, body) = await Get("cursor=bm9wZQ");

        body.GetProperty("errors").GetProperty("cursor")[0].GetString().Should().Be("invalid_cursor");
    }

    [Fact]
    public async Task List_reportsEveryMalformedValueAtOnce()
    {
        var (_, body) = await Get("entity=banana&source=origin&from=yesterday&limit=abc");

        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("entity", "source", "from", "limit");
    }

    // ---- 'enriches older occurrence entries with task, room and date context'

    [Fact]
    public async Task List_enrichesOlderOccurrenceEntriesWithTaskRoomAndDateContext()
    {
        var occurrenceId = ObjectId.GenerateNewId();
        var date = new DateTime(2026, 9, 18, 22, 0, 0, DateTimeKind.Utc);
        await database.GetCollection<BsonDocument>("occurrences").InsertOneAsync(
            new BsonDocument
            {
                { "_id", occurrenceId },
                { "taskNameSnapshot", "Douche schoonmaken" },
                { "roomNameSnapshot", "Badkamer" },
                { "date", new BsonDateTime(date) },
                { "status", "open" },
            },
            cancellationToken: Ct);
        await Insert(Now, "occurrence", occurrenceId, "uncomplete", before: new BsonDocument("status", "done"), after: new BsonDocument("status", "open"));

        var item = (await List($"entity=occurrence&entityId={occurrenceId}")).GetProperty("items")[0];

        var context = item.GetProperty("meta").GetProperty("occurrence");
        context.GetProperty("taskNameSnapshot").GetString().Should().Be("Douche schoonmaken");
        context.GetProperty("roomNameSnapshot").GetString().Should().Be("Badkamer");
        context.GetProperty("date").GetString().Should().Be("2026-09-18T22:00:00.000Z");
        database.GetCollection<BsonDocument>("auditLog").Find(new BsonDocument("meta", new BsonDocument("$exists", true))).CountDocuments(Ct).Should().Be(0, "the context is added when reading, never stored");
    }

    [Fact]
    public async Task List_keepsTheOtherMetaOfAnEntry_andLeavesEntriesThatCarryTheirOwnContextAlone()
    {
        var withOwn = ObjectId.GenerateNewId();
        var without = ObjectId.GenerateNewId();
        var occurrences = database.GetCollection<BsonDocument>("occurrences");
        await occurrences.InsertManyAsync(
            [
                new BsonDocument { { "_id", withOwn }, { "taskNameSnapshot", "Nieuw" }, { "roomNameSnapshot", BsonNull.Value }, { "date", new BsonDateTime(Now.UtcDateTime) } },
                new BsonDocument { { "_id", without }, { "taskNameSnapshot", "Afwassen" }, { "roomNameSnapshot", BsonNull.Value }, { "date", new BsonDateTime(Now.UtcDateTime) } },
            ],
            cancellationToken: Ct);
        await Insert(Now, "occurrence", withOwn, meta: new BsonDocument("occurrence", new BsonDocument("taskNameSnapshot", "Eigen")));
        await Insert(Now.AddMinutes(-1), "occurrence", without, meta: new BsonDocument("reason", "complete"));
        await Insert(Now.AddMinutes(-2), "occurrence", ObjectId.GenerateNewId());

        var items = (await List()).GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("meta").GetProperty("occurrence").GetProperty("taskNameSnapshot").GetString().Should().Be("Eigen");
        items[1].GetProperty("meta").GetProperty("reason").GetString().Should().Be("complete");
        items[1].GetProperty("meta").GetProperty("occurrence").GetProperty("roomNameSnapshot").ValueKind.Should().Be(JsonValueKind.Null);
        items[2].GetProperty("meta").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- 'requires a profile and clears the complete history'

    [Fact]
    public async Task Clear_withoutAProfile_is400ProfileRequired_andClearsNothing()
    {
        await Insert(Now);

        var (response, body) = await Send(Request(HttpMethod.Delete, "/api/v2/audit"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:profile_required");
        (await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Clear_asAPlanner_is403PermissionDenied_andClearsNothing()
    {
        await Insert(Now);

        var (response, body) = await Send(Request(HttpMethod.Delete, "/api/v2/audit", planner.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:permission_denied");
        (await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Clear_asAnAdministrator_removesTheCompleteHistory_andRecordsNothingAboutIt()
    {
        await Insert(Now.AddDays(-1));
        await Insert(Now);
        await Insert(Now, "user");

        var (response, body) = await Send(Request(HttpMethod.Delete, "/api/v2/audit", admin.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("deleted").GetInt32().Should().Be(3);
        (await List("limit=200")).GetProperty("items").GetArrayLength().Should().Be(0);
        (await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0, "clearing is deliberately not audited");
    }

    [Fact]
    public async Task Clear_ofAnEmptyLog_answersZero()
    {
        var (response, body) = await Send(Request(HttpMethod.Delete, "/api/v2/audit", admin.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("deleted").GetInt32().Should().Be(0);
    }

    // ---- the writes of other slices show up in the history

    [Fact]
    public async Task List_showsTheEntryAWriteOfAnotherSliceRecorded()
    {
        var created = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v2/rooms")
            {
                Headers = { { "X-Profile-Id", admin.Id }, { "X-Client", "web" } },
                Content = JsonContent.Create(new { name = "Zolder" }),
            },
            Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var item = (await List("entity=room")).GetProperty("items")[0];

        item.GetProperty("action").GetString().Should().Be("create");
        item.GetProperty("source").GetString().Should().Be("ui");
        item.GetProperty("actorId").GetString().Should().Be(admin.Id);
        item.GetProperty("at").GetDateTimeOffset().Should().Be(Now);
        item.GetProperty("after").GetProperty("name").GetString().Should().Be("Zolder");
    }

    // ---- policies

    [Fact]
    public void TheEndpoints_readIsOpen_andClearRequiresAnAdministrator()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText == "/api/v2/audit")
            .ToDictionary(
                e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single(),
                e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList());

        endpoints["GET"].Should().BeEmpty();
        endpoints["DELETE"].Should().Equal(AuthorizationPolicies.AdminPolicy);
    }
}
