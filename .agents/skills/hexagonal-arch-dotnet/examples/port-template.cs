// Worked example: driving and driven port interfaces for the household planner.
// Reuse the naming (IXxxService driving, ForXxx driven) and the OneOf return types. Errors are
// values, never exceptions: NotFound / ConflictError / ValidationErrors / PortError live in
// Huishoudplanner.Domain.Errors. Ids are ObjectId in storage and 24-hex strings in the API (D11);
// the domain carries its own typed id. Time comes from ForTellingTime (TimeProvider).
// Illustrative: it need not compile, but the shapes and naming are the rules.

using OneOf;
using OneOf.Types;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;

// --- Driving port: IXxxService -------------------------------------------------------------
// Location: apps/api/src/Huishoudplanner.Domain/Ports/Driving/
// The interface lives in Domain, the implementation in Huishoudplanner.Application.
// Inbound: the HTTP adapter and the jobs call into the domain through this port.
namespace Huishoudplanner.Domain.Ports.Driving;

public interface IOccurrenceService
{
    // One endpoint per intent (plan section 4.1): complete is its own method, not a PATCH with an
    // action discriminator. It can be absent, in the wrong state, or fail on the store.
    // The requestId in the command is the idempotency key; a replay returns the stored result.
    Task<OneOf<OccurrenceView, NotFound, ConflictError, ValidationErrors, PortError>> Complete(
        ActorContext actor,
        OccurrenceId id,
        CompleteOccurrenceCommand command,
        CancellationToken ct = default);

    Task<OneOf<OccurrenceView, NotFound, PortError>> GetById(OccurrenceId id, CancellationToken ct = default);

    // Every list is bounded: limit plus cursor, never an unbounded read.
    Task<OneOf<OccurrencePage, ValidationErrors, PortError>> List(
        OccurrenceQuery query,
        int limit,
        string? cursor,
        CancellationToken ct = default);
}

// --- Driven ports: ForXxx ------------------------------------------------------------------
// Location: apps/api/src/Huishoudplanner.Domain/Ports/Driven/
// Defined in Domain, implemented by Huishoudplanner.Adapters.Mongo. Outbound: the use case asks
// infrastructure to do something and never names a driver. Naming is verb-based.
namespace Huishoudplanner.Domain.Ports.Driven;

public interface ForStoringOccurrences
{
    Task<OneOf<Occurrence, NotFound, PortError>> GetById(OccurrenceId id, CancellationToken ct = default);

    // The write takes its audit entry with it: the adapter stores entity and audit inside the
    // transaction opened through ForRunningTransactions. A no-op is detected by the use case
    // before this call; the adapter never writes an unchanged entity.
    Task<OneOf<Success, NotFound, ConflictError, PortError>> Replace(
        Occurrence changed,
        AuditEntry audit,
        CancellationToken ct = default);
}

public interface ForRunningTransactions
{
    // Runs the work inside one MongoDB transaction (single-node replica set, D12). No driver type
    // appears here; the Mongo adapter flows the session to the stores it owns.
    Task<OneOf<T, PortError>> Run<T>(
        Func<CancellationToken, Task<OneOf<T, PortError>>> work,
        CancellationToken ct = default);
}

// ForTellingTime is satisfied by TimeProvider: inject TimeProvider into use cases. Production
// registers TimeProvider.System; tests register FakeTimeProvider with a fixed instant.
