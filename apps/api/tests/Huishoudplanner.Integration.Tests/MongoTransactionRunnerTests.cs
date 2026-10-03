using AwesomeAssertions;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The transaction runner (ADR-0021) against a real single-node replica set. The entity plus audit pair of every
/// test stands for a use case's entity write and its audit entry: both are visible or neither is.
/// </summary>
public sealed class MongoTransactionRunnerTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private IMongoClient client = null!;
    private IMongoDatabase database = null!;
    private IMongoCollection<BsonDocument> entities = null!;
    private IMongoCollection<BsonDocument> audit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        database = MongoClientFactory.GetDatabase(client, options);
        await database.CreateCollectionAsync("entities", cancellationToken: Ct);
        await database.CreateCollectionAsync("audit", cancellationToken: Ct);
        entities = database.GetCollection<BsonDocument>("entities");
        audit = database.GetCollection<BsonDocument>("audit");
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, Ct);
        client.Dispose();
    }

    private MongoTransactionRunner Runner(int maxAttempts = MongoTransactionRunner.DefaultMaxAttempts) =>
        new(client, TimeProvider.System, maxAttempts);

    /// <summary>What a store does: take part in the ambient transaction when there is one.</summary>
    private async Task InsertEntityAndAudit(string id, CancellationToken ct)
    {
        var session = MongoTransactionContext.Session;
        await entities.InsertOneAsync(session!, new BsonDocument { { "_id", id } }, cancellationToken: ct);
        await audit.InsertOneAsync(session!, new BsonDocument { { "entityId", id } }, cancellationToken: ct);
    }

    private async Task<(long Entities, long Audit)> Counts() =>
    (
        await entities.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct),
        await audit.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)
    );

    [Fact]
    public async Task RunAsync_CommittingOutcome_MakesEntityAndAuditVisibleAndReturnsTheValue()
    {
        var result = await Runner().RunAsync(async ct =>
        {
            await InsertEntityAndAudit("a", ct);
            return TransactionOutcome.Commit("done");
        }, Ct);

        result.AsT0.Should().Be("done");
        (await Counts()).Should().Be((1, 1));
    }

    [Fact]
    public async Task RunAsync_WritesInsideTheTransaction_AreInvisibleOutsideUntilCommit()
    {
        var gate = new TaskCompletionSource();
        var inserted = new TaskCompletionSource();
        var seenInside = 0L;
        var run = Runner().RunAsync(async ct =>
        {
            await InsertEntityAndAudit("a", ct);
            seenInside = await entities.CountDocumentsAsync(MongoTransactionContext.Session!, FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            inserted.SetResult();
            await gate.Task;
            return TransactionOutcome.Commit(1);
        }, Ct);
        await inserted.Task;

        (await Counts()).Should().Be((0, 0), "an open transaction is not visible to other readers");
        gate.SetResult();
        await run;

        seenInside.Should().Be(1, "a transaction reads its own writes");
        (await Counts()).Should().Be((1, 1));
    }

    [Fact]
    public async Task RunAsync_AbortingOutcome_RollsBackBothWritesAndStillReturnsTheValue()
    {
        var result = await Runner().RunAsync(async ct =>
        {
            await InsertEntityAndAudit("a", ct);
            return TransactionOutcome.Abort("not found");
        }, Ct);

        result.AsT0.Should().Be("not found");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_ProgrammingErrorAfterBothWrites_RollsBackAndPropagatesTheException()
    {
        var act = async () => await Runner().RunAsync<int>(async ct =>
        {
            await InsertEntityAndAudit("a", ct);
            throw new InvalidOperationException("bug");
        }, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("bug");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_DuplicateKeyOnTheSecondWrite_RollsBackTheFirstAndReturnsPortError()
    {
        await entities.InsertOneAsync(new BsonDocument { { "_id", "taken" } }, cancellationToken: Ct);

        var result = await Runner().RunAsync<int>(async ct =>
        {
            await audit.InsertOneAsync(MongoTransactionContext.Session!, new BsonDocument { { "entityId", "taken" } }, cancellationToken: ct);
            await InsertEntityAndAudit("taken", ct);
            return TransactionOutcome.Commit(1);
        }, Ct);

        result.AsT2.Message.Should().StartWith("mongo.unavailable");
        (await Counts()).Should().Be((1, 0), "the audit entry written before the failing entity write is gone");
    }

    [Fact]
    public async Task RunAsync_ConcurrentWriteOnTheSameDocument_RetriesTheLoserUntilItSucceeds()
    {
        await entities.InsertOneAsync(new BsonDocument { { "_id", "counter" }, { "n", 0 } }, cancellationToken: Ct);
        var winnerWrote = new TaskCompletionSource();
        var releaseWinner = new TaskCompletionSource();
        var loserAttempts = 0;

        var winner = Runner().RunAsync(async ct =>
        {
            await Increment(ct);
            winnerWrote.SetResult();
            await releaseWinner.Task;
            return TransactionOutcome.Commit("winner");
        }, Ct);
        await winnerWrote.Task;

        var loser = Runner().RunAsync(async ct =>
        {
            if (Interlocked.Increment(ref loserAttempts) == 2)
            {
                releaseWinner.TrySetResult();
            }

            await Increment(ct);
            return TransactionOutcome.Commit("loser");
        }, Ct);

        (await winner).AsT0.Should().Be("winner");
        (await loser).AsT0.Should().Be("loser");
        loserAttempts.Should().BeGreaterThanOrEqualTo(2, "the first attempt hit the write conflict");
        var counter = await entities.Find(new BsonDocument("_id", "counter")).SingleAsync(Ct);
        counter["n"].AsInt32.Should().Be(2, "both increments survive: nothing was lost");

        Task Increment(CancellationToken ct) => entities.UpdateOneAsync(
            MongoTransactionContext.Session!,
            new BsonDocument("_id", "counter"),
            Builders<BsonDocument>.Update.Inc("n", 1),
            cancellationToken: ct);
    }

    [Fact]
    public async Task RunAsync_ConflictPersistsPastTheAttemptLimit_ReturnsConflictErrorAndWritesNothing()
    {
        await entities.InsertOneAsync(new BsonDocument { { "_id", "counter" }, { "n", 0 } }, cancellationToken: Ct);
        var winnerWrote = new TaskCompletionSource();
        var releaseWinner = new TaskCompletionSource();
        var loserAttempts = 0;

        var winner = Runner().RunAsync(async ct =>
        {
            await entities.UpdateOneAsync(MongoTransactionContext.Session!, new BsonDocument("_id", "counter"), Builders<BsonDocument>.Update.Inc("n", 1), cancellationToken: ct);
            winnerWrote.SetResult();
            await releaseWinner.Task;
            return TransactionOutcome.Commit(1);
        }, Ct);
        await winnerWrote.Task;

        var loser = await Runner(maxAttempts: 2).RunAsync(async ct =>
        {
            loserAttempts++;
            await audit.InsertOneAsync(MongoTransactionContext.Session!, new BsonDocument { { "who", "loser" } }, cancellationToken: ct);
            await entities.UpdateOneAsync(MongoTransactionContext.Session!, new BsonDocument("_id", "counter"), Builders<BsonDocument>.Update.Inc("n", 1), cancellationToken: ct);
            return TransactionOutcome.Commit(2);
        }, Ct);
        releaseWinner.SetResult();
        await winner;

        loser.IsT1.Should().BeTrue("the loser ran out of attempts while the winner still held the document");
        loserAttempts.Should().Be(2);
        (await entities.Find(new BsonDocument("_id", "counter")).SingleAsync(Ct))["n"].AsInt32.Should().Be(1);
        (await audit.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_NoWriteInTheWork_CommitsAndLeavesTheDatabaseUntouched()
    {
        var result = await Runner().RunAsync(_ => Task.FromResult(TransactionOutcome.Commit("nothing changed")), Ct);

        result.AsT0.Should().Be("nothing changed");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_CancelledWhileWorking_RollsBackAndThrowsOperationCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var written = new TaskCompletionSource();

        var run = Runner().RunAsync<int>(async ct =>
        {
            await InsertEntityAndAudit("a", ct);
            written.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return TransactionOutcome.Commit(1);
        }, cts.Token);
        await written.Task;
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelled_DoesNotRunTheWork()
    {
        var ran = false;

        var act = async () => await Runner().RunAsync(_ =>
        {
            ran = true;
            return Task.FromResult(TransactionOutcome.Commit(1));
        }, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Session_OutsideInsideAndAfterARun_IsNullThenSetThenNull()
    {
        MongoTransactionContext.Session.Should().BeNull();
        IClientSessionHandle? inside = null;
        IClientSessionHandle? insideParallelTask = null;

        await Runner().RunAsync(async ct =>
        {
            inside = MongoTransactionContext.Session;
            await Task.Run(() => insideParallelTask = MongoTransactionContext.Session, ct);
            return TransactionOutcome.Commit(1);
        }, Ct);

        inside.Should().NotBeNull();
        insideParallelTask.Should().BeSameAs(inside, "the ambient session flows through async continuations");
        MongoTransactionContext.Session.Should().BeNull();
    }

    [Fact]
    public async Task Session_AfterAFailedRun_IsClearedAgain()
    {
        var act = async () => await Runner().RunAsync<int>(_ => throw new InvalidOperationException("bug"), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        MongoTransactionContext.Session.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_NestedRun_JoinsTheOuterTransactionAndCommitsWithIt()
    {
        var runner = Runner();
        IClientSessionHandle? outerSession = null;
        IClientSessionHandle? innerSession = null;

        var result = await runner.RunAsync(async ct =>
        {
            outerSession = MongoTransactionContext.Session;
            await runner.RunAsync(async inner =>
            {
                innerSession = MongoTransactionContext.Session;
                await InsertEntityAndAudit("a", inner);
                return TransactionOutcome.Commit(1);
            }, ct);
            return TransactionOutcome.Commit("outer");
        }, Ct);

        result.AsT0.Should().Be("outer");
        innerSession.Should().BeSameAs(outerSession);
        (await Counts()).Should().Be((1, 1));
    }

    [Fact]
    public async Task RunAsync_NestedRunThenOuterAbort_RollsBackTheNestedWritesToo()
    {
        var runner = Runner();

        var result = await runner.RunAsync(async ct =>
        {
            await runner.RunAsync(async inner =>
            {
                await InsertEntityAndAudit("a", inner);
                return TransactionOutcome.Commit(1);
            }, ct);
            return TransactionOutcome.Abort("outer failed");
        }, Ct);

        result.AsT0.Should().Be("outer failed");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_NestedRunAbortsButOuterWantsToCommit_RollsBackAndReportsPortError()
    {
        var runner = Runner();

        var result = await runner.RunAsync(async ct =>
        {
            await InsertEntityAndAudit("outer", ct);
            await runner.RunAsync(_ => Task.FromResult(TransactionOutcome.Abort(1)), ct);
            return TransactionOutcome.Commit("outer ok");
        }, Ct);

        result.AsT2.Message.Should().StartWith("transaction.rollback_only");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_DatabaseUnreachable_ReturnsPortErrorWithoutLeakingTheConnectionString()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://leaky-user:leaky-secret@127.0.0.1:1/?directConnection=true");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(300);
        using var unreachable = new MongoClient(settings);
        var runner = new MongoTransactionRunner(unreachable, TimeProvider.System);

        var result = await runner.RunAsync(async ct =>
        {
            await unreachable.GetDatabase("x").GetCollection<BsonDocument>("y")
                .InsertOneAsync(MongoTransactionContext.Session!, new BsonDocument(), cancellationToken: ct);
            return TransactionOutcome.Commit(1);
        }, Ct);

        var error = result.AsT2;
        error.Message.Should().StartWith("mongo.unavailable");
        error.Message.Should().NotContainAny("leaky-secret", "leaky-user", "127.0.0.1");
    }

    [Fact]
    public async Task RunAsync_BackoffUsesTheTimeProvider()
    {
        // A retry waits on the injected TimeProvider, so a frozen clock stalls the retry until
        // the caller cancels: proof that no wall-clock sleep is hidden in the runner.
        await entities.InsertOneAsync(new BsonDocument { { "_id", "counter" }, { "n", 0 } }, cancellationToken: Ct);
        var winnerWrote = new TaskCompletionSource();
        var releaseWinner = new TaskCompletionSource();
        var winner = Runner().RunAsync(async ct =>
        {
            await entities.UpdateOneAsync(MongoTransactionContext.Session!, new BsonDocument("_id", "counter"), Builders<BsonDocument>.Update.Inc("n", 1), cancellationToken: ct);
            winnerWrote.SetResult();
            await releaseWinner.Task;
            return TransactionOutcome.Commit(1);
        }, Ct);
        await winnerWrote.Task;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loser = new MongoTransactionRunner(client, new FrozenTimeProvider()).RunAsync(async ct =>
        {
            await entities.UpdateOneAsync(MongoTransactionContext.Session!, new BsonDocument("_id", "counter"), Builders<BsonDocument>.Update.Inc("n", 1), cancellationToken: ct);
            return TransactionOutcome.Commit(1);
        }, cts.Token);

        await Task.Delay(500, Ct);
        loser.IsCompleted.Should().BeFalse("the retry is waiting on a clock that does not move");
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => loser).Should().ThrowAsync<OperationCanceledException>();
        releaseWinner.SetResult();
        await winner;
    }

    /// <summary>A clock whose timers never fire.</summary>
    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();

        private sealed class FrozenTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
