using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Domain.Calendar;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real host over its own database of the shared replica set for the points ledger scenarios, with a fixed clock (Wednesday 2026-09-16
/// 10:00 Amsterdam; Monday 2026-09-14 is the first day of cycle 0) and three profiles inserted as documents: an administrator and two members.
/// Occurrences, tasks and ledger entries are written straight into the database like old data would be, so a test needs no plan and no generation.
/// The startup seed runs like in the application, which includes the reconciliation of the ledger.
/// </summary>
public sealed class PointsHarness : IAsyncLifetime
{
    public const string Now = "2026-09-16T08:00:00.000Z";

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private readonly MongoContainerFixture mongo;
    private readonly Func<PointsHarness, Task>? prepare;
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly List<ApiFactory> factories = [];
    private MongoClient? mongoClient;
    private ApiFactory? factory;
    private int createdMinutes;

    /// <summary>Class fixture: the host starts on an empty household. <paramref name="mongo"/> is the shared container.</summary>
    public PointsHarness(MongoContainerFixture mongo)
        : this(mongo, null)
    {
    }

    private PointsHarness(MongoContainerFixture mongo, Func<PointsHarness, Task>? prepare)
    {
        this.mongo = mongo;
        this.prepare = prepare;
        Clock = new FixedTimeProvider(Now);
    }

    /// <summary>A harness of its own (a database of its own) for one test; <paramref name="prepare"/> runs before the host starts, so a test can leave old data for the startup reconciliation.</summary>
    public static async Task<PointsHarness> StartAsync(MongoContainerFixture mongo, Func<PointsHarness, Task>? prepare = null)
    {
        var harness = new PointsHarness(mongo, prepare);
        await harness.InitializeAsync();
        return harness;
    }

    public FixedTimeProvider Clock { get; }

    /// <summary>What the host logged, over every start of the application on this database.</summary>
    public LogCollector Logs { get; } = new();

    public ObjectId Admin { get; } = ObjectId.GenerateNewId();

    public ObjectId P1 { get; } = ObjectId.GenerateNewId();

    public ObjectId P2 { get; } = ObjectId.GenerateNewId();

    public HttpClient Client { get; private set; } = null!;

    public IMongoDatabase Database => mongoClient!.GetDatabase(databaseName);

    public IMongoCollection<BsonDocument> Ledger => Database.GetCollection<BsonDocument>("pointEntries");

    public IMongoCollection<BsonDocument> Occurrences => Database.GetCollection<BsonDocument>("occurrences");

    public IMongoCollection<BsonDocument> Tasks => Database.GetCollection<BsonDocument>("tasks");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public IServiceProvider Services => factory!.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        mongoClient = new MongoClient(mongo.ConnectionString);
        await AddUserAsync(Admin, "Beheerder", "admin", true);
        await AddUserAsync(P1, "Persoon 1", "member", true);
        await AddUserAsync(P2, "Persoon 2", "member", true);
        if (prepare is not null)
        {
            await prepare(this);
        }

        factory = NewFactory();
        Client = factory.CreateClient();
    }

    public ValueTask DisposeAsync()
    {
        Client?.Dispose();
        foreach (var f in factories)
        {
            f.Dispose();
        }

        mongoClient?.DropDatabase(databaseName);
        mongoClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private ApiFactory NewFactory()
    {
        var created = ApiFactory.ForMongo(mongo, databaseName).WithPort<TimeProvider>(Clock).WithLogProvider(Logs);
        factories.Add(created);
        return created;
    }

    /// <summary>Starts the host again on the same database, as a restart of the application does: migrations, seeds and the startup reconciliation run again.</summary>
    public HttpClient Restart() => NewFactory().CreateClient();

    // ---- arrange

    public async Task AddUserAsync(ObjectId id, string name, string role, bool active)
    {
        var at = new BsonDateTime(DateTime.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).AddMinutes(createdMinutes++));
        await Database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "name", name }, { "color", "#336699" }, { "active", active }, { "role", role },
                { "unavailableWeekdays", new BsonArray() },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 600 }, { "weekend", 1200 } } },
                { "maxDailyMinutes", new BsonDocument { { "weekday", 1200 }, { "weekend", 2400 } } },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
    }

    /// <summary>The instant of local midnight of a day in the household timezone, as the application stores a calendar date.</summary>
    public static BsonDateTime Midnight(string day) => new(DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam).UtcDateTime);

    /// <summary>A ledger entry of an execution as the Node server writes it.</summary>
    public async Task<ObjectId> InsertExecutionEntryAsync(ObjectId person, string day, int amount, string title = "Taak", ObjectId? occurrenceId = null, string source = "live", ObjectId? task = null)
    {
        var occurrence = occurrenceId ?? ObjectId.GenerateNewId();
        var id = ObjectId.GenerateNewId();
        var created = new BsonDateTime(DateTime.Parse(Now, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        await Ledger.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "key", "execution:" + occurrence }, { "kind", "execution" }, { "personId", person }, { "amount", amount },
                { "date", Midnight(day) }, { "weekStart", Midnight(DayKeys.MondayOf(DayKeys.Parse(day)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) },
                { "periodStart", BsonNull.Value }, { "occurrenceId", occurrence }, { "taskId", task is { } t ? t : BsonNull.Value }, { "titleSnapshot", title },
                { "source", source }, { "createdAt", created }, { "updatedAt", created },
            },
            cancellationToken: Ct);
        return id;
    }

    /// <summary>An entry of another kind (a bonus or a redemption): present in the ledger, never touched by the reconciliation of the executions.</summary>
    public async Task<ObjectId> InsertOtherEntryAsync(ObjectId person, string day, int amount, string kind)
    {
        var id = ObjectId.GenerateNewId();
        var created = new BsonDateTime(DateTime.Parse(Now, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        var document = new BsonDocument
        {
            { "_id", id }, { "key", kind + ":" + id }, { "kind", kind }, { "personId", person }, { "amount", amount },
            { "date", Midnight(day) }, { "weekStart", Midnight(day) }, { "periodStart", kind == "redemption" ? BsonNull.Value : Midnight(day) },
            { "occurrenceId", BsonNull.Value }, { "taskId", BsonNull.Value }, { "titleSnapshot", string.Empty },
            { "source", kind == "redemption" ? "live" : "recompute" }, { "createdAt", created }, { "updatedAt", created },
        };
        if (kind == "redemption")
        {
            document.Add("note", "Pizza");
            document.Add("centsPerPointSnapshot", 5);
            document.Add("currencyCodeSnapshot", "EUR");
        }

        await Ledger.InsertOneAsync(document, cancellationToken: Ct);
        return id;
    }

    /// <summary>A done occurrence as old data holds it: no <c>pointsSnapshot</c> unless one is given.</summary>
    public async Task<ObjectId> InsertDoneOccurrenceAsync(
        string day,
        ObjectId? task,
        ObjectId? completedBy,
        ObjectId? assignee = null,
        int? snapshot = null,
        int duration = 30,
        string name = "Stofzuigen",
        int? pointsOverride = null,
        bool brokenDate = false)
    {
        var id = ObjectId.GenerateNewId();
        var created = new BsonDateTime(DateTime.Parse(Now, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).AddDays(-10));
        var document = new BsonDocument
        {
            { "_id", id }, { "taskId", task is { } t ? t : BsonNull.Value }, { "cycleId", ObjectId.GenerateNewId() }, { "planId", BsonNull.Value },
            { "date", brokenDate ? BsonNull.Value : Midnight(day) }, { "plannedDate", Midnight(day) },
            { "assigneeId", assignee is { } a ? a : BsonNull.Value }, { "status", "done" }, { "statusBeforeCompletion", "open" },
            { "completedAt", new BsonDateTime(Midnight(day).ToUniversalTime().AddHours(10)) },
            { "completedBy", completedBy is { } c ? c : BsonNull.Value }, { "skipReason", BsonNull.Value },
            { "durationMinutesSnapshot", duration }, { "taskNameSnapshot", name }, { "roomIdSnapshot", BsonNull.Value }, { "roomNameSnapshot", BsonNull.Value },
            { "origin", "adhoc" }, { "createdAt", created }, { "updatedAt", created },
        };
        if (snapshot is { } s)
        {
            document.Add("pointsSnapshot", s);
        }

        if (pointsOverride is { } p)
        {
            document.Add("pointsOverride", p);
        }

        await Occurrences.InsertOneAsync(document, cancellationToken: Ct);
        return id;
    }

    /// <summary>A task, with or without <c>points</c> (a task from before points existed has none).</summary>
    public async Task<ObjectId> InsertTaskAsync(string name, int duration, int? points)
    {
        var id = ObjectId.GenerateNewId();
        var created = new BsonDateTime(DateTime.Parse(Now, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).AddDays(-30));
        var document = new BsonDocument
        {
            { "_id", id }, { "name", name }, { "roomId", ObjectId.GenerateNewId() }, { "intervalKey", "1w" }, { "durationMinutes", duration },
            { "defaultAssigneeId", BsonNull.Value }, { "active", true }, { "notes", string.Empty }, { "tags", new BsonArray() },
            { "createdAt", created }, { "updatedAt", created },
        };
        if (points is { } value)
        {
            document.Add("points", value);
        }

        await Tasks.InsertOneAsync(document, cancellationToken: Ct);
        return id;
    }

    // ---- act

    public async Task<(System.Net.HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, ObjectId? profile, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (profile is { } id)
        {
            request.Headers.Add("X-Profile-Id", id.ToString());
            request.Headers.Add("X-Client", "web");
        }

        if (body is not null)
        {
            request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        using var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public Task<(System.Net.HttpStatusCode Status, JsonElement Body)> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null);

    // ---- observe

    public async Task<List<BsonDocument>> EntriesAsync(string? kind = null) =>
        await Ledger.Find(kind is null ? new BsonDocument() : new BsonDocument("kind", kind)).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

    public async Task<BsonDocument?> EntryOfAsync(ObjectId occurrence) =>
        await Ledger.Find(new BsonDocument("key", "execution:" + occurrence)).FirstOrDefaultAsync(Ct);

    public async Task<List<BsonDocument>> PointsAuditAsync(string? action = null) =>
        await AuditLog
            .Find(action is null ? new BsonDocument("entity", "points") : new BsonDocument { { "entity", "points" }, { "action", action } })
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(Ct);

    public async Task<long> AuditCountAsync() => await AuditLog.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct);

    public async Task<BsonDocument> StoredOccurrenceAsync(ObjectId id) => await Occurrences.Find(new BsonDocument("_id", id)).SingleAsync(Ct);

    public async Task<BsonDocument> StoredTaskAsync(ObjectId id) => await Tasks.Find(new BsonDocument("_id", id)).SingleAsync(Ct);

    /// <summary>Resolves a service of the running host, in its own scope.</summary>
    public AsyncServiceScope Scope() => Services.CreateAsyncScope();
}
