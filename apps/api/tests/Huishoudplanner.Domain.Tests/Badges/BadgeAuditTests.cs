using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Tests.Badges;

/// <summary>The audit shapes of badges and awards, as the Node server writes them (requirements 4.9).</summary>
public class BadgeAuditTests
{
    private const string BadgeId = "d00000000000000000000001";
    private const string Task1 = "a00000000000000000000001";
    private const string Task2 = "a00000000000000000000002";
    private const string Person = "b00000000000000000000001";
    private static readonly AuditActor Actor = new("c00000000000000000000001", AuditSource.Ui);
    private static readonly DateTimeOffset T = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private static Badge MakeBadge(BadgeRule? rule = null, BadgeImageInfo? image = null, string name = "Toiletjuffrouw") =>
        new(BadgeId, name, "omschrijving", rule ?? new BadgeRule(BadgeRuleType.Executions, [Task2, Task1], 10), true, null, image, T, T);

    [Fact]
    public void ACreate_recordsEveryField_withTheTasksSorted_andTheImageWithoutBytes()
    {
        var image = new BadgeImageInfo(BadgeImageType.Png, 70, new string('a', 64));

        var entry = BadgeAudit.ForCreate(Actor, MakeBadge(image: image));

        entry.Entity.Should().Be(AuditEntity.Badge);
        entry.Action.Should().Be(AuditAction.Create);
        entry.EntityId.Should().Be(BadgeId);
        entry.Before.Should().Be(AuditObject.Empty);
        entry.After.Should().Be(AuditObject.Of(
            ("name", "Toiletjuffrouw"),
            ("description", "omschrijving"),
            ("active", true),
            ("rule", AuditObject.Of(("type", "executions"), ("taskIds", AuditArray.Of(new AuditObjectId(Task1), new AuditObjectId(Task2))), ("threshold", 10))),
            ("exampleKey", AuditNull.Instance),
            ("image", AuditObject.Of(("contentType", "image/png"), ("size", 70), ("hash", new string('a', 64))))));
    }

    [Fact]
    public void AnOnTimeWeeksRule_hasNoTasksInTheEntry()
    {
        var entry = BadgeAudit.ForCreate(Actor, MakeBadge(BadgeRule.OnTimeWeeks(4)));

        entry.After["rule"].Should().Be(AuditObject.Of(("type", "onTimeWeeks"), ("threshold", 4)));
    }

    [Fact]
    public void AnUpdate_listsTheChangedFieldsOnly_withTheRuleNested()
    {
        var before = MakeBadge();
        var after = before with { Name = "Toiletkoningin", Rule = before.Rule with { Threshold = 12 } };

        var change = BadgeAudit.Between(before, after);
        var entry = BadgeAudit.ForUpdate(Actor, BadgeId, change);

        entry.Before.Should().Be(AuditObject.Of(("name", "Toiletjuffrouw"), ("rule", AuditObject.Of(("threshold", 10)))));
        entry.After.Should().Be(AuditObject.Of(("name", "Toiletkoningin"), ("rule", AuditObject.Of(("threshold", 12)))));
        BadgeAudit.AffectsAwards(change).Should().BeTrue();
    }

    [Fact]
    public void ARename_doesNotAffectTheAwards()
    {
        var before = MakeBadge();

        var change = BadgeAudit.Between(before, before with { Name = "Anders" });

        BadgeAudit.AffectsAwards(change).Should().BeFalse();
    }

    [Fact]
    public void ADeactivation_affectsTheAwards()
    {
        var before = MakeBadge();

        BadgeAudit.AffectsAwards(BadgeAudit.Between(before, before with { Active = false })).Should().BeTrue();
    }

