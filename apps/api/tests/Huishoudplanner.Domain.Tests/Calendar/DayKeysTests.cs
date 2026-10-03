using System.Globalization;
using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Tests.Calendar;

/// <summary>Scenarios of time.test.ts and the property-style rules the vectors do not export.</summary>
public class DayKeysTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone(DayKeys.AppTimezone);

    private static readonly string[] Valid = ["00:00", "07:30", "23:59"];
    private static readonly string[] Invalid = ["24:00", "7:30", "07:60", "0730", ""];

    private static DateOnly D(string key) => DayKeys.Parse(key);

    private static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void AppTimezone_resolves_to_Europe_Amsterdam() =>
        Amsterdam.GetUtcOffset(Utc("2026-09-14T00:00:00Z")).Should().Be(TimeSpan.FromHours(2));

    [Fact]
    public void Today_reads_the_time_provider() =>
        DayKeys.Today(Amsterdam, new FixedTimeProvider(Utc("2026-09-13T22:30:00Z"))).Should().Be(D("2026-09-14"));

    [Fact]
    public void FromDayKey_then_ToDayKey_round_trips_local_midnight()
    {
        var instant = DayKeys.FromDayKey(D("2026-09-14"), Amsterdam);
        instant.Should().Be(Utc("2026-09-13T22:00:00Z"));
        DayKeys.ToDayKey(instant, Amsterdam).Should().Be(D("2026-09-14"));
    }

    [Theory]
    [InlineData("2026-03-29")]
    [InlineData("2026-10-25")]
    [InlineData("2026-12-31")]
    [InlineData("2028-02-29")]
    public void FromDayKey_then_ToDayKey_round_trips_on_dst_and_boundary_days(string key) =>
        DayKeys.ToDayKey(DayKeys.FromDayKey(D(key), Amsterdam), Amsterdam).Should().Be(D(key));

    [Fact]
    public void Dst_spring_forward_day_is_23_hours_but_day_arithmetic_is_unaffected()
    {
        (DayKeys.FromDayKey(D("2026-03-30"), Amsterdam) - DayKeys.FromDayKey(D("2026-03-29"), Amsterdam))
            .Should().Be(TimeSpan.FromHours(23));
        DayKeys.AddDays(D("2026-03-28"), 2).Should().Be(D("2026-03-30"));
        DayKeys.DaysBetween(D("2026-03-28"), D("2026-03-30")).Should().Be(2);
    }

    [Fact]
    public void Dst_fall_back_day_is_25_hours_but_day_arithmetic_is_unaffected()
    {
        (DayKeys.FromDayKey(D("2026-10-26"), Amsterdam) - DayKeys.FromDayKey(D("2026-10-25"), Amsterdam))
            .Should().Be(TimeSpan.FromHours(25));
        DayKeys.DaysBetween(D("2026-10-24"), D("2026-10-27")).Should().Be(3);
    }

    [Fact]
    public void AddDays_and_DaysBetween_are_inverse()
    {
        var start = D("2026-02-27");
        for (var days = -400; days <= 400; days += 37)
        {
            DayKeys.DaysBetween(start, DayKeys.AddDays(start, days)).Should().Be(days);
        }
    }

    [Fact]
    public void Weekday_conversions_round_trip()
    {
        for (var i = 0; i < 7; i++)
        {
            DayKeys.Mon0ToSun0(DayKeys.Sun0ToMon0(i)).Should().Be(i);
            DayKeys.Sun0ToMon0(DayKeys.Mon0ToSun0(i)).Should().Be(i);
        }
    }

    [Fact]
    public void Weekdays_of_a_known_week_agree_with_the_conversions()
    {
        var monday = D("2026-09-14");
        for (var i = 0; i < 7; i++)
        {
            var day = DayKeys.AddDays(monday, i);
            DayKeys.WeekdayMon0(day).Should().Be(i);
            DayKeys.WeekdaySun0(day).Should().Be(DayKeys.Mon0ToSun0(i));
        }
    }

    [Fact]
    public void MondayOf_returns_a_Monday_within_six_days_before()
    {
        var day = D("2026-12-20");
        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            var monday = DayKeys.MondayOf(day);
            DayKeys.IsMonday(monday).Should().BeTrue();
            DayKeys.DaysBetween(monday, day).Should().BeInRange(0, 6);
        }
    }

    [Fact]
    public void IsoWeekLabel_then_MondayOfIsoWeek_gives_the_Monday_of_the_week()
    {
        var day = D("2020-12-01");
        for (var i = 0; i < 1500; i++, day = day.AddDays(1))
        {
            DayKeys.MondayOfIsoWeek(DayKeys.IsoWeekLabel(day)).Should().Be(DayKeys.MondayOf(day));
        }
    }

    [Fact]
    public void IsoWeek_handles_year_boundaries_with_week_53()
    {
        DayKeys.IsoWeekLabel(D("2027-01-04")).Should().Be("2027-W01");
        DayKeys.IsoWeekLabel(D("2025-12-29")).Should().Be("2026-W01");
        DayKeys.IsoWeekLabel(D("2021-01-01")).Should().Be("2020-W53");
        DayKeys.MondayOfIsoWeek("2027-W53").Should().BeNull();
    }

    [Fact]
    public void IsTimeOfDay_accepts_and_rejects_the_documented_samples()
    {
        Valid.Should().OnlyContain(t => DayKeys.IsTimeOfDay(t));
        Invalid.Should().OnlyContain(t => !DayKeys.IsTimeOfDay(t));
    }

    [Fact]
    public void IsDayKey_rejects_non_ascii_digits_and_surrounding_text()
    {
        DayKeys.IsDayKey("٢٠٢٦-٠٩-١٤").Should().BeFalse();
        DayKeys.IsDayKey(" 2026-09-14").Should().BeFalse();
        DayKeys.IsDayKey("2026-09-14\n").Should().BeFalse();
    }

    [Fact]
    public void FindZone_with_an_unknown_id_throws_ArgumentOutOfRangeException()
    {
        var act = () => DayKeys.FindZone("Not/AZone");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Parse_with_an_invalid_key_throws_FormatException()
    {
        var act = () => DayKeys.Parse("2026-02-30");
        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("12:30\n")]
    [InlineData("12:30 ")]
    public void IsTimeOfDay_rejects_trailing_characters(string value) =>
        DayKeys.IsTimeOfDay(value).Should().BeFalse();

    [Fact]
    public void FromDayKeyTime_with_a_trailing_newline_throws_ArgumentOutOfRangeException()
    {
        var act = () => DayKeys.FromDayKeyTime(D("2026-09-16"), "12:30\n", Amsterdam);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("2026-W38\n")]
    [InlineData("9999-W52")]
    [InlineData("9999-W53")]
    [InlineData("0000-W01")]
    public void MondayOfIsoWeek_returns_null_for_invalid_or_out_of_range_labels(string label) =>
        DayKeys.MondayOfIsoWeek(label).Should().BeNull();
}
