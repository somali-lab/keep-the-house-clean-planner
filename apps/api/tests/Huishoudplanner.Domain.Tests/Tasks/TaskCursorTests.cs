using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Tasks;

public sealed class TaskCursorTests
{
    [Fact]
    public void Encode_thenTryDecode_roundTrips_includingUnicodeNames()
    {
        var cursor = new TaskCursor("Wasruimte éè \"b\"", "0123456789abcdef01234567");

        TaskCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
        cursor.Encode().Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("e30")] // {}
    [InlineData("WzEsMl0")] // [1,2]
    [InlineData("WyJBIiwibm90LWFuLWlkIl0")] // ["A","not-an-id"]
    public void TryDecode_rejectsAnythingThisApplicationDidNotProduce(string? value)
    {
        TaskCursor.TryDecode(value, out _).Should().BeFalse();
    }
}
