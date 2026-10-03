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

`tests/Huishoudplanner.Architecture.Tests` guards what project references cannot. Three mechanisms, because no single one sees everything:

- **ArchUnitNET (namespaces and types):** Domain depends on nothing of ours and only on the BCL and `OneOf`; Application on Domain only; no adapter depends on another adapter; nothing depends on Host (the only composition root); `MongoDB.*` only in `Adapters.Mongo`, `QuestPDF` only in `Adapters.Pdf`, `Microsoft.AspNetCore.*` only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI` only in `Adapters.Ai`; every assembly keeps its types under its own root namespace; interfaces in `Domain.Ports.Driven` are named `For*` (and every `For*` interface lives there), interfaces in `Domain.Ports.Driving` are named `I*Service` (commands, DTOs and error records may sit beside them).
- **IL call scan (what a type calls):** ArchUnitNET does not load the compiler-generated types behind async lambdas, local functions and iterators, so call rules read the IL, including those nested types, and attribute each call to the type the author wrote. Rules: `DateTime`/`DateTimeOffset` `Now`, `UtcNow`, `Today` only in the exact type `Huishoudplanner.Host.SystemClock` (the `ForTellingTime` implementation); `TimeProvider.System` only in Host; no MongoDB.Driver write method (Insert, Update, Replace, Delete, BulkWrite, FindOneAnd, Drop, Create, Rename, Merge, Out, AggregateToCollection, Upload, RunCommand) outside `Adapters.Mongo`, the counterpart of `apps/server/test/lint-rule.test.ts`.
- **Build rules (what a project references):** a type of a package can live in a namespace that does not name it (`AddRouting()`), so each src project's csproj and restore result are checked against an allow-list: MongoDB packages only in `Adapters.Mongo`, QuestPDF only in `Adapters.Pdf`, ASP.NET Core packages, framework reference and the Web SDK only in `Adapters.Http` and `Host`, `Microsoft.Extensions.AI*` only in `Adapters.Ai`, Domain only `OneOf`.

Every rule is proven three ways: it passes on the shipped assemblies, it passes with positive results on a conforming layout, and it fails on deliberately violating types (or csproj text). The violating and conforming types live in `fixtures/Huishoudplanner.Architecture.Tests.Fixtures` (never shipped, only referenced by the architecture tests). Adding a rule means adding a violating type there and an entry in `ArchitectureRuleTests`, `IlRules` or `BuildRules`.
