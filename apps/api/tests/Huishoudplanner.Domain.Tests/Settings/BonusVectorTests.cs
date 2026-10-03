using System.Text.Json;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Settings;

/// <summary>Runs every case of Vectors/bonuses.json against <see cref="BonusSchedule"/>.</summary>
public class BonusVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("bonuses");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_20_cases() => Cases.Count.Should().Be(20);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "bonusAmountsOn" => BonusSchedule.AmountsOn(Rows(c.Input.GetProperty("schedule")), c.Day("day")),
        "sameBonusAmounts" => BonusSchedule.SameAmounts(Amounts(c.Input.GetProperty("a")), Amounts(c.Input.GetProperty("b"))),
        "scheduleWithAmounts" => BonusSchedule
            .WithAmounts(Rows(c.Input.GetProperty("schedule")), Amounts(c.Input.GetProperty("amounts")), c.Day("today"))
            .Select(r => new { r.From, r.Amounts.WeekDone, r.Amounts.WeekOnTime, r.Amounts.CycleDone, r.Amounts.CycleOnTime })
            .ToList(),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    private static List<BonusScheduleRow> Rows(JsonElement schedule) => schedule.EnumerateArray()
        .Select(r => new BonusScheduleRow(DateOnly.Parse(r.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture), Amounts(r)))
        .ToList();

    private static BonusAmounts Amounts(JsonElement e) => new(
        e.GetProperty("weekDone").GetInt32(),
        e.GetProperty("weekOnTime").GetInt32(),
        e.GetProperty("cycleDone").GetInt32(),
        e.GetProperty("cycleOnTime").GetInt32());
}
