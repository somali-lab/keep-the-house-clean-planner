using System.Globalization;
using System.Text.Json;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Due;

/// <summary>Runs every case of Vectors/due.json against <see cref="DueCalculator"/>.</summary>
public class DueVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("due");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_32_cases() => Cases.Count.Should().Be(32);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "dueState" => Name(DueCalculator.DueStateOf(c.Input.GetProperty("ratio").GetDouble())),
        "computeDue" => DueCalculator
            .ComputeDue(
                c.Input.GetProperty("tasks").EnumerateArray().Select(ToTask).ToList(),
                c.Input.GetProperty("intervals").EnumerateArray().Select(ToInterval).ToList(),
                c.Day("today"),
                c.Zone("timezone"))
            .Select(r => new { r.TaskId, r.DaysSince, r.PeriodDays, r.Ratio, State = Name(r.State) })
            .ToList(),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    private static string Name(DueState state) => state.ToString().ToLowerInvariant();

    private static DueTaskInput ToTask(JsonElement t) => new(
        t.GetProperty("_id").GetString()!,
        t.GetProperty("active").GetBoolean(),
        t.GetProperty("intervalKey").GetString()!,
        t.GetProperty("lastCompletedAt") is { ValueKind: JsonValueKind.String } last
            ? DateTimeOffset.Parse(last.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : null,
        DayKeys.Parse(t.GetProperty("initialDueDate").GetString()!));

    private static Interval ToInterval(JsonElement i) => new(
        i.GetProperty("key").GetString()!,
        i.GetProperty("label").GetString()!,
        i.GetProperty("perCycle") is { ValueKind: JsonValueKind.Number } per ? per.GetInt32() : null,
        i.GetProperty("periodDays").GetInt32());
}
