using Huishoudplanner.Application.Users;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Application.Tests.Users;

/// <summary>The users part of apps/server/test/seed.test.ts at the use-case level.</summary>
public sealed class UserSeedServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SeedProfile[] Defaults = [new("Persoon 1", "#2563eb"), new("Persoon 2", "#db2777")];

    private readonly UserWorld world = new();

    private UserSeedService Service() => new(new FakeUserStore(world), new FakeTransactions(world), new FakeAudit(world), new FixedTime(UserWorld.Now));

    [Fact]
    public async Task Seed_emptyDatabase_createsOneUserPerProfile_firstAdminOthersMembers_withTheSeedBudget()
    {
        var created = (await Service().SeedAsync(Defaults, Ct)).AsT0;

        created.Should().Be(2);
        world.Users.Select(u => (u.Name, u.Color, u.Role)).Should().Equal(
            ("Persoon 1", "#2563eb", Role.Admin),
            ("Persoon 2", "#db2777", Role.Member));
        world.Users.Should().OnlyContain(u => u.DailyBudgetMinutes == new DailyMinutes(60, 120) && u.Active);
    }

    [Fact]
    public async Task Seed_auditsEveryCreateAsTheSystemActor()
    {
        await Service().SeedAsync(Defaults, Ct);

        world.Audit.Should().HaveCount(2);
        world.Audit.Should().OnlyContain(e =>
            e.Actor == AuditActor.System && e.Entity == AuditEntity.User && e.Action == AuditAction.Create);
        world.Audit.Select(e => e.EntityId).Should().Equal(world.Users.Select(u => u.Id));
    }

    [Fact]
    public async Task Seed_aConfigurableNumberOfProfiles()
    {
        SeedProfile[] three = [new("Anna", "#111111"), new("Bram", "#222222"), new("Chris", "#333333")];

        var created = (await Service().SeedAsync(three, Ct)).AsT0;

        created.Should().Be(3);
        world.Users.Select(u => u.Name).Should().Equal("Anna", "Bram", "Chris");
    }

    [Fact]
    public async Task Seed_whenUsersExist_addsNothingAndAuditsNothing()
    {
        world.Add("Solo", Role.Admin);

        var created = (await Service().SeedAsync(Defaults, Ct)).AsT0;

        created.Should().Be(0);
        world.Users.Select(u => u.Name).Should().Equal("Solo");
        world.Audit.Should().BeEmpty();
    }

    [Fact]
    public async Task Seed_runTwice_isIdempotent()
    {
        var service = Service();
        await service.SeedAsync(Defaults, Ct);

        var second = (await service.SeedAsync(Defaults, Ct)).AsT0;

        second.Should().Be(0);
        world.Users.Should().HaveCount(2);
        world.Audit.Should().HaveCount(2);
    }

    [Fact]
    public async Task Seed_auditFailure_rollsEveryUserBack()
    {
        world.AuditFailure = new PortError("audit down");

        var result = await Service().SeedAsync(Defaults, Ct);

        result.AsT2.Message.Should().Be("audit down");
        world.Users.Should().BeEmpty();
    }
}
