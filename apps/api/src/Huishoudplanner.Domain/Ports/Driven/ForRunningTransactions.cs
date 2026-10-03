using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

/// <summary>
/// Runs a use case's writes as one atomic unit (ADR-0021): the entity write and its audit entry commit together or
/// not at all. Stores and the audit writer take part implicitly; the domain never sees a session.
/// </summary>
/// <remarks>
/// <para>The <c>work</c> delegate can run more than once: a transient transaction error (for example a write conflict
/// with a concurrent transaction) rolls the attempt back and runs it again, up to the adapter's attempt limit. It must
/// therefore read its state inside the delegate and have no side effect outside the transaction.</para>
/// <para>Outcomes: the work's value when it committed or aborted by <see cref="TransactionOutcome.Abort{T}"/>;
/// <see cref="ConflictError"/> when concurrent writers kept winning after the last attempt; <see cref="PortError"/>
/// for an infrastructure failure. An exception thrown by <c>work</c> that is not an infrastructure failure rolls back
/// and propagates unchanged (a programming error is never turned into a value). Cancellation rolls back and throws
/// <see cref="OperationCanceledException"/>.</para>
/// <para>A call made while another run is active on the same logical flow joins that transaction instead of starting
/// a second one; the outermost run decides commit or rollback.</para>
/// </remarks>
public interface ForRunningTransactions
{
    Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken);
}
