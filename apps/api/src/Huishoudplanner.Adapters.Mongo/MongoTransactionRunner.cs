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
/// A commit whose result stays unknown after the attempt limit is the distinct <c>mongo.commit_unknown</c>
/// <see cref="PortError"/>. The commit itself ignores the caller's cancellation token.
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
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);
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
    private async Task<OneOf<T, ConflictError, PortError>> JoinAsync<T>(
        MongoTransactionScope outer,
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(outer.Client, client))
        {
            throw new InvalidOperationException("A nested transaction run can only join a transaction of the same MongoDB client.");
        }

        if (outer.Session is not { IsInTransaction: true })
        {
            throw new InvalidOperationException("The transaction this run would join is no longer active.");
        }

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

                // Cancellation is honoured up to here; once the commit is sent it must not be able to mask it.
                cancellationToken.ThrowIfCancellationRequested();
                return await CommitAsync(session) ? outcome.Value : CommitUnknown();
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

    /// <summary>
    /// Commits with its own bounded timeout, never the caller's token: a cancel that arrives after the commit was sent
    /// would otherwise report "nothing happened" for a commit that may be durable. Returns <see langword="false"/> when
    /// the outcome is still unknown after the attempt limit.
    /// </summary>
    private async Task<bool> CommitAsync(IClientSessionHandle session)
    {
        for (var commitAttempt = 1; ; commitAttempt++)
        {
            using var timeout = new CancellationTokenSource(CommitTimeout, timeProvider);
            try
            {
                await session.CommitTransactionAsync(timeout.Token);
                return true;
            }
            catch (MongoException e) when (e.HasErrorLabel(UnknownCommitLabel) && !IsTransient(e))
            {
                if (commitAttempt >= maxAttempts)
                {
                    return false;
                }

                // The commit may have reached the server although the answer was lost: committing again is safe.
                await Task.Delay(BackoffStep * commitAttempt, timeProvider, CancellationToken.None);
            }
        }
    }

    private static PortError CommitUnknown() =>
        new("mongo.commit_unknown: the commit result could not be confirmed; the change may or may not be stored.");

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

    private static bool IsUnavailable(Exception e) => e is TimeoutException or MongoConnectionException;

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
            : new PortError($"{(IsTransient(e) ? "mongo.transient" : IsUnavailable(e) ? "mongo.unavailable" : "mongo.failed")}: the database failed ({e.GetType().Name}).");
}
