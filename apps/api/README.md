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

## Architecture rules

`tests/Huishoudplanner.Architecture.Tests` guards what project references cannot. Three mechanisms, because no single one sees everything:

- **ArchUnitNET (namespaces and types):** Domain depends on nothing of ours and only on the BCL and `OneOf`; Application on Domain only; no adapter depends on another adapter; nothing depends on Host (the only composition root); `MongoDB.*` only in `Adapters.Mongo`, `QuestPDF` only in `Adapters.Pdf`, `Microsoft.AspNetCore.*` only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI` only in `Adapters.Ai`; every assembly keeps its types under its own root namespace; interfaces in `Domain.Ports.Driven` are named `For*` (and every `For*` interface lives there), interfaces in `Domain.Ports.Driving` are named `I*Service` (commands, DTOs and error records may sit beside them).
- **IL call scan (what a type calls):** ArchUnitNET does not load the compiler-generated types behind async lambdas, local functions and iterators, so call rules read the IL, including those nested types, and attribute each call to the type the author wrote. Rules: `DateTime`/`DateTimeOffset` `Now`, `UtcNow`, `Today` only in the exact type `Huishoudplanner.Host.SystemClock` (the `ForTellingTime` implementation); `TimeProvider.System` only in Host; no MongoDB.Driver write method (Insert, Update, Replace, Delete, BulkWrite, FindOneAnd, Drop, Create, Rename, Merge, Out, AggregateToCollection, Upload, RunCommand) outside `Adapters.Mongo`, the counterpart of `apps/server/test/lint-rule.test.ts`.
- **Build rules (what a project references):** a type of a package can live in a namespace that does not name it (`AddRouting()`), so each src project's csproj and restore result are checked against an allow-list: MongoDB packages only in `Adapters.Mongo`, QuestPDF only in `Adapters.Pdf`, ASP.NET Core packages, framework reference and the Web SDK only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI*` only in `Adapters.Ai`, Domain only `OneOf`.

Every rule is proven three ways: it passes on the shipped assemblies, it passes with positive results on a conforming layout, and it fails on deliberately violating types (or csproj text). The violating and conforming types live in `fixtures/Huishoudplanner.Architecture.Tests.Fixtures` (never shipped, only referenced by the architecture tests). Adding a rule means adding a violating type there and an entry in `ArchitectureRuleTests`, `IlRules` or `BuildRules`.

## Configuration

All configuration comes from environment variables, listed in [requirements section 9](../../docs/huishoudplanner-requirements.md). They are bound to `AppOptions` (`src/Huishoudplanner.Host/Configuration`) through `IOptions<AppOptions>` and validated when the host starts: an invalid configuration refuses to start and the message names the offending variables without ever echoing their values. An empty variable counts as unset.

Two variables were renamed for .NET: `NODE_ENV` is now `ASPNETCORE_ENVIRONMENT` and `LOG_LEVEL` is now `Logging__LogLevel__Default`. The old names still work as aliases (the new name wins when both are set; for the environment the order is `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `NODE_ENV`) until the switch from `apps/server`.

## Errors and health

Errors leave the API as RFC 9457 Problem Details (`application/problem+json`): `type` is `urn:huishoudplanner:problem:<code>`, plus `status`, `detail` and an always present `traceId`; validation problems add an `errors` object. Port error values (`NotFound`, `ConflictError`, `ValidationErrors`, `PortError` in `Domain/Errors`) are mapped by `ProblemResults` in `Adapters.Http`; unhandled exceptions and empty 4xx/5xx responses go through `UseExceptionHandler` and `UseStatusCodePages` and never leak a message.

`GET /api/v2/health` answers `{ "status": "ok", "version": "1.7.0", "database": "ok" }`, or `503` with `"error"` in both status fields when the database ping fails. Integration tests build the host with `ApiFactory` (`tests/Huishoudplanner.Integration.Tests/Fixtures`): swap a driven port with `WithPort`, or use `ForMongo` for the Docker-backed variant.

## OpenAPI document

The API is described by an OpenAPI 3.1 document (`AddOpenApi("v2")` in `Adapters.Http`, document name `v2`, title and tags in `OpenApi/OpenApiSetup.cs`, endpoints describe themselves with `WithName`, `WithSummary`, `Produces<T>` and `ProducesProblem`). It is generated at build time by `Microsoft.Extensions.ApiDescription.Server` and checked in at `apps/api/openapi/v2.json`: the reviewed source of the generated TypeScript client in the web app.

- **Regenerate:** `dotnet build apps/api/src/Huishoudplanner.Host` (every build of the Host rewrites `openapi/v2.json`; review and commit the diff). It needs no MongoDB and no configuration: under the generation tool `BuildTimeGeneration.IsRunning` is true (entry assembly `GetDocument.Insider`) and `Program.cs` skips the `ValidateOnStart` of `AppOptions`.
- **Drift is caught twice:** `OpenApiDocumentTests` compares the live document (served at `/openapi/v2.json`) with the checked-in file, and the CI job `openapi-drift` builds the Host and runs `git diff --exit-code -- openapi/v2.json`.
- **Development only:** `MapOpenApi` (`/openapi/v2.json`) and the Scalar UI (`/scalar/v2`) are mapped when `ASPNETCORE_ENVIRONMENT=Development`.
- The only server entry is `/`: paths already carry the `/api/v2` prefix.
