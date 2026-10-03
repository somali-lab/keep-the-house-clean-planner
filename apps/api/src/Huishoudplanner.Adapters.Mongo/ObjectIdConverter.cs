using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>Ids are ObjectId in storage and 24 lowercase hex characters in the API (D11).</summary>
internal static class ObjectIdConverter
{
    public static string ToHex(ObjectId id) => id.ToString();

    public static bool TryParse(string? hex, out ObjectId id)
    {
        id = default;
        return hex is { Length: 24 } && hex.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) && ObjectId.TryParse(hex, out id);
    }

    public static ObjectId Parse(string hex) =>
        TryParse(hex, out var id) ? id : throw new FormatException("Not a 24 character hexadecimal ObjectId.");
}
