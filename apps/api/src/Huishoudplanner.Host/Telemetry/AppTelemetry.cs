using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Huishoudplanner.Host.Telemetry;

/// <summary>
/// The telemetry names of the application (plan section 3.6). Adapters never reference the Host, so each
/// adapter or use case creates its own <see cref="ActivitySource"/> or <see cref="Meter"/> from
/// <c>System.Diagnostics</c> with a name under <see cref="Name"/> (for example <c>Huishoudplanner.Mongo</c>);
/// the pipeline listens to <see cref="Name"/> and <see cref="NamePattern"/>. Per-use-case spans and counters
/// arrive with their use-case slices.
/// </summary>
public static class AppTelemetry
{
    /// <summary>Root name of every source and meter of the application.</summary>
    public const string Name = "Huishoudplanner";

    /// <summary>Matches every source and meter named below the root, such as <c>Huishoudplanner.Mongo</c>.</summary>
    public const string NamePattern = Name + ".*";

    /// <summary>The ActivitySource of the MongoDB driver (built in since driver 3.7).</summary>
    public const string MongoDriverSourceName = "MongoDB.Driver";

    /// <summary>Default <c>service.name</c> when OTEL_SERVICE_NAME is not set.</summary>
    public const string DefaultServiceName = "huishoudplanner-api";

    /// <summary>Spans created by the host itself.</summary>
    public static readonly ActivitySource Source = new(Name, TelemetryVersion.Current);

    /// <summary>Metrics created by the host itself.</summary>
    public static readonly Meter Meter = new(Name, TelemetryVersion.Current);
}
