# Huishoudplanner API (.NET)

The .NET 10 rewrite of the server, built in parallel with `apps/server/` (see [the plan](../../docs/plans/dotnet-rewrite.md)). Layout: [plan §3.1](../../docs/plans/dotnet-rewrite.md).

```bash
cd apps/api
dotnet restore
dotnet build      # warnings are errors
dotnet test       # xunit.v3 on Microsoft Testing Platform, all four test projects
# Integration.Tests starts a mongo:8 container (Testcontainers): Docker must be running.
```

The SDK is pinned in `global.json` (10.0.x). Package versions live only in `Directory.Packages.props`; the version number comes from the repository-root `version.txt`.

## Container image

`docker/Dockerfile.dotnet` builds the web app in a Node stage, publishes the Host (`dotnet publish -c Release`, central package management) and runs it on `mcr.microsoft.com/dotnet/aspnet:10.0-noble` as the non-root `app` user (`USER $APP_UID`). The image installs `fontconfig` and `fonts-dejavu-core` for QuestPDF, has ICU and tzdata, and contains no browser. It takes the same `APP_OFFICIAL_BUILD` and `APP_RELEASE_DATE` build arguments as `docker/Dockerfile` (they reach the web build; the API version comes from `version.txt`). At the switch (slice 8.3) it replaces `docker/Dockerfile`.

```bash
docker build -f docker/Dockerfile.dotnet -t huishoudplanner-app:dotnet .
docker run --rm -p 3000:3000 -e MONGO_URL='mongodb://host:27017/huishoudplanner?replicaSet=rs0' huishoudplanner-app:dotnet
node scripts/smoke-dotnet.mjs   # or: npm run smoke:dotnet
```

- The listening port is set by `ASPNETCORE_HTTP_PORTS=3000`. `PORT` is validated by `AppOptions` but not applied to Kestrel yet, so setting `PORT` alone does not move the listener; change `ASPNETCORE_HTTP_PORTS` (and the published port) instead.
- The image has neither `curl` nor `wget`, so the `HEALTHCHECK` is a plain `bash` TCP request to `/api/v2/health` on `ASPNETCORE_HTTP_PORTS`.
- The application needs a single-node replica set (ADR-0021). The compose Mongo is still a standalone instance, so the smoke test adds `docker/docker-compose.dotnet-smoke.yml`, which starts Mongo with `--replSet rs0`, initiates it from the healthcheck and points `MONGO_URL` at it. `scripts/smoke-dotnet.mjs` uses its own compose project, port (3200, `SMOKE_PORT`), image tag and volumes, checks health, `/`, a SPA deep link and the Problem Details 404, and always removes everything again. CI runs it as the `dotnet-container-smoke` job.

## Architecture rules

`tests/Huishoudplanner.Architecture.Tests` guards what project references cannot. Three mechanisms, because no single one sees everything:

- **ArchUnitNET (namespaces and types):** Domain depends on nothing of ours and only on the BCL and `OneOf`; Application on Domain only; no adapter depends on another adapter; nothing depends on Host (the only composition root); `MongoDB.*` only in `Adapters.Mongo`, `QuestPDF` only in `Adapters.Pdf`, `Microsoft.AspNetCore.*` only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI` only in `Adapters.Ai`; every assembly keeps its types under its own root namespace; interfaces in `Domain.Ports.Driven` are named `For*` (and every `For*` interface lives there), interfaces in `Domain.Ports.Driving` are named `I*Service` (commands, DTOs and error records may sit beside them).
- **IL call scan (what a type calls):** ArchUnitNET does not load the compiler-generated types behind async lambdas, local functions and iterators, so call rules read the IL, including those nested types, and attribute each call to the type the author wrote. Rules: `DateTime`/`DateTimeOffset` `Now`, `UtcNow`, `Today` only in the exact type `Huishoudplanner.Host.SystemClock` (the `ForTellingTime` implementation); `TimeProvider.System` only in Host; no MongoDB.Driver write method (Insert, Update, Replace, Delete, BulkWrite, FindOneAnd, Drop, Create, Rename, Merge, Out, AggregateToCollection, Upload, RunCommand) outside `Adapters.Mongo`, the counterpart of `apps/server/test/lint-rule.test.ts`.
- **Build rules (what a project references):** a type of a package can live in a namespace that does not name it (`AddRouting()`), so each src project's csproj and restore result are checked against an allow-list: MongoDB packages only in `Adapters.Mongo`, QuestPDF only in `Adapters.Pdf`, ASP.NET Core packages, framework reference and the Web SDK only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI*` only in `Adapters.Ai`, Domain only `OneOf`.

Every rule is proven three ways: it passes on the shipped assemblies, it passes with positive results on a conforming layout, and it fails on deliberately violating types (or csproj text). The violating and conforming types live in `fixtures/Huishoudplanner.Architecture.Tests.Fixtures` (never shipped, only referenced by the architecture tests). Adding a rule means adding a violating type there and an entry in `ArchitectureRuleTests`, `IlRules` or `BuildRules`.

