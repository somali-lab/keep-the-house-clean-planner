using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The household of <c>badges.test.ts</c> on the real host and a real MongoDB replica set, one host and one database per scenario, with a clock that
/// tests move. Wednesday 16 September 2026 is in the week of Monday 14 September (cycle 0). Every task has an occurrence on each Wednesday (person 1),
/// Thursday (person 2) and Friday (nobody) of four weeks. Person 1 is an administrator, person 2 a member. The state is arranged through the API;
/// only what no use case can write (damaged or drifted awards, a badge with an unreadable rule) is written straight into the database.
/// </summary>
public sealed class BadgeHarness : IAsyncLifetime
{
    public const string Monday = "2026-09-14T06:00:00.000Z";
    public const string Wednesday = "2026-09-16T08:00:00.000Z";

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly bool moveToWednesday;
    private readonly List<ApiFactory> restarts = [];
    private MongoClient? mongoClient;
    private ApiFactory? factory;

    private BadgeHarness(MongoContainerFixture mongo, bool moveToWednesday)
    {
        this.mongo = mongo;
        this.moveToWednesday = moveToWednesday;
        Clock = new FixedTimeProvider(Monday);
    }

    /// <summary>A harness of its own (a database of its own) for one test. The first start is on Monday, the anchor of the household.</summary>
    public static async Task<BadgeHarness> StartAsync(MongoContainerFixture mongo, bool moveToWednesday = true)
    {
        var harness = new BadgeHarness(mongo, moveToWednesday);
        await harness.InitializeAsync();
        return harness;
    }

    public FixedTimeProvider Clock { get; }

    public WriteCapture Capture { get; } = new();

    public ReadCounter Reads { get; } = new();

    /// <summary>The administrator, who is also the person whose work most scenarios count.</summary>
    public UserIdentity P1 { get; private set; } = null!;

    public UserIdentity P2 { get; private set; } = null!;

    /// <summary>10 minutes.</summary>
    public string Toilet { get; private set; } = null!;

    /// <summary>20 minutes.</summary>
    public string Mop { get; private set; } = null!;

    /// <summary>30 minutes.</summary>
    public string Vacuum { get; private set; } = null!;

    /// <summary>15 minutes, 0 points.</summary>
    public string Free { get; private set; } = null!;

