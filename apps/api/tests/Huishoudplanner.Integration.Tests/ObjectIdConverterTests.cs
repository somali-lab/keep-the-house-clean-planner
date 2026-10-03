using Huishoudplanner.Adapters.Mongo;
using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests;

public sealed class ObjectIdConverterTests
{
    private const string Hex = "507f1f77bcf86cd799439011";

    [Fact]
    public void TryParse_accepts_24_lowercase_hex_and_round_trips()
    {
        ObjectIdConverter.TryParse(Hex, out var id).Should().BeTrue();
        ObjectIdConverter.ToHex(id).Should().Be(Hex);
    }

    [Theory]
    [InlineData("507F1F77BCF86CD799439011")]
    [InlineData("507f1f77bcf86cd79943901F")]
    [InlineData("507f1f77bcf86cd79943901")]
    [InlineData("507f1f77bcf86cd7994390111")]
    [InlineData("zzzf1f77bcf86cd799439011")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_anything_but_24_lowercase_hex(string? value)
    {
        ObjectIdConverter.TryParse(value, out var id).Should().BeFalse();
        id.Should().Be(ObjectId.Empty);
    }

    [Fact]
    public void Parse_throws_for_uppercase()
    {
        var parse = () => ObjectIdConverter.Parse(Hex.ToUpperInvariant());
        parse.Should().Throw<FormatException>();
    }
}
