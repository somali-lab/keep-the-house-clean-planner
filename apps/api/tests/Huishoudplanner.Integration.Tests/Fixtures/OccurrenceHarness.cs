using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real host on a real MongoDB replica set with a clock that tests move, for the occurrence scenarios of <c>occurrences.test.ts</c>,
/// <c>completion-choice.test.ts</c> and <c>reschedule.test.ts</c>. The household of the Node tests: two people (Persoon 2 cannot do Tuesdays),
/// an administrator, a planner, two tasks and a plan whose slots produce the occurrences the scenarios use. The first start seeds the settings
/// on Monday 2026-09-14 (the anchor); the nightly run generates cycle 0 (14 Sep to 11 Oct) and cycle 1 (12 Oct to 8 Nov); then the clock
/// moves to Wednesday 2026-09-16 10:00 Amsterdam. One harness serves a whole test class, so every scenario uses occurrences of its own.
/// </summary>
public sealed class OccurrenceHarness : IAsyncLifetime
{
    public const string Monday = "2026-09-14T06:00:00.000Z";
    public const string Wednesday = "2026-09-16T08:00:00.000Z";

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private MongoClient? mongoClient;
    private ApiFactory? factory;

    public OccurrenceHarness(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        Clock = new FixedTimeProvider(Monday);
    }

    public FixedTimeProvider Clock { get; }

    public UserIdentity P1 { get; private set; } = null!;

    public UserIdentity P2 { get; private set; } = null!;

    public UserIdentity Admin { get; private set; } = null!;

    public UserIdentity Planner { get; private set; } = null!;

    public string Room { get; private set; } = null!;

    public string Weekly { get; private set; } = null!;

    public string Twice { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IMongoDatabase Database => mongoClient!.GetDatabase(databaseName);

    public IMongoCollection<BsonDocument> Occurrences => Database.GetCollection<BsonDocument>("occurrences");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public IMongoCollection<BsonDocument> Tasks => Database.GetCollection<BsonDocument>("tasks");

    public IServiceProvider Services => factory!.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        P1 = directory.Add(Role.Member);
        P2 = directory.Add(Role.Member);
        Admin = directory.Add(Role.Admin);
        Planner = directory.Add(Role.Planner);
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithPort<ForFindingUsers>(directory).WithPort<TimeProvider>(Clock);
        Client = factory.CreateClient();

        await SeedPersonAsync(P1, "Persoon 1", []);
        await SeedPersonAsync(P2, "Persoon 2", [2]);
        await SeedPersonAsync(Admin, "Beheerder", []);
        Room = await SeedRoomAsync("Badkamer");
        Weekly = await NewTaskAsync("Badkamer schoonmaken", "1w", 30);
        Twice = await NewTaskAsync("Wastafel", "2w", 10);
        var plan = (await SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, null)).Body.GetProperty("id").GetString()!;
        var slots = new List<object>();
        foreach (var week in Enumerable.Range(0, 4))
        {
            slots.Add(new { taskId = Weekly, weekIndex = week, weekday = 1, assigneeId = P1.Id });
            slots.Add(new { taskId = Twice, weekIndex = week, weekday = 3, assigneeId = (string?)null });
            slots.Add(new { taskId = Twice, weekIndex = week, weekday = 4, assigneeId = P2.Id });
        }

        slots.Add(new { taskId = Weekly, weekIndex = 0, weekday = 3, assigneeId = P1.Id });
        var put = await SendAsync(HttpMethod.Put, $"/api/v2/cycle-plans/{plan}/slots", new { slots }, Planner);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateUpcomingAsync(AuditActor.System, "occurrence-harness", Ct);
            run.IsT0.Should().BeTrue();
        }

        Clock.Set(Wednesday);
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        factory?.Dispose();
        mongoClient?.DropDatabase(databaseName);
        mongoClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- arrange

    private async Task SeedPersonAsync(UserIdentity identity, string name, int[] unavailable)
    {
        var at = new BsonDateTime(DateTime.Parse(Monday, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.Parse(identity.Id) }, { "name", name }, { "color", "#336699" }, { "active", true },
                { "role", identity == Admin ? "admin" : "member" },
                { "unavailableWeekdays", new BsonArray(unavailable) },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 600 }, { "weekend", 1200 } } },
                { "maxDailyMinutes", new BsonDocument { { "weekday", 1200 }, { "weekend", 2400 } } },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
    }

    private async Task<string> SeedRoomAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(DateTime.Parse(Monday, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }

    /// <summary>A new active task in the household's room, made through the API as the planner; the optional default assignee and points are part of the request.</summary>
    public async Task<string> NewTaskAsync(string name, string intervalKey, int minutes, string? defaultAssigneeId = null, int? points = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["roomId"] = Room, ["intervalKey"] = intervalKey, ["durationMinutes"] = minutes };
        if (defaultAssigneeId is not null)
        {
            body["defaultAssigneeId"] = defaultAssigneeId;
        }

        if (points is not null)
        {
            body["points"] = points;
        }

        var response = await SendAsync(HttpMethod.Post, "/api/v2/tasks", body, Planner);
        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        return response.Body.GetProperty("id").GetString()!;
    }

    // ---- act

    /// <summary>Sends a request as <paramref name="actor"/> (no profile header when <see langword="null"/>); a <see langword="null"/> body sends none.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object? body, UserIdentity? actor)
    {
        using var request = new HttpRequestMessage(method, url);
        if (actor is not null)
        {
            request.Headers.Add("X-Profile-Id", actor.Id);
        }

        request.Headers.Add("X-Client", "web");
        if (body is not null)
        {
            request.Content = body is string raw ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        using var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>The occurrence of a task on a day, from the list endpoint.</summary>
    public async Task<JsonElement> FindAsync(string taskId, string day)
    {
        var list = await SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from={day}&to={day}", null, null);
        list.Status.Should().Be(HttpStatusCode.OK, list.Body.ToString());
        return list.Body.GetProperty("items").EnumerateArray().First(o => o.GetProperty("taskId").GetString() == taskId);
    }

    public async Task<string> IdOfAsync(string taskId, string day) => (await FindAsync(taskId, day)).GetProperty("id").GetString()!;

    // ---- observe

    public async Task<List<BsonDocument>> AuditOfAsync(string entity, string? entityId = null, string? action = null)
    {
        var filter = new BsonDocument("entity", entity);
        if (entityId is not null)
        {
            filter.Add("entityId", ObjectId.Parse(entityId));
        }

        if (action is not null)
        {
            filter.Add("action", action);
        }

        return await AuditLog.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
    }

    public async Task<long> AuditCountAsync() => await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    public async Task<BsonDocument> StoredAsync(string occurrenceId) =>
        await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(occurrenceId))).SingleAsync(Ct);

    public async Task<BsonValue> LastCompletedAtAsync(string taskId) =>
        (await Tasks.Find(new BsonDocument("_id", ObjectId.Parse(taskId))).SingleAsync(Ct))["lastCompletedAt"];
}
