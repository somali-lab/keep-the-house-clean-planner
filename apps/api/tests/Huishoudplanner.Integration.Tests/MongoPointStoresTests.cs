using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The Mongo adapters of the points ledger on a real replica set: the guarded bulk write of a reconciliation, the unique key, the tolerance for
/// entries of kinds this application does not write, the sums and the list order, and the filtered writes of the field backfill.
/// </summary>
public sealed class MongoPointStoresTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Day(string day) => new(PointsHarness.Midnight(day).ToUniversalTime(), TimeSpan.Zero);

    private static ExecutionEntryFields Fields(ObjectId person, ObjectId occurrence, int amount = 30, string day = "2026-09-14", ObjectId? task = null) =>
        new(person.ToString(), amount, Day(day), Day("2026-09-14"), occurrence.ToString(), task?.ToString(), "Stofzuigen");

    private static async Task<T> InTransaction<T>(PointsHarness h, Func<CancellationToken, Task<T>> work)
    {
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        var ran = await runner.RunAsync(async ct => TransactionOutcome.Commit(await work(ct)), Ct);
        return ran.AsT0;
    }

    // ---- writes belong to a transaction

    [Fact]
    public async Task Writes_outsideATransactionAreRefusedAndWriteNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var backfill = h.Services.GetRequiredService<ForBackfillingPoints>();
        var fields = Fields(h.P1, ObjectId.GenerateNewId());
        var stored = new PointEntry(ObjectId.GenerateNewId().ToString(), "execution:x", PointEntryKind.Execution, h.P1.ToString(), 1, At, At, null, null, null, "x", PointEntrySource.Live, null, null, null, At, At);

        (await entries.InsertExecutionAsync("execution:" + fields.OccurrenceId, fields, PointEntrySource.Live, At, Ct)).AsT1.Message.Should().StartWith("pointEntries.no_transaction");
        (await entries.UpdateExecutionAsync(stored, fields, PointEntrySource.Live, At, Ct)).AsT2.Message.Should().StartWith("pointEntries.no_transaction");
        (await entries.DeleteAsync(stored, Ct)).AsT1.Message.Should().StartWith("pointEntries.no_transaction");
        (await entries.ApplyChangesAsync(new PointEntryChanges([], [], []), At, Ct)).AsT1.Message.Should().StartWith("pointEntries.no_transaction");
        (await backfill.DefaultMissingTaskPointsAsync(Ct)).AsT1.Message.Should().StartWith("points.no_transaction");
        (await backfill.SetMissingSnapshotsAsync([new SnapshotWrite(ObjectId.GenerateNewId().ToString(), 1)], Ct)).AsT1.Message.Should().StartWith("points.no_transaction");
        (await h.EntriesAsync()).Should().BeEmpty();
    }

    // ---- the unique key and the live writes

    [Fact]
    public async Task Insert_storesTheDocumentOfTheNodeServerAndASecondInsertOfTheSameKeyFailsTheTransaction()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var occurrence = ObjectId.GenerateNewId();
        var task = ObjectId.GenerateNewId();
        var key = "execution:" + occurrence;

        var inserted = await InTransaction(h, ct => entries.InsertExecutionAsync(key, Fields(h.P1, occurrence, task: task), PointEntrySource.Live, At, ct));

        var entry = inserted.AsT0;
        (entry.Key, entry.Kind, entry.PersonId, entry.Amount, entry.Source).Should().Be((key, PointEntryKind.Execution, h.P1.ToString(), 30, PointEntrySource.Live));
        var document = (await h.EntryOfAsync(occurrence))!;
        document.Names.Should().BeEquivalentTo("_id", "key", "kind", "personId", "amount", "date", "weekStart", "periodStart", "occurrenceId", "taskId", "titleSnapshot", "source", "createdAt", "updatedAt");
        (document["periodStart"], document["taskId"], document["createdAt"]).Should().Be((BsonNull.Value, task, new BsonDateTime(At.UtcDateTime)));

        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        var duplicate = await runner.RunAsync<OneOf<PointEntry, PortError>>(
            async ct =>
            {
                var result = await entries.InsertExecutionAsync(key, Fields(h.P2, occurrence), PointEntrySource.Live, At, ct);
                return result.IsT1 ? TransactionOutcome.Abort(result) : TransactionOutcome.Commit(result);
            },
            Ct);
        // A lost race for the key is a transient transaction error: the runner reruns the attempt (which would then read the winner) and, when the
        // work insists on inserting, gives up with a value-free transient error.
        duplicate.IsT2.Should().BeTrue();
        duplicate.AsT2.Message.Should().StartWith("mongo.transient");
        (await h.EntriesAsync()).Should().ContainSingle();
        var indexes = await h.Ledger.Indexes.List(Ct).ToListAsync(Ct);
        indexes.Should().Contain(i => i["name"] == "pointEntries_key_unique" && i["unique"] == true);
    }

    [Fact]
    public async Task Update_setsTheDerivedFieldsOnlyAndLeavesTheFieldsOfOtherKindsOfWritersAlone()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var occurrence = ObjectId.GenerateNewId();
        var id = await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 30, occurrenceId: occurrence);
        await h.Ledger.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument { { "requestId", "keep-me" }, { "note", "keep-too" } }), cancellationToken: Ct);
        var current = (await entries.FindByKeyAsync("execution:" + occurrence, Ct)).AsT0;

        var updated = await InTransaction(h, ct => entries.UpdateExecutionAsync(current, Fields(h.P2, occurrence, 12, "2026-09-15"), PointEntrySource.Live, At.AddHours(1), ct));

        updated.AsT0.Should().BeEquivalentTo(new { PersonId = h.P2.ToString(), Amount = 12 });
        var document = (await h.EntryOfAsync(occurrence))!;
        (document["personId"], document["amount"].AsInt32, document["date"], document["updatedAt"]).Should().Be((h.P2, 12, PointsHarness.Midnight("2026-09-15"), new BsonDateTime(At.AddHours(1).UtcDateTime)));
        (document["requestId"].AsString, document["note"].AsString, document["createdAt"]).Should().Be(("keep-me", "keep-too", new BsonDateTime(DateTime.Parse(PointsHarness.Now, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal))));
    }

    [Fact]
    public async Task Delete_removesTheEntryOnceAndTellsWhenItWasAlreadyGone()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var occurrence = ObjectId.GenerateNewId();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 30, occurrenceId: occurrence);
        var current = (await entries.FindByKeyAsync("execution:" + occurrence, Ct)).AsT0;

        var first = await InTransaction(h, ct => entries.DeleteAsync(current, ct));
        var second = await InTransaction(h, ct => entries.DeleteAsync(current, ct));

        (first.AsT0, second.AsT0).Should().Be((true, false));
        (await entries.FindByKeyAsync("execution:" + occurrence, Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Insert_twoOverlappingTransactionsOnOneKey_theLoserReruns_readsTheWinnersEntryAndUpdatesItInsteadOfFailing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        var occurrence = ObjectId.GenerateNewId();
        var key = "execution:" + occurrence;
        var firstInserted = new TaskCompletionSource();
        var secondStarted = new TaskCompletionSource();

        async Task<TransactionOutcome<string>> Upsert(bool first, CancellationToken ct)
        {
            var found = await entries.FindByKeyAsync(key, ct);
            if (found.IsT0)
            {
                var updated = await entries.UpdateExecutionAsync(found.AsT0, Fields(h.P2, occurrence, 8), PointEntrySource.Live, At, ct);
                return TransactionOutcome.Commit("updated:" + updated.IsT0);
            }

            if (!first)
            {
                secondStarted.TrySetResult();
            }

            var inserted = await entries.InsertExecutionAsync(key, Fields(h.P1, occurrence), PointEntrySource.Live, At, ct);
            if (first)
            {
                firstInserted.SetResult();
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                await Task.Delay(200, ct);
            }

            return TransactionOutcome.Commit("inserted:" + inserted.IsT0);
        }

        var winner = runner.RunAsync(ct => Upsert(true, ct), Ct);
        await firstInserted.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var loser = runner.RunAsync(ct => Upsert(false, ct), Ct);

        (await winner).AsT0.Should().Be("inserted:True");
        (await loser).AsT0.Should().Be("updated:True");
        (await h.EntriesAsync()).Should().ContainSingle().Which["personId"].Should().Be(h.P2);
    }

    // ---- the guarded bulk write of a reconciliation

    [Fact]
    public async Task ApplyChanges_insertsWithSourceBackfillUpdatesWithRecomputeAndDeletes_countingOnlyWhatHappened()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var (toUpdate, toDelete, fresh) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 30, occurrenceId: toUpdate);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 4, occurrenceId: toDelete);
        var read = (await entries.FindExecutionEntriesAsync(Ct)).AsT0.ToDictionary(e => e.Key);

        var applied = await InTransaction(h, ct => entries.ApplyChangesAsync(
            new PointEntryChanges(
                [new PointEntryInsert("execution:" + fresh, Fields(h.P2, fresh))],
                [new PointEntryUpdate(read["execution:" + toUpdate], Fields(h.P2, toUpdate, 7))],
                [read["execution:" + toDelete]]),
            At,
            ct));

        applied.AsT0.Should().Be(new AppliedPointEntryChanges(1, 1, 1));
        (await h.EntryOfAsync(fresh))!["source"].AsString.Should().Be("backfill");
        var updated = (await h.EntryOfAsync(toUpdate))!;
        (updated["source"].AsString, updated["personId"], updated["amount"].AsInt32).Should().Be(("recompute", h.P2, 7));
        (await h.EntryOfAsync(toDelete)).Should().BeNull();
    }

    [Fact]
    public async Task ApplyChanges_leavesAnEntryAloneThatChangedAfterItWasRead_forAnUpdateAndADelete()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        var (a, b) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 30, occurrenceId: a);
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-14", 30, occurrenceId: b);
        var read = (await entries.FindExecutionEntriesAsync(Ct)).AsT0.ToDictionary(e => e.Key);
        // A live sync moves both entries after the reconciliation read them.
        await h.Ledger.UpdateOneAsync(new BsonDocument("key", "execution:" + a), new BsonDocument("$set", new BsonDocument("amount", 7)), cancellationToken: Ct);
        await h.Ledger.UpdateOneAsync(new BsonDocument("key", "execution:" + b), new BsonDocument("$set", new BsonDocument("personId", h.P1)), cancellationToken: Ct);

        var applied = await InTransaction(h, ct => entries.ApplyChangesAsync(
            new PointEntryChanges([], [new PointEntryUpdate(read["execution:" + a], Fields(h.P1, a, 3))], [read["execution:" + b]]),
            At,
            ct));

        applied.AsT0.Should().Be(new AppliedPointEntryChanges(0, 0, 0));
        (await h.EntryOfAsync(a))!["amount"].AsInt32.Should().Be(7);
        (await h.EntryOfAsync(b))!["personId"].Should().Be(h.P1);
    }

    // ---- reading

    [Fact]
    public async Task Reads_skipAnEntryOfAKindThisApplicationDoesNotKnowAndMapEveryKnownKind()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 5);
        var bonus = await h.InsertOtherEntryAsync(h.P1, "2026-09-14", 10, "bonus_cycle_ontime");
        var redemption = await h.InsertOtherEntryAsync(h.P1, "2026-09-14", -3, "redemption");
        await h.Ledger.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "key", "x:1" }, { "kind", "kind-from-the-future" }, { "personId", h.P1 }, { "amount", 1 }, { "date", PointsHarness.Midnight("2026-09-14") } }, cancellationToken: Ct);
        var query = new PointEntryQuery(h.P1.ToString(), Day("2026-09-14"), Day("2026-09-15"), null, 50);

        var listed = (await entries.ListAsync(query, Ct)).AsT0;
        var executions = (await entries.FindExecutionEntriesAsync(Ct)).AsT0;

        listed.Select(e => e.Kind).Should().BeEquivalentTo([PointEntryKind.Execution, PointEntryKind.BonusCycleOnTime, PointEntryKind.Redemption]);
        executions.Should().ContainSingle().Which.Kind.Should().Be(PointEntryKind.Execution);
        var mappedRedemption = listed.Single(e => e.Id == redemption.ToString());
        (mappedRedemption.Note, mappedRedemption.CentsPerPointSnapshot, mappedRedemption.CurrencyCodeSnapshot, mappedRedemption.Amount).Should().Be(("Pizza", 5, "EUR", -3));
        listed.Single(e => e.Id == bonus.ToString()).PeriodStart.Should().Be(Day("2026-09-14"));
    }

    [Fact]
    public async Task List_isNewestDateFirstThenByIdAndTheCursorContinuesInsideADate()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 5);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 2);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-20", 1);
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-20", 99);
        var query = new PointEntryQuery(h.P1.ToString(), Day("2026-09-01"), Day("2026-09-21"), null, 2);

        var first = (await entries.ListAsync(query, Ct)).AsT0;
        var second = (await entries.ListAsync(query with { After = PointEntryCursor.After(first[^1]), Take = 5 }, Ct)).AsT0;

        first.Select(e => e.Amount).Should().Equal(1, 5);
        second.Select(e => e.Amount).Should().Equal(2);
    }

    [Fact]
    public async Task Sum_groupsPerPersonWithTheExecutionsTheBonusesAndTheRedeemedPointsOverARange()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var entries = h.Services.GetRequiredService<ForStoringPointEntries>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 20);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-15", 5);
        await h.InsertOtherEntryAsync(h.P1, "2026-09-16", 10, "bonus_week_done");
        await h.InsertOtherEntryAsync(h.P1, "2026-09-17", -6, "redemption");
        await h.InsertExecutionEntryAsync(h.P2, "2026-08-01", 3);

        var all = (await entries.SumByPersonAsync(null, null, Ct)).AsT0.ToDictionary(t => t.PersonId);
        var ranged = (await entries.SumByPersonAsync(Day("2026-09-15"), Day("2026-09-17"), Ct)).AsT0.ToDictionary(t => t.PersonId);

        all[h.P1.ToString()].Should().Be(new PointTotal(h.P1.ToString(), 29, 2, 10, 6));
        all[h.P2.ToString()].Should().Be(new PointTotal(h.P2.ToString(), 3, 1, 0, 0));
        ranged.Should().ContainKey(h.P1.ToString()).WhoseValue.Should().Be(new PointTotal(h.P1.ToString(), 15, 1, 10, 0));
        ranged.Should().NotContainKey(h.P2.ToString());
    }

    // ---- the field backfill

    [Fact]
    public async Task Backfill_defaultsTheTasksWithoutPointsOnceAndWritesTheMissingSnapshotsWithoutOverwritingOne()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var backfill = h.Services.GetRequiredService<ForBackfillingPoints>();
        var without = await h.InsertTaskAsync("Oud", 45, points: null);
        var explicitPoints = await h.InsertTaskAsync("Nieuw", 45, points: 7);
        var huge = await h.InsertTaskAsync("Lang", 5000, points: null);
        var open = await h.InsertDoneOccurrenceAsync("2026-09-14", without, h.P1);
        var has = await h.InsertDoneOccurrenceAsync("2026-09-15", without, h.P1, snapshot: 9);

        var first = (await InTransaction(h, ct => backfill.DefaultMissingTaskPointsAsync(ct))).AsT0;
        var second = (await InTransaction(h, ct => backfill.DefaultMissingTaskPointsAsync(ct))).AsT0;
        var written = (await InTransaction(h, ct => backfill.SetMissingSnapshotsAsync([new SnapshotWrite(open.ToString(), 45), new SnapshotWrite(has.ToString(), 1)], ct))).AsT0;

        first.TaskIds.Should().BeEquivalentTo([without.ToString(), huge.ToString()]);
        (first.Count, second.Count).Should().Be((2, 0));
        ((await h.StoredTaskAsync(without))["points"].AsInt32, (await h.StoredTaskAsync(huge))["points"].AsInt32, (await h.StoredTaskAsync(explicitPoints))["points"].AsInt32).Should().Be((45, 1000, 7));
        written.Should().Be(1);
        ((await h.StoredOccurrenceAsync(open))["pointsSnapshot"].AsInt32, (await h.StoredOccurrenceAsync(has))["pointsSnapshot"].AsInt32).Should().Be((45, 9));
    }

    [Fact]
    public async Task Backfill_readsDoneOccurrencesIncludingARowWithoutAReadableDateAndTheTaskValues()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var backfill = h.Services.GetRequiredService<ForBackfillingPoints>();
        var task = await h.InsertTaskAsync("Oud", 45, points: null);
        var done = await h.InsertDoneOccurrenceAsync("2026-09-14", task, h.P1, h.P2, pointsOverride: 0);
        var broken = await h.InsertDoneOccurrenceAsync("2026-09-14", null, null, brokenDate: true, name: "Kapot");
        await h.Occurrences.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "status", "open" }, { "date", PointsHarness.Midnight("2026-09-14") } }, cancellationToken: Ct);

        var sources = (await backfill.FindDoneOccurrencesAsync(Ct)).AsT0.ToDictionary(s => s.Id);
        var values = (await backfill.FindTaskPointValuesAsync(Ct)).AsT0;

        sources.Should().HaveCount(2, "only the done occurrences");
        sources[done.ToString()].Should().BeEquivalentTo(new
        {
            TaskId = task.ToString(),
            Status = OccurrenceStatus.Done,
            CompletedBy = h.P1.ToString(),
            AssigneeId = h.P2.ToString(),
            PointsSnapshot = (int?)null,
            PointsOverride = 0,
            DurationMinutesSnapshot = 30,
            TaskNameSnapshot = "Stofzuigen",
        });
        sources[done.ToString()].Date.Should().Be(Day("2026-09-14"));
        sources[broken.ToString()].Date.Should().BeNull();
        values.Should().ContainSingle().Which.Should().Be(new TaskPointValue(task.ToString(), null, 45));
    }
}
