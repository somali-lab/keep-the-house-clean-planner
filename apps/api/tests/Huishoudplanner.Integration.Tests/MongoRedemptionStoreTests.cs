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
/// The Mongo adapter of the redemptions on a real replica set: the document of the Node server, the unique request key, the balance, the count,
/// the guard of the balance and the proof that the guard is what makes two bookings of one person safe (snapshot isolation alone allows write skew).
/// </summary>
public sealed class MongoRedemptionStoreTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private static NewRedemption Draft(ObjectId person, int points = 4, string? note = "Pizza", string? requestId = "redeem-key-aaaaaaaaaaaa") => new(
        person.ToString(),
        points,
        note,
        new DateTimeOffset(PointsHarness.Midnight("2026-09-16").ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(PointsHarness.Midnight("2026-09-14").ToUniversalTime(), TimeSpan.Zero),
        25,
        "EUR",
        requestId,
        At);

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
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();

        (await store.InsertAsync(Draft(h.P1), Ct)).AsT2.Message.Should().StartWith("redemptions.no_transaction");
        (await store.DeleteAsync(ObjectId.GenerateNewId().ToString(), Ct)).AsT2.Message.Should().StartWith("redemptions.no_transaction");
        (await store.LockBalanceAsync(h.P1.ToString(), Ct)).AsT1.Message.Should().StartWith("redemptions.no_transaction");
        (await h.EntriesAsync()).Should().BeEmpty();
        (await h.Database.GetCollection<BsonDocument>("pointGuards").CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be(0);
    }

    // ---- the document

    [Fact]
    public async Task Insert_storesTheDocumentOfTheNodeServerWithEveryFieldPresent()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();

        var inserted = await InTransaction(h, ct => store.InsertAsync(Draft(h.P1), ct));

        var entry = inserted.AsT0;
        (entry.Kind, entry.PersonId, entry.Amount, entry.Source, entry.Note, entry.CentsPerPointSnapshot, entry.CurrencyCodeSnapshot).Should().Be(
            (PointEntryKind.Redemption, h.P1.ToString(), -4, PointEntrySource.Live, "Pizza", 25, "EUR"));
        entry.Key.Should().Be("redemption:" + entry.Id);
        var document = (await h.EntriesAsync("redemption")).Should().ContainSingle().Subject;
        document.Names.Should().BeEquivalentTo(
            "_id", "key", "kind", "personId", "amount", "date", "weekStart", "periodStart", "occurrenceId", "taskId", "titleSnapshot", "source",
            "note", "centsPerPointSnapshot", "currencyCodeSnapshot", "requestId", "createdAt", "updatedAt");
        (document["periodStart"], document["occurrenceId"], document["taskId"], document["titleSnapshot"], document["requestId"]).Should().Be(
            (BsonNull.Value, BsonNull.Value, BsonNull.Value, (BsonValue)string.Empty, (BsonValue)"redeem-key-aaaaaaaaaaaa"));
        (document["amount"], document["date"], document["createdAt"]).Should().Be(
            ((BsonValue)(-4), PointsHarness.Midnight("2026-09-16"), new BsonDateTime(At.UtcDateTime)));
    }

    [Fact]
    public async Task Insert_aBookingWithoutANoteOrAKeyStoresExplicitNulls()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();

        await InTransaction(h, ct => store.InsertAsync(Draft(h.P1, note: null, requestId: null), ct));
        await InTransaction(h, ct => store.InsertAsync(Draft(h.P1, note: null, requestId: null), ct));

        var documents = await h.EntriesAsync("redemption");
        documents.Should().HaveCount(2, "a missing request key is no key: the unique index only covers string keys");
        documents.Should().OnlyContain(d => d["note"] == BsonNull.Value && d["requestId"] == BsonNull.Value);
    }

    [Fact]
    public async Task Insert_aRequestKeyThatIsTakenIsReportedAsAValueAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        await InTransaction(h, ct => store.InsertAsync(Draft(h.P1), ct));
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();

        var ran = await runner.RunAsync(
            async ct =>
            {
                var result = await store.InsertAsync(Draft(h.P2, 1), ct);
                return TransactionOutcome.Abort(result);
            },
            Ct);

        ran.AsT0.IsT1.Should().BeTrue();
        (await h.EntriesAsync("redemption")).Should().ContainSingle();
        var indexes = await h.Ledger.Indexes.List(Ct).ToListAsync(Ct);
        indexes.Should().Contain(i => i["name"] == "pointEntries_request_id_unique" && i["unique"] == true);
    }

    // ---- reads

    [Fact]
    public async Task Find_findsARedemptionByItsKeyAndByItsIdAndNeverAnotherKindOfEntry()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        var inserted = (await InTransaction(h, ct => store.InsertAsync(Draft(h.P1), ct))).AsT0;
        var execution = await h.InsertExecutionEntryAsync(h.P1, "2026-09-15", 5);
        var bonus = await h.InsertOtherEntryAsync(h.P1, "2026-09-14", 3, "bonus_week_done");

        (await store.FindByRequestIdAsync("redeem-key-aaaaaaaaaaaa", Ct)).AsT0.Should().Be(inserted);
        (await store.FindByRequestIdAsync("redeem-key-zzzzzzzzzzzz", Ct)).IsT1.Should().BeTrue();
        (await store.FindAsync(inserted.Id, Ct)).AsT0.Should().Be(inserted);
        (await store.FindAsync(execution.ToString(), Ct)).IsT1.Should().BeTrue();
        (await store.FindAsync(bonus.ToString(), Ct)).IsT1.Should().BeTrue();
        (await store.FindAsync("nope", Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Balance_isTheSumOfEveryEntryOfThePersonRedemptionsAndBonusesIncluded()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-01", 10);
        await h.InsertOtherEntryAsync(h.P1, "2026-09-07", 3, "bonus_week_done");
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-15", 50);
        await InTransaction(h, ct => store.InsertAsync(Draft(h.P1, 4), ct));

        (await store.BalanceOfAsync(h.P1.ToString(), Ct)).AsT0.Should().Be(9);
        (await store.BalanceOfAsync(h.P2.ToString(), Ct)).AsT0.Should().Be(50);
        (await store.BalanceOfAsync(h.Admin.ToString(), Ct)).AsT0.Should().Be(0);
    }

    [Fact]
    public async Task Count_countsOnlyTheRedemptions()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-15", 5);
        await h.InsertOtherEntryAsync(h.P1, "2026-09-14", 3, "bonus_week_done");

        (await store.CountAsync(Ct)).AsT0.Should().Be(0);
        await InTransaction(h, ct => store.InsertAsync(Draft(h.P1, 1, requestId: null), ct));
        await InTransaction(h, ct => store.InsertAsync(Draft(h.P1, 1, requestId: null), ct));
        (await store.CountAsync(Ct)).AsT0.Should().Be(2);
    }

    // ---- delete

    [Fact]
    public async Task Delete_removesARedemptionAndReturnsItAsStoredAndLeavesOtherKindsAlone()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        var inserted = (await InTransaction(h, ct => store.InsertAsync(Draft(h.P1), ct))).AsT0;
        var execution = await h.InsertExecutionEntryAsync(h.P1, "2026-09-15", 5);

        var removed = await InTransaction(h, ct => store.DeleteAsync(inserted.Id, ct));
        var derived = await InTransaction(h, ct => store.DeleteAsync(execution.ToString(), ct));
        var again = await InTransaction(h, ct => store.DeleteAsync(inserted.Id, ct));

        removed.AsT0.Should().Be(inserted);
        derived.IsT1.Should().BeTrue();
        again.IsT1.Should().BeTrue();
        (await h.EntriesAsync()).Should().ContainSingle().Which["kind"].AsString.Should().Be("execution");
    }

    // ---- the guard

    [Fact]
    public async Task Guard_isOneDocumentPerPersonThatCountsTheBookingsAndStaysOutOfTheLedger()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();

        await InTransaction(h, ct => store.LockBalanceAsync(h.P1.ToString(), ct));
        await InTransaction(h, ct => store.LockBalanceAsync(h.P1.ToString(), ct));
        await InTransaction(h, ct => store.LockBalanceAsync(h.P2.ToString(), ct));

        var guards = await h.Database.GetCollection<BsonDocument>("pointGuards").Find(new BsonDocument()).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
        guards.Should().HaveCount(2);
        guards.Single(g => g["_id"] == h.P1)["version"].AsInt32.Should().Be(2);
        guards.Single(g => g["_id"] == h.P2)["version"].AsInt32.Should().Be(1);
        (await h.EntriesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Guard_twoFirstBookingsOfOnePersonRaceForTheInsertAndTheLoserIsRetriedNeverAnException()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(
            () => runner.RunAsync(
                async ct =>
                {
                    var locked = await store.LockBalanceAsync(h.P1.ToString(), ct);
                    return TransactionOutcome.Commit(locked);
                },
                Ct),
            Ct)));

        results.Should().OnlyContain(r => r.IsT0 || r.IsT1, "the loser of the race is retried or ends with a conflict value, never with an exception");
        var guard = await h.Database.GetCollection<BsonDocument>("pointGuards").Find(new BsonDocument("_id", h.P1)).SingleAsync(Ct);
        guard["version"].AsInt32.Should().Be(results.Count(r => r.IsT0));
    }

    /// <summary>
    /// The booking of the use case, in miniature, with the barrier that makes the interleaving certain: both transactions read the balance before
    /// either writes. <paramref name="guarded"/> is the only difference between the two tests below.
    /// </summary>
    private async Task<(int Booked, long Balance)> RaceTwoBookingsAsync(PointsHarness h, bool guarded)
    {
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-15", 10);
        using var bothRead = new Barrier(2);
        var booked = 0;

        async Task<bool> BookAsync()
        {
            var ran = await runner.RunAsync<bool>(
                async ct =>
                {
                    if (guarded && (await store.LockBalanceAsync(h.P1.ToString(), ct)).IsT1)
                    {
                        return TransactionOutcome.Abort(false);
                    }

                    var balance = (await store.BalanceOfAsync(h.P1.ToString(), ct)).AsT0;
                    // Everybody that got this far has read the balance (the guarded loser conflicts before it gets here, so the wait times out).
                    bothRead.SignalAndWait(TimeSpan.FromMilliseconds(guarded ? 150 : 5000), Ct);
                    if (balance < 6)
                    {
                        return TransactionOutcome.Abort(false);
                    }

                    var inserted = await store.InsertAsync(Draft(h.P1, 6, note: null, requestId: null), ct);
                    return inserted.IsT0 ? TransactionOutcome.Commit(true) : TransactionOutcome.Abort(false);
                },
                Ct);
            return ran.IsT0 && ran.AsT0;
        }

        var outcomes = await Task.WhenAll(Task.Run(BookAsync, Ct), Task.Run(BookAsync, Ct));
        booked = outcomes.Count(o => o);
        var balanceAfter = (await store.BalanceOfAsync(h.P1.ToString(), Ct)).AsT0;
        return (booked, balanceAfter);
    }

    [Fact]
    public async Task Race_withoutTheGuardTwoBookingsOverdrawTheBalance_soSnapshotIsolationAloneIsNotEnough()
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        var (booked, balance) = await RaceTwoBookingsAsync(h, guarded: false);

        booked.Should().Be(2, "both transactions read a balance of 10 and neither wrote a document the other wrote");
        balance.Should().Be(-2);
    }

    [Fact]
    public async Task Race_withTheGuardOnlyOneOfTwoBookingsOfTheSamePersonSucceedsAndTheBalanceStaysAboveZero()
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        var (booked, balance) = await RaceTwoBookingsAsync(h, guarded: true);

        booked.Should().Be(1);
        balance.Should().Be(4);
    }

    [Fact]
    public async Task Race_theGuardIsPerPersonSoBookingsOfDifferentPeopleDoNotBlockEachOther()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var store = h.Services.GetRequiredService<ForStoringRedemptions>();
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-15", 10);
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-15", 10);

        var results = await Task.WhenAll(new[] { h.P1, h.P2 }.Select(person => Task.Run(
            () => runner.RunAsync(
                async ct =>
                {
                    await store.LockBalanceAsync(person.ToString(), ct);
                    var inserted = await store.InsertAsync(Draft(person, 6, note: null, requestId: null), ct);
                    return TransactionOutcome.Commit(inserted.IsT0);
                },
                Ct),
            Ct)));

        results.Select(r => r.AsT0).Should().Equal(true, true);
        (await store.BalanceOfAsync(h.P1.ToString(), Ct)).AsT0.Should().Be(4);
        (await store.BalanceOfAsync(h.P2.ToString(), Ct)).AsT0.Should().Be(4);
    }
}
