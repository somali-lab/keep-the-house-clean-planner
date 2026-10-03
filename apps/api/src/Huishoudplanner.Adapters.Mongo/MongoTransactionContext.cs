using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>The transaction a flow is currently inside. Created by <see cref="MongoTransactionRunner"/>, read by stores.</summary>
/// <remarks>
/// A session is not thread safe: operations on it must run one after another, never in parallel (no
/// <c>Task.WhenAll</c> over store calls inside one unit of work).
/// </remarks>
internal sealed class MongoTransactionScope(IClientSessionHandle session)
{
    /// <summary>The client the transaction belongs to; a nested run only joins a scope of the same client.</summary>
    public IMongoClient Client { get; } = session.Client;

    /// <summary>The live session, or <see langword="null"/> once the attempt that owned it has ended.</summary>
    public IClientSessionHandle? Session { get; private set; } = session;

    /// <summary>Set when a joined (nested) run asked for a rollback; the outermost run then refuses to commit.</summary>
    public bool RollbackOnly { get; set; }

    /// <summary>Ends the scope: a task that outlives the attempt (fire and forget) no longer sees a session.</summary>
    internal void Close() => Session = null;
}

/// <summary>
/// The driver-free way for stores and the audit writer to enlist in the running transaction (ADR-0021). The runner
/// publishes the scope for the duration of one attempt of the work delegate, so the domain never passes a session
/// around. The <see cref="AsyncLocal{T}"/> holds a mutable holder (the way <c>HttpContextAccessor</c> does): when the
/// attempt ends the holder is emptied, which also reaches continuations that captured it and outlive the attempt.
/// A store passes <see cref="Session"/> to every driver call; it is <see langword="null"/> outside a transaction, in
/// which case the call is a plain single operation. Only <c>MongoTransactionRunner</c> assigns it.
/// </summary>
internal static class MongoTransactionContext
{
    private sealed class Holder
    {
        public MongoTransactionScope? Scope { get; set; }
    }

    private static readonly AsyncLocal<Holder?> Current = new();

    public static MongoTransactionScope? Scope => Current.Value?.Scope;

    /// <summary>The session of the running transaction, or <see langword="null"/> when none is active.</summary>
    public static IClientSessionHandle? Session => Scope?.Session;

    internal static void Enter(MongoTransactionScope scope) => Current.Value = new Holder { Scope = scope };

    internal static void Exit()
    {
        var holder = Current.Value;
        if (holder?.Scope is { } scope)
        {
            scope.Close();
            holder.Scope = null;
        }

        Current.Value = null;
    }
}
