namespace Huishoudplanner.Adapters.Mongo;

/// <summary>An index that exists in the database: its name and ordered keys.</summary>
internal sealed record ExistingIndex(string Name, IReadOnlyList<(string Field, int Direction)> Keys);

/// <summary>
/// The minimal collection operations the index ensurer needs. A seam so tests can inject server errors;
/// the production implementation is <see cref="MongoIndexStore"/>.
/// </summary>
internal interface IIndexStore
{
    Task<bool> CollectionExistsAsync(string collection, CancellationToken cancellationToken);

    Task CreateCollectionAsync(string collection, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExistingIndex>> ListIndexesAsync(string collection, CancellationToken cancellationToken);

    Task DropIndexAsync(string collection, string indexName, CancellationToken cancellationToken);

    Task CreateIndexesAsync(string collection, IReadOnlyList<IndexSpec> indexes, CancellationToken cancellationToken);
}
