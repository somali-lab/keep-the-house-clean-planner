using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Appends one audit entry (ADR-0004, ADR-0021). The log is append-only and an entry is never written without the
/// entity write it describes, so the call must run inside <see cref="ForRunningTransactions"/>: the entry commits
/// or rolls back with the entity write. A call outside a transaction writes nothing and returns a
/// <see cref="PortError"/> whose message starts with <c>audit.no_transaction</c>. Callers must not record an entry
/// for a no-op (see <see cref="ChangeSet"/>).
/// </summary>
public interface ForRecordingAudit
{
    Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}
