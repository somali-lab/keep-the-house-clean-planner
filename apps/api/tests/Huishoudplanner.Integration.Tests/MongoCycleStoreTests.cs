using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>The cycle store against a real MongoDB replica set: documents of the Node shape (day keys as strings), index order, and writes only inside a transaction.</summary>
public sealed class MongoCycleStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private const string Plan = "e00000000000000000000001";

    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly ForStoringCycles store;
    private readonly ForRunningTransactions transactions;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MongoCycleStoreTests(MongoContainerFixture mongo)
    {
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithoutSeeding();
        using var client = factory.CreateClient();
        store = factory.Services.GetRequiredService<ForStoringCycles>();
        transactions = factory.Services.GetRequiredService<ForRunningTransactions>();
    }

    public void Dispose()
    {
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    private IMongoCollection<BsonDocument> Cycles => mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("cycles");

    private static NewCycle NewCycle(int index, string? plan = Plan)
    {
        var start = new DateOnly(2026, 9, 14).AddDays(index * 28);
        return new NewCycle(index, start, start.AddDays(27), plan, Now, "run-1");
    }

    private async Task InsertAsync(params NewCycle[] cycles)
    {
        var ran = await transactions.RunAsync(async ct =>
        {
            foreach (var cycle in cycles)
            {
                (await store.InsertAsync(cycle, ct)).AsT0.Should().NotBeNull();
            }

            return TransactionOutcome.Commit(true);
        }, Ct);
        ran.IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task Writes_outsideATransaction_writeNothingAndReturnAPortError()
    {
        (await store.InsertAsync(NewCycle(0), Ct)).AsT1.Message.Should().StartWith("cycles.no_transaction");
        (await store.UpdateBoundsAsync("0123456789abcdef01234567", new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18), Now, "run-2", Ct)).AsT2.Message.Should().StartWith("cycles.no_transaction");
        (await store.UpdatePlanAsync("0123456789abcdef01234567", Plan, Now, "run-2", Ct)).AsT2.Message.Should().StartWith("cycles.no_transaction");
        (await Cycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ACycle_isStoredWithExactlyTheFieldsAndTypesOfTheNodeServer()
    {
        await InsertAsync(NewCycle(1));

        var document = await Cycles.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        document.Names.Should().BeEquivalentTo(["_id", "index", "startDate", "endDate", "planId", "generatedAt", "generationRunId"]);
        document["index"].BsonType.Should().Be(BsonType.Int32);
        document["startDate"].AsString.Should().Be("2026-10-12");
        document["endDate"].AsString.Should().Be("2026-11-08");
        document["planId"].Should().Be(ObjectId.Parse(Plan));
        document["generatedAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        document["generationRunId"].AsString.Should().Be("run-1");
    }

    [Fact]
    public async Task ACycleWithoutAPlan_storesAnExplicitNull()
    {
        await InsertAsync(NewCycle(0, plan: null));

        (await Cycles.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["planId"].IsBsonNull.Should().BeTrue();
        (await store.FindByIndexAsync(0, Ct)).AsT0.PlanId.Should().BeNull();
    }

    [Fact]
    public async Task Cycles_areListedInIndexOrder_negativeFirst_andPagedByCursor()
    {
        await InsertAsync(NewCycle(1), NewCycle(-1), NewCycle(0), NewCycle(2));

        var first = (await store.ListAsync(null, 2, Ct)).AsT0;
        var rest = (await store.ListAsync(new CycleCursor(first[^1].Index), 10, Ct)).AsT0;

        first.Select(c => c.Index).Should().Equal(-1, 0);
        rest.Select(c => c.Index).Should().Equal(1, 2);
        first[0].StartDate.Should().Be(new DateOnly(2026, 8, 17));
        (await store.FindByIndexAsync(7, Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task TheUniqueIndex_refusesASecondCycleWithTheSameIndex_asAPortError()
    {
        await InsertAsync(NewCycle(0));

        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Abort(await store.InsertAsync(NewCycle(0), ct)), Ct);

        ran.AsT0.AsT1.Message.Should().StartWith("cycles.failed");
        (await Cycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task UpdatingTheBoundsAndThePlan_setsOnlyTheirFieldsAndStampsTheRun()
    {
        await InsertAsync(NewCycle(0, plan: null));
        var id = (await store.FindByIndexAsync(0, Ct)).AsT0.Id;
        var later = Now.AddDays(1);

        var ran = await transactions.RunAsync(async ct =>
        {
            var bounds = (await store.UpdateBoundsAsync(id, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18), later, "run-2", ct)).AsT0;
            var plan = (await store.UpdatePlanAsync(id, Plan, later.AddHours(1), "run-3", ct)).AsT0;
            var missing = await store.UpdatePlanAsync("0123456789abcdef01234567", Plan, later, "run-3", ct);
            return TransactionOutcome.Commit((bounds, plan, missing.IsT1));
        }, Ct);

        var (bounds, plan, missingIsNotFound) = ran.AsT0;
        bounds.StartDate.Should().Be(new DateOnly(2026, 9, 21));
        bounds.PlanId.Should().BeNull();
        bounds.GenerationRunId.Should().Be("run-2");
        plan.PlanId.Should().Be(Plan);
        plan.GeneratedAt.Should().Be(later.AddHours(1));
        plan.StartDate.Should().Be(new DateOnly(2026, 9, 21));
        missingIsNotFound.Should().BeTrue();
    }
}
