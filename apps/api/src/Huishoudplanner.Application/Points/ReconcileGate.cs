namespace Huishoudplanner.Application.Points;

/// <summary>
/// Reconciliations of the points ledger never overlap in this process (ADR-0011, ADR-0005: one process): a second run waits for the first.
/// Register one instance for the whole application. A live sync does not wait for it, because the transactions order it with the reconciliation.
/// </summary>
public sealed class ReconcileGate : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => semaphore.WaitAsync(cancellationToken);

    public void Release() => semaphore.Release();

    public void Dispose() => semaphore.Dispose();
}
