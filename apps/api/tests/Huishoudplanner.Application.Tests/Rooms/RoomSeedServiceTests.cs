using Huishoudplanner.Application.Rooms;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Application.Tests.Rooms;

/// <summary>The rooms part of apps/server/test/seed.test.ts at the use-case level.</summary>
public sealed class RoomSeedServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RoomWorld world = new();

    private RoomSeedService Service() => new(world.Rooms, world.Transactions, world.Audit, world.Clock);

    [Fact]
    public async Task Seed_emptyDatabase_createsTheSevenRoomsInOrder_onlyHeleHuisVirtual()
    {
        var created = (await Service().SeedAsync(Ct)).AsT0;

        created.Should().Be(7);
        world.Rooms.Items.Select(r => (r.Name, r.SortOrder, r.Virtual, r.Active, r.Version)).Should().Equal(
            ("Keuken", 10, false, true, 1),
            ("Badkamer", 20, false, true, 1),
            ("Toilet", 30, false, true, 1),
            ("Woonkamer", 40, false, true, 1),
            ("Slaapkamer", 50, false, true, 1),
            ("Hal", 60, false, true, 1),
            ("Hele huis", 70, true, true, 1));
        world.Rooms.Items.Should().OnlyContain(r => r.CreatedAt == RoomWorld.Now);
    }

    [Fact]
    public async Task Seed_auditsEveryCreateAsTheSystemActor_withTheShapeOfANormalCreate()
    {
        await Service().SeedAsync(Ct);

        world.Audit.Entries.Should().HaveCount(7);
        world.Audit.Entries.Should().OnlyContain(e =>
            e.Actor == AuditActor.System && e.Entity == AuditEntity.Room && e.Action == AuditAction.Create);
        world.Audit.Entries.Select(e => e.EntityId).Should().Equal(world.Rooms.Items.Select(r => r.Id));
        world.Audit.Entries[^1].After.Should().Be(AuditObject.Of(("name", "Hele huis"), ("sortOrder", 70), ("active", true), ("virtual", true)));
        world.Audit.Entries[^1].Before.Should().Be(AuditObject.Of());
    }

    [Fact]
    public async Task Seed_whenRoomsExist_addsNothingAndAuditsNothing()
    {
        world.Seed("Eigen kamer", 10);

        var created = (await Service().SeedAsync(Ct)).AsT0;

        created.Should().Be(0);
        world.Rooms.Items.Select(r => r.Name).Should().Equal("Eigen kamer");
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Seed_runTwice_isIdempotent()
    {
        var service = Service();
        await service.SeedAsync(Ct);

        var second = (await service.SeedAsync(Ct)).AsT0;

        second.Should().Be(0);
        world.Rooms.Items.Should().HaveCount(7);
        world.Audit.Entries.Should().HaveCount(7);
    }

    [Fact]
    public async Task Seed_auditFailure_rollsEveryRoomBack()
    {
        world.Audit.Failure = new PortError("audit down");

        var result = await Service().SeedAsync(Ct);

        result.AsT2.Message.Should().Be("audit down");
        world.Rooms.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Seed_countFailure_isReturnedAndWritesNothing()
    {
        world.Rooms.Failure = new PortError("count down");

        var result = await Service().SeedAsync(Ct);

        result.AsT2.Message.Should().Be("count down");
        world.Rooms.Writes.Should().Be(0);
    }
}
