using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Domain.Tests.Users;

/// <summary>The limits of packages/shared/src/schemas/users.ts, the patch rules and the cursor.</summary>
public sealed class UserRulesTests
{
    private static CreateUserInput Valid() => new("Anna", "#16a34a", null, null, null, null);

    // ---- create -----------------------------------------------------------------------------

    [Fact]
    public void ParseCreate_minimalInput_getsTheDefaults()
    {
        var user = UserRules.ParseCreate(Valid()).AsT0;

        user.Role.Should().Be(Role.Member);
        user.UnavailableWeekdays.Should().BeEmpty();
        user.DailyBudgetMinutes.Should().Be(new DailyMinutes(60, 120));
        user.MaxDailyMinutes.Should().Be(new DailyMinutes(60, 120));
    }

    [Fact]
    public void ParseCreate_trimsTheName_andSortsAndDeduplicatesWeekdays()
    {
        var user = UserRules.ParseCreate(Valid() with { Name = "  Anna  ", UnavailableWeekdays = [2, 2, 0] }).AsT0;

        user.Name.Should().Be("Anna");
        user.UnavailableWeekdays.Should().Equal(0, 2);
    }

    [Fact]
    public void ParseCreate_missingRequiredFields_namesThem()
    {
        var errors = UserRules.ParseCreate(new CreateUserInput(null, null, null, null, null, null)).AsT1;

        errors.Errors.Keys.Should().BeEquivalentTo("name", "color");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseCreate_blankName_isRejected(string name)
    {
        UserRules.ParseCreate(Valid() with { Name = name }).AsT1.Errors.Should().ContainKey("name");
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("16a34a")]
    [InlineData("#gggggg")]
    [InlineData("#16a34a\n")]
    public void ParseCreate_invalidColor_isRejected(string color)
    {
        UserRules.ParseCreate(Valid() with { Color = color }).AsT1.Errors["color"].Should().Equal(UserRules.InvalidColor);
    }

    [Theory]
    [InlineData("#16a34a")]
    [InlineData("#16A34A")]
    public void ParseCreate_hexColorOfEitherCase_isAccepted(string color)
    {
        UserRules.ParseCreate(Valid() with { Color = color }).AsT0.Color.Should().Be(color);
    }

    [Fact]
    public void ParseCreate_weekdayOutsideZeroToSix_isRejectedWithItsIndex()
    {
        var errors = UserRules.ParseCreate(Valid() with { UnavailableWeekdays = [1, 7, -1] }).AsT1;

        errors.Errors.Keys.Should().BeEquivalentTo("unavailableWeekdays.1", "unavailableWeekdays.2");
    }

    [Theory]
    [InlineData("boss")]
    [InlineData("ADMIN")]
    [InlineData("")]
    public void ParseCreate_unknownRole_isRejected(string role)
    {
        UserRules.ParseCreate(Valid() with { Role = role }).AsT1.Errors.Should().ContainKey("role");
    }

    [Theory]
    [InlineData("admin", Role.Admin)]
    [InlineData("planner", Role.Planner)]
    [InlineData("member", Role.Member)]
    public void ParseCreate_knownRole_isParsed(string role, Role expected)
    {
        UserRules.ParseCreate(Valid() with { Role = role }).AsT0.Role.Should().Be(expected);
    }

    [Fact]
    public void ParseCreate_budgetNeedsBothValues_andNoNegatives()
    {
        var errors = UserRules.ParseCreate(Valid() with
        {
            DailyBudgetMinutes = new DailyMinutesInput(30, null),
            MaxDailyMinutes = new DailyMinutesInput(-1, 0),
        }).AsT1;

        errors.Errors.Keys.Should().BeEquivalentTo("dailyBudgetMinutes.weekend", "maxDailyMinutes.weekday");
    }

    [Fact]
    public void ParseCreate_zeroMinutes_areValid()
    {
        var user = UserRules.ParseCreate(Valid() with { DailyBudgetMinutes = new DailyMinutesInput(0, 0) }).AsT0;

        user.DailyBudgetMinutes.Should().Be(new DailyMinutes(0, 0));
    }

    // ---- patch ------------------------------------------------------------------------------

    [Fact]
    public void ParsePatch_emptyInput_isAnEmptyPatch()
    {
        UserRules.ParsePatch(new UpdateUserInput()).AsT0.Should().Be(new UserPatch());
    }

    [Fact]
    public void ParsePatch_validatesOnlyWhatIsGiven()
    {
        var errors = UserRules.ParsePatch(new UpdateUserInput(Name: " ", Color: "x", Role: "boss")).AsT1;

        errors.Errors.Keys.Should().BeEquivalentTo("name", "color", "role");
    }

    [Fact]
    public void ParsePatch_notificationErrors_carryThePrefix()
    {
        var errors = UserRules.ParsePatch(new UpdateUserInput(BrowserNotifications: new BrowserNotificationsInput(true, ["8:00"]))).AsT1;

        errors.Errors.Keys.Should().BeEquivalentTo("browserNotifications.times.0");
    }

    [Fact]
    public void ParsePatch_activeFalse_isCarried()
    {
        UserRules.ParsePatch(new UpdateUserInput(Active: false)).AsT0.Active.Should().BeFalse();
    }

    // ---- browser notifications --------------------------------------------------------------

    [Fact]
    public void ParseBrowserNotifications_sortsTheTimes()
    {
        var parsed = UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, ["18:30", "08:00"])).AsT0;

