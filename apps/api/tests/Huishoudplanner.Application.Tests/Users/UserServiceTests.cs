using Huishoudplanner.Application.Users;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Application.Tests.Users;

/// <summary>Every OneOf variant of the user use cases, with fakes behind the ports: audit input, rollback, no write on a no-op.</summary>
public sealed class UserServiceTests
{
    private const string UnknownId = "0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly UserWorld world = new();
    private readonly User admin;
    private readonly User member;

    public UserServiceTests()
    {
        admin = world.Add("Persoon 1", Role.Admin, minutesAfterEpoch: 0);
        member = world.Add("Persoon 2", Role.Member, minutesAfterEpoch: 1);
    }

    private UserService Service() => new(new FakeUserStore(world), new FakeTransactions(world), new FakeAudit(world), new FixedTime(UserWorld.Now));

    private static CreateUserInput NewUserInput(string name = "Logé", string? role = null, int[]? weekdays = null) =>
        new(name, "#16a34a", role, weekdays, null, null);

    // ---- list -------------------------------------------------------------------------------

    [Fact]
    public async Task List_returnsUsersOldestFirst()
    {
        var page = (await Service().ListAsync(null, null, null, Ct)).AsT0;

        page.Items.Select(u => u.Name).Should().Equal("Persoon 1", "Persoon 2");
        page.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData(true, new[] { "Persoon 1", "Persoon 2" })]
    [InlineData(false, new[] { "Tijdelijk" })]
    public async Task List_activeFilter_selectsActiveOrInactive(bool active, string[] expected)
    {
        world.Add("Tijdelijk", active: false, minutesAfterEpoch: 2);

        var page = (await Service().ListAsync(active, null, null, Ct)).AsT0;

        page.Items.Select(u => u.Name).Should().Equal(expected);
    }