    public string Room { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IMongoDatabase Database => mongoClient!.GetDatabase(databaseName);

    public IMongoCollection<BsonDocument> Badges => Database.GetCollection<BsonDocument>("badges");

    public IMongoCollection<BsonDocument> Awards => Database.GetCollection<BsonDocument>("badgeAwards");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public IMongoCollection<BsonDocument> Occurrences => Database.GetCollection<BsonDocument>("occurrences");

    public IServiceProvider Services => factory!.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        P1 = directory.Add(Role.Admin);
        P2 = directory.Add(Role.Member);
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<TimeProvider>(Clock)
            .WithWriteCapture(Capture)
            .WithReadCounter(Reads);
        Client = factory.CreateClient();

        await SeedPersonAsync(P1, "Persoon 1", "admin");
        await SeedPersonAsync(P2, "Persoon 2", "member");
        Room = await CreatedAsync("/api/v2/rooms", new { name = "Woonkamer" });
        Toilet = await CreatedAsync("/api/v2/tasks", new { name = "Toilet schoonmaken", roomId = Room, intervalKey = "1w", durationMinutes = 10 });
        Mop = await CreatedAsync("/api/v2/tasks", new { name = "Vloer dweilen", roomId = Room, intervalKey = "1w", durationMinutes = 20 });
        Vacuum = await CreatedAsync("/api/v2/tasks", new { name = "Stofzuigen", roomId = Room, intervalKey = "1w", durationMinutes = 30 });
        Free = await CreatedAsync("/api/v2/tasks", new { name = "Planten water geven", roomId = Room, intervalKey = "1w", durationMinutes = 15, points = 0 });
        var plan = (await SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, null)).Body.GetProperty("id").GetString()!;
        var slots = new[] { Toilet, Mop, Vacuum, Free }.SelectMany(task => Enumerable.Range(0, 4).SelectMany(week => new[]
        {
            new { taskId = task, weekIndex = week, weekday = 3, assigneeId = (string?)P1.Id },
            new { taskId = task, weekIndex = week, weekday = 4, assigneeId = (string?)P2.Id },
            new { taskId = task, weekIndex = week, weekday = 5, assigneeId = (string?)null },
        })).ToList();
        var put = await SendAsync(HttpMethod.Put, $"/api/v2/cycle-plans/{plan}/slots", new { slots }, P1);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateUpcomingAsync(AuditActor.System, "badge-harness", Ct);
            run.IsT0.Should().BeTrue();
        }

        if (moveToWednesday)
        {
            Clock.Set(Wednesday);
        }
    }

    public ValueTask DisposeAsync()
    {
        Client?.Dispose();
        factory?.Dispose();
        restarts.ForEach(r => r.Dispose());
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

    private async Task<string> CreatedAsync(string url, object body)
    {
        var response = await SendAsync(HttpMethod.Post, url, body, P1);
        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        return response.Body.GetProperty("id").GetString()!;
    }

    /// <summary>Starts the host again on the same database, as a restart of the application does: migrations, seeds and the startup reconciliation run again.</summary>
    public HttpClient Restart()
    {
        var restarted = ApiFactory.ForMongo(mongo, databaseName).WithPort<ForFindingUsers>(directory).WithPort<TimeProvider>(Clock);
        restarts.Add(restarted);
        return restarted.CreateClient();
    }

    // ---- act

    /// <summary>Sends a request as <paramref name="actor"/> (no profile header when <see langword="null"/>); a <see langword="null"/> body sends none.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object? body, UserIdentity? actor)
    {
        var (status, text, _) = await SendRawAsync(method, url, body, actor, null);
        return (status, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>Like <see cref="SendAsync"/>, but with the bytes, the headers and an optional extra request header (the image tests).</summary>
    public async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> SendRawAsync(
        HttpMethod method, string url, object? body, UserIdentity? actor, (string Name, string Value)? header)
    {
        using var request = new HttpRequestMessage(method, url);
        if (actor is not null)
        {
            request.Headers.Add("X-Profile-Id", actor.Id);
        }

        if (header is { } extra)
        {
            request.Headers.Add(extra.Name, extra.Value);
        }

        request.Headers.Add("X-Client", "web");
        if (body is not null)
        {
            request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text, response);
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null, null);

    /// <summary>Creates a badge as person 1 and returns it (the request must answer 201).</summary>
    public async Task<JsonElement> AddBadgeAsync(object payload)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v2/badges", payload, P1);
        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        return response.Body;
    }

    /// <summary>The id of a generated occurrence of a task on a day, from the list endpoint.</summary>
    public async Task<string> OccurrenceAsync(string day, string taskId)
    {
        var list = await GetAsync($"/api/v2/occurrences?from={day}&to={day}");
        list.Status.Should().Be(HttpStatusCode.OK, list.Body.ToString());
        return list.Body.GetProperty("items").EnumerateArray().First(o => o.GetProperty("taskId").GetString() == taskId).GetProperty("id").GetString()!;
    }

    /// <summary>Completes work at an instant: the clock moves first.</summary>
    public Task<(HttpStatusCode Status, JsonElement Body)> CompleteAtAsync(string now, string occurrenceId, object? body = null, UserIdentity? actor = null)
    {
        Clock.Set(now);
        return SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{occurrenceId}/complete", body, actor ?? P1);
    }

    /// <summary>Undoes a completion at an instant: the clock moves first.</summary>
    public Task<(HttpStatusCode Status, JsonElement Body)> UncompleteAtAsync(string now, string occurrenceId, UserIdentity? actor = null)
    {
        Clock.Set(now);
        return SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{occurrenceId}/uncomplete", null, actor ?? P1);
    }

    // ---- observe

    /// <summary>The people who hold a badge, as <c>p1</c> and <c>p2</c>, with the moment they earned it.</summary>
    public async Task<Dictionary<string, string>> HoldersAsync(string badgeId)
    {
        var result = new Dictionary<string, string>();
        var documents = await Awards.Find(new BsonDocument("badgeId", ObjectId.Parse(badgeId))).ToListAsync(Ct);
        foreach (var award in documents)
        {
            var person = award["personId"].AsObjectId.ToString();
            result[person == P1.Id ? "p1" : person == P2.Id ? "p2" : "other"] = Iso(award["awardedAt"]);
        }

        return result;
    }

    public static string Iso(BsonValue date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public async Task<List<BsonDocument>> AuditAsync(string entity) =>
        await AuditLog.Find(new BsonDocument("entity", entity)).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

    public async Task<long> AwardCountAsync() => await Awards.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    /// <summary>Runs a request and reports what it wrote: the state writes (the audit log left out) and the audit entries it inserted.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body, IReadOnlyList<CapturedWrite> Writes, int AuditInserts)> CapturedAsync(HttpMethod method, string url, object? body, UserIdentity? actor)
    {
        Capture.Clear();
        var (status, json) = await SendAsync(method, url, body, actor);
        return (status, json, Capture.Writes(), Capture.AuditInserts());
    }
}

/// <summary>
/// Counts the <c>find</c> and <c>aggregate</c> commands the application sends to a collection with a projection of certain fields, to prove which reads
/// a request makes (the executions of people are read with a projection of <c>durationMinutesSnapshot</c> and <c>completedAt</c>; the occurrence use
/// cases read whole documents).
/// </summary>
public sealed class ReadCounter
{
    private int count;
    private string collection = string.Empty;
    private string[] fields = [];

    /// <summary>The matching reads since the last <see cref="Start"/>.</summary>
    public int Count => Volatile.Read(ref count);

    public void Start(string name, params string[] projectionFields)
    {
        collection = name;
        fields = projectionFields;
        Volatile.Write(ref count, 0);
    }

    internal void Attach(MongoClientSettings settings)
    {
        var previous = settings.ClusterConfigurator;
        settings.ClusterConfigurator = builder =>
        {
            previous?.Invoke(builder);
            builder.Subscribe<CommandStartedEvent>(started =>
            {
                if (started.CommandName is not ("find" or "aggregate") || !started.Command.TryGetValue(started.CommandName, out var target) || !target.IsString || target.AsString != collection)
                {
                    return;
                }

                var projection = started.Command.TryGetValue("projection", out var p) && p.IsBsonDocument ? p.AsBsonDocument : null;
                if (fields.Length == 0 || (projection is not null && fields.All(projection.Contains)))
                {
                    Interlocked.Increment(ref count);
                }
            });
        };
    }
}

internal sealed class ReadCounterCustomizer(ReadCounter counter) : IMongoClientSettingsCustomizer
{
    public void Customize(MongoClientSettings settings) => counter.Attach(settings);
}
