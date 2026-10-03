using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Application.Tests;

public class CalendarServiceTests
{
    private static readonly DateOnly Anchor = DayKeys.Parse("2026-09-14");

    private sealed class FakeAnchor(OneOf<DateOnly, SettingsMissing, PortError> result) : ForReadingCycleAnchor
    {
        public int Calls { get; private set; }

        public Task<OneOf<DateOnly, SettingsMissing, PortError>> GetAnchorAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static (CalendarService Service, FakeAnchor Anchors) Create(OneOf<DateOnly, SettingsMissing, PortError>? anchor = null)
    {
        var anchors = new FakeAnchor(anchor ?? Anchor);
        return (new CalendarService(anchors, new HouseholdOptions("Europe/Amsterdam")), anchors);
    }

    private static async Task<OneOf<CalendarView, ValidationErrors, SettingsMissing, PortError>> Days(CalendarService service, string? from, string? to) =>
        await service.GetDaysAsync(from, to, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_range_returns_one_day_per_key_in_order_with_the_household_timezone()
    {
        var (service, _) = Create();

        var result = await Days(service, "2026-10-10", "2026-10-13");

        var view = result.AsT0;
        view.Timezone.Should().Be("Europe/Amsterdam");
        view.Days.Select(d => d.DayKey.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal("2026-10-10", "2026-10-11", "2026-10-12", "2026-10-13");
        view.Days.Select(d => d.CycleIndex).Should().Equal(0, 0, 1, 1);
        view.Days.Select(d => d.WeekIndex).Should().Equal(3, 3, 0, 0);
    }

    [Fact]
    public async Task A_single_day_range_is_allowed()
    {
        var (service, _) = Create();

        (await Days(service, "2026-10-10", "2026-10-10")).AsT0.Days.Should().ContainSingle();
    }

    [Fact]
    public async Task The_longest_range_is_371_days_and_one_more_is_refused()
    {
        var (service, _) = Create();

        (await Days(service, "2026-01-01", "2027-01-06")).AsT0.Days.Should().HaveCount(371);
        var tooLong = await Days(service, "2026-01-01", "2027-01-07");

        tooLong.AsT1.Errors.Should().ContainKey("to").WhoseValue.Should().Equal("range_too_long");
    }

    [Theory]
    [InlineData(null, "2026-10-10", "from", "required")]
    [InlineData("2026-10-10", null, "to", "required")]
    [InlineData("", "2026-10-10", "from", "required")]
    [InlineData("2026-13-01", "2026-10-10", "from", "invalid_day_key")]
    [InlineData("2026-10-10", "2026-02-30", "to", "invalid_day_key")]
    [InlineData("14-09-2026", "2026-10-10", "from", "invalid_day_key")]
    [InlineData("2026-10-11", "2026-10-10", "from", "from_after_to")]
    public async Task Invalid_input_is_a_validation_error_without_reading_the_settings(string? from, string? to, string field, string code)
    {
        var (service, anchors) = Create();

        var result = await Days(service, from, to);

        result.AsT1.Errors[field].Should().Equal(code);
        anchors.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Every_invalid_field_is_reported_at_once()
    {
        var (service, _) = Create();

        var result = await Days(service, null, "nope");

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("from", "to");
    }

    [Fact]
    public async Task Missing_settings_are_passed_on()
    {
        var (service, _) = Create(new SettingsMissing());

        (await Days(service, "2026-10-10", "2026-10-11")).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task A_port_failure_is_passed_on()
    {
        var (service, _) = Create(new PortError("down"));

        (await Days(service, "2026-10-10", "2026-10-11")).AsT3.Message.Should().Be("down");
    }

    [Fact]
    public async Task Days_before_the_anchor_get_negative_cycle_indexes()
    {
        var (service, _) = Create();

        var view = (await Days(service, "2026-09-13", "2026-09-13")).AsT0;

        view.Days[0].CycleIndex.Should().Be(-1);
        view.Days[0].WeekIndex.Should().Be(3);
    }
}
