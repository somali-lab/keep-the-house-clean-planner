using Huishoudplanner.Domain.Rooms;

namespace Huishoudplanner.Domain.Tests.Rooms;

public sealed class RoomCursorTests
{
    [Fact]
    public void Encode_thenTryDecode_roundTrips_includingUnicodeNames()
    {
        var cursor = new RoomCursor(-20, "Wasruimte éè \"b\"", "0123456789abcdef01234567");

        RoomCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
    }

    [Fact]
    public void Encode_isUrlSafe()
    {
        var encoded = new RoomCursor(10, "????>>>>", "0123456789abcdef01234567").Encode();

        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("e30")] // {}
    [InlineData("WzEsMiwzXQ")] // [1,2,3]
    [InlineData("WzEsIkEiLCJub3QtYW4taWQiXQ")] // [1,"A","not-an-id"]
    public void TryDecode_rejectsAnythingThisApplicationDidNotProduce(string? value)
    {
        RoomCursor.TryDecode(value, out _).Should().BeFalse();
    }

    [Fact]
    public void After_takesThePositionOfARoom()
    {
        var room = new Room("0123456789abcdef01234567", "Keuken", 10, true, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        RoomCursor.After(room).Should().Be(new RoomCursor(10, "Keuken", "0123456789abcdef01234567"));
    }

    [Theory]
    [InlineData("0123456789abcdef01234567", true)]
    [InlineData("0123456789ABCDEF01234567", true)]
    [InlineData("0123456789abcdef0123456", false)]
    [InlineData("0123456789abcdef012345678", false)]
    [InlineData("0123456789abcdeg01234567", false)]
    [InlineData(null, false)]
    public void RoomIds_acceptExactly24HexCharacters(string? id, bool valid)
    {
        RoomIds.IsValid(id).Should().Be(valid);
    }
}