        parsed.Should().Be(new BrowserNotifications(true, ["08:00", "18:30"]));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("8:00")]
    [InlineData("08:60")]
    [InlineData("0800")]
    [InlineData("24:00")]
    [InlineData("")]
    public void ParseBrowserNotifications_invalidTime_isRejectedOnItsIndex(string time)
    {
        var errors = UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, ["07:00", time])).AsT1;

        errors.Errors["times.1"].Should().Equal(UserRules.InvalidTime);
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("23:59")]
    [InlineData("09:05")]
    public void ParseBrowserNotifications_edgeTimes_areValid(string time)
    {
        UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, [time])).IsT0.Should().BeTrue();
    }

    [Fact]
    public void ParseBrowserNotifications_duplicateTimes_areRejected()
    {
        var errors = UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, ["08:00", "08:00"])).AsT1;

        errors.Errors["times"].Should().Equal(UserRules.DuplicateTime);
    }

    [Fact]
    public void ParseBrowserNotifications_sevenTimes_areTooMany_sixAreFine()
    {
        string[] seven = ["01:00", "02:00", "03:00", "04:00", "05:00", "06:00", "07:00"];

        UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, seven)).AsT1.Errors.Should().ContainKey("times");
        UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, seven[..6])).IsT0.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, new[] { "08:00" }, "enabled")]
    [InlineData(true, null, "times")]
    public void ParseBrowserNotifications_missingMember_isRejected(bool? enabled, string[]? times, string field)
    {
        UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(enabled, times)).AsT1.Errors.Should().ContainKey(field);
    }

    [Fact]
    public void ParseBrowserNotifications_noTimesButEnabled_isValid()
    {
        UserRules.ParseBrowserNotifications(new BrowserNotificationsInput(true, [])).AsT0.Should().Be(new BrowserNotifications(true, []));
    }

    // ---- rules ------------------------------------------------------------------------------

    private static User Admin(bool active = true) => new(
        "0123456789abcdef01234567", "A", "#000000", active, Role.Admin, [], UserDefaults.NewUserMinutes,
        UserDefaults.NewUserMinutes, BrowserNotifications.Disabled, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(null, Role.Member, true)]
    [InlineData(null, Role.Planner, true)]
    [InlineData(null, Role.Admin, false)]
    public void RemovesActiveAdmin_anActiveAdminThatIsDeactivatedOrDemoted(bool? active, Role? role, bool expected)
    {
        UserRules.RemovesActiveAdmin(Admin(), new UserPatch(Active: active, Role: role)).Should().Be(expected);
    }

    [Fact]
    public void RemovesActiveAdmin_anInactiveAdminOrANonAdmin_neverCounts()
    {
        UserRules.RemovesActiveAdmin(Admin(active: false), new UserPatch(Role: Role.Member)).Should().BeFalse();
        UserRules.RemovesActiveAdmin(Admin() with { Role = Role.Member }, new UserPatch(Active: false)).Should().BeFalse();
    }

    [Fact]
    public void Apply_setsOnlyTheGivenMembers_andTheUpdateMoment()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);

        var after = UserRules.Apply(Admin(), new UserPatch(Name: "B", Active: false), now);

        after.Name.Should().Be("B");
        after.Active.Should().BeFalse();
        after.Color.Should().Be("#000000");
        after.UpdatedAt.Should().Be(now);
        after.CreatedAt.Should().Be(DateTimeOffset.UnixEpoch);
    }

    // ---- cursor and ids ---------------------------------------------------------------------

    [Fact]
    public void Cursor_roundTrips()
    {
        var cursor = new UserCursor(DateTimeOffset.FromUnixTimeMilliseconds(1_789_000_000_123), "0123456789abcdef01234567");

        UserCursor.TryParse(cursor.Encode(), out var parsed).Should().BeTrue();

        parsed.Should().Be(cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("MTox")]
    public void Cursor_garbage_isNotParsed(string? text)
    {
        UserCursor.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0123456789abcdef01234567", true)]
    [InlineData("0123456789ABCDEF01234567", true)]
    [InlineData("0123456789abcdef0123456", false)]
    [InlineData("0123456789abcdef0123456g", false)]
    [InlineData(null, false)]
    public void IsObjectId_isTwentyFourHexCharacters(string? value, bool expected)
    {
        UserRules.IsObjectId(value).Should().Be(expected);
    }
}
