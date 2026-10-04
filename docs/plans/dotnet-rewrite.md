# Implementation plan — backend rewrite to .NET 10

Status: **planned, not started**. This document is the roadmap for the whole rewrite. `docs/BUILD.md` holds only the slice that is running right now and points back here.

## 1. Goal and outcome

Replace the Node.js/Fastify server in `apps/server` with an ASP.NET Core application on .NET 10, built as a hexagonal (ports and adapters) architecture, observable through OpenTelemetry, with an identity port that a later OAuth2/OIDC adapter can fill, and a redesigned `/api/v2` contract. The React web application stays, is moved to the v2 contract feature by feature, and loses all domain logic in the process: every rule the household depends on (calendar arithmetic, cycle and week indexes, plan validation, points, bonuses, badges) lives in exactly one place, the .NET domain.

The rewrite is built in parallel to the running Node server and switches over in one release once the two applications have run side by side against a copy of the production data and the verification checklist in §10 passes.

What stays the same for the household:

- Every behaviour in [huishoudplanner-requirements.md](../huishoudplanner-requirements.md) §1–§7, except where this plan names a change.
- MongoDB with the existing collections, documents and 24-character hex identifiers. No data migration of identifiers; startup migrations stay idempotent and small.
- One application container that serves API and web app, one database, one backup container (ADR-0005).
- Profile selection as attribution, roles enforced on the server (ADR-0003).

## 2. Decisions taken for this plan

These decisions were taken with the maintainer before the plan was written. Each one that is an architectural choice between real alternatives gets an ADR in slice 0.1; the table says which. Decisions that only restate a requirement go to the requirements.

| #   | Decision                                                                                                                                                                                                                                                                                                 | Permanent home                          |
| --- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------- |
| D1  | Build the .NET application in parallel and switch in one release; no route-by-route strangler, no big bang.                                                                                                                                                                                              | This plan (§9, §10)                     |
| D2  | MongoDB stays, with the existing collections and documents.                                                                                                                                                                                                                                              | ADR-0016 (supersedes ADR-0001 in part)  |
| D3  | The API is redesigned as `/api/v2` with RFC 9457 Problem Details; the v1 contract is not reimplemented. The web app is adapted to v2.                                                                                                                                                                    | ADR-0017                                |
| D4  | The web app becomes thin: all domain rules move to the server; `packages/shared` is deleted at the end. The web keeps only pure day-key string helpers and presentation.                                                                                                                                 | ADR-0017, requirements §7               |
| D5  | Identity is a port (actor + role from a request). The first adapter reads the profile header as today; an OIDC bearer-token adapter is the designed next step but not built now. ADR-0003 stays in force.                                                                                                | ADR-0018                                |
| D6  | OpenTelemetry traces, metrics and logs are exported over OTLP to the maintainer's Elastic stack (APM Server or EDOT Collector). The exporter is configured through the standard `OTEL_*` variables so any OTLP backend works; without an endpoint the app logs structured JSON to stdout only.           | ADR-0019                                |
| D7  | PDF sheets are rendered with QuestPDF; no browser in the image.                                                                                                                                                                                                                                          | ADR-0020 (supersedes ADR-0006)          |
| D8  | Application layer in the style of the `hexagonal-arch-dotnet` skill: driving ports `IXxxService`, driven ports `ForXxx`, errors as `OneOf` values, Minimal API endpoint groups, no MediatR.                                                                                                              | ADR-0016, skill                         |
| D9  | One project per hexagon ring plus ArchUnitNET tests.                                                                                                                                                                                                                                                     | ADR-0016                                |
| D10 | Integration tests run against a real `mongo:8` through Testcontainers; one shared container per test assembly, a uniquely named database per test class.                                                                                                                                                 | Skill `xunit-tdd-workflow`              |
| D11 | Identifiers stay `ObjectId` (24 hex in the API). This overrides the skill rule "ids are ULID strings" for this repository.                                                                                                                                                                               | ADR-0016                                |
| D12 | MongoDB runs as a single-node replica set so that an entity write and its audit entry are one transaction and plan activation is serialisable.                                                                                                                                                           | ADR-0021 (amends ADR-0004 and ADR-0008) |
| D13 | The three dark-factory skills (`hexagonal-arch-dotnet`, `mongodb-persistence`, `xunit-tdd-workflow`) are copied into `.agents/skills/`, adapted to this repository, with the deviations D11 and the replica set written in.                                                                              | `.agents/skills`, AGENTS.md             |
| D14 | Test stack: xunit.v3, AwesomeAssertions, Moq, Testcontainers, `WebApplicationFactory`, ArchUnitNET.                                                                                                                                                                                                      | Skill `xunit-tdd-workflow`              |
| D15 | The old and the new application run side by side for a period, the new one against a copy of the database, before the switch.                                                                                                                                                                            | This plan (§10)                         |
| D16 | The plan lives here, in English; `docs/BUILD.md` carries only the running slice.                                                                                                                                                                                                                         | AGENTS.md                               |
| D17 | The parallel run is short, a few days: a smoke run on live-like data. Parity across week and cycle boundaries is proven by the parity script with a frozen clock (§7.3), not by waiting. The maintainer walks the checklist and gives the go for the switch by hand; no agent switches the installation. | This plan (§7.3, §10), AGENTS.md rule 9 |
| D18 | Telemetry goes to an Elastic Agent running the EDOT Collector in front of the maintainer's Elasticsearch, over OTLP.                                                                                                                                                                                     | ADR-0019, `docs/OBSERVABILITY.md`       |
| D19 | The OIDC design note in ADR-0018 is provider-neutral: Authorization Code with PKCE, JWT validation through the discovery document, `sub` mapped to a user through `externalId`.                                                                                                                          | ADR-0018                                |
| D20 | Slices land on the integration branch `next`; `main` keeps releasing the Node application until the switch.                                                                                                                                                                                              | This plan (§9), AGENTS.md               |

ADR numbers: 0015 is retired and numbers are never reused (see the ADR index), so the architectural decisions of this plan are recorded as ADR-0016 to ADR-0021. This plan uses those numbers throughout.