    [Fact]
    public void TheSameTasksInAnotherOrder_areNoChange()
    {
        var before = MakeBadge(new BadgeRule(BadgeRuleType.Executions, [Task2, Task1], 3));
        var after = MakeBadge(new BadgeRule(BadgeRuleType.Executions, [Task1, Task2], 3));

        BadgeAudit.Between(before, after).IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void AChangedPicture_isVisibleByItsHash()
    {
        var before = MakeBadge(image: new BadgeImageInfo(BadgeImageType.Png, 70, new string('a', 64)));
        var after = before with { Image = new BadgeImageInfo(BadgeImageType.Jpeg, 80, new string('b', 64)) };

        var entry = BadgeAudit.ForUpdate(Actor, BadgeId, BadgeAudit.Between(before, after));

        entry.Before["image"].Should().Be(AuditObject.Of(("contentType", "image/png"), ("size", 70), ("hash", new string('a', 64))));
        entry.After["image"].Should().Be(AuditObject.Of(("contentType", "image/jpeg"), ("size", 80), ("hash", new string('b', 64))));
    }

    [Fact]
    public void ADelete_keepsTheRemovedFields()
    {
        var entry = BadgeAudit.ForDelete(Actor, MakeBadge());

        entry.Action.Should().Be(AuditAction.Delete);
        entry.After.Should().Be(AuditObject.Empty);
        entry.Before["name"].Should().Be(new AuditString("Toiletjuffrouw"));
    }

    // ---- awards

    private static BadgeAward Award(DateTimeOffset at) => new("e00000000000000000000001", BadgeAward.KeyOf(BadgeId, Person), BadgeId, Person, at, T, T);

    [Fact]
    public void ACreatedAward_hasItsFieldsAndTheReasonAndTheBadgeName()
    {
        var entry = BadgeAudit.ForAward(Actor, new AppliedAward(AwardChange.Created, Award(T)), "Toiletjuffrouw", PointsSyncReason.Complete);

        entry.Entity.Should().Be(AuditEntity.BadgeAward);
        entry.Action.Should().Be(AuditAction.Create);
        entry.After.Should().Be(AuditObject.Of(
            ("key", $"badge:{BadgeId}:{Person}"), ("badgeId", new AuditObjectId(BadgeId)), ("personId", new AuditObjectId(Person)), ("awardedAt", T)));
        entry.Meta.Should().Be(AuditObject.Of(("reason", "complete"), ("badgeName", "Toiletjuffrouw")));
    }

    [Fact]
    public void AMovedAward_listsTheMovedMoment_withTheBadgeAndThePersonInTheMeta()
    {
        var earlier = T.AddDays(-1);

        var entry = BadgeAudit.ForAward(Actor, new AppliedAward(AwardChange.Updated, Award(earlier), Award(T)), "Toilet", PointsSyncReason.Correction);

        entry.Before.Should().Be(AuditObject.Of(("awardedAt", T)));
        entry.After.Should().Be(AuditObject.Of(("awardedAt", earlier)));
        entry.Meta.Should().Be(AuditObject.Of(
            ("reason", "correction"), ("badgeName", "Toilet"), ("badgeId", new AuditObjectId(BadgeId)), ("personId", new AuditObjectId(Person))));
    }

    [Fact]
    public void ARemovedAward_keepsItsFields()
    {
        var entry = BadgeAudit.ForAward(Actor, new AppliedAward(AwardChange.Removed, Award(T)), "Toilet", PointsSyncReason.Uncomplete);

        entry.Action.Should().Be(AuditAction.Delete);
        entry.After.Should().Be(AuditObject.Empty);
        entry.Before["awardedAt"].Should().Be(new AuditInstant(T));
        entry.Meta!["reason"].Should().Be(new AuditString("uncomplete"));
    }

    [Fact]
    public void TheSummary_isOneEntryWithTheFixedId_theChangesSortedByKey_andTheBadgeNames()
    {
        var other = "b00000000000000000000009";
        var applied = new[]
        {
            new AppliedAward(AwardChange.Removed, Award(T) with { PersonId = other, Key = BadgeAward.KeyOf(BadgeId, other) }),
            new AppliedAward(AwardChange.Created, Award(T)),
        };

        var entry = BadgeAudit.ForSummary(Actor, BadgeEvalTrigger.Badge, new BadgeEvaluationResult(1, 0, 1), applied, new Dictionary<string, string> { [BadgeId] = "Toilet" });

        entry.Entity.Should().Be(AuditEntity.BadgeAward);
        entry.Action.Should().Be(AuditAction.Recompute);
        entry.EntityId.Should().Be("000000000000000000000003");
        var meta = entry.Meta!;
        meta["trigger"].Should().Be(new AuditString("badge"));
        (meta["created"], meta["updated"], meta["removed"], meta["changesTotal"], meta["changesTruncated"])
            .Should().Be((new AuditInteger(1), new AuditInteger(0), new AuditInteger(1), new AuditInteger(2), new AuditBool(false)));
        var changes = ((AuditArray)meta["changes"]!).Items.Cast<AuditObject>().ToList();
        changes.Select(c => ((AuditString)c["change"]!).Value).Should().Equal("created", "removed");
        changes[0]["badgeName"].Should().Be(new AuditString("Toilet"));
    }

    [Fact]
    public void TheSummary_listsAtMost100Changes_andSaysSo()
    {
        var applied = Enumerable.Range(0, 101)
            .Select(i => new AppliedAward(AwardChange.Created, Award(T) with { Key = $"badge:{BadgeId}:{i:x24}" }))
            .ToList();

        var meta = BadgeAudit.ForSummary(Actor, BadgeEvalTrigger.Nightly, new BadgeEvaluationResult(101, 0, 0), applied, new Dictionary<string, string>()).Meta!;

        ((AuditArray)meta["changes"]!).Items.Should().HaveCount(100);
        meta["changesTotal"].Should().Be(new AuditInteger(101));
        meta["changesTruncated"].Should().Be(new AuditBool(true));
    }
}

public class BadgeCursorTests
{
    private const string Id = "d00000000000000000000001";

    [Fact]
    public void ABadgeCursor_roundTrips()
    {
        var cursor = new BadgeCursor(1_789_000_000_123, Id);

        BadgeCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
    }

    [Fact]
    public void AnAwardCursor_roundTrips()
    {
        var cursor = new BadgeAwardCursor(1_789_000_000_123, Id);

        BadgeAwardCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("W10")] // []
    [InlineData("WzEsIm5vcGUiXQ")] // [1,"nope"]
    public void ACursorThisApplicationDidNotProduce_isRefused(string? value)
    {
        BadgeCursor.TryDecode(value, out _).Should().BeFalse();
        BadgeAwardCursor.TryDecode(value, out _).Should().BeFalse();
    }

    [Fact]
    public void TheNamesOfTheRuleTypes_roundTrip()
    {
        foreach (var type in Enum.GetValues<BadgeRuleType>())
        {
            BadgeNames.TryParseRuleType(BadgeNames.ToWire(type), out var parsed).Should().BeTrue();
            parsed.Should().Be(type);
        }

        BadgeNames.TryParseRuleType("streak", out _).Should().BeFalse();
    }
}
