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
