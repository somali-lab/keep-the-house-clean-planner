using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Transfer;

namespace Huishoudplanner.Domain.Tests.Transfer;

public sealed class TransferTests
{
    private sealed class Parsed(int version) : ParsedImport(version, "2026-09-16T08:00:00.000Z");

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(4, true, true)]
    [InlineData(5, false, true)]
    [InlineData(6, false, false)]
    public void AnOlderFile_hasNoRedemptionsBeforeVersion5_andNoBadgesBeforeVersion6(int version, bool losesRedemptions, bool losesBadges)
    {
        TransferVersions.LosesRedemptions(version).Should().Be(losesRedemptions);
        TransferVersions.LosesBadges(version).Should().Be(losesBadges);
    }

    [Fact]
    public void TheCurrentVersion_isTheOneTheNodeServerWrites()
    {
        TransferVersions.Current.Should().Be(6);
        TransferVersions.Oldest.Should().Be(1);
    }

    [Fact]
    public void TheAuditEntry_isOneCreateOfTheImportEntity_withTheCountsFlatInAfter_andTheFileInMeta()
    {
        var result = new ImportResult(new ReplacedCounts(1, 3, 2, 4, 1, 2, 9, 1, 2), 7, 5, 1, 4, 3);
        var actor = new AuditActor("0123456789abcdef01234567", AuditSource.Ui);

        var entry = ImportAudit.ForImport(actor, new Parsed(5), result, "aaaaaaaaaaaaaaaaaaaaaaaa");

        entry.Entity.Should().Be(AuditEntity.Import);
        entry.Action.Should().Be(AuditAction.Create);
        entry.EntityId.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaa");
        entry.Before.Count.Should().Be(0);
        entry.After.Keys.Should().Equal(
            "settings", "users", "rooms", "tasks", "cyclePlans", "cycles", "occurrences", "pointEntries", "badges",
            "auditAdded", "removedPointEntries", "removedRedemptions", "removedBadges", "removedBadgeAwards");
        entry.After["occurrences"].Should().Be((AuditValue)9);
        entry.After["auditAdded"].Should().Be((AuditValue)7);
        entry.After["removedBadges"].Should().Be((AuditValue)4);
        entry.After["removedBadgeAwards"].Should().Be((AuditValue)3);
        entry.Meta!.Keys.Should().Equal("mode", "schemaVersion", "exportedAt");
        entry.Meta["mode"].Should().Be((AuditValue)"replace");
        entry.Meta["schemaVersion"].Should().Be((AuditValue)5);
        entry.Meta["exportedAt"].Should().Be((AuditValue)"2026-09-16T08:00:00.000Z");
    }
}
