using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The live follow-up of an execution (<c>syncExecutionPoints</c> of the Node server, ADR-0011): after every complete, undo, correction, deletion,
/// retract and recorded execution the occurrence use cases make the single <c>execution:&lt;occurrenceId&gt;</c> ledger entry match its occurrence.
/// The occurrence use cases call it inside their own transaction (it joins it), so the occurrence, its audit entries and the ledger entry commit
/// together; called alone it runs its own.
/// </summary>
public interface IExecutionPointsService
{
    /// <summary>
    /// Creates, updates or deletes the one entry of <paramref name="occurrenceId"/> (which may no longer exist: then any entry is removed), with one
    /// audit entry per real change (<c>meta: { occurrenceId, reason }</c>) and nothing for a no-op. Without settings the ledger cannot be dated
    /// and is left alone.
    /// </summary>
    Task<OneOf<SyncOutcome, PortError>> SyncAsync(AuditActor actor, string occurrenceId, PointsSyncReason reason, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IExecutionPointsService.SyncAsync"/> did to the entry.</summary>
public enum SyncOutcome
{
    Unchanged,
    Created,
    Updated,
    Deleted,
}
