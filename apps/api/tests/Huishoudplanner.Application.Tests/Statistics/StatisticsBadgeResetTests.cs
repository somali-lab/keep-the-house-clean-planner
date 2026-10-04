using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Statistics;
using static Huishoudplanner.Application.Tests.Statistics.StatisticsWorld;

namespace Huishoudplanner.Application.Tests.Statistics;

/// <summary>The awards follow the history they are derived from (ADR-0014): after a statistics reset has committed they are rebuilt, and that never fails the reset.</summary>
public sealed class StatisticsBadgeResetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_rebuildsTheAwardsAfterItCommitted_asTheAdministrator_safely(bool purge)
    {
        var world = new StatisticsWorld();

        var result = await world.Service.ResetAsync(Admin, purge ? new DateOnly(2026, 10, 12) : null, Ct);

        result.IsT0.Should().BeTrue();
        world.Audit.Entries.Should().ContainSingle("the badge summary is written by the badge evaluation, which is a fake here");
        var call = world.Badges.Reconciles.Should().ContainSingle().Subject;
        (call.Trigger, call.Safely, call.Actor, call.Names).Should().Be((BadgeEvalTrigger.Reset, true, AuditActor.From(Admin), null));
    }

    [Fact]
    public async Task Reset_thatOnlyMovesTheFloor_rebuildsNoAwards()
    {
        var world = new StatisticsWorld();
        world.Resetter.Result = new StatisticsResetResult(0, 0, 0, 0, 0, 0, 0);

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.IsT0.Should().BeTrue();
        world.Audit.Entries.Should().ContainSingle("the floor move is audited");
        world.Badges.Reconciles.Should().BeEmpty("the history did not change, so the awards derived from it did not either (they never read the floor)");
    }

    [Fact]
    public async Task Reset_thatIsRefused_rebuildsNothing()
    {
        var world = new StatisticsWorld();

        var result = await world.Service.ResetAsync(Admin, new DateOnly(2026, 10, 15), Ct);

        result.IsT1.Should().BeTrue();
        world.Badges.Reconciles.Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_withoutSettings_rebuildsNothing()
    {
        var world = new StatisticsWorld { Settings = null };

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.IsT2.Should().BeTrue();
        world.Badges.Reconciles.Should().BeEmpty();
    }
}
