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
/// The household of <c>points-bonuses.test.ts</c> on the real host and a real MongoDB replica set, one host and one database per scenario, with
/// a clock that tests move: Monday 2026-09-14 is the first day of cycle 0 (14 Sep to 11 Oct). Every cycle has one task of 30 minutes (30 points)
/// on Monday (person 1), Tuesday (person 2) and Wednesday (nobody) of its first week; the first and the second cycle are both generated. The
/// state is arranged through the occurrence endpoints; only data no use case can write (an unreadable occurrence, a missing floor) is written
/// straight into the database.
/// </summary>
public sealed class BonusHarness : IAsyncLifetime
{
    public const string Monday = "2026-09-14T06:00:00.000Z";

    /// <summary>The amounts of the Node scenarios: week 5 / 3, cycle 20 / 10.</summary>
    public const string Amounts = """{ "weekDone": 5, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }""";

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private readonly MongoContainerFixture mongo;
    private readonly bool amounts;
    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private MongoClient? mongoClient;
    private ApiFactory? factory;

    private BonusHarness(MongoContainerFixture mongo, bool amounts)
    {
        this.mongo = mongo;
        this.amounts = amounts;
        Clock = new FixedTimeProvider(Monday);
    }

    /// <summary>A harness of its own; <paramref name="amounts"/> false leaves the bonus amounts at their default of 0.</summary>
    public static async Task<BonusHarness> StartAsync(MongoContainerFixture mongo, bool amounts = true)
    {
        var harness = new BonusHarness(mongo, amounts);
        await harness.InitializeAsync();
        return harness;
    }

    public FixedTimeProvider Clock { get; }

    public UserIdentity P1 { get; private set; } = null!;

    public UserIdentity P2 { get; private set; } = null!;

    public UserIdentity Admin { get; private set; } = null!;

    public UserIdentity Planner { get; private set; } = null!;

