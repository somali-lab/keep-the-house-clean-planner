using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The household of <c>points-progress.test.ts</c> on the real host and a real MongoDB replica set, one host and one database per scenario. Monday
/// 14 Sep 2026 is the first day of cycle 0 (14 Sep to 11 Oct); the cycle is generated on that Monday morning and the clock then moves to Wednesday
/// 16 Sep. Stofzuigen (3 minutes, 3 points) is planned on Monday for person 1, Tuesday for person 2 and Wednesday for nobody; Dweilen (1 minute, 1 point)
/// on Thursday of week 0 and Monday of week 1, both for person 1. A guest is an active person nothing is planned for. The state is arranged through
/// the use cases; only ledger entries no use case writes are inserted straight into the database.
/// </summary>
public sealed class ProgressHarness : IAsyncLifetime
{
    public const string Monday = "2026-09-14T06:00:00.000Z";

    public const string Wednesday = "2026-09-16T08:00:00.000Z";

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly Dictionary<string, string> tasks = [];
    private MongoClient? mongoClient;
    private ApiFactory? factory;

    private ProgressHarness(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        Clock = new FixedTimeProvider(Monday);
    }

    public static async Task<ProgressHarness> StartAsync(MongoContainerFixture mongo)
    {
        var harness = new ProgressHarness(mongo);
        await harness.InitializeAsync();
        return harness;
    }

    public FixedTimeProvider Clock { get; }

    public UserIdentity P1 { get; private set; } = null!;

    public UserIdentity P2 { get; private set; } = null!;

    public UserIdentity Admin { get; private set; } = null!;

    public UserIdentity Planner { get; private set; } = null!;

