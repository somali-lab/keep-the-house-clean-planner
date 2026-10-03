# ADR-0001 — Monorepo without a build step for server and shared code

Status: Superseded in part by ADR-0016 (the server no longer runs as TypeScript sources; the web application and the shared package keep this decision until the shared package is removed)

## Context

The server, the web client, and the rules they both depend on (API schemas, calendar arithmetic, plan validation) live in one repository. If the shared package were compiled to a `dist` directory, every change would need a rebuild before the server, the web client, and the tests all saw the same code. That synchronisation step is a reliable source of "works in tests, fails at runtime" defects, and it gives three consumers three chances to read a stale artefact.

## Decision

The repository is an npm workspace with three members: a shared contract package, the server application, and the web application.

The server and the shared package run TypeScript sources directly on the runtime, without a compile step. The shared package exports source files rather than build output. Only the web application is bundled, because a browser needs it.

Running sources directly constrains the TypeScript that may be used: no syntax that requires emit, explicit file extensions on relative imports, and type-only imports marked as such.

## Consequences

- There is exactly one copy of every shared rule, and the server, the web client, and every test read the same file.
- No build output to invalidate, and no ordering requirement between workspaces during development.
- The web bundle must import shared runtime values through narrow entry points. Importing through the package index pulls the full dependency graph of the shared package into the browser bundle.
- A newer language feature that cannot be erased at runtime is unavailable on the server until the runtime supports it.