No open points remain from planning; the four raised on 2026-10-03 were settled the same day and are D17–D20.

## 3. Target architecture

### 3.1 Solution layout

The .NET solution lives in `apps/api/` next to `apps/server/` (which stays until the switch) and `apps/web/`.

```
apps/api/
  Huishoudplanner.slnx
  Directory.Build.props            # TargetFramework net10.0, nullable, warnings as errors, version from version.txt
  Directory.Packages.props         # central package management, one version per package
  src/
    Huishoudplanner.Domain/        # entities, value objects, calendar, rules; ports in Ports/Driving and Ports/Driven; no package references except OneOf
    Huishoudplanner.Application/   # use cases that implement the driving ports; depends on Domain only
    Huishoudplanner.Adapters.Mongo/      # ForXxx storage ports, class maps, indexes, startup migrations, transactions, audit writer
    Huishoudplanner.Adapters.Http/       # Minimal API endpoint groups, contracts (request/response records), Problem Details mapping, identity adapter, OpenAPI
    Huishoudplanner.Adapters.Ai/         # ForProposingPlans etc. over Microsoft.Extensions.AI: Anthropic, OpenAI-compatible, Ollama, mock, none
    Huishoudplanner.Adapters.Notify/     # ntfy, Home Assistant, none
    Huishoudplanner.Adapters.Pdf/        # QuestPDF sheets
    Huishoudplanner.Adapters.Jobs/       # cron-driven hosted services (generation, retention, morning message)
    Huishoudplanner.Host/                # Program.cs: configuration, DI composition, OpenTelemetry, health, static web files, SPA fallback
  tests/
    Huishoudplanner.Domain.Tests/        # pure unit tests, golden vectors
    Huishoudplanner.Application.Tests/   # use cases with fake driven ports
    Huishoudplanner.Architecture.Tests/  # ArchUnitNET: dependency direction, naming, "no driver outside its adapter"
    Huishoudplanner.Integration.Tests/   # Mongo adapters and the HTTP pipeline against Testcontainers
```

Project references make a wrong dependency a compile error: Domain references nothing of ours, Application references Domain, every adapter references Domain (and Application only where it needs a driving port), Host references everything and is the only composition root. ArchUnitNET guards what project references cannot: `MongoDB.Driver` types only inside `Adapters.Mongo`, `QuestPDF` only inside `Adapters.Pdf`, `Microsoft.AspNetCore.*` only inside `Adapters.Http` and `Host`, no `DateTime.Now`/`DateTime.UtcNow` outside the clock adapter, and every driven port named `For*`, every driving port `I*Service`.

### 3.2 Ports

| Kind                                            | Naming                                                                | Examples                                                                                                                                                                                                         |
| ----------------------------------------------- | --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Driving (the HTTP adapter and the jobs call in) | `IXxxService`, one per bounded capability, implemented in Application | `IOccurrenceService`, `ICyclePlanService`, `IPointsService`, `IBadgeService`, `IAiAssistService`, `IExportService`, `IJobService`                                                                                |
| Driven (the domain calls out)                   | `ForXxx`, verb-based, implemented by one adapter each                 | `ForStoringOccurrences`, `ForStoringCyclePlans`, `ForRecordingAudit`, `ForRunningTransactions`, `ForTellingTime`, `ForResolvingActors`, `ForChattingWithAModel`, `ForSendingNotifications`, `ForRenderingSheets` |

Errors are values: every port method returns `OneOf<Success, NotFound, ConflictError, ValidationErrors, PortError>` or the subset that can actually occur. No exception crosses a port boundary; the HTTP adapter maps each variant to a Problem Details response (§4.2). Infrastructure failures are caught with a filter that names what an infrastructure failure is and become `PortError`.

### 3.3 Identity port (D5)

`ForResolvingActors` turns the identity-relevant request values (an `ActorRequest` of header values, never an `HttpContext`, which the Domain may not reference) into `Actor? { ActorId, Role, Source }`. Adapter one reads `X-Profile-Id` and `X-Client` exactly as `apps/server/src/identity/index.ts` does today. The authorisation policies `RequireActor`, `RequirePlanner`, `RequireAdmin` are ASP.NET Core authorization policies evaluated against the resolved actor, so endpoint code never looks at a header. Adding OIDC later means a second adapter that validates a bearer token and maps `sub` to a user; the policies and endpoints do not change. The design note for that adapter is written in ADR-0018 now so the port is shaped for it; the code is not. The note is provider-neutral (D19): Authorization Code with PKCE in the web app, JWT validation against the issuer's discovery document, `sub` mapped to a user through a new `users.externalId`, and a statement of what happens to the profile header once a token is present.

### 3.4 Time and calendar

`ForTellingTime` is `TimeProvider`. The household timezone is a domain value; all calendar reasoning uses `DateOnly` day keys and `TimeZoneInfo` with IANA ids (ADR-0002 stays). The TypeScript helpers in `packages/shared/src/time.ts`, `cycle.ts`, `due.ts`, `points.ts`, `bonuses.ts`, `badges.ts`, `rewards.ts` and `validation/plan.ts` are ported one by one, each with the golden vectors of §7.2 before the C# exists.

### 3.5 Persistence (D2, D11, D12)

- Collections, field names and index definitions stay as in requirements §3; `apps/server/src/data/db.ts` is the reference list. The .NET adapter ensures the same indexes at startup and drops nothing the Node app relies on until the switch.
- `BsonClassMap` registration in one idempotent call; no Bson attributes on domain types; `SetIgnoreExtraElements(true)` everywhere.
- Ids are `ObjectId` in storage, 24-hex strings in the API; the mapping lives in one converter per adapter boundary.
- Every write that changes state runs inside a transaction with its audit entry (`ForRunningTransactions` wraps a `IClientSessionHandle`). The nightly reconciliation stays as the safety net for drift from before the switch.
- A write that changes nothing writes and audits nothing (ADR-0004 unchanged).
- Startup migrations are idempotent, named, and recorded in a `migrations` collection; the two existing ones (legacy slot index drop, occurrence room snapshot backfill) are the first entries.

