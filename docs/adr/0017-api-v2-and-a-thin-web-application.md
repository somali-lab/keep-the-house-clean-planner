# ADR-0017 — API v2 and a thin web application

Status: Accepted

## Context

The v1 contract grew with the Node server: one `PATCH` with an action discriminator per resource, ad-hoc error shapes, and domain rules (cycle and week indexes, plan validation, default points, bonuses in force) computed in the browser from a shared package. Every rule that exists in two runtimes must be ported twice and can drift.

## Decision

The API is redesigned as `/api/v2` and the v1 contract is not reimplemented. Resources are plural nouns, non-CRUD actions are `POST` sub-resources with one endpoint per intent, every list is bounded with a cursor, and errors are RFC 9457 Problem Details whose `type` is a stable URN per error code. The OpenAPI 3.1 document is generated at build time, checked in, and a drift test fails when it changes unreviewed; the web client is generated from it.

The web application becomes thin. Every domain rule lives in the .NET domain, and the server delivers what the browser used to compute: calendar facts, plan validation, limits, default points, bonuses in force and reward progress. The browser keeps pure day-key string arithmetic and presentation. The shared package is deleted at the end of the rewrite.

## Alternatives considered

- Keep v1 and implement it in .NET: smallest web change, but it freezes a contract whose shape is the reason for the redesign.
- Keep the shared TypeScript rules and port them to C# as well: two implementations of every rule, kept equal only by tests.
- Compile the rules to WebAssembly for the browser: one implementation, but a build and runtime surface that is out of proportion for a household application.

## Consequences

- The plan editor depends on a server round trip for validation; it debounces and shows the same errors a save would return.
- ADR-0002's rules about identifiers and instants remain; only the transport of errors changes.
- The web application moves to v2 feature by feature, so a period exists where the web app on the integration branch no longer matches the Node server.
- A generated client turns any contract change into a reviewable diff of the OpenAPI document.
