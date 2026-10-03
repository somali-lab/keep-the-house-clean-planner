using Huishoudplanner.Adapters.Jobs;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Jobs;

/// <summary>
/// The nightly generation through the scheduler on the real host and a real MongoDB replica set. The scheduler's clock is a fake that a test moves
/// to 03:00 household time (the generation itself reads the harness clock, Monday 2026-09-14), so nothing waits for a cron time. Ports the nightly
/// parts of generation.test.ts: the run is attributed to the system, is idempotent and records its run id.
/// </summary>
public sealed class NightlyJobMongoTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobScheduler SchedulerOf(GenerationHarness h, FakeTimeProvider clock) =>
        new(h.Services.GetServices<IJob>(), h.Services.GetRequiredService<JobRunner>(), clock,
            new JobsOptions(true, TimeZoneInfo.FindSystemTimeZoneById(JobsRig.Amsterdam)));

    private static async Task<(string Room, string Plan, string Task)> ArrangeAsync(GenerationHarness h)
    {
        var room = await h.SeedRoomAsync("Badkamer");
        var plan = await h.ActivePlanIdAsync();
        var task = await h.NewTaskAsync("Badkamer", room, "1w");
        await h.StoreSlotsAsync(plan, [.. Enumerable.Range(0, 4).Select(week => (task, week, 3, (string?)null))]);
        return (room, plan, task);
    }

    [Fact]
    public async Task A_tick_at_0300_generates_the_current_and_the_next_cycle_as_the_system_with_one_run_id()
    {
        using var h = new GenerationHarness(mongo);
        await ArrangeAsync(h);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)); // 02:00 CEST
        var scheduler = SchedulerOf(h, clock);

        scheduler.StartDue(Ct).Should().BeEmpty();
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 14, 1, 0, 0, TimeSpan.Zero)); // 03:00 CEST
        var outcomes = await Task.WhenAll(scheduler.StartDue(Ct));

        outcomes.Should().Equal(JobOutcome.Succeeded);
        (await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(8);
        var created = await h.AuditAsync("occurrence", "create");
        created.Should().HaveCount(8);
        created.Select(e => e["actorId"].AsObjectId.ToString()).Distinct().Should().Equal("000000000000000000000000");
        created.Select(e => e["source"].AsString).Distinct().Should().Equal("system");
        created.Select(e => e["meta"]["runId"].AsString).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_night_changes_and_audits_nothing()
    {
        using var h = new GenerationHarness(mongo);
        await ArrangeAsync(h);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
        var scheduler = SchedulerOf(h, clock);
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 14, 1, 0, 0, TimeSpan.Zero));
        await Task.WhenAll(scheduler.StartDue(Ct));
        var audited = await h.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero)); // 03:00 the next night
        var outcomes = await Task.WhenAll(scheduler.StartDue(Ct));

        outcomes.Should().BeEquivalentTo([JobOutcome.Succeeded, JobOutcome.Skipped]); // the 03:45 retention of the first night was not run yet, so it is due as well
        (await h.Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(8);
        (await h.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(audited);
    }

    [Fact]
    public async Task Audit_retention_at_0345_is_skipped_without_AUDIT_RETENTION_DAYS_and_deletes_nothing()
    {
        using var h = new GenerationHarness(mongo);
        await h.AuditLog.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "at", new BsonDateTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
                { "actorId", ObjectId.Parse("000000000000000000000000") }, { "entity", "task" }, { "entityId", ObjectId.GenerateNewId() },
                { "action", "update" }, { "before", new BsonDocument() }, { "after", new BsonDocument() }, { "source", "system" },
            },
            cancellationToken: Ct);
        var before = await h.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
        var scheduler = SchedulerOf(h, clock);

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 14, 1, 45, 0, TimeSpan.Zero));
        var outcomes = await Task.WhenAll(scheduler.StartDue(Ct));

        outcomes.Should().BeEquivalentTo([JobOutcome.Succeeded, JobOutcome.Skipped]); // generation at 03:00 was due as well
        (await h.AuditLog.CountDocumentsAsync(new BsonDocument("entity", "task"), cancellationToken: Ct)).Should().Be(1);
        before.Should().BeGreaterThan(0);
    }
}
