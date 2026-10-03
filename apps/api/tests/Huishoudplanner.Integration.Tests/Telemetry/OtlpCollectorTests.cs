#pragma warning disable CA1848, CA1873 // Probe log calls: the message is the point of this test.
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Huishoudplanner.Host.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Huishoudplanner.Integration.Tests.Telemetry;

/// <summary>
/// One OpenTelemetry Collector (contrib image) with the <c>debug</c> exporter at detailed verbosity, shared by the
/// tests of this class. OTLP gRPC listens on 4317 and OTLP HTTP on 4318, exactly like an EDOT Collector would.
/// </summary>
public sealed class CollectorFixture : IAsyncLifetime
{
    private const string Config = """
        receivers:
          otlp:
            protocols:
              grpc:
                endpoint: 0.0.0.0:4317
              http:
                endpoint: 0.0.0.0:4318
        exporters:
          debug:
            verbosity: detailed
        service:
          pipelines:
            traces:
              receivers: [otlp]
              exporters: [debug]
            metrics:
              receivers: [otlp]
              exporters: [debug]
            logs:
              receivers: [otlp]
              exporters: [debug]
        """;

    private readonly IContainer container = new ContainerBuilder("otel/opentelemetry-collector-contrib:0.161.0")
        .WithPortBinding(4317, true)
        .WithPortBinding(4318, true)
        .WithResourceMapping(Encoding.UTF8.GetBytes(Config), "/etc/otelcol-contrib/config.yaml")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Everything is ready"))
        .Build();

    public string GrpcEndpoint => $"http://{container.Hostname}:{container.GetMappedPublicPort(4317)}";

    public string HttpEndpoint => $"http://{container.Hostname}:{container.GetMappedPublicPort(4318)}";

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    /// <summary>Everything the collector has printed so far (stdout and stderr).</summary>
    public async Task<string> LogsAsync()
    {
        var (stdout, stderr) = await container.GetLogsAsync();
        return stdout + stderr;
    }
}

// Slice 0.5 end-to-end proof: the pipeline of AddAppTelemetry delivers a span, a metric and a log to a real OTLP
// receiver over both protocols. Needs Docker and pulls otel/opentelemetry-collector-contrib.
[Collection("telemetry-console")]
public sealed class OtlpCollectorTests : IClassFixture<CollectorFixture>
{
    private readonly CollectorFixture collector;

    public OtlpCollectorTests(CollectorFixture collector) => this.collector = collector;

    [Theory]
    [InlineData("grpc")]
    [InlineData("http/protobuf")]
    public async Task SpanMetricAndLog_reachTheCollector(string protocol)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var spanName = $"kthc-probe-span-{id}";
        var metricName = $"huishoudplanner.probe_{id}";
        var logBody = $"kthc probe log {id}";
        var endpoint = protocol == "grpc" ? collector.GrpcEndpoint : collector.HttpEndpoint;

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol,
            ["OTEL_SERVICE_NAME"] = $"kthc-e2e-{id}",
        });
        builder.Services.AddAppTelemetry(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        using (var activity = AppTelemetry.Source.StartActivity(spanName))
        {
            activity.Should().NotBeNull();
        }

        AppTelemetry.Meter.CreateCounter<long>(metricName).Add(3);
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("probe").LogInformation("{Message}", logBody);

        host.Services.GetRequiredService<TracerProvider>().ForceFlush().Should().BeTrue();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush().Should().BeTrue();
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush().Should().BeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);

        var logs = await WaitForAsync(collector, spanName, metricName, logBody);
        logs.Should().Contain(spanName, "the span reaches the collector");
        logs.Should().Contain(metricName, "the metric reaches the collector");
        logs.Should().Contain(logBody, "the log record reaches the collector");
        logs.Should().Contain($"kthc-e2e-{id}", "service.name comes from OTEL_SERVICE_NAME");
        logs.Should().Contain("service.version", "the resource carries the application version");
    }

    private static async Task<string> WaitForAsync(CollectorFixture collector, params string[] needles)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var logs = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            logs = await collector.LogsAsync();
            if (needles.All(n => logs.Contains(n, StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        return logs;
    }
}
