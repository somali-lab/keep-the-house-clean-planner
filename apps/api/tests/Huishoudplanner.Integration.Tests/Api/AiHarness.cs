using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Adapters.Ai;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Settings;
using AdapterProviderType = Huishoudplanner.Adapters.Ai.AiProviderType;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>Wraps a model port and keeps every request it receives (system, user, options and the schema the use case sent).</summary>
internal sealed class RecordingModel(ForChattingWithAModel inner) : ForChattingWithAModel
{
    private readonly List<ChatRequest> requests = [];

    public IReadOnlyList<ChatRequest> Requests => requests;

    public string Provider => inner.Provider;

    public Task<OneOf<ChatReply, AiUnavailable, PortError>> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        requests.Add(request);
        return inner.ChatAsync(request, cancellationToken);
    }
}

/// <summary>
/// The model selector of the tests: a recording mock with the given answers whatever the settings say (like the Node test app's
/// <c>aiProvider</c> override), or, without one, the real provider choice (the provider <c>none</c> is disabled).
/// </summary>
internal sealed class TestModels(IReadOnlyDictionary<string, MockResponder>? responders, bool real = false) : ForSelectingAModel
{
    public RecordingModel Model { get; } = new(AiProviderFactory.Create(
        new AiProviderOptions(AdapterProviderType.Mock),
        new AiProviderHooks(MockResponders: responders ?? DefaultMockResponders.Responders)));

    public List<AiProviderSettings> Chosen { get; } = [];

    public ForChattingWithAModel ChooseFor(AiProviderSettings settings)
    {
        Chosen.Add(settings);
        return real
            ? AiProviderFactory.Create(new AiProviderOptions(Enum.Parse<AdapterProviderType>(settings.Type.ToString()), settings.Endpoint, settings.Model))
            : Model;
    }
}

/// <summary>
/// The real Host over its own database of the shared replica set with a fixed clock and the startup seed (Persoon 1 administrator and
/// Persoon 2, the plan "Standaard", settings with the provider none): the setup of the Node AI tests. Persoon 1 is the actor.
/// </summary>
internal sealed class AiHarness : IAsyncDisposable
{
    private readonly ApiFactory factory;
    private readonly MongoClient mongoClient;
    private readonly string databaseName;

    private AiHarness(ApiFactory factory, MongoClient mongoClient, string databaseName, TestModels models, HttpClient client, IMongoDatabase database, string p1, string p2)
    {
        this.factory = factory;
        this.mongoClient = mongoClient;
        this.databaseName = databaseName;
        Models = models;
        Client = client;
        Database = database;
        P1 = p1;
        P2 = p2;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TestModels Models { get; }

    public RecordingModel Model => Models.Model;

    public HttpClient Client { get; }

    public IMongoDatabase Database { get; }

    /// <summary>"Persoon 1", the seeded administrator and the actor of every call.</summary>
    public string P1 { get; }

    public string P2 { get; }

    public IMongoCollection<BsonDocument> Plans => Database.GetCollection<BsonDocument>("cyclePlans");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public static async Task<AiHarness> StartAsync(MongoContainerFixture mongo, IReadOnlyDictionary<string, MockResponder>? responders = null, bool realModels = false, string now = "2026-09-14T06:00:00Z")
    {
        var databaseName = MongoContainerFixture.NewDatabaseName();
        var mongoClient = new MongoClient(mongo.ConnectionString);
        var models = new TestModels(responders, realModels);
        var factory = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<TimeProvider>(new FixedTimeProvider(now))
            .WithPort<ForSelectingAModel>(models);
        var client = factory.CreateClient();
        var database = mongoClient.GetDatabase(databaseName);
        var users = await database.GetCollection<BsonDocument>("users")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("createdAt").Ascending("_id"))
            .ToListAsync(Ct);
        return new AiHarness(factory, mongoClient, databaseName, models, client, database, users[0]["_id"].AsObjectId.ToString(), users[1]["_id"].AsObjectId.ToString());
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, string? profile = null, bool noProfile = false)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!noProfile)
        {
            request.Headers.Add("X-Profile-Id", profile ?? P1);
            request.Headers.Add("X-Client", "web");
        }

        if (body is not null)
        {
            request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        return await Client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });

    /// <summary>A POST with no body and no content type at all.</summary>
    public Task<HttpResponseMessage> PostEmpty(string url) => Send(HttpMethod.Post, url);

    public static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<string> Created(string url, object body)
    {
        var response = await Post(url, body);
        var json = await Body(response);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetString()!;
    }

    public Task<string> Room(string name) => Created("/api/v2/rooms", new { name });

    public Task<string> Task(string name, string roomId, string intervalKey, int minutes) =>
        Created("/api/v2/tasks", new { name, roomId, intervalKey, durationMinutes = minutes });

    public async Task Patch(string url, object body)
    {
        var response = await Send(HttpMethod.Patch, url, body);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
    }

    public async Task<string> ActivePlanId() =>
        (await Body(await Send(HttpMethod.Get, "/api/v2/cycle-plans/active", noProfile: true))).GetProperty("id").GetString()!;

    public async Task<BsonDocument> Plan(string id) =>
        await Plans.Find(new BsonDocument("_id", new ObjectId(id))).SingleAsync(Ct);

    public async Task<long> PlanCount() => await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    public async Task<List<BsonDocument>> PlanAudit(string action) =>
        await AuditLog.Find(new BsonDocument { { "entity", "cyclePlan" }, { "action", action } }).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

    /// <summary>The number of documents in every collection that an AI call could write to; unchanged means the call stored nothing.</summary>
    public async Task<string> Snapshot()
    {
        var parts = new List<string>();
        foreach (var name in new[] { "tasks", "rooms", "users", "cyclePlans", "settings", "auditLog", "occurrences" })
        {
            parts.Add($"{name}={await Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)}");
        }

        var settings = await Database.GetCollection<BsonDocument>("settings").Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        parts.Add($"settingsUpdatedAt={settings.GetValue("updatedAt", BsonNull.Value)}");
        return string.Join(", ", parts);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        factory.Dispose();
        await mongoClient.DropDatabaseAsync(databaseName);
        mongoClient.Dispose();
    }

    public static string Hex(string id) => id;

    /// <summary>The request of the Nth call as the use case sent it.</summary>
    public ChatRequest Request(int index = 0) => Model.Requests[index];

    public static T Payload<T>(ChatRequest request) =>
        JsonSerializer.Deserialize<T>(request.Messages[^1].Content, PromptJson.Options)!;

    /// <summary>A valid plan built from the prompt the server sent (the Node <c>validAnswer</c>).</summary>
    public static string ValidAnswer(MockRequest request) =>
        DefaultMockResponders.DeterministicPlan(JsonDocument.Parse(request.User).RootElement).ToJsonString(PromptJson.Options);
}
