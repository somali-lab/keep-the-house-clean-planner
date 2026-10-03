using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>Runs every case of Vectors/points.json against <see cref="PointsMoney"/> and <see cref="Currencies"/>.</summary>
public class PointsVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("points");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_18_cases() => Cases.Count.Should().Be(18);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "pointsToCents" => PointsMoney.PointsToCents(c.Input.GetProperty("points").GetInt64(), c.Input.GetProperty("centsPerPoint").GetInt64()),
        "isTwoDecimalCurrency" => Currencies.HasTwoDecimals(c.Text("code")),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };
}
