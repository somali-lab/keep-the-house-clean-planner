using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Badges;

/// <summary>
/// How badges and awards appear in the audit log, as the Node server writes them (<c>data/badges.ts</c>, <c>domain/badgeAwards.ts</c>,
/// requirements 4.9). A badge is its own entity (create with its fields, update with the changed fields, delete with the removed fields); an
/// image is recorded as <c>{ contentType, size, hash }</c> and never as bytes. A live change of an award is its own <c>badgeAward</c> entry with
/// <c>meta: { reason, badgeName }</c>; a bulk evaluation records one summary entry (<c>recompute</c>, the fixed id) when it changed anything.
/// </summary>
public static class BadgeAudit
{
    /// <summary>The fixed id of the awards as a whole, the entity id of a bulk summary (like <see cref="PointsAudit.LedgerId"/>).</summary>
    public const string AwardsLedgerId = "000000000000000000000003";

    /// <summary>The most changes a summary lists.</summary>
    public const int MaxChanges = 100;

    private static IReadOnlyList<string> NoIgnore { get; } = [];

    private static IReadOnlyList<string> AwardIgnore { get; } = ["id", "createdAt", "updatedAt"];

    /// <summary>What an entry says about a badge: everything except the bytes.</summary>
    public static AuditObject Fields(Badge badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        return AuditObject.Of(
            ("name", badge.Name),
            ("description", badge.Description),
            ("active", badge.Active),
            ("rule", RuleFields(badge.Rule)),
            ("exampleKey", badge.ExampleKey is { } key ? key : AuditNull.Instance),
            ("image", badge.Image is { } image ? ImageFields(image) : AuditNull.Instance));
    }

    public static AuditObject RuleFields(BadgeRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Type == BadgeRuleType.OnTimeWeeks
            ? AuditObject.Of(("type", BadgeNames.ToWire(rule.Type)), ("threshold", rule.Threshold))
            : AuditObject.Of(
                ("type", BadgeNames.ToWire(rule.Type)),
                ("taskIds", new AuditArray([.. BadgeValidation.SortedIds(rule.TaskIds).Select(id => (AuditValue)new AuditObjectId(id))])),
                ("threshold", rule.Threshold));
    }

    private static AuditObject ImageFields(BadgeImageInfo image) =>
        AuditObject.Of(("contentType", image.ContentType.ContentType()), ("size", image.Size), ("hash", image.Hash));

    public static AuditEntry ForCreate(AuditActor actor, Badge badge, AuditObject? meta = null) =>
        ChangeSet.Between(null, Fields(badge), NoIgnore).ToEntry(actor, AuditEntity.Badge, badge.Id, AuditAction.Create, meta);

    public static AuditEntry ForDelete(AuditActor actor, Badge badge) =>
        ChangeSet.Between(Fields(badge), null, NoIgnore).ToEntry(actor, AuditEntity.Badge, badge.Id, AuditAction.Delete);

    /// <summary>The change from <paramref name="before"/> to <paramref name="after"/>; <see langword="null"/> from a no-op (nothing is written, nothing is audited).</summary>
    public static ChangeSet Between(Badge before, Badge after) => ChangeSet.Between(Fields(before), Fields(after), NoIgnore);

    public static AuditEntry ForUpdate(AuditActor actor, string badgeId, ChangeSet change, AuditObject? meta = null) =>
        change.ToEntry(actor, AuditEntity.Badge, badgeId, AuditAction.Update, meta);

    /// <summary>Whether the rule or the active flag changed: the awards must be recomputed.</summary>
    public static bool AffectsAwards(ChangeSet change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Diff.After["rule"] is not null || change.Diff.After["active"] is not null;
    }

    // ---- awards

    private static AuditObject AwardFields(BadgeAward award) => AuditObject.Of(
        ("key", award.Key),
        ("badgeId", new AuditObjectId(award.BadgeId)),
        ("personId", new AuditObjectId(award.PersonId)),
        ("awardedAt", award.AwardedAt));

    /// <summary>The entry of one live change of an award (<c>mode: each</c>).</summary>
    public static AuditEntry ForAward(AuditActor actor, AppliedAward applied, string badgeName, PointsSyncReason reason)
    {
        ArgumentNullException.ThrowIfNull(applied);
        var award = applied.Award;
        var reasonWire = PointNames.ToWire(reason);
        switch (applied.Change)
        {
            case AwardChange.Created:
                return ChangeSet.Between(null, AwardFields(award), AwardIgnore)
                    .ToEntry(actor, AuditEntity.BadgeAward, award.Id, AuditAction.Create, AuditObject.Of(("reason", reasonWire), ("badgeName", badgeName)));
            case AwardChange.Updated:
                return ChangeSet.Between(AwardFields(applied.Previous ?? award), AwardFields(award), AwardIgnore)
                    .ToEntry(
                        actor,
                        AuditEntity.BadgeAward,
                        award.Id,
                        AuditAction.Update,
                        AuditObject.Of(("reason", reasonWire), ("badgeName", badgeName), ("badgeId", new AuditObjectId(award.BadgeId)), ("personId", new AuditObjectId(award.PersonId))));
            default:
                return ChangeSet.Between(AwardFields(award), null, AwardIgnore)
                    .ToEntry(actor, AuditEntity.BadgeAward, award.Id, AuditAction.Delete, AuditObject.Of(("reason", reasonWire), ("badgeName", badgeName)));
        }
    }

    /// <summary>The one summary of a bulk evaluation that changed something; the changes are listed by key and cut at <see cref="MaxChanges"/>.</summary>
    public static AuditEntry ForSummary(
        AuditActor actor,
        BadgeEvalTrigger trigger,
        BadgeEvaluationResult result,
        IReadOnlyList<AppliedAward> applied,
        IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(names);
        var ordered = applied.OrderBy(a => a.Award.Key, StringComparer.Ordinal).ToList();
        var changes = ordered.Take(MaxChanges).Select(a => (AuditValue)AuditObject.Of(
            ("key", a.Award.Key),
            ("badgeId", new AuditObjectId(a.Award.BadgeId)),
            ("badgeName", names.GetValueOrDefault(a.Award.BadgeId) ?? string.Empty),
            ("personId", new AuditObjectId(a.Award.PersonId)),
            ("change", a.Change switch { AwardChange.Created => "created", AwardChange.Updated => "updated", _ => "removed" })));
        var meta = AuditObject.Of(
            ("trigger", BadgeTriggers.ToWire(trigger)),
            ("created", result.Created),
            ("updated", result.Updated),
            ("removed", result.Removed),
            ("changes", new AuditArray([.. changes])),
            ("changesTotal", ordered.Count),
            ("changesTruncated", ordered.Count > MaxChanges));
        return new AuditEntry(actor, AuditEntity.BadgeAward, AwardsLedgerId, AuditAction.Recompute, AuditObject.Empty, AuditObject.Empty, meta);
    }
}
