using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The household of <c>transfer.test.ts</c> for the JSON export and import: a source with a done occurrence, recorded work (an extra execution and a
/// one-off task), reward goals, a redemption and two badges with images, exported once as a file (<see cref="Export"/>) next to a snapshot of what the
/// database held (<see cref="Snapshot"/>). The badges and their award are written below the application, because the badge endpoints are another slice.
/// The collections of the file, in the order the export writes them, are <see cref="Collections"/>.
/// </summary>
public sealed class TransferWorld : IAsyncLifetime
{
    public const string ExtraKey = "transfer-extra-request-key-0001";

    public const string OneOffKey = "transfer-one-off-request-key-0001";

    public const string Wednesday = "2026-09-16T08:00:00.000Z";

    public static readonly string[] Collections = ["settings", "users", "rooms", "tasks", "cyclePlans", "cycles", "occurrences", "pointEntries", "badges", "auditLog"];

    public static readonly byte[] PngBytes = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4, 5, 6, 7, 8];

    public static readonly byte[] JpegBytes = [0xff, 0xd8, 0xff, 0xe0, 9, 8, 7, 6, 5, 4];

    public static readonly byte[] SvgBytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

    private readonly MongoContainerFixture mongo;

    public TransferWorld(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        Source = new AuditCoverageHarness(mongo);
    }

    public MongoContainerFixture Mongo => mongo;

    public AuditCoverageHarness Source { get; }

    /// <summary>The exported file of the source, as it came over the wire.</summary>
    public JsonObject Export { get; private set; } = null!;

    /// <summary>The raw documents of every transfer collection of the source, ordered by id, at the moment of the export.</summary>
    public Dictionary<string, List<BsonDocument>> Snapshot { get; private set; } = null!;

    public string BadgeTaskId => Source.Weekly;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Source.InitializeAsync();
        var h = Source;
        var monday = await h.OccurrenceIdAsync(h.Weekly, "2026-09-14");
        await Ok(h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{monday}/complete", null, h.P1));
        await Ok(h.SendAsync(HttpMethod.Patch, "/api/v2/settings", new { rewardGoals = new { weekPoints = 12, cyclePoints = (int?)null } }, h.Admin));
        await Ok(h.SendAsync(HttpMethod.Post, "/api/v2/occurrences", new { taskId = h.Weekly, date = "2026-09-16", done = true, requestId = ExtraKey }, h.P1));
        await Ok(h.SendAsync(HttpMethod.Post, "/api/v2/occurrences/one-off", new { name = "Gordijnen ophangen", roomId = h.Room, durationMinutes = 40, date = "2026-09-16", done = true, requestId = OneOffKey }, h.P1));
        await Ok(h.SendAsync(HttpMethod.Post, "/api/v2/points/redemptions", new { points = 3, note = "Pizza", requestId = "transfer-redemption-key-0001" }, h.P1));
        await InsertBadgesAsync(h.Database, h.Weekly, h.P1.Id);

        var response = await h.Client.SendAsync(AsAdmin(h.Admin), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Export = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        Snapshot = await SnapshotOfAsync(h.Database);
    }

    private static HttpRequestMessage AsAdmin(UserIdentity admin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/export/json");
        request.Headers.Add("X-Profile-Id", admin.Id);
        return request;
    }

    public async ValueTask DisposeAsync() => await Source.DisposeAsync();

    /// <summary>A copy of the file that a test may mutate.</summary>
    public JsonObject Clone() => (JsonObject)Export.DeepClone();

    public static JsonArray Docs(JsonObject file, string collection) => file["collections"]![collection]!.AsArray();

    public static JsonObject Doc(JsonObject file, string collection, int index) => Docs(file, collection)[index]!.AsObject();

    /// <summary>A version-5 file predates the badges (ADR-0014).</summary>
    public JsonObject AsVersion5()
    {
        var file = Clone();
        file["schemaVersion"] = 5;
        file["collections"]!.AsObject().Remove("badges");
        return file;
    }

    /// <summary>A version-4 file predates the redemptions and the badges.</summary>
    public JsonObject AsVersion4()
    {
        var file = AsVersion5();
        file["schemaVersion"] = 4;
        file["collections"]!.AsObject().Remove("pointEntries");
        return file;
    }

    /// <summary>A version-1 file predates recordedDone, requestId and points; strip them to get one from a current export.</summary>
    public JsonObject AsVersion1()
    {
        var file = AsVersion4();
        file["schemaVersion"] = 1;
        foreach (var task in Docs(file, "tasks"))
        {
            task!.AsObject().Remove("points");
        }

        foreach (var occurrence in Docs(file, "occurrences"))
        {
            var o = occurrence!.AsObject();
            o.Remove("recordedDone");
            o.Remove("requestId");
            o.Remove("pointsSnapshot");
        }

        return file;
    }

    public static StringContent Json(JsonNode file) => new(file.ToJsonString(), Encoding.UTF8, "application/json");

    public static async Task<Dictionary<string, List<BsonDocument>>> SnapshotOfAsync(IMongoDatabase database)
    {
        var all = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in Collections)
        {
            var filter = name == "pointEntries" ? new BsonDocument("kind", "redemption") : [];
            all[name] = await database.GetCollection<BsonDocument>(name).Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
        }

        return all;
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static BsonDocument BadgeImage(byte[] bytes, string contentType) => new()
    {
        { "data", new BsonBinaryData(bytes) },
        { "contentType", contentType },
        { "size", bytes.Length },
        { "hash", Hex(bytes) },
    };

    /// <summary>The two badges of the Node test, and the award the first one earned, written below the application.</summary>
    private static async Task InsertBadgesAsync(IMongoDatabase database, string taskId, string personId)
    {
        var at = new BsonDateTime(DateTime.Parse(Wednesday, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        var first = ObjectId.GenerateNewId();
        var second = ObjectId.GenerateNewId();
        await database.GetCollection<BsonDocument>("badges").InsertManyAsync(
            [
                new BsonDocument
                {
                    { "_id", first }, { "name", "Aanrechtheld" }, { "description", "Het aanrecht gedaan" },
                    { "rule", new BsonDocument { { "type", "executions" }, { "taskIds", new BsonArray { ObjectId.Parse(taskId) } }, { "threshold", 1 } } },
                    { "active", true }, { "exampleKey", BsonNull.Value }, { "image", BadgeImage(PngBytes, "image/png") }, { "createdAt", at }, { "updatedAt", at },
                },
                new BsonDocument
                {
                    { "_id", second }, { "name", "Alles-doener" }, { "description", string.Empty },
                    { "rule", new BsonDocument { { "type", "minutes" }, { "taskIds", new BsonArray() }, { "threshold", 5 } } },
                    { "active", false }, { "exampleKey", BsonNull.Value }, { "image", BadgeImage(JpegBytes, "image/jpeg") }, { "createdAt", at }, { "updatedAt", at },
                },
            ],
            cancellationToken: Ct);
        await InsertAwardAsync(database, first, personId);
    }

    public static async Task InsertAwardAsync(IMongoDatabase database, ObjectId badgeId, string personId)
    {
        var at = new BsonDateTime(DateTime.Parse(Wednesday, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        await database.GetCollection<BsonDocument>("badgeAwards").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", $"badge:{badgeId}:{personId}" }, { "badgeId", badgeId },
                { "personId", ObjectId.Parse(personId) }, { "awardedAt", at }, { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
    }

    private static async Task Ok(Task<(HttpStatusCode Status, JsonElement Body)> request)
    {
        var (status, body) = await request;
        ((int)status).Should().BeLessThan(300, body.ToString());
    }
}

/// <summary>
/// A second application on its own database that a test imports into: its own people (an administrator, a planner and a member known to the
/// profile directory), a clock on the day of the export, and the write capture that proves what a request wrote.
/// </summary>
public sealed class TransferTarget : IDisposable
{
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient client;
    private readonly ApiFactory factory;

    public TransferTarget(MongoContainerFixture mongo, bool seeded = false)
    {
        var directory = new FakeUserDirectory();
        Admin = directory.Add(Role.Admin);
        Planner = directory.Add(Role.Planner);
        Member = directory.Add(Role.Member);
        client = new MongoClient(mongo.ConnectionString);
        Database = client.GetDatabase(databaseName);
        factory = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<TimeProvider>(new FixedTimeProvider(TransferWorld.Wednesday))
            .WithWriteCapture(Capture);
        if (!seeded)
        {
            factory = factory.WithoutSeeding();
        }

        Client = factory.CreateClient();
    }

    public UserIdentity Admin { get; }

    public UserIdentity Planner { get; }

    public UserIdentity Member { get; }

    public WriteCapture Capture { get; } = new();

    public IMongoDatabase Database { get; }

    public HttpClient Client { get; }

    public const string ImportUrl = "/api/v2/import/json?mode=replace&confirm=true";

    public async Task<(HttpStatusCode Status, JsonElement Body)> ImportAsync(JsonNode file, string query = "", UserIdentity? actor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ImportUrl + query) { Content = TransferWorld.Json(file) };
        request.Headers.Add("X-Profile-Id", (actor ?? Admin).Id);
        request.Headers.Add("X-Client", "web");
        return await SendAsync(request);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpRequestMessage request)
    {
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public async Task<JsonObject> ExportAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/export/json");
        request.Headers.Add("X-Profile-Id", Admin.Id);
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
    }

    public void Dispose()
    {
        Client.Dispose();
        factory.Dispose();
        client.DropDatabase(databaseName);
        client.Dispose();
    }
}
