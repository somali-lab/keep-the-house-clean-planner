namespace Huishoudplanner.Host.Telemetry;

/// <summary>
/// What the pipeline decided from the standard OTEL_* variables. Registered as a singleton so tests and
/// diagnostics can see which exporters exist without reflecting into the SDK. Holds no header values.
/// </summary>
public sealed record TelemetryOptions(
    string ServiceName,
    string ServiceVersion,
    bool TracesExporterEnabled,
    bool MetricsExporterEnabled,
    bool LogsExporterEnabled)
{
    /// <summary>True when at least one signal is exported over OTLP.</summary>
    public bool AnyExporterEnabled => TracesExporterEnabled || MetricsExporterEnabled || LogsExporterEnabled;

    /// <summary>
    /// A signal is exported when its own endpoint (OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT) or the
    /// shared OTEL_EXPORTER_OTLP_ENDPOINT is set; an empty variable counts as unset.
    /// </summary>
    public static TelemetryOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var shared = IsSet(configuration, "OTEL_EXPORTER_OTLP_ENDPOINT");
        var name = configuration["OTEL_SERVICE_NAME"];
        return new TelemetryOptions(
            string.IsNullOrWhiteSpace(name) ? AppTelemetry.DefaultServiceName : name.Trim(),
            TelemetryVersion.Current,
            shared || IsSet(configuration, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"),
            shared || IsSet(configuration, "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"),
            shared || IsSet(configuration, "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"));
    }

    private static bool IsSet(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]);
}
