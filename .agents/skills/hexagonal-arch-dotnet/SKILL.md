---
name: hexagonal-arch-dotnet
description: Hexagonal architecture (ports and adapters) for the .NET API in apps/api. Use when adding a project, port, use case or adapter, judging whether a dependency may cross a boundary, wiring dependency injection, or reviewing layering in apps/api.
---

# Hexagonal architecture for the .NET API

Adapted from the `hexagonal-arch-dotnet` skill of the dark-factory repository (plan decision D13). The rules below are this repository's; the worked examples use the household domain and need not compile.

## Authoritative sources

- [AGENTS.md](../../../AGENTS.md): the non-negotiables for `apps/api`.
- [docs/plans/dotnet-rewrite.md](../../../docs/plans/dotnet-rewrite.md): §3.1 solution layout, §3.2 ports, §5 cross-cutting rules, D8 to D14.
- [.github/instructions/api.instructions.md](../../../.github/instructions/api.instructions.md): the short rule list for `apps/api/**/*.cs`.

## When to use this skill

- Creating a project, port, use case or adapter.
- Wiring dependency injection.
- Deciding where a piece of code belongs.
- Reviewing layer boundaries.

## Layers (one project per ring)

```
apps/api/src/
  Huishoudplanner.Domain/            entities, value objects, calendar, rules; Ports/Driving, Ports/Driven; only OneOf as package
  Huishoudplanner.Application/       use cases implementing the driving ports; references Domain only
  Huishoudplanner.Adapters.Mongo/    ForStoringXxx ports, class maps, indexes, migrations, transactions, audit writer
  Huishoudplanner.Adapters.Http/     Minimal API endpoint groups, contracts, Problem Details mapping, identity adapter
  Huishoudplanner.Adapters.Ai/       ForChattingWithAModel over Microsoft.Extensions.AI
  Huishoudplanner.Adapters.Notify/   ForSendingNotifications (ntfy, Home Assistant, none)
  Huishoudplanner.Adapters.Pdf/      ForRenderingSheets over QuestPDF
  Huishoudplanner.Adapters.Jobs/     cron-driven hosted services
  Huishoudplanner.Host/              Program.cs, composition root, OpenTelemetry, static files
```

Project references make a wrong dependency a compile error: Domain references nothing of ours, Application references Domain, every adapter references Domain (and Application only where it needs a driving port), Host references everything and is the only composition root. The React app in `apps/web` talks to `Adapters.Http` over `/api/v2` and never to anything below it.

## Port naming

| Kind                                    | Name                                      | Location                                                      |
| --------------------------------------- | ----------------------------------------- | ------------------------------------------------------------- |
| Driving (HTTP adapter and jobs call in) | `IXxxService`, one per bounded capability | interface in `Domain/Ports/Driving/`, class in `Application/` |
| Driven (the domain calls out)           | `ForXxx`, verb-based, one adapter each    | `Domain/Ports/Driven/`                                        |

Driving examples: `IOccurrenceService`, `ICyclePlanService`, `IPointsService`. Driven examples: `ForStoringOccurrences`, `ForRecordingAudit`, `ForRunningTransactions`, `ForTellingTime`, `ForResolvingActors`, `ForChattingWithAModel`, `ForSendingNotifications`, `ForRenderingSheets`.

Worked example: [examples/port-template.cs](./examples/port-template.cs).

## Rules

1. **Errors are values.** Port methods return `OneOf<Success, NotFound, ConflictError, ValidationErrors, PortError>` or the subset that can occur. No exception crosses a port boundary; the HTTP adapter maps each variant to RFC 9457 Problem Details with the stable `urn:huishoudplanner:problem:<code>` type. Never an unfiltered `catch (Exception)`: catch with a filter that names what an infrastructure failure is and return `PortError`.
2. **Ids are `ObjectId`** in storage and 24-character hex strings in the API (D11). Never `Guid` or ULID. The conversion lives in one converter per adapter boundary; the domain carries its own id type, not `ObjectId`.
3. **Time is `TimeProvider`** (`ForTellingTime`). No `DateTime.Now` or `DateTime.UtcNow` outside the clock adapter. Calendar reasoning uses `DateOnly` day keys and `TimeZoneInfo` with IANA ids; DST behaviour is part of the contract.
4. **Packages stay behind their adapter.** `MongoDB.Driver` only in `Adapters.Mongo`, `QuestPDF` only in `Adapters.Pdf`, `Microsoft.Extensions.AI` only in `Adapters.Ai`, `Microsoft.AspNetCore.*` only in `Adapters.Http` and `Host`. ArchUnitNET in `Architecture.Tests` fails the build otherwise.
5. **No MediatR.** Endpoints call a driving port directly; the use case is one method on an `IXxxService` implementation and is one telemetry span.
6. **Every state change writes its audit entry in the same transaction** (D12). The use case runs the write and the audit input through `ForRunningTransactions`; only `Adapters.Mongo` performs writes. A no-op writes and audits nothing.
7. **A fake sits behind the same port as the real thing.** The mock AI provider, the `none` notifier and the fixed clock implement the same driven port, so replacing them is configuration, not surgery.
8. **Business rules live in the domain** (entities, value objects, domain services such as plan validation), not in an endpoint, a use case glue method or an adapter. The web app holds no domain rule (D4).
9. **Each project owns one `DependencyInjection.cs`** with a single extension method (`AddApplication`, `AddMongoAdapter`, ...). Host calls them and is the only place that knows every project. See [examples/di-registration-template.cs](./examples/di-registration-template.cs).

## Boundary violations to avoid

| Violation                                                  | Correct approach                                                                       |
| ---------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Domain references a driver, framework or adapter project   | Domain defines a `ForXxx` port; the adapter references the package                     |
| Endpoint reaches into storage                              | Endpoint calls a driving port, which calls a driven port, which the adapter implements |
| Adapter throws a business exception                        | Adapter returns a `OneOf` error variant                                                |
| Unfiltered `catch (Exception)`                             | Catch with a filter that states what an infrastructure failure is                      |
| Business logic in an endpoint or an adapter                | Logic lives in a domain entity or domain service                                       |
| A write outside `Adapters.Mongo`, or a write without audit | Write and audit entry in one transaction through the Mongo adapter                     |
| `DateTime.UtcNow` in domain or application code            | Inject `TimeProvider` (`ForTellingTime`)                                               |
| A fake that bypasses the port it replaces                  | The fake implements the port                                                           |

## Checklist for a new capability

1. Driving port `IXxxService` in `Domain/Ports/Driving/`.
2. Driven ports `ForXxx` in `Domain/Ports/Driven/`.
3. Domain entity or service with the business rules.
4. Use case in `Application/` implementing the driving port, depending only on ports.
5. Adapter per driven port, in its own project.
6. DI registration in the owning project; Host composes.
7. Minimal API endpoint group in `Adapters.Http/` calling the driving port, with the `OneOf` to Problem Details mapping.
8. Tests per layer, test first (see `xunit-tdd-workflow`); persistence per `mongodb-persistence`.
9. `Architecture.Tests` still green; requirements, README and ADR index updated when behaviour or an architectural choice changed.

## Scripts

| Script                                                             | Purpose                                                                                                                                     |
| ------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------- |
| [check-layer-boundaries.ps1](./scripts/check-layer-boundaries.ps1) | Advisory grep-based check that the inner rings stay free of infrastructure; the authoritative check is `Huishoudplanner.Architecture.Tests` |
