namespace Huishoudplanner.Adapters.Pdf.Tests;

/// <summary>A clock that stands still, so the PDF metadata dates (and therefore the bytes) are the same on every render.</summary>
internal sealed class FixedTimeProvider(string now) : TimeProvider
{
    private readonly DateTimeOffset _now = DateTimeOffset.Parse(now, global::System.Globalization.CultureInfo.InvariantCulture);

    public override DateTimeOffset GetUtcNow() => _now;
}
