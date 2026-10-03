namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>A clock that stands still until a test moves it (host tests that depend on today).</summary>
public sealed class FixedTimeProvider(string now) : TimeProvider
{
    private DateTimeOffset current = DateTimeOffset.Parse(now, global::System.Globalization.CultureInfo.InvariantCulture);

    public void Set(string instant) => current = DateTimeOffset.Parse(instant, global::System.Globalization.CultureInfo.InvariantCulture);

    public override DateTimeOffset GetUtcNow() => current;
}
