using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real host on a real MongoDB replica set with a clock that tests move, for the scenarios that need tasks, a plan and generated
/// occurrences (ported from <c>generation.test.ts</c>, <c>cycles-api.test.ts</c> and <c>interval-change.test.ts</c>). The first start seeds
/// the settings on the clock's day, so a clock on Monday 2026-09-14 gives the anchor 2026-09-14: cycle 0 is 14 Sep to 11 Oct and cycle 1 is
/// 12 Oct to 8 Nov (it contains the end of DST on 25 Oct). Generation runs through <see cref="IGenerationService"/> because the nightly and
/// manual job endpoints sit behind the same use cases.
/// </summary>
public sealed class GenerationHarness : IDisposable
{
    public const string MondayMorning = "2026-09-14T06:00:00.000Z";

    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;

    public GenerationHarness(MongoContainerFixture mongo, string now = MondayMorning, ForSendingNotifications? notifier = null)
    {
        Planner = directory.Add(Role.Planner);
        Admin = directory.Add(Role.Admin);
        Clock = new FixedTimeProvider(now);
        mongoClient = new MongoClient(mongo.ConnectionString);
        Database = mongoClient.GetDatabase(databaseName);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithPort<ForFindingUsers>(directory).WithPort<TimeProvider>(Clock);
        if (notifier is not null)
        {
            factory.WithPort(notifier);
        }
        Client = factory.CreateClient();
    }

    public UserIdentity Planner { get; }

    public UserIdentity Admin { get; }

    public FixedTimeProvider Clock { get; }

    public IMongoDatabase Database { get; }

    public HttpClient Client { get; }

    public IMongoCollection<BsonDocument> Occurrences => Database.GetCollection<BsonDocument>("occurrences");

    public IMongoCollection<BsonDocument> Cycles => Database.GetCollection<BsonDocument>("cycles");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public IMongoCollection<BsonDocument> Tasks => Database.GetCollection<BsonDocument>("tasks");

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

    public async Task<string> SeedRoomAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(Clock.GetUtcNow().UtcDateTime.AddDays(-30));
        await Database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }

    public async Task<string> SeedPersonAsync(string name, int[]? unavailable = null)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(Clock.GetUtcNow().UtcDateTime.AddDays(-30));
        await Database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "name", name }, { "color", "#336699" }, { "active", true }, { "role", "member" },
                { "unavailableWeekdays", new BsonArray(unavailable ?? []) },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 600 }, { "weekend", 1200 } } },
                { "maxDailyMinutes", new BsonDocument { { "weekday", 1200 }, { "weekend", 2400 } } },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
        return id.ToString();
    }

    public async Task<string> NewTaskAsync(string name, string roomId, string intervalKey, int minutes = 10)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v2/tasks", new { name, roomId, intervalKey, durationMinutes = minutes });
        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        return response.Body.GetProperty("id").GetString()!;
    }

    public async Task<string> ActivePlanIdAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, withProfile: false);
        return response.Body.GetProperty("id").GetString()!;
    }

    /// <summary>Saves slots through the API, which synchronises the upcoming occurrences when the plan is the active one.</summary>
    public async Task<JsonElement> PutSlotsAsync(string planId, params (string Task, int Week, int Weekday, string? Assignee)[] slots)
    {
        var response = await SendAsync(
            HttpMethod.Put,
            $"/api/v2/cycle-plans/{planId}/slots",
            new { slots = slots.Select(s => new { taskId = s.Task, weekIndex = s.Week, weekday = s.Weekday, assigneeId = s.Assignee }).ToArray() });
        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        return response.Body;
    }

    /// <summary>Stores slots without synchronising, like a plan edited before the synchronisation existed, so a run has work to repair.</summary>
    public async Task StoreSlotsAsync(string planId, params (string Task, int Week, int Weekday, string? Assignee)[] slots)
    {
        var bson = new BsonArray(slots.Select(s => new BsonDocument
        {
            { "taskId", ObjectId.Parse(s.Task) }, { "weekIndex", s.Week }, { "weekday", s.Weekday },
            { "assigneeId", s.Assignee is null ? BsonNull.Value : ObjectId.Parse(s.Assignee) }, { "sortOrder", 0 },
        }));
        await Database.GetCollection<BsonDocument>("cyclePlans").UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(planId)), new BsonDocument("$set", new BsonDocument("slots", bson)), cancellationToken: Ct);
    }

    // ---- act

    public async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> SendWithResponseAsync(HttpMethod method, string url, object? body = null, bool withProfile = true, bool asAdmin = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (withProfile)
        {
            request.Headers.Add("X-Profile-Id", asAdmin ? Admin.Id : Planner.Id);
        }

        request.Headers.Add("X-Client", "web");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object? body = null, bool withProfile = true, bool asAdmin = false)
    {
        var (status, json, _) = await SendWithResponseAsync(method, url, body, withProfile, asAdmin);
        return (status, json);
    }

    /// <summary>The nightly run (<c>generateUpcoming</c>): the use case that slice 6.3 will put behind the job and the manual trigger.</summary>
    public async Task<GenerationRun> GenerateUpcomingAsync(AuditActor? actor = null)
    {
        using var scope = Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IGenerationService>()
            .GenerateUpcomingAsync(actor ?? AuditActor.System, GenerationRunIds.New(), Ct);
        return result.AsT0;
    }

    public async Task<GenerationResult> GenerateCycleAsync(int cycleIndex, string runId = "test-run")
    {
        using var scope = Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateCycleAsync(AuditActor.System, cycleIndex, runId, Ct);
        return result.AsT0;
    }

    // ---- observe

    public async Task<List<BsonDocument>> OccurrencesOfAsync(string? taskId = null, FilterDefinition<BsonDocument>? extra = null)
    {
        var filter = Builders<BsonDocument>.Filter.Empty;
        if (taskId is not null)
        {
            filter &= new BsonDocument("taskId", ObjectId.Parse(taskId));
        }

        if (extra is not null)
        {
            filter &= extra;
        }

        return await Occurrences.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("date").Ascending("_id")).ToListAsync(Ct);
    }

    public static string DayOf(BsonDocument occurrence) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(occurrence["date"].ToUniversalTime(), TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"))
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static List<string> DaysOf(IEnumerable<BsonDocument> occurrences) => [.. occurrences.Select(DayOf).Order(StringComparer.Ordinal)];

    public async Task<List<BsonDocument>> AuditAsync(string entity, string? action = null, FilterDefinition<BsonDocument>? extra = null)
    {
        var filter = new BsonDocument("entity", entity);
        if (action is not null)
        {
            filter.Add("action", action);
        }

        var all = Builders<BsonDocument>.Filter.And(filter, extra ?? Builders<BsonDocument>.Filter.Empty);
        return await AuditLog.Find(all).Sort(Builders<BsonDocument>.Sort.Ascending("at").Ascending("_id")).ToListAsync(Ct);
    }
}
