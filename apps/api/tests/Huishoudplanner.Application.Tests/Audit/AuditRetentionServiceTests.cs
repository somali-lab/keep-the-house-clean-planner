using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Application.Tests.Audit;

/// <summary>Port of apps/server/test/audit-retention.test.ts (the use case; the manual trigger belongs to the jobs slice).</summary>
public sealed class AuditRetentionServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditWorld WithEntries(int? days)
    {
        var world = new AuditWorld { Retention = { Policy = new AuditRetentionPolicy(days) } };
        world.Add(AuditWorld.Now.AddDays(-31), meta: AuditObject.Of(("label", "old")));
        world.Add(AuditWorld.Now.AddDays(-30), meta: AuditObject.Of(("label", "exactly-at-cutoff")));
        world.Add(AuditWorld.Now.AddDays(-29), meta: AuditObject.Of(("label", "recent")));
        world.Add(AuditWorld.Now, meta: AuditObject.Of(("label", "today")));
        return world;
    }

    private static string[] Labels(AuditWorld world) =>
        [.. world.Store.Entries.OrderBy(e => e.At).Select(e => ((AuditString)e.Meta!["label"]!).Value)];

    [Fact]
    public async Task Run_deletesOnlyEntriesOlderThanTheRetention_andKeepsTheOneExactlyAtTheCutoff()
    {
        var world = WithEntries(30);

        var result = (await world.RetentionJob.RunAsync(Ct)).AsT1;

        result.Should().Be(new RetentionDone(AuditWorld.Now.AddDays(-30), 1));
        Labels(world).Should().Equal("exactly-at-cutoff", "recent", "today");
    }

    [Fact]
    public async Task Run_whenRetentionIsNotConfigured_isDisabled_andDeletesNothing()
    {
        var world = WithEntries(null);

        var result = await world.RetentionJob.RunAsync(Ct);

        result.IsT0.Should().BeTrue();
        Labels(world).Should().Equal("old", "exactly-at-cutoff", "recent", "today");
        world.Store.Cutoffs.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_isIdempotent()
    {
        var world = WithEntries(30);
        await world.RetentionJob.RunAsync(Ct);

        var second = (await world.RetentionJob.RunAsync(Ct)).AsT1;

        second.Deleted.Should().Be(0);
        Labels(world).Should().Equal("exactly-at-cutoff", "recent", "today");
    }

    [Fact]
    public async Task Run_followsTheClock()
    {
        var world = WithEntries(30);
        world.Time.Now = AuditWorld.Now.AddDays(2);

        var result = (await world.RetentionJob.RunAsync(Ct)).AsT1;

        result.Should().Be(new RetentionDone(AuditWorld.Now.AddDays(-28), 3));
    }

    [Fact]
    public async Task Run_writesNoAuditEntry()
    {
        var world = WithEntries(1);

        await world.RetentionJob.RunAsync(Ct);

        world.Store.Entries.Should().OnlyContain(e => e.At >= AuditWorld.Now.AddDays(-1));
        world.Store.Entries.Count.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Run_withANonPositiveRetention_refusesInsteadOfDeletingEverything(int days)
    {
        var world = WithEntries(days);

        var result = await world.RetentionJob.RunAsync(Ct);

        result.AsT2.Message.Should().StartWith("audit_retention.invalid_days");
        world.Store.Entries.Should().HaveCount(4);
    }

    [Fact]
    public async Task Run_passesOnAFailingPolicyRead_andAFailingDelete()
    {
        var failing = WithEntries(30);
        failing.Retention.Failure = new PortError("no config");
        (await failing.RetentionJob.RunAsync(Ct)).AsT2.Message.Should().Be("no config");

        var failingDelete = WithEntries(30);
        failingDelete.Store.Failure = new PortError("down");
        (await failingDelete.RetentionJob.RunAsync(Ct)).AsT2.Message.Should().Be("down");
    }
}
