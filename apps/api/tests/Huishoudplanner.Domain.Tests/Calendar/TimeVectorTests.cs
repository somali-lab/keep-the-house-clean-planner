using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Calendar;

/// <summary>Runs every case of Vectors/time.json against <see cref="DayKeys"/>.</summary>
public class TimeVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("time");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "isDayKey" => DayKeys.IsDayKey(c.Text("key")),
        "today" => DayKeys.Today(c.Zone("tz"), c.Instant("now")),
        "toDayKey" => DayKeys.ToDayKey(c.Instant("instant"), c.Zone("tz")),
        "fromDayKey" => DayKeys.FromDayKey(c.Day("key"), c.Zone("tz")),
        "isTimeOfDay" => DayKeys.IsTimeOfDay(c.Text("value")),
        "fromDayKeyTime" => DayKeys.FromDayKeyTime(c.Day("key"), c.Text("time"), c.Zone("tz")),
        "addDays" => DayKeys.AddDays(c.Day("key"), c.WholeNumber("days")),
        "daysBetween" => DayKeys.DaysBetween(c.Day("from"), c.Day("to")),
        "weekdaySun0" => DayKeys.WeekdaySun0(c.Day("key")),
        "weekdayMon0" => DayKeys.WeekdayMon0(c.Day("key")),
        "sun0ToMon0" => DayKeys.Sun0ToMon0(c.WholeNumber("weekday")),
        "mon0ToSun0" => DayKeys.Mon0ToSun0(c.WholeNumber("weekday")),
        "isMonday" => DayKeys.IsMonday(c.Text("key")),
        "isWeekend" => DayKeys.IsWeekend(c.Day("key")),
        "isoWeek" => DayKeys.IsoWeek(c.Day("key")),
        "isoWeekLabel" => DayKeys.IsoWeekLabel(c.Day("key")),
        "mondayOfIsoWeek" => DayKeys.MondayOfIsoWeek(c.Text("label")),
        "mondayOf" => DayKeys.MondayOf(c.Day("key")),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };
}
