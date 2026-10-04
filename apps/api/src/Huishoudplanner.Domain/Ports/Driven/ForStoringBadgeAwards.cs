using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The derived badge awards (collection <c>badgeAwards</c>, shared with the Node server; ADR-0014). The key <c>badge:&lt;badgeId&gt;:&lt;personId&gt;</c>
/// is unique, so an award exists at most once. The write only runs inside <see cref="ForRunningTransactions"/> (a live evaluation together with its
/// entries, a reconciliation together with its summary); called outside a transaction it writes nothing and returns a <see cref="PortError"/> whose
/// message starts with <c>badgeAwards.no_transaction</c>. Reads join the running transaction when there is one.
/// </summary>
public interface ForStoringBadgeAwards
{
    /// <summary>At most <paramref name="take"/> awards after the cursor, oldest first (<c>awardedAt</c>, then id), optionally of one person.</summary>
    Task<OneOf<IReadOnlyList<BadgeAward>, PortError>> ListAsync(BadgeAwardQuery query, int take, CancellationToken cancellationToken);

    /// <summary>The stored awards of the given people (of everybody when <see langword="null"/>): what an evaluation compares the data with.</summary>
    Task<OneOf<IReadOnlyList<BadgeAward>, PortError>> FindForEvaluationAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the plan, one write each. A move and a removal are compare-and-set on the award that was read, and a duplicate key on an insert (it
    /// already exists) is skipped, so a change that did not happen is neither reported nor audited. Returns what was really changed.
    /// </summary>
    Task<OneOf<IReadOnlyList<AppliedAward>, PortError>> ApplyAsync(BadgeAwardPlan plan, DateTimeOffset at, CancellationToken cancellationToken);
}