## Audit coverage

Every actual state change has an audit entry in the same transaction; a no-op writes and audits nothing (ADR-0004). Two Integration tests prove it against the real host and a real replica set, the counterparts of `audit-coverage.test.ts` and `write-routes-coverage.test.ts`:

- **`WriteCapture`** (`Fixtures/WriteCapture.cs`, switched on per test factory with `ApiFactory.WithWriteCapture`) listens to the MongoDB driver's command events on the application's own client, through the internal `IMongoClientSettingsCustomizer` seam in `Adapters.Mongo` (nothing registers it in production). It records every command that changed something (insert, update, delete, findAndModify, and schema commands such as `createIndexes`), the collection and the number of documents. An update that modified nothing (like the insert-if-absent upsert of the generation) and a delete that removed nothing are no-ops and are not recorded; writes of an aborted transaction are dropped. `Writes()` leaves out the audit log itself and `migrations`.
- **`AuditCoverageTests`** checks what an audit entry records (actor, source, fields), that a no-op update sends no write at all, and that a deliberately bad test-only endpoint writing around the audit recording is caught by the capture.
- **`WriteRouteCoverageTests`** walks every non-GET endpoint registered by the real host. Each one needs a scenario in its `Scenarios` table, arranged against a seeded household; the scenario is executed and the capture must show audit entries of the expected entity, action and source attributed to the requesting profile for every state change, no write outside the collections of `MongoCollections`, no schema change while serving a request, and, where marked `Idempotent`, that the same request repeated writes nothing. Read-only POSTs must write nothing at all; the endpoints that deliberately record no audit entry (`DELETE /api/v2/audit` and `POST /api/v2/jobs/audit-retention`, which prune the log itself) are `Unaudited` scenarios with a reason and may only touch the audit log.

**A new write endpoint fails `WriteRouteCoverageTests` until it has a scenario.** The message lists the endpoints without one. Slices that add write endpoints (the points ledger, promote, ad-hoc work, badges, import and so on) extend the table in the same change. The `POST /api/v2/import/json` scenario imports the export of the household itself and declares in `Covers` every collection its one `import` summary entry stands for.

## JSON export and import

`GET /api/v2/export/json` and `POST /api/v2/import/json` (slice 6.5, requirements 8) move the whole dataset as MongoDB relaxed Extended JSON; both are for administrators (the export is open in the Node server, a maintainer decision of slice 6.8). The file is read and validated completely by `Adapters.Mongo.Transfer` before anything is written (`ImportReader` for the envelope and the rules between documents, `ImportSchemas` for the shape and storage types of every document, a small declarative language in `ImportShape` instead of one validator class per collection), then replaced in one transaction together with its `import` audit entry; the points ledger is rebuilt afterwards through `IPointsService` with the import trigger. The body limit is 200 MB (`RequestSizeLimit` metadata on the endpoint) and the file is buffered in memory, once as bytes and once parsed. Tests: `TransferServiceTests` (Application, with fakes), `TransferEndpointTests` (Integration, against a real replica set; ports `transfer.test.ts`).

`DELETE /api/v2/tasks/{id}` (slice 6.6, planners) removes a task for good: `TaskService.DeleteAsync` takes its slots out of every plan that holds one (an audited plan `update` with `meta.reason: task_delete`), deletes the task (a `delete` entry that keeps the removed fields) and calls `IBadgeService.RemoveTaskFromRulesAsync` (audited badge updates with `meta.reason: task_deleted`, a rule left without tasks is deactivated), all in one transaction; occurrences, points and history stay because they carry their own snapshot. Tests: `TaskServiceTests` (Application, with fakes), `TaskDeleteEndpointTests` (Integration, against a real replica set; ports the delete scenario of `tasks.test.ts` and the `task_deleted` scenario of `badges.test.ts`).

## Configuration

All configuration comes from environment variables, listed in [requirements section 9](../../docs/huishoudplanner-requirements.md). They are bound to `AppOptions` (`src/Huishoudplanner.Host/Configuration`) through `IOptions<AppOptions>` and validated when the host starts: an invalid configuration refuses to start and the message names the offending variables without ever echoing their values. An empty variable counts as unset.

