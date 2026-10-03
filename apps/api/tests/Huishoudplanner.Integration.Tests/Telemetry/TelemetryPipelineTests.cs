#pragma warning disable CA1848, CA1873, CA1727, CA2254 // Probe log calls: the template and its arguments are the point of these tests.
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Huishoudplanner.Host.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Huishoudplanner.Integration.Tests.Telemetry;

// Slice 0.5: the pipeline without a receiver. No Docker; the collector end-to-end proof is OtlpCollectorTests.
// Console output is process-wide, so these tests run one after the other.
[Collection("telemetry-console")]
public sealed class TelemetryPipelineTests
{
    private static IHost Build(
        IDictionary<string, string?> config,
        List<Activity>? spans = null,
        List<LogRecord>? logs = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddAppTelemetry(builder.Configuration);
        if (spans is not null)
        {
            builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans));
        }

        if (logs is not null)
        {
            builder.Services.ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs));
        }

        return builder.Build();
    }

    private static Dictionary<string, string?> Config(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => (string?)v.Value);

    [Fact]
    public void NoEndpoint_registersNoOtlpExporter()
    {
        using var host = Build(Config());
        host.Services.GetRequiredService<TracerProvider>();

        var options = host.Services.GetRequiredService<TelemetryOptions>();
        options.AnyExporterEnabled.Should().BeFalse();
        options.TracesExporterEnabled.Should().BeFalse();
        options.MetricsExporterEnabled.Should().BeFalse();
        options.LogsExporterEnabled.Should().BeFalse();
    }

    [Fact]
    public void EmptyEndpoint_countsAsUnset()
    {
        using var host = Build(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", " ")));

        host.Services.GetRequiredService<TelemetryOptions>().AnyExporterEnabled.Should().BeFalse();
    }

    [Fact]
    public void Endpoint_registersTheExporterWithProtocolAndHeadersFromTheEnvironment()
    {
        using var host = Build(Config(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector.example:4318"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=ApiKey placeholder,x-extra=1")));
        host.Services.GetRequiredService<TracerProvider>();

        var options = host.Services.GetRequiredService<TelemetryOptions>();
        options.TracesExporterEnabled.Should().BeTrue();
        options.MetricsExporterEnabled.Should().BeTrue();
        options.LogsExporterEnabled.Should().BeTrue();

        var exporter = host.Services.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get(Options.DefaultName);
        exporter.Protocol.Should().Be(OtlpExportProtocol.HttpProtobuf);
        exporter.Endpoint.ToString().Should().StartWith("http://collector.example:4318");
        exporter.Headers.Should().Contain("Authorization=ApiKey placeholder");
    }

    [Fact]
    public void Endpoint_selectsGrpcWhenAsked()
    {
        using var host = Build(Config(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector.example:4317"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")));
        host.Services.GetRequiredService<TracerProvider>();

        var exporter = host.Services.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get(Options.DefaultName);
        exporter.Protocol.Should().Be(OtlpExportProtocol.Grpc);
    }

    [Fact]
    public void SignalSpecificEndpoint_enablesOnlyThatSignal()
    {
        using var host = Build(Config(("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://collector.example:4318/v1/traces")));

        var options = host.Services.GetRequiredService<TelemetryOptions>();
        options.TracesExporterEnabled.Should().BeTrue();
        options.MetricsExporterEnabled.Should().BeFalse();
        options.LogsExporterEnabled.Should().BeFalse();
    }

    [Fact]
    public void Processor_removesAuthorizationAndApiKeyHeadersFromSpans()
    {
        var spans = new List<Activity>();
        using var host = Build(Config(), spans);
        var tracer = host.Services.GetRequiredService<TracerProvider>();

        using (var activity = AppTelemetry.Source.StartActivity("redaction-probe"))
        {
            activity.Should().NotBeNull();
            activity!.SetTag("http.request.header.authorization", "ApiKey secret-value");
            activity.SetTag("http.request.header.x_api_key", "secret-value");
            activity.SetTag("http.request.header.x-api-key", "secret-value");
            activity.SetTag("http.request.header.accept", "application/json");
        }

        tracer.ForceFlush();
        var exported = spans.Should().ContainSingle().Subject;
        exported.TagObjects.Select(t => t.Key).Should().BeEquivalentTo(["http.request.header.accept"]);
    }

    [Fact]
    public void Processor_removesAuthorizationAndApiKeyHeadersFromLogRecords()
    {
        var logs = new List<LogRecord>();
        using var host = Build(Config(), logs: logs);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("probe");

        logger.LogInformation("{authorization} {x-api-key} {kept}", "ApiKey secret-value", "secret-value", "visible");
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush();

        var record = logs.Should().ContainSingle().Subject;
        var keys = record.Attributes!.Select(a => a.Key).ToList();
        keys.Should().Contain("kept");
        keys.Should().NotContain("authorization").And.NotContain("x-api-key");
    }

    [Theory]
    [InlineData("authorization", true)]
    [InlineData("Authorization", true)]
    [InlineData("http.request.header.authorization", true)]
    [InlineData("http.request.header.x-api-key", true)]
    [InlineData("http.request.header.x_api_key", true)]
    [InlineData("http.response.header.x_api_key", true)]
    [InlineData("http.request.header.accept", false)]
    [InlineData("author", false)]
    public void IsSensitiveKey_matchesTheRedactionList(string key, bool expected) =>
        Redaction.IsSensitiveKey(key).Should().Be(expected);

    [Fact]
    public void Resource_carriesServiceNameAndTheApplicationVersion()
    {
        using var host = Build(Config(("OTEL_SERVICE_NAME", "kthc-test")));
        var attributes = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].Should().Be("kthc-test");
        attributes["service.version"].Should().Be(TelemetryVersion.Current);
        TelemetryVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    public void Resource_usesTheDefaultServiceNameWithoutTheVariable()
    {
        using var host = Build(Config());
        var attributes = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].Should().Be(AppTelemetry.DefaultServiceName);
    }

    [Fact]
    public async Task EveryJsonLogLineCarriesTraceAndSpanIds()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            string traceId, spanId;
            using (var host = Build(Config()))
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
                host.Services.GetRequiredService<TracerProvider>();
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("probe");
                using var activity = AppTelemetry.Source.StartActivity("json-probe");
                activity.Should().NotBeNull();
                traceId = activity!.TraceId.ToString();
                spanId = activity.SpanId.ToString();
                logger.LogInformation("hello from inside a span");
                await host.StopAsync(TestContext.Current.CancellationToken);
            }

            var line = captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(l => l.Contains("hello from inside a span", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(line);
            var scopes = json.RootElement.GetProperty("Scopes").EnumerateArray()
                .SelectMany(s => s.EnumerateObject()).ToDictionary(p => p.Name, p => p.Value.GetString());
            scopes["TraceId"].Should().Be(traceId);
            scopes["SpanId"].Should().Be(spanId);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void Version_matchesTheRepositoryVersionFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "version.txt")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        var fromFile = File.ReadAllText(Path.Combine(dir!.FullName, "version.txt")).Trim();
        TelemetryVersion.Current.Should().Be(Regex.Match(fromFile, @"^\d+\.\d+\.\d+").Value);
    }
}
