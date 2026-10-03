using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real host on a real MongoDB replica set with a clock that tests move, for the statistics scenarios (ported from <c>stats.test.ts</c> and
/// <c>stats-reset.test.ts</c>). The occurrence actions (complete, skip, reschedule) are slice 3.2, so state is arranged with direct writes of
/// occurrence documents in the shape the Node server stores them. The startup seeds run like in the application: the settings get the anchor of
/// the clock's day (a clock on Monday 2026-09-14 gives cycle 0 = 14 Sep to 11 Oct, cycle 1 = 12 Oct to 8 Nov), and the two seeded people
/// "Persoon 1" and "Persoon 2" exist.
/// </summary>
public sealed class StatisticsHarness : IDisposable
{
    public const string MondayMorning = "2026-09-14T06:00:00.000Z";

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;

    public StatisticsHarness(MongoContainerFixture mongo, string now = MondayMorning, Func<ApiFactory, ApiFactory>? configure = null)
    {
        Admin = directory.Add(Role.Admin);
        Planner = directory.Add(Role.Planner);
        Member = directory.Add(Role.Member);
        Clock = new FixedTimeProvider(now);
        mongoClient = new MongoClient(mongo.ConnectionString);
        Database = mongoClient.GetDatabase(databaseName);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithPort<ForFindingUsers>(directory).WithPort<TimeProvider>(Clock);
        factory = configure?.Invoke(factory) ?? factory;
        Client = factory.CreateClient();
    }

    public UserIdentity Admin { get; }

    public UserIdentity Planner { get; }

    public UserIdentity Member { get; }

    public FixedTimeProvider Clock { get; }

    public IMongoDatabase Database { get; }

    public HttpClient Client { get; }

    public IMongoCollection<BsonDocument> Occurrences => Database.GetCollection<BsonDocument>("occurrences");

    public IMongoCollection<BsonDocument> Cycles => Database.GetCollection<BsonDocument>("cycles");

    public IMongoCollection<BsonDocument> Tasks => Database.GetCollection<BsonDocument>("tasks");

    public IMongoCollection<BsonDocument> Rooms => Database.GetCollection<BsonDocument>("rooms");

    public IMongoCollection<BsonDocument> Users => Database.GetCollection<BsonDocument>("users");

    public IMongoCollection<BsonDocument> Settings => Database.GetCollection<BsonDocument>("settings");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public IMongoCollection<BsonDocument> PointEntries => Database.GetCollection<BsonDocument>("pointEntries");

    public IServiceProvider Services => factory.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Client.Dispose();
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    // ---- arrange

    /// <summary>The id of a seeded person ("Persoon 1" or "Persoon 2").</summary>
    public async Task<string> PersonAsync(string name) =>
        (await Users.Find(new BsonDocument("name", name)).SingleAsync(Ct))["_id"].AsObjectId.ToString();

    public static BsonDateTime Midnight(string day) => new(DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam).UtcDateTime);

    public static BsonDateTime Instant(string iso) => new(DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);

    public async Task<string> InsertRoomAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var at = Instant(MondayMorning);
        await Rooms.InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }

    public async Task<string> InsertTaskAsync(string name, string roomId, string intervalKey, int minutes, bool active = true, string? lastCompletedAt = null)
    {
        var id = ObjectId.GenerateNewId();
        var at = Instant(MondayMorning);
        await Tasks.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "name", name }, { "roomId", ObjectId.Parse(roomId) }, { "intervalKey", intervalKey },
                { "durationMinutes", minutes }, { "points", minutes }, { "defaultAssigneeId", BsonNull.Value }, { "active", active },
                { "notes", "" }, { "tags", new BsonArray() },
                { "lastCompletedAt", lastCompletedAt is null ? BsonNull.Value : Instant(lastCompletedAt) },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
        return id.ToString();
    }

    public async Task<string> InsertCycleAsync(int index, string start, string end)
    {
        var id = ObjectId.GenerateNewId();
        await Cycles.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "index", index }, { "startDate", start }, { "endDate", end }, { "planId", BsonNull.Value },
                { "generatedAt", Instant(MondayMorning) }, { "generationRunId", "arranged" },
            },
            cancellationToken: Ct);
        return id.ToString();
    }

    /// <summary>
    /// Stores an occurrence like the Node server does. <paramref name="planned"/> is the day of its slot when it was moved. A done occurrence
    /// keeps <c>statusBeforeCompletion</c> open. <paramref name="origin"/> <c>adhoc</c> with a null task is a one-off task.
    /// </summary>
    public async Task<string> InsertOccurrenceAsync(
        string cycleId,
        string? taskId,
        string day,
        string? assignee,
        string status,
        string? completedAt = null,
        string? completedBy = null,
        string? planned = null,
        int minutes = 10,
        string name = "Taak",
        string? roomId = null,
        bool recorded = false,
        string origin = "generated",
        string? skipReason = null,
        int? pointsSnapshot = null)
    {
        var id = ObjectId.GenerateNewId();
        var at = Instant(MondayMorning);
        var document = new BsonDocument
        {
            { "_id", id },
            { "taskId", taskId is null ? BsonNull.Value : ObjectId.Parse(taskId) },
            { "cycleId", ObjectId.Parse(cycleId) },
            { "planId", BsonNull.Value },
            { "date", Midnight(day) },
            { "plannedDate", Midnight(planned ?? day) },
            { "assigneeId", assignee is null ? BsonNull.Value : ObjectId.Parse(assignee) },
            { "status", status },
            { "statusBeforeCompletion", status == "done" ? "open" : BsonNull.Value },
            { "completedAt", completedAt is null ? BsonNull.Value : Instant(completedAt) },
            { "completedBy", completedBy is null ? BsonNull.Value : ObjectId.Parse(completedBy) },
            { "skipReason", skipReason is null ? BsonNull.Value : skipReason },
            { "durationMinutesSnapshot", minutes },
            { "taskNameSnapshot", name },
            { "roomIdSnapshot", roomId is null ? BsonNull.Value : ObjectId.Parse(roomId) },
            { "roomNameSnapshot", BsonNull.Value },
            { "origin", origin },
            { "createdAt", at },
            { "updatedAt", at },
        };
        if (recorded)
        {
            document["recordedDone"] = true;
        }

        if (pointsSnapshot is { } points)
        {
            document["pointsSnapshot"] = points;
        }

        await Occurrences.InsertOneAsync(document, cancellationToken: Ct);
        return id.ToString();
    }

    public async Task InsertPointEntryAsync(string kind, string person, string day, int amount, string? key = null)
    {
        await PointEntries.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", key ?? $"{kind}:{ObjectId.GenerateNewId()}" }, { "kind", kind },
                { "personId", ObjectId.Parse(person) }, { "date", Midnight(day) }, { "amount", amount }, { "createdAt", Instant(MondayMorning) },
            },
            cancellationToken: Ct);
    }

    // ---- act

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, UserIdentity? profile = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (profile is not null)
        {
            request.Headers.Add("X-Profile-Id", profile.Id);
        }

        request.Headers.Add("X-Client", "web");
        using var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>A report read, which needs no profile; it must answer 200.</summary>
    public async Task<JsonElement> GetAsync(string url)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, url);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    // ---- observe

    public async Task<List<BsonDocument>> AuditAsync(string entity, string action) =>
        await AuditLog.Find(new BsonDocument { { "entity", entity }, { "action", action } }).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
}
