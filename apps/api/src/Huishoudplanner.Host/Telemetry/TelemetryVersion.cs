using System.Reflection;

namespace Huishoudplanner.Host.Telemetry;

/// <summary>The application version as telemetry reports it: the version.txt value stamped into the assembly.</summary>
public static class TelemetryVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AssemblyMarker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AssemblyMarker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // "1.2.3+commitsha" -> "1.2.3": the build metadata is not part of the release version.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
