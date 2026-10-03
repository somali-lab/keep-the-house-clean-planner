namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// One named startup migration. <see cref="ApplyAsync"/> must be idempotent: concurrent runners (two replicas
/// starting at once) can both apply it, and a run that failed after a partial effect is applied again.
/// </summary>
internal interface IMigration
{
    /// <summary>Unique and stable, ordered by its numeric prefix (for example <c>001-drop-legacy-generated-slot-index</c>).</summary>
    string Name { get; }

    Task ApplyAsync(CancellationToken cancellationToken);
}
