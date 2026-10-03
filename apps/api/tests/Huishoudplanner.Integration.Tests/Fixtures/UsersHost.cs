using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real Host over its own database on the shared replica set, with the clock fixed at 2026-09-16T08:00:00Z and the
/// default seed (Persoon 1 administrator, Persoon 2 member) done at startup: the .NET counterpart of the Node
/// <c>createTestApp()</c> + <c>seededUsers()</c> for the users tests. One host per test keeps the tests independent.
/// </summary>
public sealed class UsersHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory factory;
    private readonly MongoClient mongoClient;

    private UsersHost(ApiFactory factory, HttpClient client, MongoClient mongoClient, IMongoDatabase database)
    {
        this.factory = factory;
        Client = client;
        this.mongoClient = mongoClient;
        Database = database;
    }

    public HttpClient Client { get; }

    public IMongoDatabase Database { get; }

    public IMongoCollection<BsonDocument> Users => Database.GetCollection<BsonDocument>("users");

    public IMongoCollection<BsonDocument> AuditLog => Database.GetCollection<BsonDocument>("auditLog");

    /// <summary>The seeded administrator (the first profile).</summary>
    public string AdminId { get; private set; } = string.Empty;

    /// <summary>The seeded member (the second profile).</summary>
    public string MemberId { get; private set; } = string.Empty;

    public static async Task<UsersHost> StartAsync(MongoContainerFixture mongo, Func<ApiFactory, ApiFactory>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mongo);
        var databaseName = MongoContainerFixture.NewDatabaseName();
        TimeProvider clock = new FixedClock(Now);
        var factory = ApiFactory.ForMongo(mongo, databaseName).WithPort(clock);
        factory = configure?.Invoke(factory) ?? factory;
        var client = factory.CreateClient();
        var mongoClient = new MongoClient(mongo.ConnectionString);
        var host = new UsersHost(factory, client, mongoClient, mongoClient.GetDatabase(databaseName));
        await host.LoadSeededUsersAsync();
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await factory.DisposeAsync();
        await mongoClient.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
        mongoClient.Dispose();
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? actorId = null, object? body = null, string? client = "web")
    {
        using var request = new HttpRequestMessage(method, path);
        if (actorId is not null)
        {
            request.Headers.Add("X-Profile-Id", actorId);
        }

        if (client is not null)
        {
            request.Headers.Add("X-Client", client);
        }

        if (body is not null)
        {
            request.Content = body is string raw
                ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The JSON body; buffered, so a test can read it more than once.</summary>
    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>The audit entries written since <paramref name="skip"/> entries existed, oldest first.</summary>
    public async Task<List<BsonDocument>> AuditSince(long skip = 0) =>
        await AuditLog.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).Skip((int)skip)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async Task<long> AuditCount() =>
        await AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken);

    public async Task<BsonDocument> StoredUser(string id) =>
        await Users.Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id))).FirstAsync(TestContext.Current.CancellationToken);

    private async Task LoadSeededUsersAsync()
    {
        var users = await Users.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("createdAt").Ascending("_id"))
            .ToListAsync(TestContext.Current.CancellationToken);
        AdminId = users[0]["_id"].AsObjectId.ToString();
        MemberId = users[1]["_id"].AsObjectId.ToString();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
