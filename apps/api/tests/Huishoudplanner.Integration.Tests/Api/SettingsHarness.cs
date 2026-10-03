using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The real Host over its own database of the shared replica set, with a fixed clock and three profiles (administrator, planner,
/// member) inserted as documents. The startup seed runs like in the application unless a test asks for an empty database.
/// </summary>
public sealed class SettingsHarness : IAsyncDisposable
{
    private readonly ApiFactory factory;
    private readonly IMongoClient mongoClient;
    private readonly string databaseName;

    private SettingsHarness(ApiFactory factory, IMongoClient mongoClient, string databaseName, FixedTimeProvider clock, HttpClient client, IMongoDatabase database, ObjectId admin, ObjectId planner, ObjectId member)
    {
        this.factory = factory;
        this.mongoClient = mongoClient;
        this.databaseName = databaseName;
        Clock = clock;
        Client = client;
        Database = database;
        Admin = admin.ToString();
        Planner = planner.ToString();
        Member = member.ToString();
    }

    public FixedTimeProvider Clock { get; }

    public HttpClient Client { get; }

    public IMongoDatabase Database { get; }

    public string Admin { get; }

    public string Planner { get; }

    public string Member { get; }

    public IMongoCollection<BsonDocument> SettingsCollection => Database.GetCollection<BsonDocument>("settings");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    public static async Task<SettingsHarness> StartAsync(
        MongoContainerFixture mongo,
        string now = "2026-09-16T08:00:00Z",
        Func<IMongoDatabase, Task>? prepare = null,
        Func<ApiFactory, ApiFactory>? configure = null)
    {
        var databaseName = MongoContainerFixture.NewDatabaseName();
        var mongoClient = new MongoClient(mongo.ConnectionString);
        var database = mongoClient.GetDatabase(databaseName);
        var users = database.GetCollection<BsonDocument>("users");
        BsonDocument User(string name, string role, ObjectId id) => new() { { "_id", id }, { "name", name }, { "active", true }, { "role", role } };
        var (admin, planner, member) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        await users.InsertManyAsync([User("Admin", "admin", admin), User("Planner", "planner", planner), User("Member", "member", member)], cancellationToken: TestContext.Current.CancellationToken);
        if (prepare is not null)
        {
            await prepare(database);
        }

        var clock = new FixedTimeProvider(now);
        var factory = ApiFactory.ForMongo(mongo, databaseName).WithPort<TimeProvider>(clock);
        factory = configure?.Invoke(factory) ?? factory;
        var client = factory.CreateClient();
        return new SettingsHarness(factory, mongoClient, databaseName, clock, client, database, admin, planner, member);
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? profile = null, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (profile is not null)
        {
            request.Headers.Add("X-Profile-Id", profile);
            request.Headers.Add("X-Client", "web");
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> Get(string path = "/api/v2/settings") => Send(HttpMethod.Get, path);

    public Task<HttpResponseMessage> Patch(string json, string? profile = null) => Send(HttpMethod.Patch, "/api/v2/settings", profile ?? Admin, json);

    public async Task<JsonElement> SettingsBody()
    {
        var response = await Get();
        response.EnsureSuccessStatusCode();
        return await Body(response);
    }

    public static async Task<JsonElement> Body(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>The audit entries of the settings, oldest first; <paramref name="action"/> narrows them (the startup seed writes a <c>create</c>).</summary>
    public async Task<List<BsonDocument>> AuditEntries(string? action = "update") =>
        await AuditLog
            .Find(Builders<BsonDocument>.Filter.Eq("entity", "settings") & (action is null ? FilterDefinition<BsonDocument>.Empty : Builders<BsonDocument>.Filter.Eq("action", action)))
            .SortBy(e => e["_id"])
            .ToListAsync(TestContext.Current.CancellationToken);

    public async Task<BsonDocument> StoredSettings() =>
        await SettingsCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", new ObjectId("000000000000000000000001"))).SingleAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        factory.Dispose();
        await mongoClient.DropDatabaseAsync(databaseName);
        mongoClient.Dispose();
    }
}
