using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Calendar;

/// <summary>Runs every case of Vectors/cycle.json against <see cref="Cycles"/>.</summary>
public class CycleVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("cycle");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "assertValidAnchor" => AssertValidAnchor(c.Day("anchor")),
        "cycleIndexFor" => Cycles.CycleIndexFor(c.Day("date"), c.Day("anchor")),
        "cycleStart" => Cycles.CycleStart(c.WholeNumber("index"), c.Day("anchor")),
        "cycleEnd" => Cycles.CycleEnd(c.WholeNumber("index"), c.Day("anchor")),
        "weekIndexFor" => Cycles.WeekIndexFor(c.Day("date"), c.Day("anchor")),
        "slotDate" => Cycles.SlotDate(c.Day("cycleStartDate"), c.WholeNumber("weekIndex"), c.WholeNumber("weekday")),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    private static object? AssertValidAnchor(DateOnly anchor)
    {
        Cycles.AssertValidAnchor(anchor);
        return null;
    }
}
