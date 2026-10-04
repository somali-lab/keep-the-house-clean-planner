using System.Globalization;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Statistics;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Statistics;

/// <summary>
/// <see cref="ForResettingStatistics"/>: the destructive part of a statistics reset (port of <c>resetStatisticsData</c> in
/// <c>data/statisticsReset.ts</c>), on the collections the Node server shares. Everything runs in the session of the running transaction, so a
/// failure in any step leaves nothing changed, and without a transaction nothing is written.
/// </summary>
/// <remarks>
/// The points ledger (<c>pointEntries</c>) is cleared here by <c>kind</c> and <c>date</c> only, with the Node field names, because the ledger
/// itself is slice 4.1. Executions and the four bonus kinds are the derived entries (ADR-0011, ADR-0012), redemptions are booked entries
/// (requirements 4.12); a restart from today removes all of them, a purge those dated before the boundary.
/// </remarks>
internal sealed class MongoStatisticsResetStore : ForResettingStatistics
{
    private const string TransientLabel = "TransientTransactionError";

    private static readonly string[] DerivedKinds = ["execution", "bonus_week_done", "bonus_week_ontime", "bonus_cycle_done", "bonus_cycle_ontime"];

    private readonly IMongoCollection<BsonDocument> occurrences;
    private readonly IMongoCollection<BsonDocument> tasks;
    private readonly IMongoCollection<BsonDocument> cycles;
    private readonly IMongoCollection<BsonDocument> pointEntries;
    private readonly IMongoCollection<BsonDocument> settings;

    public MongoStatisticsResetStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
        tasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
        cycles = database.GetCollection<BsonDocument>(MongoCollections.Cycles);
        pointEntries = database.GetCollection<BsonDocument>(MongoCollections.PointEntries);
        settings = database.GetCollection<BsonDocument>(MongoCollections.Settings);
    }

    public async Task<OneOf<StatisticsResetResult, PortError>> ResetAsync(StatisticsResetPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return new PortError("statistics.no_transaction: a statistics reset can only run inside a transaction, together with its audit entry.");
        }

        var boundary = new BsonDateTime(plan.Boundary.UtcDateTime);
        var now = new BsonDateTime(plan.Now.UtcDateTime);
        try
        {
            var deleted = await occurrences.DeleteManyAsync(session, new BsonDocument("date", new BsonDocument("$lt", boundary)), cancellationToken: cancellationToken).ConfigureAwait(false);
            long deletedRecorded = 0;
            long resetOccurrences = 0;
            long resetTasks = 0;
            if (plan.RestartFromToday)
            {
                // Recorded work has no planned state to return to, so it is deleted instead of reopened.
                deletedRecorded = (await occurrences.DeleteManyAsync(session, new BsonDocument("recordedDone", true), cancellationToken: cancellationToken).ConfigureAwait(false)).DeletedCount;
                var reopened = await occurrences.UpdateManyAsync(
                    session,
                    new BsonDocument("$or", new BsonArray
                    {
                        new BsonDocument("status", new BsonDocument("$ne", "open")),
                        new BsonDocument("completedAt", new BsonDocument("$ne", BsonNull.Value)),
                        new BsonDocument("completedBy", new BsonDocument("$ne", BsonNull.Value)),
                        new BsonDocument("skipReason", new BsonDocument("$ne", BsonNull.Value)),
                        new BsonDocument("pointsSnapshot", new BsonDocument("$ne", BsonNull.Value)),
                    }),
                    new BsonDocument("$set", new BsonDocument
                    {
                        { "status", "open" },
                        { "statusBeforeCompletion", BsonNull.Value },
                        { "completedAt", BsonNull.Value },
                        { "completedBy", BsonNull.Value },
                        { "skipReason", BsonNull.Value },
                        { "pointsSnapshot", BsonNull.Value },
                        { "updatedAt", now },
                    }),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                resetOccurrences = reopened.ModifiedCount;
                var reopenedTasks = await tasks.UpdateManyAsync(
                    session,
                    new BsonDocument("lastCompletedAt", new BsonDocument("$ne", BsonNull.Value)),
                    EntityVersioning.Raise(new BsonDocument("$set", new BsonDocument { { "lastCompletedAt", BsonNull.Value }, { "updatedAt", now } })),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                resetTasks = reopenedTasks.ModifiedCount;
            }

            // The ledger follows the history it is derived from: a restart removes every derived entry and every redemption, a purge those dated before the boundary.
            BsonDocument Ledger(BsonValue kind)
            {
                var filter = new BsonDocument("kind", kind);
                if (!plan.RestartFromToday)
                {
                    filter["date"] = new BsonDocument("$lt", boundary);
                }

                return filter;
            }

            var removedDerived = (await pointEntries.DeleteManyAsync(session, Ledger(new BsonDocument("$in", new BsonArray(DerivedKinds))), cancellationToken: cancellationToken).ConfigureAwait(false)).DeletedCount;
            var removedRedemptions = (await pointEntries.DeleteManyAsync(session, Ledger("redemption"), cancellationToken: cancellationToken).ConfigureAwait(false)).DeletedCount;

            var pastCycles = await cycles.DeleteManyAsync(session, new BsonDocument("index", new BsonDocument("$lt", plan.BoundaryCycle)), cancellationToken: cancellationToken).ConfigureAwait(false);

            if (plan.MovesBonusFloor)
            {
                await settings.UpdateOneAsync(
                    session,
                    new BsonDocument("_id", SettingsDocument.SingletonId),
                    EntityVersioning.Raise(new BsonDocument("$set", new BsonDocument { { "bonusFloor", plan.BonusFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "updatedAt", now } })),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return new StatisticsResetResult(
                Count(deleted.DeletedCount),
                Count(deletedRecorded),
                Count(resetOccurrences),
                Count(resetTasks),
                Count(pastCycles.DeletedCount),
                Count(removedDerived + removedRedemptions),
                Count(removedRedemptions));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return new PortError($"statistics.failed: could not reset the statistics ({e.GetType().Name}).");
        }
    }

    private static int Count(long value) => (int)Math.Min(value, int.MaxValue);

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));
}
