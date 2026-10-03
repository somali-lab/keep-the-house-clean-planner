using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Huishoudplanner.Host.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>
    /// Builds the OpenTelemetry pipeline (plan section 3.6, ADR-0019): traces, metrics and logs, an OTLP exporter
    /// per signal only when an endpoint is configured through the standard OTEL_* variables (endpoint, headers,
    /// protocol and timeouts are read by the exporter from <paramref name="configuration"/>, nothing is invented
    /// here), and logs as JSON on stdout with trace and span ids on every line.
    /// </summary>
    public static IServiceCollection AddAppTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = TelemetryOptions.From(configuration);
        services.AddSingleton(options);

        services.AddLogging(logging => logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true; // carries TraceId, SpanId and ParentId (ActivityTrackingOptions below)
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        }));
        services.Configure<LoggerFactoryOptions>(factory =>
            factory.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(options.ServiceName, serviceVersion: options.ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AppTelemetry.Name, AppTelemetry.NamePattern, AppTelemetry.MongoDriverSourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddProcessor(new RedactingActivityProcessor()); // before the exporter, so it never sees a secret
                if (options.TracesExporterEnabled)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AppTelemetry.Name, AppTelemetry.NamePattern)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (options.MetricsExporterEnabled)
                {
                    metrics.AddOtlpExporter();
                }
            })
            .WithLogging(
                logging =>
                {
                    logging.AddProcessor(new RedactingLogRecordProcessor());
                    if (options.LogsExporterEnabled)
                    {
                        logging.AddOtlpExporter();
                    }
                },
                logOptions =>
                {
                    logOptions.IncludeFormattedMessage = true;
                    logOptions.IncludeScopes = true;
                    logOptions.ParseStateValues = true;
                });

        return services;
    }
}
