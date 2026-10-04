using System.Globalization;
using System.Text;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Transfer;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Transfer;

/// <summary>
/// <see cref="ForTransferringData"/> on the collections the Node server shares (<c>data/transfer.ts</c>). The file holds MongoDB relaxed Extended JSON,
/// so ids, dates and binary badge images survive the round trip. The replacement runs in the session of the running transaction, so a failure in any step
/// leaves nothing changed, and without a transaction nothing is written.
/// </summary>
/// <remarks>
/// The audit log is merged, not replaced: the entries of the file that are not in the log yet are added, found by their id before the insert, because in a
/// transaction a duplicate key error would abort the whole replacement. The points ledger is dropped except for the redemptions of the file (the derived
/// entries are rebuilt, ADR-0011) and the badge awards are dropped (derived, ADR-0014). The <c>pointGuards</c> bookkeeping of the redemptions is not household
/// data and is left alone.
/// </remarks>
internal sealed class MongoTransferStore : ForTransferringData
{
    private const string TransientLabel = "TransientTransactionError";
    private const int AuditLookupBatch = 1000;

    private static readonly JsonWriterSettings Relaxed = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    /// <summary>
    /// The collections whose documents carry a concurrency version (ADR-0022). The file never does (an import keeps only the known fields), so a replacement
    /// gives every document of such a collection one version above the highest one of the collection it replaces: an ETag that was handed out before the import
    /// can never match an imported document, whatever its id.
    /// </summary>
    private static readonly HashSet<string> Versioned =
    [
        MongoCollections.Settings, MongoCollections.Users, MongoCollections.Rooms, MongoCollections.Tasks, MongoCollections.CyclePlans, MongoCollections.Badges,
    ];

    private static readonly string[] ReplacedInOrder =
    [
        MongoCollections.Settings, MongoCollections.Users, MongoCollections.Rooms, MongoCollections.Tasks, MongoCollections.CyclePlans, MongoCollections.Cycles,
        MongoCollections.Occurrences,
    ];

    private readonly IMongoDatabase database;

