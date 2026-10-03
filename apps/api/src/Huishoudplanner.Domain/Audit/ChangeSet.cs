namespace Huishoudplanner.Domain.Audit;

/// <summary>
/// The rule "a write that changes nothing writes and audits nothing" (ADR-0004) as a value. A use case computes the
/// change set between the stored state and the requested state before it writes: when <see cref="IsNoOp"/> it returns
/// the unchanged entity without any write or audit entry; otherwise it writes and records <see cref="ToEntry"/>.
/// </summary>
public sealed record ChangeSet(FieldDiff Diff)
{
    public bool IsNoOp => Diff.IsEmpty;

    /// <summary>The change between two states, <see cref="AuditDiff.DefaultIgnore"/> (<c>updatedAt</c>) left out unless <paramref name="ignore"/> says otherwise.</summary>
    public static ChangeSet Between(AuditObject? before, AuditObject? after, IEnumerable<string>? ignore = null) =>
        new(AuditDiff.Diff(before, after, ignore));

    public AuditEntry ToEntry(AuditActor actor, AuditEntity entity, string entityId, AuditAction action, AuditObject? meta = null) =>
        AuditEntry.For(actor, entity, entityId, action, Diff, meta);
}
