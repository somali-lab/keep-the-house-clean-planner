namespace Huishoudplanner.Domain.Audit;

/// <summary>The changed fields only, as they were and as they became (the <c>before</c> and <c>after</c> of an audit entry).</summary>
public sealed record FieldDiff(AuditObject Before, AuditObject After)
{
    public static FieldDiff Empty { get; } = new(AuditObject.Empty, AuditObject.Empty);

    /// <summary>Nothing changed: the write and its audit entry are both skipped.</summary>
    public bool IsEmpty => Before.Count == 0 && After.Count == 0;
}
