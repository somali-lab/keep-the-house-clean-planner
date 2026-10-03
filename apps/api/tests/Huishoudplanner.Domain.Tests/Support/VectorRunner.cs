using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Huishoudplanner.Domain.Tests.Support;

/// <summary>Runs a vector case against a dispatch function and asserts the expected value or the thrown error.</summary>
public static class VectorRunner
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void Run(VectorCase vectorCase, Func<VectorCase, object?> dispatch)
    {
        if (vectorCase.Throws)
        {
            Assert.Equal("RangeError", vectorCase.ThrowsKind);
            var act = () => dispatch(vectorCase);
            act.Should().Throw<Exception>().Which.Should().Match<Exception>(
                e => e is ArgumentOutOfRangeException || e is FormatException,
                "the vectors' RangeError maps to ArgumentOutOfRangeException or FormatException");
            return;
        }

        var actual = ToNode(dispatch(vectorCase));
        var expected = vectorCase.Expected;
        JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            "{0} expected {1} but was {2}", vectorCase, expected?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");
    }

    /// <summary>Instants and day keys use the notation of the vector files.</summary>
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        DateOnly day => JsonValue.Create(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTimeOffset instant => JsonValue.Create(
            instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        string s => JsonValue.Create(s),
        _ => JsonSerializer.SerializeToNode(value, Options),
    };
}