### 3.6 Observability (D6)

- `OpenTelemetry` with the ASP.NET Core, HttpClient and runtime instrumentation, plus the MongoDB driver's built-in `ActivitySource` (`MongoDB.Driver`), exported by `OpenTelemetry.Exporter.OpenTelemetryProtocol`. Configuration through the standard variables only: `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` (the Elastic API key as `Authorization=ApiKey …`), `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`. No endpoint means no exporter. The vanilla SDK is used, not the Elastic distribution, so the same configuration works for any OTLP backend. The maintainer's intake is an Elastic Agent running the EDOT Collector (D18, §13); the app points `OTEL_EXPORTER_OTLP_ENDPOINT` at that collector.
- Logs go through `ILogger` → OpenTelemetry logs (same OTLP pipeline) and to stdout as JSON. Trace and span ids are in every log line.
- Domain-level signals: one `ActivitySource` per adapter, a `Meter` with the counters the household cares about (occurrences completed, generation runs, audit entries written, AI calls by provider and outcome, notification deliveries, PDF renders). Each use case is one span named after the use case.
- Secrets never appear in spans, logs or metrics. The redaction list of today's logger (`authorization`, `x-api-key`) becomes a processor on the OTel pipeline.
- A compose profile `observability` is **not** added: the maintainer already runs Elastic. `docs/OBSERVABILITY.md` documents the variables and a working example against an Elastic Agent with the EDOT Collector, and notes that any other OTLP collector takes the same variables.

### 3.7 Configuration

Environment variables as in requirements §9, bound through `IOptions<T>` with `ValidateDataAnnotations().ValidateOnStart()` so invalid configuration fails at startup without echoing values. Renames: `NODE_ENV` → `ASPNETCORE_ENVIRONMENT`, `LOG_LEVEL` → `Logging__LogLevel__Default`; both old names are read as aliases until the switch is documented, then dropped. `WEB_DIST_DIR` keeps its meaning. `OTEL_*` is new.

### 3.8 Background jobs

A single hosted service per job with Cronos expressions in the household timezone, `noOverlap` semantics through a `SemaphoreSlim`, a system actor, and one span per run. Same times as today (03:00 generation and reconciliation, 03:45 retention, 07:30 morning message). `DISABLE_SCHEDULER=true` registers none of them.

### 3.9 AI (requirements §5)

`ForChattingWithAModel` is an `IChatClient` from `Microsoft.Extensions.AI`. Providers: the official Anthropic .NET SDK (NuGet `Anthropic`, `AsIChatClient`), `Microsoft.Extensions.AI.OpenAI` for OpenAI-compatible endpoints, OllamaSharp for Ollama, a deterministic mock and `none`. Prompt building, JSON extraction, the one re-prompt and the validation against the plan rules are domain/application code with no provider type in sight; the adapter only sends and receives messages. Tests never call a provider (requirements §10).

### 3.10 PDF (D7)

`ForRenderingSheets` renders the four sheets (week range, day, due list, task list) from view models with QuestPDF. Layout rules of requirements §6 are unit-tested on the view model and on the rendered text (via a PDF text extractor), byte-level determinism is asserted by rendering twice. The Community licence is set explicitly in code with a comment that names the threshold.

### 3.11 Web app and static files

The Host serves `apps/web/dist` with the same SPA fallback rule as today (non-API `GET` without a file extension → `index.html`). The web app's PWA caching rule changes from `/api/` to `/api/v2/` with the same exclusions.

## 4. API v2 (D3)

### 4.1 Principles

- Base path `/api/v2`. Resources are plural nouns; actions that are not CRUD are sub-resources with `POST` (`/occurrences/{id}/complete`, `/cycle-plans/{id}/activation`), one endpoint per intent instead of today's `PATCH /occurrences/:id` with an action discriminator, so OpenAPI describes each body exactly.
- JSON in camelCase, `DateOnly` as `YYYY-MM-DD`, instants as ISO 8601 with offset, ids as 24-hex strings (ADR-0002 unchanged).
- Idempotency keys (`requestId`) stay for the three writes that have them (occurrence, one-off, redemption) with the same replay semantics; they are documented per endpoint.
- Every list is bounded: `limit`, `cursor`, `nextCursor`. No unbounded collection reads.
- The OpenAPI 3.1 document is generated at build time by the built-in ASP.NET Core OpenAPI support, checked into `apps/api/openapi/v2.json` and verified in CI to be unchanged (a drift test), so the generated TypeScript client in the web app is always built from a reviewed document.
- Non-blocking `warnings` stay on the write responses that have them.

### 4.2 Errors

RFC 9457 Problem Details with `application/problem+json`. The current `code` becomes `type` (a stable URN `urn:huishoudplanner:problem:<code>`), `message` becomes `detail`, `details` becomes an `errors` extension for validation (`HttpValidationProblemDetails`) or a named extension otherwise (`weeks`, `count`, `balance`, `requested`, `status`, `action`). Every code in requirements §8 keeps its name and status. `traceId` is always present.

### 4.3 What the thin web app needs (D4)

The server adds the values the web app computes today, so the client-side logic can be deleted:

| Today computed in the web app                                 | v2 delivers                                                                                                                                                                                              |
| ------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `weekIndexFor`, `cycleIndexFor`, `cycleStart`, `cycleEnd`     | `GET /calendar?from&to`: for a day range the cycle index, week index, ISO week and week start per day; every occurrence view also carries `cycleIndex` and `weekIndex`.                                  |
| `validatePlan` in the plan editor                             | `POST /cycle-plans/{id}/validation` (and `POST /cycle-plans/validation` for an unsaved draft) returns the same `ValidationErrors` and `PlanSummary` the save would; the editor debounces and shows them. |
| `bonusAmountsOn`, schedule display                            | `GET /settings` carries `bonusesInForce` and the schedule rows with a `startsInFuture` flag.                                                                                                             |
| `defaultPointsForDuration`, `MIN/MAX_*` limits                | `GET /meta/limits` once per session: all limits and defaults; `POST /tasks` computes default points when `points` is omitted.                                                                            |
| `eggsForPercent`, reward meter                                | `GET /points/progress` carries `eggs` and `eggCount`.                                                                                                                                                    |
| `pointsToCents`, `formatCents`                                | Money is delivered in cents with currency; formatting is `Intl.NumberFormat` in the browser (presentation, not a rule).                                                                                  |
| Day-key arithmetic for the week grid, notification scheduling | Stays as a small `dayKey.ts` module in the web app: adding days to a `YYYY-MM-DD` string is presentation plumbing, not a domain rule. Its tests move with it.                                            |

