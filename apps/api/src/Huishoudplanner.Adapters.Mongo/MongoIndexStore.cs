using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

internal sealed class MongoIndexStore(IMongoDatabase database) : IIndexStore
{
    public async Task<bool> CollectionExistsAsync(string collection, CancellationToken cancellationToken)
    {
        var options = new ListCollectionNamesOptions { Filter = new BsonDocument("name", collection) };
        using var cursor = await database.ListCollectionNamesAsync(options, cancellationToken);
        return (await cursor.ToListAsync(cancellationToken)).Count > 0;
    }

    public Task CreateCollectionAsync(string collection, CancellationToken cancellationToken) =>
        database.CreateCollectionAsync(collection, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<ExistingIndex>> ListIndexesAsync(string collection, CancellationToken cancellationToken)
    {
        using var cursor = await database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(cancellationToken);
        var documents = await cursor.ToListAsync(cancellationToken);
        return
        [
            .. documents.Select(d => new ExistingIndex(
                d["name"].AsString,
                [.. d["key"].AsBsonDocument.Select(e => (e.Name, e.Value.ToInt32()))])),
        ];
    }

    public Task DropIndexAsync(string collection, string indexName, CancellationToken cancellationToken) =>
        database.GetCollection<BsonDocument>(collection).Indexes.DropOneAsync(indexName, cancellationToken);

    public async Task CreateIndexesAsync(string collection, IReadOnlyList<IndexSpec> indexes, CancellationToken cancellationToken)
    {
        var models = indexes.Select(spec =>
        {
            var keys = new BsonDocument();
            foreach (var (field, direction) in spec.Keys)
            {
                keys.Add(field, direction);
            }

            return new CreateIndexModel<BsonDocument>(
                keys,
                new CreateIndexOptions<BsonDocument>
                {
                    Name = spec.Name,
                    Unique = spec.Unique ? true : null,
                    PartialFilterExpression = spec.PartialFilter,
                });
        });
        await database.GetCollection<BsonDocument>(collection).Indexes.CreateManyAsync(models, cancellationToken);
    }
}
