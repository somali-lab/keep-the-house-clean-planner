namespace Huishoudplanner.Domain.Ports.Driven;

/// <summary>
/// What a unit of work hands back to <see cref="ForRunningTransactions"/>: its value and whether the transaction may
/// commit. A use case that ends in a failure value (not found, validation, its own conflict) returns
/// <see cref="Abort"/> so that nothing it wrote survives, without throwing.
/// </summary>
public readonly record struct TransactionOutcome<T>(T Value, bool ShouldCommit);

public static class TransactionOutcome
{
    /// <summary>The work succeeded: commit everything it wrote and return <paramref name="value"/>.</summary>
    public static TransactionOutcome<T> Commit<T>(T value) => new(value, true);

    /// <summary>The work ended in a failure value: roll everything back and still return <paramref name="value"/>.</summary>
    public static TransactionOutcome<T> Abort<T>(T value) => new(value, false);
}