### 4.4 Documentation

Requirements §8 is rewritten for v2 in the slice that completes each resource group; the generated OpenAPI document is the authoritative shape and §8 describes behaviour and error codes only, to avoid two copies of every field.

## 5. Cross-cutting rules for every slice

- Test first; a new test is red on the code before the change (skill `xunit-tdd-workflow`).
- Every Node test file for the area is read and its scenarios are ported or consciously dropped; the dropped ones are listed in the pull request.
- Every state change writes an audit entry in the same transaction; the audit coverage test (the .NET counterpart of `apps/server/test/audit-coverage.test.ts`) walks every v2 write endpoint.
- Every use case is one span; every adapter call is one child span.
- No `MongoDB.Driver`, `QuestPDF`, `Microsoft.Extensions.AI` or `Microsoft.AspNetCore` type outside its adapter; ArchUnitNET fails the build otherwise.
- Requirements, README and the ADR index are updated in the same slice when behaviour, configuration or an architectural choice changes.
- Conventional Commit per coherent change; pull request per slice with a `BEGIN_COMMIT_OVERRIDE` block when it has several release-worthy entries.

## 6. Repository, CI and release changes

- `apps/api/` is added; `apps/server/` stays untouched and keeps shipping until the switch.
- `ci.yml` gets jobs `dotnet-build-test` (restore, build with warnings as errors, `dotnet test` for the four test projects, Docker available for Testcontainers) and `openapi-drift`. The Node jobs stay until the switch.
- `docker/Dockerfile.dotnet` builds the web app in a Node stage and publishes the Host with `dotnet publish` onto `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (non-root through `USER $APP_UID`, port 3000 through `ASPNETCORE_HTTP_PORTS`, `fontconfig` and one font package for QuestPDF, ICU and tzdata present); no Chromium, no chiseled variant (§13). At the switch it replaces `docker/Dockerfile`.
- Release Please stays `simple`; `Directory.Build.props` reads `version.txt` so the About page and the OTel `service.version` show the same version. The switch release carries a `BREAKING CHANGE` footer (API v1 removed, configuration renames), so it becomes a major version.
- `docker-compose.yml`: `mongo` starts with `--replSet rs0` and an init step; a `parallel` profile adds `app-next` on a second port against database `huishoudplanner_next` (§10).
- Agent context: AGENTS.md repository map and non-negotiables, `.github/instructions/api.instructions.md` for `apps/api/**/*.cs` (mirrored in `.cursor/rules`), the three adapted skills, `verify-household-planner` extended with the .NET commands, `change-server-api` rewritten for v2.

## 7. Testing strategy

### 7.1 Layers

| Layer            | Project                     | Proves                                                                                                                          |
| ---------------- | --------------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| Domain unit      | `Domain.Tests`              | Calendar, cycles, due, points, bonuses, badges, plan rules; golden vectors                                                      |
| Application unit | `Application.Tests`         | Use cases with fake driven ports: every `OneOf` variant, audit input, no write on no-op                                         |
| Architecture     | `Architecture.Tests`        | Dependency direction and naming                                                                                                 |
| Integration      | `Integration.Tests`         | Mongo adapters (transactions, indexes, migrations) and the HTTP pipeline through `WebApplicationFactory` against Testcontainers |
| End-to-end       | `apps/web/e2e` (Playwright) | The existing journeys, pointed at the .NET host                                                                                 |

### 7.2 Golden vectors

Before any calendar or points rule is ported, a one-off script exports the inputs and expected outputs of the existing TypeScript tests (`packages/shared/src/*.test.ts`, the DST cases in particular) to `apps/api/tests/Huishoudplanner.Domain.Tests/Vectors/*.json`. The C# tests read the same files. When `packages/shared` is deleted, the vectors remain the executable memory of the rules.

### 7.3 Behaviour parity

A parity script (`scripts/parity.mjs`) replays a fixed scenario against both applications (v1 and v2 paths differ, so it compares domain outcomes: the generated occurrences for a cycle, due states, balances, bonuses, badge progress), not byte-equal responses. Because the parallel run is short (D17), the script is what proves the period boundaries: it starts both applications in test mode with a frozen clock (`APP_FAKE_NOW` exists for this), replays the scenario, advances the clock across a week boundary and a cycle boundary, triggers the nightly job on both sides and compares again. It runs in CI from slice 8.1 on and once more against the live copy during the parallel run (§10). Differences are findings, each resolved as a bug or an accepted change written to the requirements.

## 8. Phases

Each phase ends in something demonstrable. Slices are small enough for one pull request; the checkbox is the state. A slice's test files are named in the slice so the port is checkable.

### Phase 0 — Foundation (nothing visible to the household yet)

- [x] 0.1 ADRs 0016–0021 written and indexed; ADR-0001 and ADR-0006 marked superseded; ADR-0004 and ADR-0008 amended for transactions. `docs/DECISIONS.md` lists the pointers.
- [x] 0.2 Skills copied and adapted into `.agents/skills/` (D13), AGENTS.md gains the .NET rules and the repository map entry for `apps/api`; `.github/instructions/api.instructions.md` and its Cursor mirror exist.
- [x] 0.3 Solution skeleton: all projects of §3.1, `Directory.Build.props`, central package versions, `OneOf`, nullable and analyzers on; `Architecture.Tests` green with the dependency rules (the .NET counterpart of `lint-rule.test.ts`: writes only in the Mongo adapter); `dotnet build` and `dotnet test` in CI.
- [x] 0.4 Host: configuration binding with startup validation (§3.7), Problem Details, OpenAPI generation and drift test, health endpoint `GET /api/v2/health` reporting database reachability, static files and SPA fallback. `WebApplicationFactory` fixture. Port `config.test.ts`, `static.test.ts`, `health.test.ts` (health cases), `health-down.test.ts`.
- [ ] 0.5 OpenTelemetry pipeline with OTLP export, stdout JSON logs, redaction processor, `docs/OBSERVABILITY.md` with the Elastic example; verified against the maintainer's Elastic stack. (built and verified against an OTel Collector; verification against the maintainer's Elastic Agent is still to do)
- [x] 0.6 Mongo adapter base: client, class-map registration, index ensurer with today's index list, `migrations` collection with the two existing migrations, transaction runner, Testcontainers fixture with a single-node replica set; compose `mongo` as replica set with a documented `rs.initiate()` step for existing installations. Port the `ensureIndexes` cases of `health.test.ts`.
- [x] 0.7 Identity port and the profile-header adapter, the three authorization policies, and the audit writer (`ForRecordingAudit`) inside the transaction. Port `identity.test.ts`, `roles.test.ts`, `audit-diff.test.ts`.
- [x] 0.8 `docker/Dockerfile.dotnet` and a smoke test that starts the image against the compose Mongo and calls health.
- [x] 0.9 Golden vectors exported from the TypeScript tests (§7.2); the `time`, `cycle` and `due` helpers ported and green against them.

### Phase 1 — People, rooms, settings

- [x] 1.1 Users: list, create, patch, browser notification moments, `last_admin` rule, seeding from `SEED_USERS`. Port `users.test.ts`, `seed.test.ts`, `browser-notifications.test.ts`.
- [x] 1.2 Rooms and `room_in_use`. Port `rooms.test.ts`.
- [x] 1.3 Settings: read, patch, the bonus schedule rows with `bonus_schedule_conflict`, currency and cents per point, reward goals, intervals, AI settings without the key, `settings_missing`. Port `settings.test.ts`, `interval-change.test.ts`.
- [x] 1.4 `GET /meta/limits` and `GET /calendar` (§4.3).

### Phase 2 — Tasks and plans

- [x] 2.1 Tasks: CRUD, bulk create per room, default points, `interval_in_use`, deactivate instead of delete. Port `tasks.test.ts`.
- [x] 2.2 Cycle plans: CRUD, slots, `default_plan`, `active_plan`, diff. Port `cyclePlans.test.ts`.
- [x] 2.3 Plan validation as a domain service and the two validation endpoints (§4.3); golden vectors from `validation/plan.test.ts`.
- [x] 2.4 Activation preview and activation in one transaction with the preview token (ADR-0008 amended). Port the preview and activation cases of `cyclePlans.test.ts`.

### Phase 3 — Cycles, generation, daily use

- [x] 3.1 Cycles and generation (current and next cycle, `removed`/`generated`, idempotent). Port `generation.test.ts`, `cycles-api.test.ts`.
- [x] 3.2 Occurrences: list, complete/uncomplete/edit completion/skip/reschedule/assign/claim as intent endpoints, `invalid_transition`, `already_claimed`, `completion_choice_*`, warnings. Port `occurrences.test.ts`, `completion-choice.test.ts`, `reschedule.test.ts`. (Deferred: the points ledger that follows a completion goes to phase 4 and the ad-hoc scenarios of `occurrences.test.ts` to 3.3; `DELETE /occurrences/{id}`, the administrator correction of `occurrences.test.ts`, is included here.)
- [x] 3.3 Extra executions and one-off tasks with idempotency keys and retract (ADR-0009). Port `adhoc-occurrences.test.ts`, `one-off-occurrences.test.ts`. (Deferred: the points ledger effects of recorded work and its retract go to phase 4; the AI-input assertion of the one-off test is structural, the proposal reads the task store only.)
- [x] 3.4 Due engine. Port `due-api.test.ts`.

### Phase 4 — Points, bonuses, badges

- [x] 4.1 Points ledger as projection, reconciliation (startup and nightly), recompute endpoint (ADR-0011). Port `points.test.ts`, `points-api.test.ts`, `points-reconcile.test.ts`.
- [x] 4.2 Week and cycle bonuses (ADR-0012). Port `points-bonuses.test.ts`.
- [x] 4.3 Redemptions with balance check, lock and idempotency. Port `points-redemptions.test.ts`.
- [x] 4.4 Progress and reward meter values (§4.3). Port `points-progress.test.ts`.
- [x] 4.5 Badges: definitions, images, examples, awards, progress (ADR-0014). Port `badges.test.ts`.

### Phase 5 — Statistics, history, promotion

- [x] 5.1 Statistics endpoints and the statistics reset. Port `stats.test.ts`, `stats-reset.test.ts`. (Done in the statistics slice: the reports as pure domain calculations, the reset in one transaction with its audit entry and the ledger entries it removes; deferred: the badge-awards rebuild after a reset, which belongs to the badges slice.)
- [x] 5.2 Audit log read with cursor paging, clear, retention job. Port `audit-api.test.ts`, `audit-retention.test.ts`.
- [x] 5.3 Promote suggestions. Port `promote.test.ts`.
- [x] 5.4 Audit coverage and write-route coverage tests over every v2 write endpoint (ADR-0004). Port `audit-coverage.test.ts`, `write-routes-coverage.test.ts`.

### Phase 6 — Assistance, notifications, jobs, exports

- [x] 6.1 AI port and providers (§3.9), prompt info, test, propose, rebalance, suggest tasks, explain, with the one re-prompt. Port `ai-assist.test.ts`, `ai-draft.test.ts`, `ai-proposals.test.ts`, `ai-providers.test.ts`, `ai-anthropic.test.ts`. (Done in two parts: 6.1a the port and the providers, 6.1b the use cases and endpoints. The activation of an AI draft in `ai-draft.test.ts` belongs to 2.4.)
- [x] 6.2 Notifications: ntfy and Home Assistant adapters, morning message. Port `notify.test.ts`. (Done in two parts: the adapters and the morning text in 6.2, the morning use case, job and endpoint in 6.3b.)
- [x] 6.3 Scheduler and the manual job endpoints. Port the nightly and manual-run cases of `generation.test.ts`, `points-reconcile.test.ts`, `points-bonuses.test.ts` and `badges.test.ts` that were deferred in phases 3 and 4. (Done in two parts: 6.3a the scheduler, the generation and retention jobs and their endpoints, 6.3b the morning message job and endpoint, the `due` summary of the generation answer and the nightly and manual-run cases of the points tests; the badge step is part of the reconciliation since 4.5.)
- [x] 6.4 PDF sheets with QuestPDF (§3.10). Port `pdf-export.test.ts`.
- [x] 6.5 JSON export and import with the confirmation and acknowledgement rules. Port `transfer.test.ts`.

### Phase 7 — Web app to v2 (D4)

The generated TypeScript client (`openapi-typescript` + `openapi-fetch`) replaces `apps/web/src/api/client.ts`; the profile header middleware stays. One feature per slice; each slice deletes the shared imports the feature used and moves their tests.

- [ ] 7.1 Client generation, `dayKey.ts`, limits and calendar queries; the `today` and `week` features.
- [ ] 7.2 `mobile-tasks`, `due`, `tasks` (default points from the server).
- [ ] 7.3 `planner` with server-side validation, `promote`, `ai`, `ai-prompts`.
- [ ] 7.4 `reward`, `stats`, `completions`, `badges`.
- [ ] 7.5 `settings` (bonuses in force from the server), `notifications`, `distribution`, `history`, `export`, `about`.
- [ ] 7.6 PWA cache rule for `/api/v2/`, offline queue against the intent endpoints; e2e journeys green against the .NET host.
- [ ] 7.7 `packages/shared` deleted; workspace, lint and typecheck configuration updated.

### Phase 8 — Parallel run and switch (§10)

- [ ] 8.1 Compose `parallel` profile, database copy script, parity script.
- [ ] 8.2 Parallel run of a few days (D17); findings resolved; the maintainer signs off the checklist.
- [ ] 8.3 Switch release: Dockerfile replaced, Node server and its CI jobs removed, configuration renames final, `BREAKING CHANGE` footer, README and requirements §9–§10 updated, ADR-0001 superseded in full.
- [ ] 8.4 Post-switch: `apps/server` deleted, this plan emptied to a pointer in the ADR index.

## 9. Branching

Slices land through pull requests into a long-lived integration branch `next`, created from `main` (D20). `main` keeps receiving fixes for the Node application and is merged into `next` weekly. The switch (8.3) is one pull request from `next` to `main`. This keeps every release from `main` runnable while the web app on `next` is already talking to v2. AGENTS.md steps 2–3 apply with `next` as the base for rewrite work; slice 0.2 writes that rule into AGENTS.md.

## 10. Parallel run and switch (D15)

1. Prepare the installation: back up, convert `mongo` to a single-node replica set (`--replSet rs0`, `rs.initiate()`), restart the Node app and verify it still works (the Node driver needs no change for a replica set).
2. Copy: `mongodump` the production database and `mongorestore` it as `huishoudplanner_next` on the same instance.
3. Run: `docker compose --profile parallel up` adds `app-next` (the .NET image, `MONGO_URL` pointing at `huishoudplanner_next`) on a second port. The household keeps using the Node app.
4. Verify for a few days (D17): the checklist (health, at least one nightly generation produced the same occurrences as production, due list equal, balances equal, badges equal, the four PDF sheets reviewed by the maintainer, morning message received once, Kibana shows traces, metrics and logs of `app-next`), plus the parity script against the live copy. The maintainer walks the checklist and gives the go by hand.
5. Switch, by the maintainer: back up, stop both, run the .NET image against the production database (its startup migrations are idempotent), start, run the checklist again on live data, remove `huishoudplanner_next`.
6. Rollback within the first days: stop the .NET app, start the last Node image against the same database. The .NET app changes no document shape the Node app cannot read until a later, separately planned change, so this stays possible.

## 11. Risks

| Risk                                                                              | Mitigation                                                                                                                                   |
| --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| Calendar and DST drift between TS and C#                                          | Golden vectors first (0.9), parity script, `TimeZoneInfo` with IANA ids on Linux and Windows (ICU present in the image).                     |
| A rule gets lost in the port                                                      | Every Node test file is named in a slice; the pull request lists dropped scenarios.                                                          |
| Long period without releases of the web app                                       | Integration branch; `main` stays releasable for fixes.                                                                                       |
| Replica set conversion on the existing installation                               | Documented procedure with backup and rollback; the Node app is verified on the replica set before the .NET app ever touches production data. |
| QuestPDF layout differs from the HTML sheets                                      | Sheets reviewed by the maintainer in the parallel run; content rules asserted on extracted text.                                             |
| OTel export to Elastic needs a header format or protocol the plan did not foresee | Slice 0.5 is verified against the real stack before anything builds on it.                                                                   |
| Testcontainers needs Docker on every machine that runs tests                      | Documented in README; CI runners have Docker.                                                                                                |
| A parallel run of a few days never crosses a week or cycle boundary               | The parity script crosses both with a frozen clock (§7.3) and runs in CI before the switch is even planned.                                  |

## 12. Open points for the maintainer

None at the time of writing. A slice that meets a choice this plan does not settle asks the maintainer directly when there is a conversation, and writes a `docs/BLOCKERS.md` entry and stops when there is not (AGENTS.md).

## 13. Technology baseline

Versions verified against the official documentation and package registries on 2026-10-03; a slice that adds a package checks the registry again.

| Surface                        | Verified fact                                                                                                                                                                                                                                                                                                                                            | Consequence for the plan                                                                                                                                                                |
| ------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET 10                        | LTS, released 2025-11-11, supported until 2028-11-14; ships C# 14. .NET 11 is at RC1 and will be STS; not a target.                                                                                                                                                                                                                                      | `net10.0` everywhere; no preview SDKs.                                                                                                                                                  |
| MongoDB.Driver                 | 3.12.0; compatible with server 4.4–9.0; pin ≥ 3.11.2 (CVE fixes). Transactions need a replica set, a single-node one qualifies. Built-in OpenTelemetry tracing since 3.7 through `ActivitySource` `MongoDB.Driver`.                                                                                                                                      | `AddSource("MongoDB.Driver")`, no extra instrumentation package (§3.6).                                                                                                                 |
| OpenTelemetry .NET             | `OpenTelemetry` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.19.1; `OpenTelemetry.Instrumentation.AspNetCore` and `.Http` 1.19.0. Standard variables: `OTEL_EXPORTER_OTLP_ENDPOINT` (gRPC 4317 or HTTP 4318), `OTEL_EXPORTER_OTLP_HEADERS` as `key=value,key=value`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_SERVICE_NAME`.                            | Vanilla SDK, not the Elastic distribution, so any OTLP backend works (D6).                                                                                                              |
| Elastic ingestion              | Elastic no longer recommends the APM Server OTLP intake for new setups; the recommended paths are the EDOT Collector, which is built into Elastic Agent, or the Managed OTLP endpoint on Elastic Cloud. API key header: `Authorization=ApiKey <key>`. `Elastic.OpenTelemetry` (EDOT .NET, 1.5.0) exists but is only supported against those two intakes. | Slice 0.5 targets the maintainer's Elastic Agent with the EDOT Collector (D18); the APM Server intake stays a documented fallback.                                                      |
| QuestPDF                       | 2026.9.1; Community licence free under USD 1M revenue, set with `QuestPDF.Settings.License = LicenseType.Community`. No SkiaSharp dependency, the package bundles native Skia and qpdf; slim Linux images need `fontconfig` and a font package.                                                                                                          | Dockerfile installs `fontconfig` and the chosen font; the chiseled image variants are out (§6, below).                                                                                  |
| Microsoft.Extensions.AI        | 10.10.0 GA; `Microsoft.Extensions.AI.OpenAI` 10.10.1 for OpenAI-compatible endpoints; `Microsoft.Extensions.AI.Ollama` is deprecated in favour of OllamaSharp 5.5.0 (implements `IChatClient`). The official Anthropic SDK is the NuGet package `Anthropic` (12.53.0, owner Anthropic) with `client.AsIChatClient(model)`.                               | One `IChatClient` port, four adapters (§3.9).                                                                                                                                           |
| Testcontainers                 | `Testcontainers.MongoDb` 4.15.0; `new MongoDbBuilder().WithReplicaSet("rs0")` starts a single-node replica set.                                                                                                                                                                                                                                          | Integration tests run transactions like production (D10, D12).                                                                                                                          |
| xunit.v3                       | 4.0.1; the template default runner is Microsoft Testing Platform v2 (`xunit.v3.mtp-v2`); `TestContext.Current.CancellationToken` exists.                                                                                                                                                                                                                 | MTP runner, `dotnet test` in CI; no `Microsoft.NET.Test.Sdk`.                                                                                                                           |
| ArchUnitNET                    | `TngTech.ArchUnitNET.xUnitV3` 0.13.4 (the `.xUnit` package is for xunit v2).                                                                                                                                                                                                                                                                             | Architecture tests (D9).                                                                                                                                                                |
| OneOf, Cronos                  | OneOf 3.0.271 (stable, slow-moving). Cronos 0.13.0 with explicit `TimeZoneInfo` support and *nix-cron DST semantics.                                                                                                                                                                                                                                     | Errors as values (D8); jobs in the household timezone (§3.8).                                                                                                                           |
| ASP.NET Core OpenAPI           | `Microsoft.AspNetCore.OpenApi` with `AddOpenApi()`/`MapOpenApi()`, OpenAPI 3.1 by default; build-time generation through `Microsoft.Extensions.ApiDescription.Server`; no UI bundled. `openapi-typescript` 7.13.0 and `openapi-fetch` 0.17.0 for the web client.                                                                                         | §4.1 drift test on the build-time document; Scalar UI only in Development.                                                                                                              |
| Problem Details and validation | `AddProblemDetails()` with `UseExceptionHandler()` and `UseStatusCodePages()`; .NET 10 `AddValidation()` validates data annotations on Minimal API parameters into `HttpValidationProblemDetails` (`errors` dictionary).                                                                                                                                 | Shape validation by annotations, rule validation in the domain as `ValidationErrors`; both end in the same `errors` extension (§4.2).                                                   |
| Docker images                  | `mcr.microsoft.com/dotnet/aspnet:10.0-noble` sets `APP_UID=1654` and a user `app`; `ASPNETCORE_HTTP_PORTS` defaults to 8080. Chiseled and distroless variants omit ICU, tzdata and fontconfig.                                                                                                                                                           | `10.0-noble` with `USER $APP_UID`, `ASPNETCORE_HTTP_PORTS=3000`, `fontconfig` and a font installed; ICU and tzdata needed for `TimeZoneInfo` with IANA ids and for currency formatting. |

## 14. Unattended run

This section applies when the maintainer starts an unattended run, for example an overnight session. Where it deviates from AGENTS.md or from §5, this section wins for that run; everything it does not mention follows AGENTS.md.

### 14.1 Mandate (settled 2026-10-03)

- Do as much as the usage limit and the night allow: phase 0 first, then phase 1 and onwards in plan order. Do not stop at a phase boundary.
- Slice pull requests go to the integration branch `next` and are **merged by the orchestrator** (squash) as soon as their CI is green. `main` is never touched; no release, no deploy, no installation.
- Writing subagents may run in parallel, at most three at a time, each in its own git worktree, and only for slices that touch disjoint files. Dependent slices run after each other.
- Slice 0.5 builds the OpenTelemetry pipeline without a receiver being available: the OTLP exporter is configured from the standard variables and tested against an OpenTelemetry Collector container with the debug exporter, and the pull request names "verified against the maintainer's Elastic Agent" as the one step left to the maintainer.
- Tooling present on the maintainer's machine on 2026-10-03: .NET SDK 10.0.401, Docker 29, Node 24, `gh` logged in.

### 14.2 Models

The orchestrator runs on Sonnet. Nothing in this plan needs Fable. Opus is used only as a reviewer of the three slices with structural risk and as the escalation after two failed attempts on one task.

| Level    | Claude Code `model` | Used for                                                                                                                                                               |
| -------- | ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Light    | `haiku`             | Looking up code and tests, running `dotnet test` or `npm run verify` and reporting only the failures, checking README, requirements and agent context for stale text.  |
| Standard | `sonnet`            | The orchestrator, every builder, every reviewer not named below.                                                                                                       |
| Heavy    | `opus`              | Reviewer of 0.3b (architecture rules), 0.6c (transactions and audit) and 0.7a (identity policies) before the merge; builder of any task a Sonnet builder failed twice. |

Builders stay on Sonnet by keeping slices small. Where §8 names one slice, the run splits it:

| §8 slice      | Sub-slices for the run                                                                                                                                                                                                                                                                               |
| ------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0.3           | 0.3a projects, references, `Directory.Build.props`, `Directory.Packages.props`, `dotnet build` green, CI job `dotnet-build-test`; 0.3b `Architecture.Tests` with the rules of §3.1.                                                                                                                  |
| 0.4           | 0.4a options binding with startup validation and the `config.test.ts` cases; 0.4b Problem Details, health, `WebApplicationFactory` fixture, `health.test.ts` and `health-down.test.ts`; 0.4c static files, SPA fallback, `static.test.ts`; 0.4d OpenAPI document, build-time generation, drift test. |
| 0.6           | 0.6a client, class-map registration, index ensurer, `ensureIndexes` cases; 0.6b `migrations` collection with the two existing migrations; 0.6c transaction runner and the Testcontainers fixture with a replica set.                                                                                 |
| 0.7           | 0.7a identity port, header adapter, the three policies, `identity.test.ts` and `roles.test.ts`; 0.7b audit writer inside the transaction, `audit-diff.test.ts`.                                                                                                                                      |
| 0.9           | 0.9a export script and the JSON vectors (TypeScript only); 0.9b `time` and `cycle` in C#; 0.9c `due` in C#.                                                                                                                                                                                          |
| 1.x and later | One resource per sub-slice: read endpoints first, then each write endpoint with its audit entry and tests.                                                                                                                                                                                           |

### 14.3 Waves for phase 0

| Wave | Parallel (own worktree each)                                                       | Why they do not collide                                                   |
| ---- | ---------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| 1    | 0.1 ADRs · 0.2 skills and agent context · 0.3a solution skeleton                   | `docs/adr`, `.agents` plus AGENTS.md, `apps/api`                          |
| 2    | 0.9a vector export · 0.3b architecture tests · 0.6a Mongo base                     | `scripts` plus vectors JSON, `tests/Architecture.Tests`, `Adapters.Mongo` |
| 3    | 0.4a–0.4d in order (all touch `Host/Program.cs`) · 0.6b then 0.6c · 0.9b then 0.9c | Host, Mongo adapter, Domain are disjoint                                  |
| 4    | 0.7a then 0.7b · 0.8 Dockerfile · 0.5 OpenTelemetry                                | Http adapter, `docker/`, Host telemetry extension                         |

After phase 0 the resources of phases 1 to 6 are independent enough to run two or three at a time, each resource in its own worktree, as long as no two touch the same adapter file; the orchestrator checks the file lists before starting a wave.

### 14.4 Branches

- `next` is created from `main` once at the start of the run if it does not exist; `main` is merged into `next` at the start of every run.
- A slice branch is named `<type>/claude-<slice>-<short-name>` (for example `feat/claude-0-3a-solution-skeleton`) and starts from the current `next`.
- The pull request targets `next`, carries a Conventional Commit title, the markers `loop-status: done` or `loop-status: blocked`, the checks that ran, and a heading **Decisions for review** with the lines that would otherwise go to `docs/DECISIONS.md`.
- `next` has no required checks; the orchestrator waits for `gh pr checks <n> --watch` to report success before `gh pr merge <n> --squash --delete-branch`. A conflict against `next` is resolved by the orchestrator in the slice branch, never by force-push.
- Worktrees live under `../kthc-worktrees/<branch>` and are removed after the merge.

### 14.5 Every round

1. Read progress from GitHub: `gh pr list --base next --state all --json headRefName,title,url,body`. The first slice in plan order without a `done` pull request, whose dependencies are `done`, is next; several such slices may start together within the limits of 14.3.
2. Check usage with the host's usage tool (`get_usage` in the desktop app; otherwise `npx -y ccusage@latest blocks --active --json`). At 95% of the five-hour or weekly limit, start nothing: schedule a wake-up of `min(3600, seconds to reset)` and check again on waking. The skill `stay-within-limits` describes this.
3. For every slice to start: create the worktree and branch, write the slice's steps in `docs/BUILD.md` on that branch, and hand the builder a self-contained brief with the slice text from §8, the Node test files to port, the relevant ADRs and skills, and the acceptance criterion "every named test file ported or its dropped scenarios listed".
4. Run the controller (`haiku`) and the reviewer, push, open the pull request, read its description back, wait for CI, merge.
5. Schedule the next round after 60 seconds. When no slice can start (all done, all blocked, or the limit reached), write the end report: per slice the pull request, status, checks, decisions for review and open questions; send a notification if the host can; stop.

### 14.6 Deviations because nobody is watching

- A product question the plan does not answer is not answered by the agent: commit the coherent part, put the question in `docs/BLOCKERS.md` on the branch, open the pull request with `loop-status: blocked` and the question at the top, do not merge it, and continue with the next slice that does not depend on it.
- Technical design choices within the decisions of §2 may be taken; they are listed under **Decisions for review**. A choice that contradicts §2 is a blocker.
- A check that still fails after two serious repair attempts makes the slice `blocked` with the relevant output in the pull request.
- Checkboxes in §8 are ticked in the slice that completes them, in the same pull request.
- Never merge to `main`, release, deploy, force-push, or touch a real installation or its database.
