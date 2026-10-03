using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Settings;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// <see cref="ForStoringSettings"/> (and the cycle anchor read of the calendar) on the singleton document of the <c>settings</c>
/// collection. Writes enlist in the transaction of <see cref="MongoTransactionRunner"/> when there is one (always, for a use case);
/// a read outside a transaction is a plain read. The modification time is the <see cref="TimeProvider"/>'s.
/// </summary>
internal sealed class MongoSettingsStore : ForStoringSettings, ForReadingCycleAnchor
{
    private static readonly FilterDefinition<BsonDocument> SingletonFilter = Builders<BsonDocument>.Filter.Eq("_id", SettingsDocument.SingletonId);

    private readonly IMongoCollection<BsonDocument> settings;
    private readonly TimeProvider timeProvider;

    public MongoSettingsStore(IMongoClient client, MongoOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        settings = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Settings);
        this.timeProvider = timeProvider;
    }

    public async Task<OneOf<HouseholdSettings, SettingsMissing, PortError>> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await Find(cancellationToken).ConfigureAwait(false);
            return document is null ? new SettingsMissing() : SettingsDocument.Read(document);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failure("read", e);
        }
    }

    public async Task<OneOf<DateOnly, SettingsMissing, PortError>> GetAnchorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var query = MongoTransactionContext.Session is { } session ? settings.Find(session, SingletonFilter) : settings.Find(SingletonFilter);
            var document = await query
                .Project(Builders<BsonDocument>.Projection.Include("cycleAnchorDate"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return document is null ? new SettingsMissing() : SettingsDocument.ReadAnchor(document);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failure("read the cycle anchor of", e);
        }
    }

    public async Task<OneOf<HouseholdSettings, SettingsMissing, PortError>> UpdateAsync(SettingsChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sets = SettingsDocument.ToSets(changes)
            .Select(set => Builders<BsonDocument>.Update.Set(set.Key, set.Value))
            .Append(Builders<BsonDocument>.Update.Set("updatedAt", new BsonDateTime(timeProvider.GetUtcNow().UtcDateTime)))
            .ToList();
        var options = new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After };
        try
        {
            var update = Builders<BsonDocument>.Update.Combine(sets);
            var document = MongoTransactionContext.Session is { } session
                ? await settings.FindOneAndUpdateAsync(session, SingletonFilter, update, options, cancellationToken).ConfigureAwait(false)
                : await settings.FindOneAndUpdateAsync(SingletonFilter, update, options, cancellationToken).ConfigureAwait(false);
            return document is null ? new SettingsMissing() : SettingsDocument.Read(document);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failure("write", e);
        }
    }

    public async Task<OneOf<bool, PortError>> InsertIfMissingAsync(HouseholdSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Only an actual insert counts: a concurrent start that inserted first leaves this one a no-op (idempotent seeding).
        var update = Builders<BsonDocument>.Update.Combine(
            SettingsDocument.ToDocument(settings).Where(element => element.Name != "_id")
                .Select(element => Builders<BsonDocument>.Update.SetOnInsert(element.Name, element.Value)));
        var options = new UpdateOptions { IsUpsert = true };
        try
        {
            var result = MongoTransactionContext.Session is { } session
                ? await this.settings.UpdateOneAsync(session, SingletonFilter, update, options, cancellationToken).ConfigureAwait(false)
                : await this.settings.UpdateOneAsync(SingletonFilter, update, options, cancellationToken).ConfigureAwait(false);
            return result.UpsertedId is not null;
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failure("seed", e);
        }
    }

    private Task<BsonDocument?> Find(CancellationToken cancellationToken) =>
        (MongoTransactionContext.Session is { } session ? settings.Find(session, SingletonFilter) : settings.Find(SingletonFilter))
            .FirstOrDefaultAsync(cancellationToken)!;

    // A document that cannot be read (FormatException, InvalidCastException, KeyNotFoundException) is as unusable as an unreachable one.
    private static bool IsInfrastructureFailure(Exception e) =>
        e is MongoException or TimeoutException or FormatException or InvalidCastException or KeyNotFoundException;

    // A transient transaction error is not a value: it propagates so the runner retries the whole attempt.
    private static bool IsTransient(Exception e) => e is MongoException m && m.HasErrorLabel("TransientTransactionError");

    private static PortError Failure(string action, Exception e) =>
        new($"settings.failed: could not {action} the settings ({e.GetType().Name}).");
}
