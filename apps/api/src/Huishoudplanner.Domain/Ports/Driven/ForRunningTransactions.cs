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
/// <para>A <see cref="PortError"/> whose message starts with <c>mongo.commit_unknown</c> means the commit was sent but its
/// result could not be confirmed: the change may be stored. Callers must not blindly retry such a request (a retry
/// can apply it twice); the cancellation token no longer applies once the commit is sent.</para>
/// <para>Operations inside one run must not execute in parallel (the adapter's session is not thread safe), and a task
/// that outlives the run does not see the transaction.</para>
/// <para>A call made while another run is active on the same logical flow joins that transaction instead of starting
/// a second one; the outermost run decides commit or rollback.</para>
/// </remarks>
public interface ForRunningTransactions
{
    Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken);
}