    [Fact]
    public async Task List_pagesWithACursor_withoutLosingOrRepeatingAUser()
    {
        world.Add("Derde", minutesAfterEpoch: 2);
        var service = Service();

        var first = (await service.ListAsync(null, null, 2, Ct)).AsT0;
        var second = (await service.ListAsync(null, first.NextCursor, 2, Ct)).AsT0;

        first.Items.Select(u => u.Name).Should().Equal("Persoon 1", "Persoon 2");
        first.NextCursor.Should().NotBeNull();
        second.Items.Select(u => u.Name).Should().Equal("Derde");
        second.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("MTox")]
    public async Task List_malformedCursor_isAValidationErrorOnCursor(string cursor)
    {
        var result = await Service().ListAsync(null, cursor, null, Ct);

        result.AsT1.Errors.Should().ContainKey("cursor");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(UserLimits.MaxPageSize + 1)]
    public async Task List_limitOutOfRange_isAValidationErrorOnLimit(int limit)
    {
        var result = await Service().ListAsync(null, null, limit, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
    }

    [Fact]
    public async Task List_storeFailure_isThePortError()
    {
        world.ListFailure = new PortError("down");

        var result = await Service().ListAsync(null, null, null, Ct);

        result.AsT2.Message.Should().Be("down");
    }

    // ---- create -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_validInput_storesTheUser_withDefaults_andAuditsTheCreate()
    {
        var actor = UserWorld.ActorOf(admin, ActorSource.Ui);

        var user = (await Service().CreateAsync(actor, NewUserInput(weekdays: [2, 2, 0]), Ct)).AsT0;

        user.Name.Should().Be("Logé");
        user.Active.Should().BeTrue();
        user.Role.Should().Be(Role.Member);
        user.UnavailableWeekdays.Should().Equal(0, 2);
        user.DailyBudgetMinutes.Should().Be(new DailyMinutes(60, 120));
        user.MaxDailyMinutes.Should().Be(new DailyMinutes(60, 120));
        user.CreatedAt.Should().Be(UserWorld.Now);
        user.UpdatedAt.Should().Be(UserWorld.Now);
        user.BrowserNotifications.Should().Be(BrowserNotifications.Disabled);
        world.Users.Should().Contain(u => u.Id == user.Id);
        var entry = world.Audit.Should().ContainSingle().Subject;
        entry.Entity.Should().Be(AuditEntity.User);
        entry.Action.Should().Be(AuditAction.Create);
        entry.EntityId.Should().Be(user.Id);
        entry.Actor.Should().Be(new AuditActor(admin.Id, AuditSource.Ui));
        entry.Before.Count.Should().Be(0);
        entry.After["name"].Should().Be(new AuditString("Logé"));
        entry.After["role"].Should().Be(new AuditString("member"));
        entry.After["active"].Should().Be(new AuditBool(true));
        entry.After["unavailableWeekdays"].Should().Be(AuditArray.Of(0, 2));
    }

    [Fact]
    public async Task Create_apiClient_isAuditedWithSourceApi()
    {
        await Service().CreateAsync(UserWorld.ActorOf(admin, ActorSource.Api), NewUserInput(), Ct);

        world.Audit.Single().Actor.Source.Should().Be(AuditSource.Api);
    }

    [Fact]
    public async Task Create_withRoleAndBudgets_usesThem()
    {
        var input = NewUserInput(role: "planner") with
        {
            DailyBudgetMinutes = new DailyMinutesInput(45, 90),
            MaxDailyMinutes = new DailyMinutesInput(30, 60),
        };

        var user = (await Service().CreateAsync(UserWorld.ActorOf(admin), input, Ct)).AsT0;

        user.Role.Should().Be(Role.Planner);
        user.DailyBudgetMinutes.Should().Be(new DailyMinutes(45, 90));
        user.MaxDailyMinutes.Should().Be(new DailyMinutes(30, 60));
    }

    [Fact]
    public async Task Create_invalidInput_isValidationErrors_andWritesNothing()
    {
        var input = new CreateUserInput(null, "blue", null, [7], null, null);

        var result = await Service().CreateAsync(UserWorld.ActorOf(admin), input, Ct);

        result.AsT1.Errors.Keys.Should().Contain(["name", "color", "unavailableWeekdays.0"]);
        world.Inserts.Should().Be(0);
        world.Audit.Should().BeEmpty();
        world.TransactionRuns.Should().Be(0);
    }

    [Fact]
    public async Task Create_auditFailure_rollsTheUserBack_andIsThePortError()
    {
        world.AuditFailure = new PortError("audit down");

        var result = await Service().CreateAsync(UserWorld.ActorOf(admin), NewUserInput(), Ct);

        result.AsT3.Message.Should().Be("audit down");
        world.Users.Should().HaveCount(2);
        world.Audit.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_storeFailure_isThePortError_andWritesNoAudit()
    {
        world.InsertFailure = new PortError("insert failed");

        var result = await Service().CreateAsync(UserWorld.ActorOf(admin), NewUserInput(), Ct);

        result.AsT3.Message.Should().Be("insert failed");
        world.Audit.Should().BeEmpty();
    }

    // ---- update -----------------------------------------------------------------------------

    [Fact]
    public async Task Update_availabilityAndBudget_auditsOnlyTheChangedFields()
    {
        var input = new UpdateUserInput(UnavailableWeekdays: [2], DailyBudgetMinutes: new DailyMinutesInput(45, 120));

        var user = (await Service().UpdateAsync(UserWorld.ActorOf(admin, ActorSource.Api), member.Id, input, Ct)).AsT0;

        user.UnavailableWeekdays.Should().Equal(2);
        user.DailyBudgetMinutes.Should().Be(new DailyMinutes(45, 120));
        user.UpdatedAt.Should().Be(UserWorld.Now);
        var entry = world.Audit.Should().ContainSingle().Subject;
        entry.Action.Should().Be(AuditAction.Update);
        entry.EntityId.Should().Be(member.Id);
        entry.Actor.Source.Should().Be(AuditSource.Api);
        entry.Before.Should().Be(AuditObject.Of(
            ("unavailableWeekdays", AuditArray.Of()),
            ("dailyBudgetMinutes", AuditObject.Of(("weekday", 60)))));
        entry.After.Should().Be(AuditObject.Of(
            ("unavailableWeekdays", AuditArray.Of(2)),
            ("dailyBudgetMinutes", AuditObject.Of(("weekday", 45)))));
    }

    [Fact]
    public async Task Update_normalisesThePatch_andLeavesOtherFieldsAlone()
    {
        var user = (await Service().UpdateAsync(
            UserWorld.ActorOf(admin), member.Id, new UpdateUserInput(Name: "  Anna  ", UnavailableWeekdays: [3, 1, 3]), Ct)).AsT0;

        user.Name.Should().Be("Anna");
        user.UnavailableWeekdays.Should().Equal(1, 3);
        user.Color.Should().Be(member.Color);
    }

    [Fact]
    public async Task Update_deactivate_isAnUpdateNotADelete()
    {
        var user = (await Service().UpdateAsync(UserWorld.ActorOf(admin), member.Id, new UpdateUserInput(Active: false), Ct)).AsT0;

        user.Active.Should().BeFalse();
        world.Audit.Single().Action.Should().Be(AuditAction.Update);
    }

    [Fact]
    public async Task Update_patchThatChangesNothing_writesAndAuditsNothing_andReturnsTheStoredUser()
    {
        var input = new UpdateUserInput(Name: member.Name, UnavailableWeekdays: [], DailyBudgetMinutes: new DailyMinutesInput(60, 120));

        var user = (await Service().UpdateAsync(UserWorld.ActorOf(admin), member.Id, input, Ct)).AsT0;

        user.Should().Be(member);
        world.Updates.Should().Be(0);
        world.Audit.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_emptyPatch_isANoOp()
    {
        await Service().UpdateAsync(UserWorld.ActorOf(admin), member.Id, new UpdateUserInput(), Ct);

        world.Updates.Should().Be(0);
        world.Audit.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_unknownUser_isNotFound()
    {
        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), UnknownId, new UpdateUserInput(Name: "X"), Ct);

        result.IsT1.Should().BeTrue();
        world.Audit.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456")]
    public async Task Update_malformedId_isAValidationErrorOnId(string id)
    {
        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), id, new UpdateUserInput(Name: "X"), Ct);

        result.AsT2.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task Update_invalidInput_isValidationErrors_andWritesNothing()
    {
        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), member.Id, new UpdateUserInput(Color: "blue", Role: "boss"), Ct);

        result.AsT2.Errors.Keys.Should().Contain(["color", "role"]);
        world.Updates.Should().Be(0);
    }

