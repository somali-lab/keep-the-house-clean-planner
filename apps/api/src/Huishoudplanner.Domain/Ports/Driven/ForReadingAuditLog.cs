using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>Reads the stored audit entries (read only; the writes are <see cref="ForRecordingAudit"/> and <see cref="ForDeletingAuditEntries"/>).</summary>
public interface ForReadingAuditLog
{
    /// <summary>At most <paramref name="take"/> entries matching the filter after the cursor, newest first (<c>at</c> then id, descending).</summary>
    Task<OneOf<IReadOnlyList<AuditLogEntry>, PortError>> ListAsync(AuditLogFilter filter, AuditCursor? after, int take, CancellationToken cancellationToken);
}
