# ADR-0016 — Hexagonal .NET backend on MongoDB, built in parallel

Status: Accepted

## Context

The Node server is replaced by an ASP.NET Core application on .NET 10. Three choices come with that: how the code is layered, whether the data moves to another store, and how identifiers look. The household runs the planner every day, so the new application must not endanger the existing data, and rules must not be able to leak into infrastructure code the way a flat route-and-helper layout allows.

## Decision

The application is a hexagon with one project per ring: Domain, Application, one adapter project per technology (Mongo, Http, Ai, Notify, Pdf, Jobs) and a Host that is the only composition root. Driving ports are `IXxxService`, driven ports are `ForXxx`, errors cross a port as `OneOf` values, endpoints are Minimal API groups, and there is no mediator library. Project references make a wrong dependency a compile error; ArchUnitNET tests guard what references cannot, such as a driver type outside its adapter.

MongoDB stays, with the existing collections, documents, field names and indexes. The new application is built next to the running one and switches over in one release, after both have run side by side against a copy of the production data.

Identifiers stay `ObjectId` in storage and 24-character hex strings in the API. This deliberately deviates from the house-style skill that prescribes ULID strings.

The part of ADR-0001 that says the server runs TypeScript sources without a build step no longer holds; its statement about the web application and the shared package stays until the shared package is removed.

## Alternatives considered

- A route-by-route strangler: both runtimes serve the same API for months, which forces the v1 contract to be reimplemented and every rule to exist twice.
- A relational store: better tooling for transactions, but it needs a data migration of every document and identifier for no behaviour the household gains.
- ULID identifiers: cleaner in .NET, but they change every stored reference and every URL, which makes rollback to the Node application impossible.
- A layered project without ring-per-project: fewer files, but the dependency direction is then a convention, which is the weakness ADR-0004 was written to avoid.

## Consequences

- Rolling back to the Node application stays possible until a later, separately planned change alters a document shape.
- Rules exist twice until the Node server is deleted; the parity script and golden vectors keep them equal meanwhile.
- More projects and ports than a small application strictly needs. That is the price of dependency rules the compiler checks.
- The skills copied into the repository carry this deviation and the replica set (ADR-0021) so they do not contradict the code.
