# Huishoudplanner API (.NET)

The .NET 10 rewrite of the server, built in parallel with `apps/server/` (see [the plan](../../docs/plans/dotnet-rewrite.md)). Layout: [plan §3.1](../../docs/plans/dotnet-rewrite.md).

```bash
cd apps/api
dotnet restore
dotnet build      # warnings are errors
dotnet test       # xunit.v3 on Microsoft Testing Platform, all four test projects
```

The SDK is pinned in `global.json` (10.0.x). Package versions live only in `Directory.Packages.props`; the version number comes from the repository-root `version.txt`.

## Configuration

All configuration comes from environment variables, listed in [requirements section 9](../../docs/huishoudplanner-requirements.md). They are bound to `AppOptions` (`src/Huishoudplanner.Host/Configuration`) through `IOptions<AppOptions>` and validated when the host starts: an invalid configuration refuses to start and the message names the offending variables without ever echoing their values. An empty variable counts as unset.

Two variables were renamed for .NET: `NODE_ENV` is now `ASPNETCORE_ENVIRONMENT` and `LOG_LEVEL` is now `Logging__LogLevel__Default`. The old names still work as aliases (the new name wins when both are set; for the environment the order is `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `NODE_ENV`) until the switch from `apps/server`.
