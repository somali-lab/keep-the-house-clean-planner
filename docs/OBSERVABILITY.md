# Observability

The .NET application (`apps/api`) emits traces, metrics and logs with the OpenTelemetry .NET SDK and exports them over OTLP ([ADR-0019](adr/0019-opentelemetry-over-otlp-to-an-edot-collector.md), [plan section 3.6](plans/dotnet-rewrite.md)). It uses the vanilla SDK, not the Elastic distribution, so any OTLP receiver works with the same variables.

## Variables

Only the standard OpenTelemetry variables configure the exporter. Nothing else in the application knows about the backend.

| Variable | Meaning |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Base URL of the OTLP receiver, `http://host:4317` for gRPC or `http://host:4318` for HTTP. **Unset or empty means no exporter at all.** |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` or `http/protobuf`. Use the port that matches. |
| `OTEL_EXPORTER_OTLP_HEADERS` | Headers sent with every export, `key=value,key=value`. Carries the credential, for example `Authorization=ApiKey <key>`. |
| `OTEL_SERVICE_NAME` | The `service.name` resource attribute. Default `huishoudplanner-api`. |
| `OTEL_RESOURCE_ATTRIBUTES` | Extra resource attributes, `key=value,key=value`, for example `deployment.environment.name=production`. |

The per-signal variants (`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, `..._METRICS_...`, `..._LOGS_...`) are honoured too and enable only their own signal. The other standard exporter variables (timeouts, per-signal protocol and headers, compression) are read by the SDK as well. The application sets `service.version` itself to the application version (the value of `version.txt`, so it matches the About page).

## Behaviour with and without an endpoint

- **Without an endpoint** no OTLP exporter is registered and nothing leaves the process. Logs still go to stdout as JSON.
- **With an endpoint** a span, metric and log exporter is registered per signal, with the protocol and headers from the variables. Only the endpoint gates the exporters: `OTEL_SDK_DISABLED` and `OTEL_TRACES_EXPORTER=none` (and the metrics and logs equivalents) are **not** honoured; remove the endpoint to turn export off.
- **Logs** always go to stdout as one JSON object per line (the .NET JSON console formatter). With a span active the line carries `TraceId`, `SpanId` and `ParentId` in its `Scopes`. With an endpoint the same records are also exported over OTLP on the same pipeline.
- **Sources listened to:** ASP.NET Core and HttpClient instrumentation, runtime metrics, the MongoDB driver (`MongoDB.Driver`), and every `ActivitySource` or `Meter` named `Huishoudplanner` or `Huishoudplanner.*`. Adapters create such sources from `System.Diagnostics`; the OpenTelemetry packages are referenced by the Host project only.
- **Secrets:** a processor on the pipeline removes the headers `authorization` and `x-api-key` from span tags and from log record attributes before the OTLP exporter sees them (`Telemetry/RedactionProcessors.cs`). When a log record had such an attribute, its formatted message is replaced by a fixed notice because it was rendered from the secret. Log scopes are not exported over OTLP (the console JSON keeps them for the trace ids). Limits: tags on span **events** and **links** are not filtered (the .NET types are immutable; none of our code adds header tags there), secrets written into free text or other attribute names are not detected, and the stdout JSON log is not run through the processor, so never pass a secret to `ILogger`. A new sensitive header needs an entry in `Redaction.Headers`. The exporter headers themselves are never logged or exported.

## Example: Elastic Agent running the EDOT Collector

The maintainer's intake is an Elastic Agent that runs the EDOT Collector in front of Elasticsearch. Its OTLP receiver listens on 4317 (gRPC) and 4318 (HTTP). Point the application at it:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://elastic-agent.example:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_SERVICE_NAME=huishoudplanner-api
OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=production
# Only when the receiver requires an API key (placeholder, use your own and keep it out of the repository):
OTEL_EXPORTER_OTLP_HEADERS=Authorization=ApiKey <your-elastic-api-key>
```

For gRPC use `http://elastic-agent.example:4317` and `OTEL_EXPORTER_OTLP_PROTOCOL=grpc`. In Docker Compose these are ordinary `environment:` entries of the application service; keep the key in an untracked `.env`.

### Fallback: the APM Server intake

Elastic no longer recommends the APM Server OTLP intake for new setups, but it still accepts OTLP. Use the APM Server URL as the endpoint (`http://apm-server.example:8200` with `http/protobuf`) and a secret token or API key as `Authorization=Bearer <secret-token>` or `Authorization=ApiKey <key>`.

### Any other OTLP collector

Grafana Alloy, a plain OpenTelemetry Collector, Jaeger or any hosted OTLP endpoint takes the same variables; only the endpoint and headers change.

## Verification status

- **Verified:** the pipeline against an OpenTelemetry Collector (`otel/opentelemetry-collector-contrib`) with the `debug` exporter at detailed verbosity, over both gRPC (4317) and HTTP/protobuf (4318): a span, a metric and a log record emitted by the application pipeline appear in the collector output together with `service.name` and `service.version`. This is the test class `OtlpCollectorTests` in `apps/api/tests/Huishoudplanner.Integration.Tests/Telemetry` (needs Docker). Unit tests cover the no-endpoint case, protocol and headers from the variables, redaction, the resource attributes and the trace id in the JSON log line.
- **NOT yet verified:** delivery to the maintainer's Elastic Agent running the EDOT Collector, including the API key header and how the data shows up in Kibana. The Elastic example above follows the documented variables but has not been run against that stack. This is the one step left to the maintainer; plan slice 0.5 stays open until then.
- **NOT verified:** behaviour with a receiver that is down or slow (the SDK is expected to drop data without blocking requests, and the shutdown flush is expected to be bounded by the exporter timeout, but no test proves it).
