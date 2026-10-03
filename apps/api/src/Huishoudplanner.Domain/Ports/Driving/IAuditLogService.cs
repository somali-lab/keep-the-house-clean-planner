using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The history of changes (requirements 4.9). Who may call what (everyone reads, administrators clear) is decided by the
/// driving adapter.
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// A page of the history, newest first. A bad id filter, <c>limit</c> or cursor is a <see cref="ValidationErrors"/>.
    /// Old entries of occurrences get the task, room and date of their occurrence in <c>meta.occurrence</c>.
    /// </summary>
    Task<OneOf<AuditLogPage, ValidationErrors, PortError>> ListAsync(AuditLogFilter filter, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>Deletes the whole history and returns the number of entries removed. Deliberately not recorded in the log itself.</summary>
    Task<OneOf<int, PortError>> ClearAsync(CancellationToken cancellationToken);
}