    /// <summary>An active person that nothing is planned for.</summary>
    public string Guest { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IMongoDatabase Database => mongoClient!.GetDatabase(databaseName);

    public IMongoCollection<BsonDocument> Ledger => Database.GetCollection<BsonDocument>("pointEntries");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

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

        await SeedPersonAsync(P1.Id, "Persoon 1", "member");
        await SeedPersonAsync(P2.Id, "Persoon 2", "member");
        await SeedPersonAsync(Admin.Id, "Beheerder", "admin");
        Guest = ObjectId.GenerateNewId().ToString();
        await SeedPersonAsync(Guest, "Logé", "member");

        var room = ObjectId.GenerateNewId();
        var at = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", room }, { "name", "Woonkamer" }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        foreach (var (name, minutes) in new[] { ("Stofzuigen", 3), ("Dweilen", 1) })
        {
            var created = await SendAsync(HttpMethod.Post, "/api/v2/tasks", new { name, roomId = room.ToString(), intervalKey = "1w", durationMinutes = minutes }, Planner);
            created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
            tasks[name] = created.Body.GetProperty("id").GetString()!;
        }

        var plan = (await SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, null)).Body.GetProperty("id").GetString()!;
        var slots = new[]
        {
            new { taskId = tasks["Stofzuigen"], weekIndex = 0, weekday = 1, assigneeId = (string?)P1.Id },
            new { taskId = tasks["Stofzuigen"], weekIndex = 0, weekday = 2, assigneeId = (string?)P2.Id },
            new { taskId = tasks["Stofzuigen"], weekIndex = 0, weekday = 3, assigneeId = (string?)null },
            new { taskId = tasks["Dweilen"], weekIndex = 0, weekday = 4, assigneeId = (string?)P1.Id },
            new { taskId = tasks["Dweilen"], weekIndex = 1, weekday = 1, assigneeId = (string?)P1.Id },
        };
        var put = await SendAsync(HttpMethod.Put, $"/api/v2/cycle-plans/{plan}/slots", new { slots }, Planner);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateUpcomingAsync(AuditActor.System, "progress-harness", Ct);
            run.IsT0.Should().BeTrue();
        }

        Clock.Set(Wednesday);
    }

    public ValueTask DisposeAsync()
    {
        Client?.Dispose();
        factory?.Dispose();
        mongoClient?.DropDatabase(databaseName);
        mongoClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SeedPersonAsync(string id, string name, string role)
    {
        var at = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.Parse(id) }, { "name", name }, { "color", "#336699" }, { "active", true }, { "role", role },
                { "unavailableWeekdays", new BsonArray() },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 600 }, { "weekend", 1200 } } },
                { "maxDailyMinutes", new BsonDocument { { "weekday", 1200 }, { "weekend", 2400 } } },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
    }

    // ---- act

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

    /// <summary>Performs one request as the person (person 1 by default) and expects it to succeed.</summary>
    public async Task<JsonElement> DoAsync(HttpMethod method, string url, object? body = null, UserIdentity? actor = null, string? now = null)
    {
        if (now is not null)
        {
            Clock.Set(now);
        }

        var response = await SendAsync(method, url, body, actor ?? P1);
        ((int)response.Status).Should().BeInRange(200, 299, $"{method} {url}: {response.Body}");
        return response.Body;
    }

    public Task<JsonElement> ActAsync(string occurrence, string action, object? body = null, UserIdentity? actor = null, string? now = null) =>
        DoAsync(HttpMethod.Post, $"/api/v2/occurrences/{occurrence}/{action}", body, actor, now);

    public Task<JsonElement> PatchSettingsAsync(string json) => DoAsync(HttpMethod.Patch, "/api/v2/settings", json, Admin);

    /// <summary>The id of the occurrence of a task on a day.</summary>
    public async Task<string> OccurrenceAsync(string day, string task = "Stofzuigen")
    {
        var list = await SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from={day}&to={day}", null, null);
        list.Status.Should().Be(HttpStatusCode.OK, list.Body.ToString());
        return list.Body.GetProperty("items").EnumerateArray().First(o => o.GetProperty("taskId").GetString() == tasks[task]).GetProperty("id").GetString()!;
    }

    public async Task<JsonElement> ProgressAsync(string person, string period)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v2/points/progress?personId={person}&period={period}", null, null);
        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        return response.Body;
    }

    /// <summary>Moves the clock and runs the reconciliation the nightly job runs.</summary>
    public async Task ReconcileAtAsync(string now)
    {
        Clock.Set(now);
        using var scope = Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPointsService>().RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);
        result.IsT0.Should().BeTrue();
    }

    /// <summary>A ledger entry of an execution as the Node server writes it, for a day.</summary>
    public async Task EarnAsync(string person, string day, int amount)
    {
        var date = new BsonDateTime(DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam).UtcDateTime);
        var monday = new BsonDateTime(DayKeys.FromDayKey(DayKeys.MondayOf(DayKeys.Parse(day)), Amsterdam).UtcDateTime);
        var occurrence = ObjectId.GenerateNewId();
        var created = new BsonDateTime(DateTime.Parse(Wednesday, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        await Ledger.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", "execution:" + occurrence }, { "kind", "execution" }, { "personId", ObjectId.Parse(person) }, { "amount", amount },
                { "date", date }, { "weekStart", monday }, { "periodStart", BsonNull.Value }, { "occurrenceId", occurrence }, { "taskId", BsonNull.Value },
                { "titleSnapshot", "Taak" }, { "source", "live" }, { "createdAt", created }, { "updatedAt", created },
            },
            cancellationToken: Ct);
    }

    /// <summary>A redemption in the ledger: the points a person gave up, as a negative entry.</summary>
    public async Task RedeemAsync(string person, string day, int points)
    {
        var date = new BsonDateTime(DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam).UtcDateTime);
        var id = ObjectId.GenerateNewId();
        var created = new BsonDateTime(DateTime.Parse(Wednesday, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        await Ledger.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "key", "redemption:" + id }, { "kind", "redemption" }, { "personId", ObjectId.Parse(person) }, { "amount", -points },
                { "date", date }, { "weekStart", date }, { "periodStart", BsonNull.Value }, { "occurrenceId", BsonNull.Value }, { "taskId", BsonNull.Value },
                { "titleSnapshot", string.Empty }, { "source", "live" }, { "note", "Ijsje" }, { "centsPerPointSnapshot", 0 }, { "currencyCodeSnapshot", "EUR" },
                { "createdAt", created }, { "updatedAt", created },
            },
            cancellationToken: Ct);
    }

    /// <summary>Every ledger document and the number of audit entries: equal before and after a read.</summary>
    public async Task<(string Ledger, long Audits)> FingerprintAsync() =>
        (string.Join("\n", (await Ledger.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct)).Select(e => e.ToJson())),
            await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
}
