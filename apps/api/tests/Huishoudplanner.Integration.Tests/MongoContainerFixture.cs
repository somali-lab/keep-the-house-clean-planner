using Testcontainers.MongoDb;

[assembly: AssemblyFixture(typeof(Huishoudplanner.Integration.Tests.MongoContainerFixture))]

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// One real MongoDB container for the whole test assembly (decision D10). Test classes take it as a constructor
/// argument and ask for their own uniquely named database. The container is a single-node replica set
/// (<c>WithReplicaSet("rs0")</c>, ADR-0021) so multi-document transactions work like in production; its data
/// directory is on tmpfs and test commands are enabled for the failpoints of the transaction tests because every majority write waits for the journal.
/// </summary>
public sealed class MongoContainerFixture : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:8").WithReplicaSet("rs0").WithTmpfsMount("/data/db").WithCommand("--setParameter", "enableTestCommands=1").Build();

    public string ConnectionString => container.GetConnectionString();

    /// <summary>A database name that no other test class uses.</summary>
    public static string NewDatabaseName() => $"test_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
