# Huishoudplanner API (.NET)

The .NET 10 rewrite of the server, built in parallel with `apps/server/` (see [the plan](../../docs/plans/dotnet-rewrite.md)). Layout: [plan §3.1](../../docs/plans/dotnet-rewrite.md).

```bash
cd apps/api
dotnet restore
dotnet build      # warnings are errors
dotnet test       # xunit.v3 on Microsoft Testing Platform, all four test projects
```

The SDK is pinned in `global.json` (10.0.x). Package versions live only in `Directory.Packages.props`; the version number comes from the repository-root `version.txt`.

## Architecture rules

`tests/Huishoudplanner.Architecture.Tests` (ArchUnitNET) guards what project references cannot:

- Dependency direction: Domain depends on nothing of ours and only on the BCL and `OneOf`; Application on Domain only; no adapter depends on another adapter; nothing depends on Host (the only composition root).
- One home per driver: `MongoDB.*` only in `Adapters.Mongo` (and no Mongo write method called elsewhere, the counterpart of `apps/server/test/lint-rule.test.ts`), `QuestPDF` only in `Adapters.Pdf`, `Microsoft.AspNetCore.*` only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI` only in `Adapters.Ai`.
- Time: `DateTime`/`DateTimeOffset` `Now`, `UtcNow`, `Today` only in a `*Clock` type inside the `Host` namespace (the `ForTellingTime` implementation); everything else goes through the port.
- Ports: driven ports are interfaces named `For*` in `Domain.Ports.Driven` (and every `For*` interface lives there); driving ports are interfaces named `I*Service` in `Domain.Ports.Driving`.

Every rule is written once against a namespace layout and proven three ways: it passes on the shipped assemblies, it passes with positive results on a conforming layout, and it fails on deliberately violating types. Those types live in `fixtures/Huishoudplanner.Architecture.Tests.Fixtures` (never shipped, only referenced by the architecture tests). Adding a rule means adding a violating type there and an entry in `ArchitectureRuleTests`.

