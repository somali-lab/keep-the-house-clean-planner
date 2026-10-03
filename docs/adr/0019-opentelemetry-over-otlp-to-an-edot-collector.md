# ADR-0019 — OpenTelemetry over OTLP to an EDOT Collector

Status: Accepted

## Context

The Node server logs structured JSON to stdout and nothing else. The .NET application should be observable in the maintainer's Elastic stack without binding the code to Elastic.

## Decision

Traces, metrics and logs use the OpenTelemetry .NET SDK and are exported over OTLP. Configuration is only the standard `OTEL_*` variables; without an endpoint no exporter is registered and the application logs JSON to stdout only. The vanilla SDK is used, not the Elastic distribution, so any OTLP backend works.

The maintainer's intake is an Elastic Agent running the EDOT Collector in front of Elasticsearch, because Elastic no longer recommends the APM Server OTLP intake for new setups. The APM Server intake stays a documented fallback. Each use case is one span, each adapter call one child span, secrets are removed by a processor, and no observability stack is added to the compose file.

## Alternatives considered

- The Elastic distribution of the .NET SDK: less configuration, but it is only supported against Elastic's own intakes.
- The APM Server OTLP intake: works today, but is no longer the recommended path.
- Stdout logs only: keeps the image simple, but gives no traces or metrics across the Mongo and AI calls.
- A bundled observability stack in compose: self-contained, but the maintainer already runs Elastic.

## Consequences

- Moving to another backend is a change of variables, not of code.
- The step that cannot be proven without the real stack, delivery to the Elastic Agent, is verified by the maintainer.
- Redaction has to be maintained on the pipeline; a new sensitive header needs an entry.