    [Fact]
    public async Task Update_unknownUserWithInvalidInput_isValidationFirst()
    {
        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), UnknownId, new UpdateUserInput(Color: "blue"), Ct);

        result.IsT2.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(null, "member")]
    [InlineData(null, "planner")]
    public async Task Update_removingTheLastActiveAdmin_isLastAdmin_andWritesNothing(bool? active, string? role)
    {
        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), admin.Id, new UpdateUserInput(Active: active, Role: role), Ct);

        var conflict = result.AsT3;
        conflict.Code.Should().Be("last_admin");
        world.Updates.Should().Be(0);
        world.Audit.Should().BeEmpty();
        world.Users.Single(u => u.Id == admin.Id).Should().Be(admin);
    }

    [Fact]
    public async Task Update_removingAnAdminWhenAnotherActiveAdminExists_succeeds()
    {
        world.Add("Tweede admin", Role.Admin, minutesAfterEpoch: 5);

        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), admin.Id, new UpdateUserInput(Role: "member"), Ct);

        result.AsT0.Role.Should().Be(Role.Member);
    }

    [Fact]
    public async Task Update_anInactiveAdminDoesNotCountAsAnotherAdmin()
    {
        world.Add("Oud admin", Role.Admin, active: false, minutesAfterEpoch: 5);

        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), admin.Id, new UpdateUserInput(Active: false), Ct);

        result.AsT3.Code.Should().Be("last_admin");
    }

    [Fact]
    public async Task Update_savingTheLastAdminAsAdmin_isNotAnAttemptToRemoveThem()
    {
        var result = await Service().UpdateAsync(
            UserWorld.ActorOf(admin), admin.Id, new UpdateUserInput(Role: "admin", Active: true, Name: "Nieuwe naam"), Ct);

        result.AsT0.Name.Should().Be("Nieuwe naam");
    }

    [Fact]
    public async Task Update_demotingAnInactiveAdmin_isAllowed()
    {
        var inactiveAdmin = world.Add("Oud admin", Role.Admin, active: false, minutesAfterEpoch: 5);

        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), inactiveAdmin.Id, new UpdateUserInput(Role: "member"), Ct);

        result.AsT0.Role.Should().Be(Role.Member);
    }

    [Fact]
    public async Task Update_auditFailure_rollsTheUpdateBack()
    {
        world.AuditFailure = new PortError("audit down");

        var result = await Service().UpdateAsync(UserWorld.ActorOf(admin), member.Id, new UpdateUserInput(Name: "Anna"), Ct);

        result.AsT4.Message.Should().Be("audit down");
        world.Users.Single(u => u.Id == member.Id).Name.Should().Be(member.Name);
    }

    // ---- browser notification moments -------------------------------------------------------

    [Fact]
    public async Task SetBrowserNotifications_ownMoments_areStoredSorted_andAudited()
    {
        var input = new BrowserNotificationsInput(true, ["18:30", "08:00"]);

        var user = (await Service().SetBrowserNotificationsAsync(UserWorld.ActorOf(member), member.Id, input, Ct)).AsT0;

        user.BrowserNotifications.Should().Be(new BrowserNotifications(true, ["08:00", "18:30"]));
        var entry = world.Audit.Should().ContainSingle().Subject;
        entry.Actor.ActorId.Should().Be(member.Id);
        entry.EntityId.Should().Be(member.Id);
        entry.Action.Should().Be(AuditAction.Update);
        entry.Before.Should().Be(AuditObject.Of(("browserNotifications", AuditObject.Of(("enabled", false), ("times", AuditArray.Of())))));
        entry.After.Should().Be(AuditObject.Of(("browserNotifications", AuditObject.Of(
            ("enabled", true), ("times", AuditArray.Of("08:00", "18:30"))))));
    }

    [Fact]
    public async Task SetBrowserNotifications_onlyTheChangedFieldIsAudited()
    {
        var service = Service();
        await service.SetBrowserNotificationsAsync(UserWorld.ActorOf(member), member.Id, new BrowserNotificationsInput(true, ["18:30", "08:00"]), Ct);
        world.Audit.Clear();

        await service.SetBrowserNotificationsAsync(UserWorld.ActorOf(member), member.Id, new BrowserNotificationsInput(false, ["18:30", "08:00"]), Ct);

        var entry = world.Audit.Should().ContainSingle().Subject;
        entry.Before.Should().Be(AuditObject.Of(("browserNotifications", AuditObject.Of(("enabled", true)))));
        entry.After.Should().Be(AuditObject.Of(("browserNotifications", AuditObject.Of(("enabled", false)))));
    }

    [Fact]
    public async Task SetBrowserNotifications_unchangedSetting_writesAndAuditsNothing()
    {
        var service = Service();
        await service.SetBrowserNotificationsAsync(UserWorld.ActorOf(member), member.Id, new BrowserNotificationsInput(false, ["08:00", "18:30"]), Ct);
        world.Audit.Clear();
        var updates = world.Updates;

        var result = await service.SetBrowserNotificationsAsync(UserWorld.ActorOf(member), member.Id, new BrowserNotificationsInput(false, ["18:30", "08:00"]), Ct);

        result.IsT0.Should().BeTrue();
        world.Updates.Should().Be(updates);
        world.Audit.Should().BeEmpty();
    }

    [Fact]
    public async Task SetBrowserNotifications_aMemberForSomeoneElse_isForbidden_andWritesNothing()
    {
        var result = await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(member), admin.Id, new BrowserNotificationsInput(true, ["07:00"]), Ct);

        result.AsT3.Detail.Should().NotBeNullOrWhiteSpace();
        world.Updates.Should().Be(0);
        world.Audit.Should().BeEmpty();
    }

    [Fact]
    public async Task SetBrowserNotifications_aPlannerForSomeoneElse_isForbidden()
    {
        var planner = world.Add("Planner", Role.Planner, minutesAfterEpoch: 5);

        var result = await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(planner), member.Id, new BrowserNotificationsInput(true, ["07:00"]), Ct);

        result.IsT3.Should().BeTrue();
    }

    [Fact]
    public async Task SetBrowserNotifications_anAdminForSomeoneElse_isAttributedToTheAdmin()
    {
        var user = (await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(admin), member.Id, new BrowserNotificationsInput(true, ["07:15"]), Ct)).AsT0;

        user.BrowserNotifications.Times.Should().Equal("07:15");
        var entry = world.Audit.Should().ContainSingle().Subject;
        entry.Actor.ActorId.Should().Be(admin.Id);
        entry.EntityId.Should().Be(member.Id);
    }

    [Fact]
    public async Task SetBrowserNotifications_actorIdInOtherCase_isStillTheSamePerson()
    {
        var actor = new Actor(member.Id.ToUpperInvariant(), Role.Member, ActorSource.Ui);

        var result = await Service().SetBrowserNotificationsAsync(actor, member.Id, new BrowserNotificationsInput(true, ["07:15"]), Ct);

        result.IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task SetBrowserNotifications_unknownPerson_isNotFound_forAnAdmin()
    {
        var result = await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(admin), UnknownId, new BrowserNotificationsInput(true, []), Ct);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task SetBrowserNotifications_malformedId_isAValidationErrorOnId()
    {
        var result = await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(admin), "nope", new BrowserNotificationsInput(true, []), Ct);

        result.AsT2.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task SetBrowserNotifications_invalidMoments_areValidationErrors_beforeThePermissionCheck()
    {
        var result = await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(member), admin.Id, new BrowserNotificationsInput(true, ["25:00"]), Ct);

        result.AsT2.Errors.Should().ContainKey("times.0");
        world.Updates.Should().Be(0);
    }

    [Fact]
    public async Task SetBrowserNotifications_auditFailure_rollsBack()
    {
        world.AuditFailure = new PortError("audit down");

        var result = await Service().SetBrowserNotificationsAsync(
            UserWorld.ActorOf(member), member.Id, new BrowserNotificationsInput(true, ["08:00"]), Ct);

        result.AsT5.Message.Should().Be("audit down");
        world.Users.Single(u => u.Id == member.Id).BrowserNotifications.Should().Be(BrowserNotifications.Disabled);
    }
}
