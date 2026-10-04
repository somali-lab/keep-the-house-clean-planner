using System.Text;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>Small files with the signature of a real PNG, JPEG and WebP (and an SVG, which is never accepted), as <c>helpers/badgeImages.ts</c> of the Node tests.</summary>
public static class SampleImages
{
    public static byte[] Png { get; } = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0x0d, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0];

    public static byte[] Jpeg { get; } = [0xff, 0xd8, 0xff, 0xe0, 0, 0x10, 0x4a, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xff, 0xd9];

    public static byte[] Webp { get; } = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8, 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Svg { get; } = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\"><script>alert(1)</script></svg>");

    /// <summary>The request member of an image: the declared type and the bytes as base64.</summary>
    public static object Input(byte[] bytes, string contentType) => new { contentType, data = Convert.ToBase64String(bytes) };

    /// <summary>The head padded with zeros to exactly <paramref name="size"/> bytes.</summary>
    public static byte[] Padded(byte[] head, int size)
    {
        var bytes = new byte[size];
        head.CopyTo(bytes, 0);
        return bytes;
    }
}