    public MongoTransferStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        database = MongoClientFactory.GetDatabase(client, options);
    }

    private IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);

    public async Task<OneOf<byte[], PortError>> ExportAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            await using (var writer = new StreamWriter(buffer, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(
                    $"{{\"schemaVersion\":{TransferVersions.Current.ToString(CultureInfo.InvariantCulture)},\"exportedAt\":\"{now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}\",\"collections\":{{");
                var first = true;
                foreach (var name in ImportReader.Order)
                {
                    await writer.WriteAsync((first ? string.Empty : ",") + "\"" + name + "\":[");
                    first = false;
                    // Only the redemptions of the ledger are exported: they are booked, so they cannot be derived (requirements 4.12).
                    var filter = name == MongoCollections.PointEntries ? new BsonDocument("kind", "redemption") : [];
                    using var cursor = await Collection(name).Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToCursorAsync(cancellationToken).ConfigureAwait(false);
                    var firstDocument = true;
                    while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        foreach (var document in cursor.Current)
                        {
                            await writer.WriteAsync((firstDocument ? string.Empty : ",") + document.ToJson(Relaxed));
                            firstDocument = false;
                        }
                    }

                    await writer.WriteAsync("]");
                }

                await writer.WriteAsync("}}");
            }

            return buffer.ToArray();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return new PortError($"transfer.export_failed: could not read the data ({e.GetType().Name}).");
        }
    }

    public async Task<OneOf<ParsedImport, ValidationErrors, PortError>> ParseAsync(Stream body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var read = await ImportReader.ReadAsync(body, now, cancellationToken).ConfigureAwait(false);
        return read.Match<OneOf<ParsedImport, ValidationErrors, PortError>>(parsed => parsed, errors => errors);
    }

    public async Task<OneOf<ExistingCounts, PortError>> CountExistingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var redemptions = await Collection(MongoCollections.PointEntries)
                .CountDocumentsAsync(new BsonDocument("kind", "redemption"), cancellationToken: cancellationToken).ConfigureAwait(false);
            var badges = await Collection(MongoCollections.Badges).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ExistingCounts(Count(redemptions), Count(badges));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return new PortError($"transfer.count_failed: could not count the data ({e.GetType().Name}).");
        }
    }

    public async Task<OneOf<ImportResult, PortError>> ReplaceAsync(ParsedImport parsed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return new PortError("transfer.no_transaction: an import can only run inside a transaction, together with its audit entry.");
        }

        if (parsed is not MongoParsedImport file)
        {
            return new PortError("transfer.foreign_import: the import was not read by this adapter.");
        }

        try
        {
            var replaced = new Dictionary<string, int>(StringComparer.Ordinal);
            var removedBadges = await Collection(MongoCollections.Badges).CountDocumentsAsync(session, FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var name in ReplacedInOrder.Append(MongoCollections.Badges))
            {
                await ReplaceCollectionAsync(session, name, file.Documents[name], cancellationToken).ConfigureAwait(false);
                replaced[name] = file.Documents[name].Count;
            }

            // The derived part of the ledger is not exported: the old ledger is dropped, the booked redemptions of the file are put back and the caller
            // rebuilds the rest from the new occurrences (ADR-0011).
            var ledger = Collection(MongoCollections.PointEntries);
            var removedRedemptions = await ledger.CountDocumentsAsync(session, new BsonDocument("kind", "redemption"), cancellationToken: cancellationToken).ConfigureAwait(false);
            var removedPointEntries = (await ledger.DeleteManyAsync(session, FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false)).DeletedCount;
            var redemptions = file.Documents[MongoCollections.PointEntries];
            if (redemptions.Count > 0)
            {
                await ledger.InsertManyAsync(session, redemptions, new InsertManyOptions { IsOrdered = true }, cancellationToken).ConfigureAwait(false);
            }

            replaced[MongoCollections.PointEntries] = redemptions.Count;

            // Awards are derived from the executions and the badge definitions of the file: drop them, the caller rebuilds them (ADR-0014).
            var removedAwards = (await Collection(MongoCollections.BadgeAwards).DeleteManyAsync(session, FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false)).DeletedCount;
            var auditAdded = await MergeAuditAsync(session, file.Documents[MongoCollections.AuditLog], cancellationToken).ConfigureAwait(false);

            return new ImportResult(
                new ReplacedCounts(
                    replaced[MongoCollections.Settings], replaced[MongoCollections.Users], replaced[MongoCollections.Rooms], replaced[MongoCollections.Tasks],
                    replaced[MongoCollections.CyclePlans], replaced[MongoCollections.Cycles], replaced[MongoCollections.Occurrences],
                    replaced[MongoCollections.PointEntries], replaced[MongoCollections.Badges]),
                auditAdded,
                Count(removedPointEntries),
                Count(removedRedemptions),
                Count(removedBadges),
                Count(removedAwards));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return new PortError($"transfer.failed: could not replace the data ({e.GetType().Name}).");
        }
    }

    private async Task ReplaceCollectionAsync(IClientSessionHandle session, string name, List<BsonDocument> documents, CancellationToken cancellationToken)
    {
        var collection = Collection(name);
        if (Versioned.Contains(name) && documents.Count > 0)
        {
            var highest = await collection.Find(session, FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Descending(EntityVersioning.Field))
                .Limit(1)
                .Project(new BsonDocument(EntityVersioning.Field, 1))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var next = (highest is null ? 0 : EntityVersioning.VersionOf(highest)) + 1;
            foreach (var document in documents)
            {
                document[EntityVersioning.Field] = next;
            }
        }

        await collection.DeleteManyAsync(session, FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (documents.Count > 0)
        {
            await collection.InsertManyAsync(session, documents, new InsertManyOptions { IsOrdered = true }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds the entries of the file that the log does not have yet (the log is append-only); a repeat inside the file counts once.</summary>
    private async Task<int> MergeAuditAsync(IClientSessionHandle session, List<BsonDocument> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        var log = Collection(MongoCollections.AuditLog);
        var present = new HashSet<ObjectId>();
        for (var start = 0; start < entries.Count; start += AuditLookupBatch)
        {
            var ids = entries.Skip(start).Take(AuditLookupBatch).Select(e => e["_id"]).ToList();
            var found = await log.Find(session, new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids))))
                .Project(new BsonDocument("_id", 1)).ToListAsync(cancellationToken).ConfigureAwait(false);
            present.UnionWith(found.Select(d => d["_id"].AsObjectId));
        }

        var added = new List<BsonDocument>();
        foreach (var entry in entries)
        {
            if (present.Add(entry["_id"].AsObjectId))
            {
                added.Add(entry);
            }
        }

        if (added.Count > 0)
        {
            await log.InsertManyAsync(session, added, new InsertManyOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        return added.Count;
    }

    private static int Count(long value) => (int)Math.Min(value, int.MaxValue);

    /// <summary>An infrastructure failure becomes a value; a transient transaction error propagates so that the runner retries the attempt.</summary>
    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException or BsonException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));
}
