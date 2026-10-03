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

## Configuration

All configuration comes from environment variables, listed in [requirements section 9](../../docs/huishoudplanner-requirements.md). They are bound to `AppOptions` (`src/Huishoudplanner.Host/Configuration`) through `IOptions<AppOptions>` and validated when the host starts: an invalid configuration refuses to start and the message names the offending variables without ever echoing their values. An empty variable counts as unset.

Two variables were renamed for .NET: `NODE_ENV` is now `ASPNETCORE_ENVIRONMENT` and `LOG_LEVEL` is now `Logging__LogLevel__Default`. The old names still work as aliases (the new name wins when both are set; for the environment the order is `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `NODE_ENV`) until the switch from `apps/server`.

## Errors and health

Errors leave the API as RFC 9457 Problem Details (`application/problem+json`): `type` is `urn:huishoudplanner:problem:<code>`, plus `status`, `detail` and an always present `traceId`; validation problems add an `errors` object. Port error values (`NotFound`, `ConflictError`, `ValidationErrors`, `PortError` in `Domain/Errors`) are mapped by `ProblemResults` in `Adapters.Http`; unhandled exceptions and empty 4xx/5xx responses go through `UseExceptionHandler` and `UseStatusCodePages` and never leak a message.

`GET /api/v2/health` answers `{ "status": "ok", "version": "1.7.0", "database": "ok" }`, or `503` with `"error"` in both status fields when the database ping fails. Integration tests build the host with `ApiFactory` (`tests/Huishoudplanner.Integration.Tests/Fixtures`): swap a driven port with `WithPort`, or use `ForMongo` for the Docker-backed variant.
