using System.Globalization;
using System.Text;

namespace Huishoudplanner.Domain.Users;

/// <summary>
/// The position after a user in the list order (<c>createdAt</c>, then <c>_id</c>). Opaque to clients: base64url of
/// <c>unixMilliseconds:id</c>.
/// </summary>
public sealed record UserCursor(DateTimeOffset CreatedAt, string Id)
{
    public static UserCursor For(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserCursor(user.CreatedAt, user.Id);
    }

    public string Encode()
    {
        var raw = $"{CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}:{Id}";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryParse(string? encoded, out UserCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 100)
        {
            return false;
        }

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var raw = Encoding.ASCII.GetString(Convert.FromBase64String(padded));
            var parts = raw.Split(':');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var millis) ||
                !UserRules.IsObjectId(parts[1]))
            {
                return false;
            }

            cursor = new UserCursor(DateTimeOffset.FromUnixTimeMilliseconds(millis), parts[1].ToLowerInvariant());
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
