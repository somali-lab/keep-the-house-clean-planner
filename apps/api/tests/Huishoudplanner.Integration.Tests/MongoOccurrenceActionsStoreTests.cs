using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The reads and the guarded update of the occurrence actions (slice 3.2) against a real MongoDB replica set: ranges, filters and the cursor in
/// the display order, one <c>$set</c> per changed field, the filter on the state that was read, the explicit nulls of the Node documents, and
/// the rule that an update runs inside a transaction only.
/// </summary>
public sealed class MongoOccurrenceActionsStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private const string Task1 = "a00000000000000000000001";
    private const string Task2 = "a00000000000000000000002";
    private const string CycleA = "c00000000000000000000001";
    private const string Anna = "0000000000000000000000a1";
    private const string Bram = "0000000000000000000000a2";

    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly ForStoringOccurrences store;
    private readonly ForRunningTransactions transactions;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MongoOccurrenceActionsStoreTests(MongoContainerFixture mongo)
    {
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithoutSeeding();
        using var client = factory.CreateClient();
        store = factory.Services.GetRequiredService<ForStoringOccurrences>();
        transactions = factory.Services.GetRequiredService<ForRunningTransactions>();
    }

    public void Dispose()
    {
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    private IMongoCollection<BsonDocument> Occurrences => mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("occurrences");

    private static DateTime Day(int day) => new(2026, 9, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A document the way the Node server stores a generated occurrence.</summary>
    private async Task<string> SeedAsync(int day, string name = "Badkamer", string? task = null, string cycle = CycleA, string? assignee = Anna, string status = "open", Action<BsonDocument>? tweak = null)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            { "_id", id },
            { "taskId", task is null ? ObjectId.GenerateNewId() : ObjectId.Parse(task) },
            { "cycleId", ObjectId.Parse(cycle) },
            { "planId", ObjectId.Parse("e00000000000000000000001") },
            { "date", Day(day) },
            { "plannedDate", Day(day) },
            { "assigneeId", assignee is null ? BsonNull.Value : ObjectId.Parse(assignee) },
            { "status", status },
            { "statusBeforeCompletion", BsonNull.Value },
            { "completedAt", BsonNull.Value },
            { "completedBy", BsonNull.Value },
            { "skipReason", BsonNull.Value },
            { "durationMinutesSnapshot", 30 },
            { "taskNameSnapshot", name },
            { "roomIdSnapshot", ObjectId.Parse("b00000000000000000000001") },
            { "roomNameSnapshot", "Badkamer" },
            { "origin", "generated" },
            { "createdAt", Now.UtcDateTime },
            { "updatedAt", Now.UtcDateTime },
        };
        tweak?.Invoke(document);
        await Occurrences.InsertOneAsync(document, cancellationToken: Ct);
        return id.ToString();
    }

    private async Task<Occurrence> FindAsync(string id) => (await store.FindAsync(id, Ct)).AsT0;

    private async Task<TResult> InTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> work)
    {
        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Commit(await work(ct)), Ct);
        return ran.AsT0;
    }

    private static OccurrenceQuery Query(int from, int to, string? assignee = null, OccurrenceStatus? status = null, OccurrenceCursor? after = null, int take = 100, OccurrenceOrder order = OccurrenceOrder.Ascending) =>
        new(new DateTimeOffset(Day(from), TimeSpan.Zero), new DateTimeOffset(Day(to + 1), TimeSpan.Zero), assignee, status, after, take, order);

    // ---- reads

    [Fact]
    public async Task Find_readsTheNodeDocumentAndAnUnknownOrMalformedIdIsNotFound()
    {
        var id = await SeedAsync(16);

        var found = await FindAsync(id);

        (found.Status, found.AssigneeId, found.TaskNameSnapshot, found.Origin, found.RecordedDone).Should().Be((OccurrenceStatus.Open, Anna, "Badkamer", OccurrenceOrigin.Generated, false));
        found.PeriodOwnerFrozen.Should().BeFalse();
        (await store.FindAsync("0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await store.FindAsync("x", Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Find_anExplicitNullPeriodOwnerReadsAsFrozenButAMissingOneDoesNot()
    {
        var frozen = await SeedAsync(16, tweak: d => d["periodOwnerId"] = BsonNull.Value);
        var missing = await SeedAsync(17);

        var a = await FindAsync(frozen);
        var b = await FindAsync(missing);

        (a.PeriodOwnerFrozen, a.PeriodOwnerId, a.HasPeriodOwner).Should().Be((true, null, true));
        b.HasPeriodOwner.Should().BeFalse();
    }

    [Fact]
    public async Task List_returnsTheRangeInDisplayOrderAndFiltersByAssigneeAndStatus()
    {
        await SeedAsync(16, "Wastafel", assignee: null);
        await SeedAsync(16, "Badkamer");
        await SeedAsync(14, "Badkamer", assignee: Bram, status: "done");
        await SeedAsync(20, "Badkamer");

        var week = (await store.ListAsync(Query(14, 19), Ct)).AsT0;
        var mine = (await store.ListAsync(Query(14, 20, assignee: Bram), Ct)).AsT0;
        var open = (await store.ListAsync(Query(14, 20, status: OccurrenceStatus.Open), Ct)).AsT0;

        week.Select(o => (o.Date.Day, o.TaskNameSnapshot)).Should().Equal((14, "Badkamer"), (16, "Badkamer"), (16, "Wastafel"));
        mine.Should().ContainSingle().Which.Status.Should().Be(OccurrenceStatus.Done);
        open.Should().HaveCount(3);
        (await store.ListAsync(Query(14, 20, assignee: "x"), Ct)).AsT0.Should().BeEmpty();
    }

    [Fact]
    public async Task List_pagesWithTheCursorWithoutRepeatingOrSkipping()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(16, "Zelfde naam");
        }

        await SeedAsync(15, "Eerder");
        var seen = new List<string>();
        OccurrenceCursor? after = null;
        while (true)
        {
            var page = (await store.ListAsync(Query(14, 20, after: after, take: 2), Ct)).AsT0;
            seen.AddRange(page.Select(o => o.Id));
            if (page.Count < 2)
            {
                break;
            }

            after = OccurrenceCursor.After(page[^1]);
        }

        seen.Should().HaveCount(6).And.OnlyHaveUniqueItems();
        var all = (await store.ListAsync(Query(14, 20), Ct)).AsT0;
        seen.Should().Equal(all.Select(o => o.Id));
    }

    private async Task<List<string>> PageThroughAsync(int from, int to, OccurrenceOrder order, int take, string? assignee = null, OccurrenceStatus? status = null)
    {
        var seen = new List<string>();
        OccurrenceCursor? after = null;
        while (true)
        {
            var page = (await store.ListAsync(Query(from, to, assignee, status, after, take, order), Ct)).AsT0;
            seen.AddRange(page.Select(o => o.Id));
            if (page.Count < take)
            {
                return seen;
            }

            after = OccurrenceCursor.After(page[^1], order);
        }
    }

    [Fact]
    public async Task List_descendingIsTheReverseOfAscendingAcrossPagesIncludingTiesOnTheSameDay()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(16, "Zelfde naam");
        }

        await SeedAsync(16, "Andere naam");
        await SeedAsync(15, "Eerder");
        await SeedAsync(18, "Later");
        await SeedAsync(17, "Later");

        var ascending = await PageThroughAsync(14, 20, OccurrenceOrder.Ascending, take: 2);
        var descending = await PageThroughAsync(14, 20, OccurrenceOrder.Descending, take: 2);
        var oneBigPage = (await store.ListAsync(Query(14, 20, order: OccurrenceOrder.Descending), Ct)).AsT0.Select(o => o.Id).ToList();

        ascending.Should().HaveCount(9).And.OnlyHaveUniqueItems();
        descending.Should().Equal(ascending.AsEnumerable().Reverse());
        oneBigPage.Should().Equal(descending);
    }

    [Fact]
    public async Task List_descendingStartsWithTheNewestDayAndCombinesWithTheFilters()
    {
        await SeedAsync(14, "A", assignee: Bram, status: "done");
        await SeedAsync(16, "B", assignee: Bram);
        await SeedAsync(18, "C", assignee: Anna);
        await SeedAsync(19, "D", assignee: Bram, status: "done");
        await SeedAsync(25, "Buiten bereik", assignee: Bram);

        var all = (await store.ListAsync(Query(14, 20, order: OccurrenceOrder.Descending), Ct)).AsT0;
        var mine = await PageThroughAsync(15, 20, OccurrenceOrder.Descending, take: 1, assignee: Bram);
        var done = (await store.ListAsync(Query(14, 20, status: OccurrenceStatus.Done, order: OccurrenceOrder.Descending), Ct)).AsT0;

        all.Select(o => o.Date.Day).Should().Equal(19, 18, 16, 14);
        mine.Should().HaveCount(2);
        done.Select(o => o.TaskNameSnapshot).Should().Equal("D", "A");
    }

    [Fact]
    public async Task FindLatestCompletion_isTheNewestCompletedAtOfTheDoneOccurrencesOfThatTask()
    {
        await SeedAsync(14, task: Task1, status: "done", tweak: d => d["completedAt"] = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc));
        await SeedAsync(15, task: Task1, status: "done", tweak: d => d["completedAt"] = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));
        await SeedAsync(16, task: Task1, status: "open", tweak: d => d["completedAt"] = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));
        await SeedAsync(17, task: Task2, status: "done", tweak: d => d["completedAt"] = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

        var latest = (await store.FindLatestCompletionAsync(Task1, Ct)).AsT0;
        var none = (await store.FindLatestCompletionAsync("a00000000000000000000009", Ct)).AsT0;

        latest.At.Should().Be(new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero));
        none.At.Should().BeNull();
    }

    // ---- update

    [Fact]
    public async Task Update_writesOnlyTheChangedFieldsAndTheNodeShapeOfTheValues()
    {
        var id = await SeedAsync(16);
        var before = await FindAsync(id);
        var after = before with
        {
            Status = OccurrenceStatus.Done,
            StatusBeforeCompletion = OccurrenceStatus.Open,
            CompletedAt = Now,
            CompletedBy = Bram,
            AssigneeId = Bram,
            PointsSnapshot = 30,
            PeriodOwnerId = Anna,
            PeriodOwnerFrozen = true,
        };
        var later = Now.AddMinutes(5);

        var stored = (await InTransactionAsync(ct => store.UpdateAsync(before, after, new OccurrenceGuard(OccurrenceStatus.Open), later, ct))).AsT0;

        stored.UpdatedAt.Should().Be(later);
        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);
        document["status"].AsString.Should().Be("done");
        document["statusBeforeCompletion"].AsString.Should().Be("open");
        document["completedAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        document["completedBy"].Should().Be(ObjectId.Parse(Bram));
        document["assigneeId"].Should().Be(ObjectId.Parse(Bram));
        document["pointsSnapshot"].BsonType.Should().Be(BsonType.Int32);
        document["periodOwnerId"].Should().Be(ObjectId.Parse(Anna));
        document["updatedAt"].ToUniversalTime().Should().Be(later.UtcDateTime);
        document["date"].ToUniversalTime().Should().Be(Day(16));
        document["createdAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Update_clearsFieldsWithExplicitNullsAndFreezesAnUnassignedOwnerAsNull()
    {
        var id = await SeedAsync(16, status: "done", assignee: null, tweak: d =>
        {
            d["completedAt"] = Now.UtcDateTime;
            d["completedBy"] = ObjectId.Parse(Bram);
            d["statusBeforeCompletion"] = "skipped";
            d["pointsSnapshot"] = 30;
        });
        var before = await FindAsync(id);
        var after = before with { Status = OccurrenceStatus.Skipped, StatusBeforeCompletion = null, CompletedAt = null, CompletedBy = null, PointsSnapshot = null, PeriodOwnerFrozen = true };

        (await InTransactionAsync(ct => store.UpdateAsync(before, after, new OccurrenceGuard(OccurrenceStatus.Done), Now, ct))).IsT0.Should().BeTrue();

        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);
        document["status"].AsString.Should().Be("skipped");
        document["statusBeforeCompletion"].IsBsonNull.Should().BeTrue();
        document["completedAt"].IsBsonNull.Should().BeTrue();
        document["completedBy"].IsBsonNull.Should().BeTrue();
        document["pointsSnapshot"].IsBsonNull.Should().BeTrue();
        document["periodOwnerId"].IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task Update_aWriteToAFieldOfAnotherActionIsNeverOverwritten()
    {
        var id = await SeedAsync(16);
        var before = await FindAsync(id);
        await Occurrences.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)), new BsonDocument("$set", new BsonDocument("skipReason", "van een ander")), cancellationToken: Ct);

        (await InTransactionAsync(ct => store.UpdateAsync(before, before with { AssigneeId = Bram }, new OccurrenceGuard(OccurrenceStatus.Open), Now, ct))).IsT0.Should().BeTrue();

        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct);
        document["skipReason"].AsString.Should().Be("van een ander");
        document["assigneeId"].Should().Be(ObjectId.Parse(Bram));
    }

    [Fact]
    public async Task Update_aStatusThatChangedSinceTheReadIsStateChangedAndNothingIsWritten()
    {
        var id = await SeedAsync(16);
        var before = await FindAsync(id);
        await Occurrences.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)), new BsonDocument("$set", new BsonDocument("status", "skipped")), cancellationToken: Ct);

        var result = await InTransactionAsync(ct => store.UpdateAsync(before, before with { AssigneeId = Bram }, new OccurrenceGuard(OccurrenceStatus.Open), Now, ct));

        result.IsT2.Should().BeTrue();
        (await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Ct))["assigneeId"].Should().Be(ObjectId.Parse(Anna));
    }

    [Fact]
    public async Task Update_aClaimOnlyMatchesWhileNobodyHasTheOccurrence()
    {
        var id = await SeedAsync(16, assignee: null);
        var before = await FindAsync(id);
        var guard = new OccurrenceGuard(OccurrenceStatus.Open, RequireUnassigned: true);

        var first = await InTransactionAsync(ct => store.UpdateAsync(before, before with { AssigneeId = Anna }, guard, Now, ct));
        var second = await InTransactionAsync(ct => store.UpdateAsync(before, before with { AssigneeId = Bram }, guard, Now, ct));

        first.IsT0.Should().BeTrue();
        second.IsT2.Should().BeTrue();
        (await FindAsync(id)).AssigneeId.Should().Be(Anna);
    }

    [Fact]
    public async Task Update_anOccurrenceThatIsGoneIsNotFound()
    {
        var id = await SeedAsync(16);
        var before = await FindAsync(id);
        await Occurrences.DeleteOneAsync(new BsonDocument("_id", ObjectId.Parse(id)), Ct);

        var result = await InTransactionAsync(ct => store.UpdateAsync(before, before with { AssigneeId = Bram }, new OccurrenceGuard(OccurrenceStatus.Open), Now, ct));

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Update_outsideATransactionWritesNothingAndReturnsAPortError()
    {
        var id = await SeedAsync(16);
        var before = await FindAsync(id);

        var result = await store.UpdateAsync(before, before with { AssigneeId = Bram }, new OccurrenceGuard(OccurrenceStatus.Open), Now, Ct);

        result.AsT3.Message.Should().StartWith("occurrences.no_transaction");
        (await FindAsync(id)).AssigneeId.Should().Be(Anna);
    }

    [Fact]
    public async Task Update_aRollbackLeavesTheOccurrenceAsItWas()
    {
        var id = await SeedAsync(16);
        var before = await FindAsync(id);

        var ran = await transactions.RunAsync(
            async ct =>
            {
                (await store.UpdateAsync(before, before with { AssigneeId = Bram }, new OccurrenceGuard(OccurrenceStatus.Open), Now, ct)).IsT0.Should().BeTrue();
                return TransactionOutcome.Abort(true);
            },
            Ct);

        ran.AsT0.Should().BeTrue();
        (await FindAsync(id)).AssigneeId.Should().Be(Anna);
    }
}
