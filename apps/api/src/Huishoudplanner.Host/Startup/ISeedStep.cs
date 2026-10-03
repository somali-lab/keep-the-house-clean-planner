namespace Huishoudplanner.Host.Startup;

/// <summary>
/// One piece of first-run data (the Node <c>seed</c> is a list of these). Runs at startup after the storage is prepared,
/// in registration order, and must be idempotent. A failure stops the start: the host does not run on half a seed.
/// </summary>
public interface ISeedStep
{
    /// <summary>A short name for the log and for failure messages.</summary>
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
