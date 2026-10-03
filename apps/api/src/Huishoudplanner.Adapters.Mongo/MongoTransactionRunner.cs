using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// <see cref="ForRunningTransactions"/> on a MongoDB client session (ADR-0021). One transaction per outermost run:
/// snapshot reads, majority writes, primary only. Commit on a committing outcome, abort on an aborting outcome, an
/// exception or cancellation.
/// </summary>
/// <remarks>
/// Retry policy: the driver's <c>WithTransactionAsync</c> retries transient errors for a hard-coded 120 seconds, too
/// long for a household app, so the same protocol (retry the whole attempt on <c>TransientTransactionError</c>, retry
/// only the commit on <c>UnknownTransactionCommitResult</c>) runs here with an attempt limit and a short linear
/// backoff. When the attempts run out on a write conflict the result is <see cref="ConflictError"/>; any other
/// exhausted transient error is a <see cref="PortError"/>.
/// Infrastructure failures are exactly <see cref="MongoException"/> and <see cref="TimeoutException"/>; anything else
/// rolls back and propagates.
/// </remarks>
internal sealed class MongoTransactionRunner(
    IMongoClient client,
    TimeProvider timeProvider,
    int maxAttempts = MongoTransactionRunner.DefaultMaxAttempts) : ForRunningTransactions
{
    public const int DefaultMaxAttempts = 5;

    private const int WriteConflictCode = 112;
    private const string TransientLabel = "TransientTransactionError";
    private const string UnknownCommitLabel = "UnknownTransactionCommitResult";
    private static readonly TimeSpan BackoffStep = TimeSpan.FromMilliseconds(25);

    private static readonly TransactionOptions Options = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        return MongoTransactionContext.Scope is { } outer
            ? await JoinAsync(outer, work, cancellationToken)
            : await StartAsync(work, cancellationToken);
    }

    /// <summary>A nested run takes part in the outer transaction; only the outermost run commits or rolls back.</summary>
    private static async Task<OneOf<T, ConflictError, PortError>> JoinAsync<T>(
        MongoTransactionScope outer,
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            outer.RollbackOnly = true;
        }

        return outcome.Value;
    }

    private async Task<OneOf<T, ConflictError, PortError>> StartAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        try
        {
            using var session = await client.StartSessionAsync(cancellationToken: cancellationToken);
            return await RunAttemptsAsync(session, work, cancellationToken);
        }
        catch (Exception e) when (IsInfrastructureFailure(e))
        {
            return Failure<T>(e);
        }
    }

    private async Task<OneOf<T, ConflictError, PortError>> RunAttemptsAsync<T>(
        IClientSessionHandle session,
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var scope = new MongoTransactionScope(session);
            session.StartTransaction(Options);
            MongoTransactionContext.Enter(scope);
            try
            {
                var outcome = await work(cancellationToken);
                if (!outcome.ShouldCommit)
                {
                    await AbortAsync(session);
                    return outcome.Value;
                }

                if (scope.RollbackOnly)
                {
                    await AbortAsync(session);
                    return new PortError("transaction.rollback_only: a joined transaction asked for a rollback.");
                }

                await CommitAsync(session, cancellationToken);
                return outcome.Value;
            }
            catch (Exception e) when (IsTransient(e) && attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await AbortAsync(session);
                await Task.Delay(BackoffStep * attempt, timeProvider, cancellationToken);
            }
            catch
            {
                // Every other exit (infrastructure failure, programming error, cancellation) rolls back first; the
                // caller of this method decides what the exception becomes.
                await AbortAsync(session);
                throw;
            }
            finally
            {
                MongoTransactionContext.Exit();
            }
        }
    }

    private static async Task CommitAsync(IClientSessionHandle session, CancellationToken cancellationToken)
    {
        // The commit may have reached the server although the answer was lost: committing again is safe.
        for (var commitAttempt = 1; ; commitAttempt++)
        {
            try
            {
                await session.CommitTransactionAsync(cancellationToken);
                return;
            }
            catch (MongoException e) when (e.HasErrorLabel(UnknownCommitLabel) && !IsTransient(e) && commitAttempt < DefaultMaxAttempts)
            {
                // retry the commit only
            }
        }
    }

    /// <summary>Best effort: the server also discards the transaction when the session ends.</summary>
    private static async Task AbortAsync(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
        {
            return;
        }

        try
        {
            await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is MongoException or InvalidOperationException)
        {
            // already aborted or committed on the server, or the connection is gone: nothing left to roll back
        }
    }

    private static bool IsTransient(Exception e) => e is MongoException m && m.HasErrorLabel(TransientLabel);

    private static bool IsInfrastructureFailure(Exception e) => e is MongoException or TimeoutException;

    private static bool IsWriteConflict(Exception e) => e switch
    {
        MongoCommandException c => c.Code == WriteConflictCode,
        MongoWriteException w => w.WriteError.Code == WriteConflictCode,
        MongoBulkWriteException b => b.WriteErrors.Any(x => x.Code == WriteConflictCode),
        _ => false,
    };

    /// <summary>Messages are value-free: the exception type only, never its text, which can echo a connection string.</summary>
    private static OneOf<T, ConflictError, PortError> Failure<T>(Exception e) =>
        IsTransient(e) && IsWriteConflict(e)
            ? new ConflictError("write_conflict", "A concurrent change won the write; retry the request.")
            : new PortError($"{(IsTransient(e) ? "mongo.transient" : "mongo.unavailable")}: the database failed ({e.GetType().Name}).");
}
