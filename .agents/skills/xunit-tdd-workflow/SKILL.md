---
name: xunit-tdd-workflow
description: Writing or reviewing .NET tests for apps/api. xunit.v3 with AwesomeAssertions and Moq, red before green, fixtures and IAsyncLifetime, Testcontainers mongo replica set for adapters, WebApplicationFactory for the HTTP pipeline, ArchUnitNET, golden vectors, covering every OneOf variant. Use when tests are the task.
---

# xUnit.v3 TDD workflow

Adapted from the `xunit-tdd-workflow` skill of the dark-factory repository (plan decisions D13, D14, D10). The shapes in the examples are worked examples, not required literals; they use the household domain and need not compile.

## Authoritative sources

- [docs/plans/dotnet-rewrite.md](../../../docs/plans/dotnet-rewrite.md): §5 cross-cutting rules, §7 testing strategy, §13 technology baseline (xunit.v3 4.0.1 on the Microsoft Testing Platform runner, `Testcontainers.MongoDb`, `TngTech.ArchUnitNET.xUnitV3`).
- [AGENTS.md](../../../AGENTS.md) testing and completion rules: every harness owns its own state; no wall-clock time.
- [xunit.v3 documentation](https://xunit.net/docs/getting-started/v3/cmdline) and [AwesomeAssertions](https://github.com/AwesomeAssertions/AwesomeAssertions) (`Should()` syntax).

## When to use this skill

- Writing unit tests for domain entities, services and use cases.
- Writing integration tests for Mongo adapters or the HTTP pipeline.
- Following red-green-refactor, setting up fixtures.
- Porting a Node test file: read it, port every scenario or consciously drop it and list the dropped ones in the pull request.

## Red-green-refactor

```
1. RED      write a failing test that describes the expected behaviour
2. GREEN    write the minimum code to make it pass
3. REFACTOR clean up while everything stays green
```

A new test must be red on the code from before the change. A test that already passes proves nothing. For calendar and points rules, the golden vectors exported from the TypeScript tests (`apps/api/tests/Huishoudplanner.Domain.Tests/Vectors/*.json`) exist before the C# does.

## Test projects (plan section 3.1)

```
apps/api/tests/
  Huishoudplanner.Domain.Tests/         pure unit tests, golden vectors
  Huishoudplanner.Application.Tests/    use cases with fake or mocked driven ports
  Huishoudplanner.Architecture.Tests/   ArchUnitNET: dependency direction, naming, no driver outside its adapter
  Huishoudplanner.Integration.Tests/    Mongo adapters and the HTTP pipeline against Testcontainers
    Fixtures/                           shared container, API factory, HTTP helpers
    Persistence/
    Api/
```

Integration tests share one `mongo:8` single-node replica set per test assembly (`MongoDbBuilder().WithReplicaSet("rs0")`, data on tmpfs); each test class carves out its own uniquely named database. Run the suites with [scripts/run-tests.ps1](./scripts/run-tests.ps1) or `dotnet test` in `apps/api`; Docker must be running for the integration project.

Worked examples: [unit test](./examples/unit-test-template.cs) · [integration test](./examples/integration-test-template.cs) · [API integration test](./examples/api-integration-test-template.cs).

## Key rules

1. **xunit.v3** on the Microsoft Testing Platform runner, not v2; no `Microsoft.NET.Test.Sdk`. Architecture tests use `TngTech.ArchUnitNET.xUnitV3`.
2. **AwesomeAssertions** `Should()`, not FluentAssertions. **Moq** for driven ports.
3. **Test naming**: `MethodName_Scenario_ExpectedResult`.
4. **One behaviour per test**; several assertions about one concept are fine.
5. **Mock driven ports only**, never a domain entity or value object.
6. **`IAsyncLifetime`** with `ValueTask InitializeAsync()` and `ValueTask DisposeAsync()`.
7. **Own state.** A unique database per test class on the shared container, fixtures created through the adapters, own port per host. A test that depends on another test's leftovers or a running stack is broken.
8. **Time is fixed.** Use `FakeTimeProvider` (or a fixed `TimeProvider`) and fixed day keys; never the machine's date or timezone. DST cases are part of the contract.
9. **Arrange-Act-Assert**, visibly separated.
10. **Business rules belong in domain tests**: calendar, cycles, due, points, bonuses, badges, plan validation.
11. **Cover every `OneOf` variant**: the success path and each error variant, including that a no-op writes nothing and that a failed transaction leaves neither entity nor audit entry.
12. **Audit.** Application tests assert the audit input of every state change; the audit coverage test walks every v2 write endpoint.
13. **Thread the cancellation token** from `TestContext.Current.CancellationToken` through every awaited call.
14. **Never call real AI providers, notification endpoints or a real installation.** Use the mock provider and fakes behind their ports.
15. **End-to-end browser tests** are the Playwright suite in `apps/web/e2e`, not part of these .NET projects.

## Scripts

| Script                                   | Purpose                                                                                                      |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| [run-tests.ps1](./scripts/run-tests.ps1) | Run the .NET suites (`-Layer all\|domain\|application\|architecture\|integration`), optionally with coverage |
