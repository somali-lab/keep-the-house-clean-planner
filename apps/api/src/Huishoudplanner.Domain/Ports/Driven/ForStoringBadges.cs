using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The badge definitions (collection <c>badges</c>, shared with the Node server; ADR-0014). The writes only run inside
/// <see cref="ForRunningTransactions"/> (together with their audit entry); called outside a transaction they write nothing and return a
/// <see cref="PortError"/> whose message starts with <c>badges.no_transaction</c>. Reads join the running transaction when there is one. The
/// picture is stored as binary in the document and is only read by <see cref="FindImageAsync"/>: no other read loads the bytes.
/// </summary>
public interface ForStoringBadges
{
    /// <summary>At most <paramref name="take"/> badges after the cursor, oldest first (creation instant, then id).</summary>
    Task<OneOf<IReadOnlyList<Badge>, PortError>> ListAsync(bool? active, BadgeCursor? after, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Every badge, oldest first, for the evaluation of the awards. Bounded by the badge limit (at most 100 badges can be created); the read stops
    /// at 1000 so that a document set that was written by other means cannot make it unbounded.
    /// </summary>
    Task<OneOf<IReadOnlyList<Badge>, PortError>> ListAllAsync(bool? active, CancellationToken cancellationToken);

    /// <summary><see cref="NotFound"/> also for an id that is not a valid id.</summary>
    Task<OneOf<Badge, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken);

    Task<OneOf<Badge, NotFound, PortError>> FindByExampleKeyAsync(string exampleKey, CancellationToken cancellationToken);

    /// <summary>How many badges exist.</summary>
    Task<OneOf<int, PortError>> CountAsync(CancellationToken cancellationToken);

    /// <summary>The badges whose rule names the task.</summary>
    Task<OneOf<IReadOnlyList<Badge>, PortError>> FindNamingTaskAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>The stored picture; <see cref="NotFound"/> for an unknown badge and for a badge without a picture.</summary>
    Task<OneOf<BadgeImageData, NotFound, PortError>> FindImageAsync(string id, CancellationToken cancellationToken);

    Task<OneOf<Badge, PortError>> InsertAsync(NewBadge badge, CancellationToken cancellationToken);

    /// <summary>Sets the given fields and <c>updatedAt</c>; returns the badge as stored afterwards.</summary>
    Task<OneOf<Badge, NotFound, PortError>> UpdateAsync(string id, BadgeChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken);
}
