using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

#pragma warning disable CA1715

/// <summary>
/// The two exceptions to the append-only audit log (requirements 4.9): an administrator's explicit clear and the retention
/// job. Neither writes an audit entry; each is one atomic delete, so no transaction is needed.
/// </summary>
public interface ForDeletingAuditEntries
{
    /// <summary>Deletes every entry and returns how many there were.</summary>
    Task<OneOf<int, PortError>> ClearAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the entries strictly older than <paramref name="cutoff"/> and returns how many there were.</summary>
    Task<OneOf<int, PortError>> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
