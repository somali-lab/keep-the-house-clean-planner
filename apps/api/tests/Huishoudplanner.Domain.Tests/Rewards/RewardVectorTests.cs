using System.Text.Json;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Rewards;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tests.Support;
using static Huishoudplanner.Domain.Tests.Support.PointsVectorArguments;

namespace Huishoudplanner.Domain.Tests.Rewards;

/// <summary>Runs every case of Vectors/rewards.json against <see cref="RewardMeter"/>.</summary>
public class RewardVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("rewards");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_61_cases() => Cases.Count.Should().Be(61);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "automaticGoal" => RewardMeter.AutomaticGoalFor(
            [.. c.Input.GetProperty("items").EnumerateArray().Select(i => new GoalOccurrence(ReadOccurrence(i), i.GetProperty("points").GetInt32()))],
            c.Text("personId"),
            PeriodOf(c.Input.GetProperty("period"))),
        "resolveRewardGoal" => Resolve(c),
        "rewardPercent" => RewardMeter.Percent(
            c.Input.GetProperty("earnedPoints").GetInt64(),
            NullableInt(c.Input.GetProperty("goalPoints"))),
        "eggsForPercent" => RewardMeter.EggsForPercent(c.Input.GetProperty("percent").GetDouble()),
        "sameRewardGoals" => RewardMeter.SameGoals(Goals(c.Input.GetProperty("a")), Goals(c.Input.GetProperty("b"))),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    private static object Resolve(VectorCase c)
    {
        int? explicitGoal = c.Input.TryGetProperty("explicit", out var value) ? NullableInt(value) : null;
        var automatic = c.Input.GetProperty("automatic");
        var resolved = RewardMeter.ResolveGoal(explicitGoal, new AutomaticGoal(automatic.GetProperty("planned").GetInt32(), automatic.GetProperty("points").GetInt32()));
        return new { resolved.GoalPoints, Source = resolved.Source == RewardGoalSource.Explicit ? "explicit" : "automatic" };
    }

    private static Period PeriodOf(JsonElement e) => new(PeriodUnit.Week, DayKeys.Parse(e.GetProperty("start").GetString()!), DayKeys.Parse(e.GetProperty("end").GetString()!));

    private static int? NullableInt(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    private static RewardGoals Goals(JsonElement e) => new(NullableInt(e.GetProperty("weekPoints")), NullableInt(e.GetProperty("cyclePoints")));
}
