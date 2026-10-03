using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>A <see cref="BsonDocument"/> is also a sequence of elements, which hides the object assertions; this compares the documents themselves.</summary>
internal static class BsonAssertions
{
    public static void ShouldBeBson(this BsonValue actual, BsonValue expected, string because = "")
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        actual.ToJson().Should().Be(expected.ToJson(), because);
    }
}