Two variables were renamed for .NET: `NODE_ENV` is now `ASPNETCORE_ENVIRONMENT` and `LOG_LEVEL` is now `Logging__LogLevel__Default`. The old names still work as aliases (the new name wins when both are set; for the environment the order is `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `NODE_ENV`) until the switch from `apps/server`.

## Scheduler and jobs

`Adapters.Jobs` runs the scheduled jobs (plan section 3.8, requirements 4.10). The design is one `IJob` per job (`Name`, a five-field cron `Schedule`, `RunAsync(scope)`), a registry of the registered jobs, one `JobRunner` and **one** hosted service (`JobSchedulerService`) over the registry, instead of one hosted service per job: a new job is one class and one `services.AddJob<T>()`.

- **Time:** `JobScheduler` holds when each job fires next, computed with Cronos (0.13.0) in the household timezone (`TZ_APP`, an explicit `TimeZoneInfo`). It has no timer; `JobSchedulerService` sleeps on `TimeProvider` (at most an hour at a time) and calls `StartDue`. Tests inject a `FakeTimeProvider` and call `StartDue`, so none waits for a cron time. Daylight saving follows *nix cron: 03:00 and 03:45 keep their local time on both change days; a fixed time inside the skipped spring hour runs right after the gap and a repeated autumn time runs once (`JobSchedulerTests`). A run missed while the process slept happens once.
- **noOverlap:** the runner holds a `SemaphoreSlim` per job and a trigger that finds the previous run still going is not started (counted as `overlapped`), like `noOverlap` of the Node scheduler. Different jobs may run together.
- **Actor and failures:** jobs call driving ports as `AuditActor.System`. A failing job is logged with its name and the exception type only (never the message) and never stops the host or the other jobs.
- **Telemetry:** one span `job <name>` per run and the counter `huishoudplanner.jobs.runs` by `job` and `outcome` (`succeeded`, `skipped`, `failed`, `overlapped`, `cancelled`), source and meter `Huishoudplanner.Jobs`.
- **Configuration:** `DISABLE_SCHEDULER=true` leaves the scheduler without any job; build-time OpenAPI generation registers no scheduler at all. The integration test host sets `DISABLE_SCHEDULER=true` by default; `WithSetting("DISABLE_SCHEDULER", "false")` turns it on.
- **Jobs today:** `nightly-generation` at 03:00 (`INightlyService`: generation, then the reconciliation of the ledger, the bonuses and the badge awards, which `PointsService` runs as one step), `audit-retention` at 03:45 (`IAuditRetentionService`; it ends as `skipped` without `AUDIT_RETENTION_DAYS`, where Node does not schedule it) and `morning-notify` at 07:30 (`IMorningNotifyService`). A job whose `IJob.Enabled` is false is not scheduled at all: the morning message needs a notification channel (`NOTIFY_TYPE` other than `none`), like in Node.
- **Manual triggers:** `POST /api/v2/jobs/generation` (its answer carries the `due` summary), `POST /api/v2/jobs/audit-retention` and `POST /api/v2/jobs/morning-notify` (planners, see requirements section 8), implemented in `Adapters.Http/Jobs`; they call the same ports as the jobs, with the requesting profile as actor.

## Observability

Traces, metrics and logs go out over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; without it the application logs JSON to stdout only. The pipeline lives in `src/Huishoudplanner.Host/Telemetry`; variables, the Elastic example and the verification status are in [docs/OBSERVABILITY.md](../../docs/OBSERVABILITY.md).

## Errors and health

Errors leave the API as RFC 9457 Problem Details (`application/problem+json`): `type` is `urn:huishoudplanner:problem:<code>`, plus `status`, `detail` and an always present `traceId`; validation problems add an `errors` object. Port error values (`NotFound`, `ConflictError`, `ValidationErrors`, `PortError` in `Domain/Errors`) are mapped by `ProblemResults` in `Adapters.Http`; unhandled exceptions and empty 4xx/5xx responses go through `UseExceptionHandler` and `UseStatusCodePages` and never leak a message.

`GET /api/v2/health` answers `{ "status": "ok", "version": "1.7.0", "database": "ok" }`, or `503` with `"error"` in both status fields when the database ping fails. Integration tests build the host with `ApiFactory` (`tests/Huishoudplanner.Integration.Tests/Fixtures`): swap a driven port with `WithPort`, or use `ForMongo` for the Docker-backed variant.

## OpenAPI document

The API is described by an OpenAPI 3.1 document (`AddOpenApi("v2")` in `Adapters.Http`, document name `v2`, title and tags in `OpenApi/OpenApiSetup.cs`, endpoints describe themselves with `WithName`, `WithSummary`, `Produces<T>` and `ProducesProblem`). It is generated at build time by `Microsoft.Extensions.ApiDescription.Server` and checked in at `apps/api/openapi/v2.json`: the reviewed source of the generated TypeScript client in the web app.

- **Regenerate:** `dotnet build apps/api/src/Huishoudplanner.Host` (every build of the Host rewrites `openapi/v2.json`; review and commit the diff). It needs no MongoDB and no configuration: under the generation tool `BuildTimeGeneration.IsRunning` is true (entry assembly `GetDocument.Insider`) and `Program.cs` skips the `ValidateOnStart` of `AppOptions`.
- **Drift is caught twice:** `OpenApiDocumentTests` compares the live document (served at `/openapi/v2.json`) with the checked-in file, and the CI job `openapi-drift` builds the Host and runs `git diff --exit-code -- openapi/v2.json`.
- **Development only:** `MapOpenApi` (`/openapi/v2.json`) and the Scalar UI (`/scalar/v2`) are mapped when `ASPNETCORE_ENVIRONMENT=Development`.
- The only server entry is `/`: paths already carry the `/api/v2` prefix.
