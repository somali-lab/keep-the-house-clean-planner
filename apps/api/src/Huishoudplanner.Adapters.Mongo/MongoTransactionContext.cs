using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>The transaction a flow is currently inside. Created by <see cref="MongoTransactionRunner"/>, read by stores.</summary>
internal sealed class MongoTransactionScope(IClientSessionHandle session)
{
    public IClientSessionHandle Session { get; } = session;

    /// <summary>Set when a joined (nested) run asked for a rollback; the outermost run then refuses to commit.</summary>
    public bool RollbackOnly { get; set; }
}

/// <summary>
/// The driver-free way for stores and the audit writer to enlist in the running transaction (ADR-0021). The
/// runner sets the scope in an <see cref="AsyncLocal{T}"/> for the duration of one attempt of the work delegate, so
/// the domain never passes a session around. A store passes <see cref="Session"/> to every driver call; it is
/// <see langword="null"/> outside a transaction, in which case the call is a plain single operation. Only
/// <c>MongoTransactionRunner</c> assigns it.
/// </summary>
internal static class MongoTransactionContext
{
    private static readonly AsyncLocal<MongoTransactionScope?> CurrentScope = new();

    public static MongoTransactionScope? Scope => CurrentScope.Value;

    /// <summary>The session of the running transaction, or <see langword="null"/> when none is active.</summary>
    public static IClientSessionHandle? Session => CurrentScope.Value?.Session;

    internal static void Enter(MongoTransactionScope scope) => CurrentScope.Value = scope;

    internal static void Exit() => CurrentScope.Value = null;
}
