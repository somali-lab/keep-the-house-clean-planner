using System.Buffers.Text;
using System.Text.Json;

namespace Huishoudplanner.Domain.Rooms;

/// <summary>What a list read asks for. The order is fixed: sort order, then name, then id.</summary>
public sealed record RoomListQuery(bool? Active, int Limit, RoomCursor? After)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>One page of rooms. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record RoomList(IReadOnlyList<Room> Items, string? NextCursor);

/// <summary>
/// The position after a room in the list order (sort order, name, id). Opaque to clients: <see cref="Encode"/> gives the string
/// they pass back as <c>cursor</c>.
/// </summary>
public sealed record RoomCursor(int SortOrder, string Name, string Id)
{
    public static RoomCursor After(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new(room.SortOrder, room.Name, room.Id);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { SortOrder, Name, Id }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out RoomCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 3 ||
                !root[0].TryGetInt32(out var sortOrder) ||
                root[1].ValueKind != JsonValueKind.String ||
                root[2].ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = root[1].GetString()!;
            var id = root[2].GetString()!;
            if (!RoomIds.IsValid(id))
            {
                return false;
            }

            cursor = new RoomCursor(sortOrder, name, id);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>The id format of the API: 24 hexadecimal characters.</summary>
public static class RoomIds
{
    public static bool IsValid(string? id) =>
        id is { Length: 24 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}
