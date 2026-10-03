using Testcontainers.MongoDb;

[assembly: AssemblyFixture(typeof(Huishoudplanner.Integration.Tests.MongoContainerFixture))]

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// One real MongoDB container for the whole test assembly (decision D10). Test classes take it as a constructor
/// argument and ask for their own uniquely named database. Slice 0.6c upgrades this single place to a replica set
/// (<c>WithReplicaSet("rs0")</c>) so transactions work; nothing else needs to change.
/// </summary>
public sealed class MongoContainerFixture : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:8").Build();

    public string ConnectionString => container.GetConnectionString();

    /// <summary>A database name that no other test class uses.</summary>
    public static string NewDatabaseName() => $"test_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
