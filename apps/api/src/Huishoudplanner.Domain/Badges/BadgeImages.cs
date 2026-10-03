namespace Huishoudplanner.Domain.Badges;

/// <summary>The image types a badge may carry. No SVG: an image that can carry script would run in the page that shows it (ADR-0014).</summary>
public enum BadgeImageType
{
    Png,
    Jpeg,
    Webp,
}

public static class BadgeImages
{
    public static string ContentType(this BadgeImageType type) => type switch
    {
        BadgeImageType.Png => "image/png",
        BadgeImageType.Jpeg => "image/jpeg",
        BadgeImageType.Webp => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown badge image type."),
    };

    /// <summary>The image type a file really is, from its first bytes; <see langword="null"/> for anything else (an SVG included). TS <c>sniffBadgeImageType</c>.</summary>
    public static BadgeImageType? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual<byte>([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
        {
            return BadgeImageType.Png;
        }

        if (bytes.Length >= 3 && bytes[..3].SequenceEqual<byte>([0xff, 0xd8, 0xff]))
        {
            return BadgeImageType.Jpeg;
        }

        // RIFF <size> WEBP
        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual<byte>([0x52, 0x49, 0x46, 0x46])
            && bytes[8..12].SequenceEqual<byte>([0x57, 0x45, 0x42, 0x50]))
        {
            return BadgeImageType.Webp;
        }

        return null;
    }
}