    public string TaskId { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IMongoDatabase Database => mongoClient!.GetDatabase(databaseName);

    public IMongoCollection<BsonDocument> Ledger => Database.GetCollection<BsonDocument>("pointEntries");

    public IMongoCollection<BsonDocument> Occurrences => Database.GetCollection<BsonDocument>("occurrences");

    public IMongoCollection<BsonDocument> Settings => Database.GetCollection<BsonDocument>("settings");

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

        await SeedPersonAsync(P1, "Persoon 1", "member");
        await SeedPersonAsync(P2, "Persoon 2", "member");
        await SeedPersonAsync(Admin, "Beheerder", "admin");
        var room = ObjectId.GenerateNewId();
        var at = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", room }, { "name", "Woonkamer" }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        var created = await SendAsync(HttpMethod.Post, "/api/v2/tasks", new { name = "Stofzuigen", roomId = room.ToString(), intervalKey = "1w", durationMinutes = 30 }, Planner);
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        TaskId = created.Body.GetProperty("id").GetString()!;
        var plan = (await SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, null)).Body.GetProperty("id").GetString()!;
        var slots = new[]
        {
            new { taskId = TaskId, weekIndex = 0, weekday = 1, assigneeId = (string?)P1.Id },
            new { taskId = TaskId, weekIndex = 0, weekday = 2, assigneeId = (string?)P2.Id },
            new { taskId = TaskId, weekIndex = 0, weekday = 3, assigneeId = (string?)null },
        };
        var put = await SendAsync(HttpMethod.Put, $"/api/v2/cycle-plans/{plan}/slots", new { slots }, Planner);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateUpcomingAsync(AuditActor.System, "bonus-harness", Ct);
            run.IsT0.Should().BeTrue();
        }

        if (amounts)
        {
            var response = await SendAsync(HttpMethod.Patch, "/api/v2/settings", $$"""{ "periodBonuses": {{Amounts}} }""", Admin);
            response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        }
    }

    public ValueTask DisposeAsync()
    {
        Client?.Dispose();
        factory?.Dispose();
        mongoClient?.DropDatabase(databaseName);
        mongoClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SeedPersonAsync(UserIdentity identity, string name, string role)
    {
        var at = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.Parse(identity.Id) }, { "name", name }, { "color", "#336699" }, { "active", true }, { "role", role },
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

    /// <summary>Moves the clock, performs one request as the person and expects it to succeed.</summary>
    public async Task<JsonElement> AtAsync(string now, HttpMethod method, string url, object? body = null, UserIdentity? actor = null)
    {
        Clock.Set(now);
        var response = await SendAsync(method, url, body, actor ?? P1);
        ((int)response.Status).Should().BeInRange(200, 299, $"{method} {url}: {response.Body}");
        return response.Body;
    }

    public Task<JsonElement> CompleteAsync(string now, string occurrence, UserIdentity actor, object? body = null) =>
        AtAsync(now, HttpMethod.Post, $"/api/v2/occurrences/{occurrence}/complete", body, actor);

    public Task<JsonElement> PostAsync(string now, string occurrence, string action, object? body = null, UserIdentity? actor = null) =>
        AtAsync(now, HttpMethod.Post, $"/api/v2/occurrences/{occurrence}/{action}", body, actor);

    /// <summary>The id of the occurrence of the task on a day.</summary>
    public async Task<string> OccurrenceAsync(string day)
    {
        var list = await SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from={day}&to={day}", null, null);
        list.Status.Should().Be(HttpStatusCode.OK, list.Body.ToString());
        return list.Body.GetProperty("items").EnumerateArray().First(o => o.GetProperty("taskId").GetString() == TaskId).GetProperty("id").GetString()!;
    }

    /// <summary>Person 1 and person 2 do their task on time in the week of 14 September; the unassigned one stays open.</summary>
    public async Task DoTheWeekAsync()
    {
        var monday = await OccurrenceAsync("2026-09-14");
        var tuesday = await OccurrenceAsync("2026-09-15");
        await CompleteAsync("2026-09-14T07:00:00.000Z", monday, P1);
        await CompleteAsync("2026-09-15T07:00:00.000Z", tuesday, P2);
    }

    /// <summary>Moves the clock and runs the reconciliation the nightly job runs.</summary>
    public async Task<PointsRecomputeResult> ReconcileAtAsync(string now, PointsRecomputeTrigger trigger = PointsRecomputeTrigger.Nightly)
    {
        Clock.Set(now);
        using var scope = Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPointsService>().RecomputeAsync(AuditActor.System, trigger, Ct);
        return result.AsT0;
    }

    /// <summary>The whole nightly run (generation, then the reconciliation) at an instant.</summary>
    public async Task NightlyAtAsync(string now)
    {
        Clock.Set(now);
        using var scope = Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<INightlyService>().RunAsync(AuditActor.System, "bonus-nightly", Ct);
        run.IsT0.Should().BeTrue();
    }

    // ---- observe

    /// <summary>A readable view of the stored bonuses: kind, person, first day of the period and amount, in a stable order.</summary>
    public async Task<List<string>> BonusesAsync()
    {
        string Name(ObjectId id) => id.ToString() == P1.Id ? "p1" : id.ToString() == P2.Id ? "p2" : id.ToString();
        var bonusKinds = new BsonArray(BonusKindNames);
        var entries = await Ledger.Find(new BsonDocument("kind", new BsonDocument("$in", bonusKinds))).ToListAsync(Ct);
        return [.. entries
            .Select(e => $"{e["kind"].AsString} {Name(e["personId"].AsObjectId)} {DayOf(e["periodStart"])} {e["amount"].AsInt32}")
            .Order(StringComparer.Ordinal)];
    }

    public static string[] BonusKindNames { get; } = ["bonus_week_done", "bonus_week_ontime", "bonus_cycle_done", "bonus_cycle_ontime"];

    public static string DayOf(BsonValue instant) =>
        DayKeys.ToDayKey(new DateTimeOffset(instant.ToUniversalTime(), TimeSpan.Zero), Amsterdam).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<List<BsonDocument>> RecomputeAuditAsync() =>
        await AuditLog.Find(new BsonDocument { { "entity", "points" }, { "action", "recompute" } }).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

    /// <summary>Every ledger document and the number of audit entries: equal before and after a run that wrote nothing.</summary>
    public async Task<(string Ledger, long Audits)> FingerprintAsync() =>
        (string.Join("\n", (await Ledger.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct)).Select(e => e.ToJson())),
            await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));

    public async Task<BsonDocument> StoredAsync(string occurrenceId) =>
        await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(occurrenceId))).SingleAsync(Ct);

    public async Task<List<BsonDocument>> OccurrenceAuditAsync(string occurrenceId) =>
        await AuditLog.Find(new BsonDocument { { "entity", "occurrence" }, { "entityId", ObjectId.Parse(occurrenceId) } }).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
}
