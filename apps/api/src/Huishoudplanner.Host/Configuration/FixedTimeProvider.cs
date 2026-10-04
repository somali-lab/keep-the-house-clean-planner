namespace Huishoudplanner.Host.Configuration;

/// <summary>
/// The clock of a host started with <c>APP_FAKE_NOW</c> (the test environment only, see <see cref="AppOptionsBinder"/>): it stands
/// still at that instant, which is what the end-to-end journeys run on. Time zones stay the system's own.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
