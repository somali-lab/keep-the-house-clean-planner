namespace Huishoudplanner.Adapters.Mongo;

/// <summary>Where the application's data lives. The connection string is a secret: never log it.</summary>
public sealed record MongoOptions
{
    public required string ConnectionString { get; init; }

    public required string DatabaseName { get; init; }

    public override string ToString() => $"MongoOptions {{ DatabaseName = {DatabaseName} }}";
}
