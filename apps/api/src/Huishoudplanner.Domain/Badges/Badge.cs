using System.Buffers.Text;
using System.Text.Json;

namespace Huishoudplanner.Domain.Badges;

/// <summary>What identifies the stored bytes of a badge image and where it is served from; the bytes themselves never travel in a <see cref="Badge"/>.</summary>
/// <param name="ContentType">The checked type.</param>
/// <param name="Size">The number of bytes.</param>
/// <param name="Hash">Hex SHA-256 of the bytes (lower case).</param>
public sealed record BadgeImageInfo(BadgeImageType ContentType, int Size, string Hash)
{
    /// <summary>The length of the hash prefix in the <c>v</c> parameter of the image address.</summary>
    public const int VersionLength = 12;

    /// <summary>The first <see cref="VersionLength"/> characters of the hash: a changed picture has a new address, so it can be cached for good.</summary>
    public string Version => Hash[..VersionLength];
}

/// <summary>A checked image with its bytes, as stored and as served (ADR-0014: binary in the badge document).</summary>
public sealed record BadgeImageData(byte[] Bytes, BadgeImageType ContentType, string Hash)
{
    public int Size => Bytes.Length;

    public BadgeImageInfo Info => new(ContentType, Bytes.Length, Hash);
}

/// <summary>A badge definition (requirements 3 <c>badges</c>, 4.13). The rule names its tasks once, as sorted lower case ids.</summary>
public sealed record Badge(
    string Id,
    string Name,
    string Description,
    BadgeRule Rule,
    bool Active,
    string? ExampleKey,
    BadgeImageInfo? Image,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A badge as the store is asked to create it. The store assigns the id.</summary>
public sealed record NewBadge(string Name, string Description, BadgeRule Rule, bool Active, string? ExampleKey, BadgeImageData? Image, DateTimeOffset CreatedAt);

/// <summary>
/// The fields a store write changes: exactly the ones that differ from the stored badge, already normalised. <see cref="Image"/> is
/// <see langword="null"/> when the picture does not change; <see cref="ClearImage"/> removes it.
/// </summary>
public sealed record BadgeChanges(
    string? Name = null,
    string? Description = null,
    BadgeRule? Rule = null,
    bool? Active = null,
    BadgeImageData? Image = null,
    bool ClearImage = false);

/// <summary>One derived award: a person holds a badge since <see cref="AwardedAt"/>, the moment the data first crossed the threshold.</summary>
public sealed record BadgeAward(string Id, string Key, string BadgeId, string PersonId, DateTimeOffset AwardedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The unique key of the award of one person: <c>badge:&lt;badgeId&gt;:&lt;personId&gt;</c>.</summary>
    public static string KeyOf(string badgeId, string personId)
    {
        ArgumentNullException.ThrowIfNull(badgeId);
        ArgumentNullException.ThrowIfNull(personId);
        return $"badge:{badgeId.ToLowerInvariant()}:{personId.ToLowerInvariant()}";
    }
}

/// <summary>A done execution credited to a person, as a badge rule reads it (the credit rule of the points ledger).</summary>
public sealed record CreditedExecution(string PersonId, BadgeExecution Execution);

/// <summary>An on-time week bonus of a person, dated on the last day of that week.</summary>
public sealed record OnTimeWeek(string PersonId, DateTimeOffset Date);

/// <summary>The wire names of the badge rule kinds (the TypeScript <c>BADGE_RULE_TYPES</c>) and image types.</summary>
public static class BadgeNames
{
    public static string ToWire(BadgeRuleType type) => type switch
    {
        BadgeRuleType.Executions => "executions",
        BadgeRuleType.Minutes => "minutes",
        BadgeRuleType.OnTimeWeeks => "onTimeWeeks",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParseRuleType(string? wire, out BadgeRuleType type)
    {
        foreach (var candidate in Enum.GetValues<BadgeRuleType>())
        {
            if (string.Equals(ToWire(candidate), wire, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    public static string ToWire(BadgeLanguage language) => language == BadgeLanguage.Nl ? "nl" : "en";

    public static bool TryParseImageType(string? wire, out BadgeImageType type)
    {
        foreach (var candidate in Enum.GetValues<BadgeImageType>())
        {
            if (string.Equals(candidate.ContentType(), wire, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }
}

/// <summary>What a list read asks for. The order is fixed: oldest first (creation instant, then id), like the Node server.</summary>
public sealed record BadgeListQuery(bool? Active, int Limit, BadgeCursor? After)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 100;
}

/// <summary>One page of badges. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record BadgeList(IReadOnlyList<Badge> Items, string? NextCursor);

/// <summary>The position after a badge in the list order (creation instant in milliseconds, id).</summary>
public sealed record BadgeCursor(long CreatedAtMs, string Id)
{
    public static BadgeCursor After(Badge badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        return new(badge.CreatedAt.ToUnixTimeMilliseconds(), badge.Id);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { CreatedAtMs, Id }));

    public static bool TryDecode(string? value, out BadgeCursor cursor) => CursorCodec.TryDecode(value, out cursor, (at, id) => new BadgeCursor(at, id));
}

/// <summary>What a list of awards asks for. The order is fixed: <c>awardedAt</c>, then id.</summary>
public sealed record BadgeAwardQuery(string? PersonId, int Limit, BadgeAwardCursor? After)
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;
}

/// <summary>One page of awards. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record BadgeAwardList(IReadOnlyList<BadgeAward> Items, string? NextCursor);

/// <summary>The position after an award in the list order (<c>awardedAt</c> in milliseconds, id).</summary>
public sealed record BadgeAwardCursor(long AwardedAtMs, string Id)
{
    public static BadgeAwardCursor After(BadgeAward award)
    {
        ArgumentNullException.ThrowIfNull(award);
        return new(award.AwardedAt.ToUnixTimeMilliseconds(), award.Id);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { AwardedAtMs, Id }));

    public static bool TryDecode(string? value, out BadgeAwardCursor cursor) => CursorCodec.TryDecode(value, out cursor, (at, id) => new BadgeAwardCursor(at, id));
}

internal static class CursorCodec
{
    public static bool TryDecode<T>(string? value, out T cursor, Func<long, string, T> create)
    {
        cursor = default!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 ||
                !root[0].TryGetInt64(out var at) || root[1].ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var id = root[1].GetString()!;
            if (!BadgeIds.IsValid(id))
            {
                return false;
            }

            cursor = create(at, id);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>The id format of the API: 24 hexadecimal characters.</summary>
public static class BadgeIds
{
    public static bool IsValid(string? id) =>
        id is { Length: 24 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}
