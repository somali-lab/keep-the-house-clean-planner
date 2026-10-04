using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// Reads the stored <c>version</c> of a document straight from the collection (ADR-0022). A document that no use case of this application wrote yet has
/// none and counts as 0. A writer that is not an entity endpoint must raise it, so that an editor holding the ETag from before is told with a 412;
/// a test of such a writer asserts that the number went up.
/// </summary>
public static class StoredVersion
{
    public static async Task<int> VersionAsync(this IMongoCollection<BsonDocument> collection, ObjectId id) =>
        await VersionAsync(collection, new BsonDocument("_id", id));

    public static async Task<int> VersionAsync(this IMongoCollection<BsonDocument> collection, FilterDefinition<BsonDocument> filter)
    {
        var document = await collection.Find(filter).SingleAsync(TestContext.Current.CancellationToken);
        return document.TryGetValue("version", out var version) && version.IsNumeric ? version.ToInt32() : 0;
    }
}
