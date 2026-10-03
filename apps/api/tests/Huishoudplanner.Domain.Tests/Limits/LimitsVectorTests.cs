using System.Text.Json;
using System.Text.Json.Nodes;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Limits;

/// <summary>
/// Runs every case of Vectors/limits.json: each limit and default the endpoint publishes equals the TypeScript constant
/// (the vector is generated from packages/shared by scripts/vectors.ts).
/// </summary>
public class LimitsVectorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<VectorCase> Cases => VectorCase.Load("limits");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    /// <summary>Guards the other direction: a limit added to the C# document without a TypeScript counterpart fails here.</summary>
    [Fact]
    public void Every_published_limit_is_pinned_by_the_vector()
    {
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "limits.json")))!;
        var expected = (JsonObject)file["cases"]!.AsArray().Single(c => c!["function"]!.GetValue<string>() == "limits")!["expected"]!;

        var published = Flatten(HouseholdLimits.Current).Select(p => p.Key);

        published.Should().BeEquivalentTo(expected.Select(p => p.Key));
    }

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "limits" => Flatten(HouseholdLimits.Current),
        "defaultPointsForDuration" => TaskPoints.DefaultForDuration(c.WholeNumber("minutes")),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    /// <summary>Group.name keys, arrays kept whole.</summary>
    private static JsonObject Flatten(HouseholdLimits limits)
    {
        var flat = new JsonObject();
        foreach (var (group, node) in (JsonObject)JsonSerializer.SerializeToNode(limits, Web)!)
        {
            foreach (var (name, value) in (JsonObject)node!)
            {
                flat[$"{group}.{name}"] = value!.DeepClone();
            }
        }

        return flat;
    }
}
