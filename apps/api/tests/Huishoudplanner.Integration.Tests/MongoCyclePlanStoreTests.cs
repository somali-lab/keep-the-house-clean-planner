using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests;

/// <summary>The plan store against a real MongoDB replica set: it only writes inside a transaction, rolls back with its audit entry and orders plans oldest first.</summary>
public sealed class MongoCyclePlanStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 30, 0, TimeSpan.Zero);

    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly ForStoringCyclePlans store;
    private readonly ForRunningTransactions transactions;
    private readonly ForRecordingAudit audit;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MongoCyclePlanStoreTests(MongoContainerFixture mongo)
    {
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithoutSeeding();
        using var client = factory.CreateClient();
        store = factory.Services.GetRequiredService<ForStoringCyclePlans>();
        transactions = factory.Services.GetRequiredService<ForRunningTransactions>();
        audit = factory.Services.GetRequiredService<ForRecordingAudit>();
    }

    public void Dispose()
    {
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    private IMongoCollection<BsonDocument> Plans => mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("cyclePlans");

    private static NewCyclePlan NewPlan(string name, int minutes = 0) =>
        new(name, false, [], CyclePlanRules.EmptyWeekThemes, Now.AddMinutes(minutes));

    [Fact]
    public async Task Writes_outsideATransaction_writeNothingAndReturnAPortError()
    {
        var inserted = await store.InsertAsync(NewPlan("X"), Ct);
        var updated = await store.UpdateMetaAsync("0123456789abcdef01234567", new PlanMetaChanges("Y"), Now, Ct);
        var replaced = await store.ReplaceSlotsAsync("0123456789abcdef01234567", [], Now, Ct);
        var deleted = await store.DeleteAsync("0123456789abcdef01234567", Ct);

        inserted.AsT1.Message.Should().StartWith("cycle_plans.no_transaction");
        updated.AsT2.Message.Should().StartWith("cycle_plans.no_transaction");
        replaced.AsT2.Message.Should().StartWith("cycle_plans.no_transaction");
        deleted.AsT2.Message.Should().StartWith("cycle_plans.no_transaction");
        (await Plans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task APlanAndItsAuditEntry_commitTogether_orNotAtAll()
    {
        var committed = await transactions.RunAsync(async ct =>
        {
            var plan = (await store.InsertAsync(NewPlan("Blijft"), ct)).AsT0;
            await audit.RecordAsync(CyclePlanAudit.ForCreate(AuditActor.System, plan), ct);
            return TransactionOutcome.Commit(plan.Id);
        }, Ct);
        var aborted = await transactions.RunAsync(async ct =>
        {
            var plan = (await store.InsertAsync(NewPlan("Verdwijnt"), ct)).AsT0;
            await audit.RecordAsync(CyclePlanAudit.ForCreate(AuditActor.System, plan), ct);
            return TransactionOutcome.Abort(plan.Id);
        }, Ct);

        committed.IsT0.Should().BeTrue();
        aborted.IsT0.Should().BeTrue();
        (await Plans.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).Select(d => d["name"].AsString).Should().Equal("Blijft");
        (await mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("auditLog").CountDocumentsAsync(new BsonDocument("entity", "cyclePlan"), cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Plans_areListedOldestFirst_withTheIdAsTieBreaker_andTheFirstIsTheDefault()
    {
        var ids = new List<string>();
        await transactions.RunAsync(async ct =>
        {
            ids.Add((await store.InsertAsync(NewPlan("Tweede", minutes: 5), ct)).AsT0.Id);
            ids.Add((await store.InsertAsync(NewPlan("Eerste", minutes: 0), ct)).AsT0.Id);
            ids.Add((await store.InsertAsync(NewPlan("Gelijk", minutes: 5), ct)).AsT0.Id);
            return TransactionOutcome.Commit(true);
        }, Ct);

        var page = (await store.ListAsync(null, 10, Ct)).AsT0;
        var afterFirst = (await store.ListAsync(CyclePlanCursor.After(page[0]), 10, Ct)).AsT0;
        var oldest = (await store.FindDefaultAsync(Ct)).AsT0;

        page.Select(p => p.Name).Should().Equal("Eerste", "Tweede", "Gelijk");
        afterFirst.Select(p => p.Name).Should().Equal("Tweede", "Gelijk");
        oldest.Name.Should().Be("Eerste");
        (await store.CountAsync(Ct)).AsT0.Should().Be(3);
        (await store.FindAsync("nope", Ct)).IsT1.Should().BeTrue();
        (await store.FindActiveAsync(Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task AMetaWrite_setsOnlyTheGivenFieldsAndTheTimestamp_andDeleteRemovesThePlan()
    {
        var id = string.Empty;
        await transactions.RunAsync(async ct =>
        {
            id = (await store.InsertAsync(NewPlan("A"), ct)).AsT0.Id;
            return TransactionOutcome.Commit(true);
        }, Ct);

        OneOf<CyclePlan, NotFound, PortError> renamed = default;
        OneOf<Success, NotFound, PortError> deleted = default;
        await transactions.RunAsync(async ct =>
        {
            renamed = await store.UpdateMetaAsync(id, new PlanMetaChanges(Name: "B"), Now.AddHours(1), ct);
            return TransactionOutcome.Commit(true);
        }, Ct);
        await transactions.RunAsync(async ct =>
        {
            deleted = await store.DeleteAsync(id, ct);
            return TransactionOutcome.Commit(true);
        }, Ct);

        var plan = renamed.AsT0;
        plan.Name.Should().Be("B");
        plan.WeekThemes.Should().Equal("", "", "", "");
        plan.UpdatedAt.Should().Be(Now.AddHours(1));
        plan.CreatedAt.Should().Be(Now);
        deleted.IsT0.Should().BeTrue();
        (await store.FindAsync(id, Ct)).IsT1.Should().BeTrue();
    }
}
