using Huishoudplanner.Application.Settings;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Application.Tests.Settings;

/// <summary>The settings part of the seed of apps/server/src/domain/seed.ts (seed.test.ts), with fake driven ports.</summary>
public class SettingsSeedServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static (SettingsSeedService Service, FakeSettingsStore Store, FakeAudit Audit) Create(HouseholdSettings? existing, string now = "2026-09-16T08:00:00Z")
    {
        var store = new FakeSettingsStore(existing);
        var audit = new FakeAudit();
        var clock = new FixedClock(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        return (new SettingsSeedService(store, audit, new FakeTransactions(store, audit), clock, new HouseholdOptions("Europe/Amsterdam")), store, audit);
    }

    [Fact]
    public async Task Seed_without_settings_creates_the_defaults_anchored_on_the_monday_of_this_week()
    {
        var (service, store, _) = Create(null);

        var result = await service.SeedAsync(Ct);

        result.AsT0.Should().Be(SettingsSeedResult.Created);
        var settings = store.Document!;
        settings.CycleAnchorDate.Should().Be(new DateOnly(2026, 9, 14), "16 September 2026 is a Wednesday");
        settings.WeekStartsOn.Should().Be(1);
        settings.Timezone.Should().Be("Europe/Amsterdam");
        settings.VacationRanges.Should().BeEmpty();
        settings.Intervals.Should().Equal(DueCalculator.DefaultIntervals);
        settings.AiProvider.Should().Be(new AiProviderSettings(AiProviderType.None));
        settings.CompletionControl.Should().Be(CompletionControl.Circle);
        settings.AiPrompts.Should().Be(SettingsDefaults.AiPrompts);
        settings.PromoteThreshold.Should().Be(2);
        settings.DismissedPromotions.Should().BeEmpty();
        settings.BonusSchedule.Should().BeNull("a missing schedule means no bonuses");
        settings.CreatedAt.Should().Be(settings.UpdatedAt);
    }

    [Fact]
    public async Task Seed_audits_the_creation_with_the_whole_document_as_the_system_actor()
    {
        var (service, _, audit) = Create(null);

        await service.SeedAsync(Ct);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Entity.Should().Be(AuditEntity.Settings);
        entry.Action.Should().Be(AuditAction.Create);
        entry.EntityId.Should().Be("000000000000000000000001");
        entry.Actor.Should().Be(AuditActor.System);
        entry.Before.Count.Should().Be(0);
        entry.After["cycleAnchorDate"].Should().Be((AuditValue)"2026-09-14");
        entry.After.Keys.Should().NotContain(["createdAt", "updatedAt", "_id", "bonusSchedule"]);
    }

    [Fact]
    public async Task Seed_is_idempotent_and_audits_nothing_for_existing_settings()
    {
        var existing = SettingsSamples.Seeded() with { PromoteThreshold = 4 };
        var (service, store, audit) = Create(existing);

        var result = await service.SeedAsync(Ct);

        result.AsT0.Should().Be(SettingsSeedResult.Unchanged);
        store.Document.Should().Be(existing);
        store.Writes.Should().Be(0);
        audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Seed_adds_the_shipped_3w_interval_before_2w_to_older_settings()
    {
        var older = SettingsSamples.Seeded() with { Intervals = [.. DueCalculator.DefaultIntervals.Where(i => i.Key != "3w")] };
        var (service, store, audit) = Create(older);

        var result = await service.SeedAsync(Ct);

        result.AsT0.Should().Be(SettingsSeedResult.IntervalAdded);
        store.Document!.Intervals.Select(i => i.Key).Should().Equal("daily", "3w", "2w", "1w", "2wk", "4wk", "quarter");
        var entry = audit.Entries.Should().ContainSingle().Subject;
        (entry.Action, entry.Actor).Should().Be((AuditAction.Update, AuditActor.System));
    }

    [Fact]
    public async Task Seed_appends_the_3w_interval_when_2w_is_missing_too()
    {
        var older = SettingsSamples.Seeded() with { Intervals = [new Interval("1w", "1x per week", 4, 7)] };
        var (service, store, _) = Create(older);

        await service.SeedAsync(Ct);

        store.Document!.Intervals.Select(i => i.Key).Should().Equal("1w", "3w");
    }

    [Fact]
    public async Task Seed_passes_a_store_port_error_on()
    {
        var (service, store, _) = Create(null);
        store.Failure = new PortError("down");

        (await service.SeedAsync(Ct)).AsT2.Message.Should().Be("down");
    }

    [Fact]
    public async Task Seed_rolls_the_creation_back_when_the_audit_entry_fails()
    {
        var (service, store, audit) = Create(null);
        audit.Failure = new PortError("audit down");

        var result = await service.SeedAsync(Ct);

        result.AsT2.Message.Should().Be("audit down");
        store.Document.Should().BeNull();
    }
}
