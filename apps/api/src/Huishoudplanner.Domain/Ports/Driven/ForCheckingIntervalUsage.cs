using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Asks which intervals tasks still use. A read port on tasks: the task domain itself arrives in a later slice (2.1), until
/// then the adapter reads the <c>intervalKey</c> of the existing <c>tasks</c> collection. Joins the running transaction when there is one.
/// </summary>
public interface ForCheckingIntervalUsage
{
    /// <summary>The distinct interval keys of all tasks, active or inactive.</summary>
    Task<OneOf<IReadOnlyList<string>, PortError>> GetKeysInUseAsync(CancellationToken cancellationToken);
}
