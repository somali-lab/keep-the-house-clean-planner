using System.Buffers.Text;
using System.Text;
using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Tests.Audit;

public sealed class AuditCursorTests
{
    private const string Id = "0123456789abcdef01234567";

    private static readonly DateTimeOffset At = new(2026, 9, 18, 8, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Encode_thenTryDecode_roundTrips()
    {
        var cursor = new AuditCursor(At, Id);

        AuditCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
    }

    [Fact]
    public void Encode_usesTheFormatOfTheNodeServer_soCursorsWorkAcrossBothApplications()
    {
        var node = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("2026-09-18T08:00:00.123Z|" + Id));

        new AuditCursor(At, Id).Encode().Should().Be(node);
        AuditCursor.TryDecode(node, out var decoded).Should().BeTrue();
        decoded.Should().Be(new AuditCursor(At, Id));
    }

    [Fact]
    public void Encode_convertsToUtc_andKeepsMilliseconds()
    {
        var cursor = new AuditCursor(new DateTimeOffset(2026, 9, 18, 10, 0, 0, 5, TimeSpan.FromHours(2)), Id);

        var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.Encode()));

        text.Should().Be("2026-09-18T08:00:00.005Z|" + Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bm9wZQ")] // nope
    [InlineData("not a cursor!")]
    public void TryDecode_rejectsAnythingThisApplicationDidNotProduce(string? value)
    {
        AuditCursor.TryDecode(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("|0123456789abcdef01234567")]
    [InlineData("2026-09-18T08:00:00.000Z|")]
    [InlineData("2026-09-18T08:00:00.000Z|not-an-id")]
    [InlineData("yesterday|0123456789abcdef01234567")]
    public void TryDecode_rejectsABadInstantOrId(string text)
    {
        AuditCursor.TryDecode(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text)), out _).Should().BeFalse();
    }

    [Fact]
    public void After_takesThePositionOfAnEntry()
    {
        var entry = new AuditLogEntry(Id, At, Id, "room", Id, "create", AuditObject.Empty, AuditObject.Empty, "ui", null);

        AuditCursor.After(entry).Should().Be(new AuditCursor(At, Id));
    }

    [Theory]
    [InlineData("room", true)]
    [InlineData("badgeAward", true)]
    [InlineData("Room", false)]
    [InlineData("banana", false)]
    [InlineData(null, false)]
    public void TryParseEntity_acceptsTheWireNamesOnly(string? wire, bool valid)
    {
        AuditNames.TryParseEntity(wire, out _).Should().Be(valid);
    }

    [Theory]
    [InlineData("system", true)]
    [InlineData("origin", false)]
    public void TryParseSource_acceptsTheWireNamesOnly(string wire, bool valid)
    {
        AuditNames.TryParseSource(wire, out _).Should().Be(valid);
    }
}
