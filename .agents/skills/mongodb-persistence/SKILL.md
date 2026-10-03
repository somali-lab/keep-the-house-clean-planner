---
name: mongodb-persistence
description: MongoDB persistence patterns for the .NET API in apps/api. BsonClassMap, ObjectId ids, single-node replica set, entity plus audit in one transaction, idempotent indexes and migrations, bounded reads, Testcontainers. Use when creating or reviewing the Mongo adapter, a ForStoringXxx implementation, an index, a migration or a transaction.
---

# MongoDB persistence for the .NET API

Adapted from the `mongodb-persistence` skill of the dark-factory repository (plan decision D13). Two deliberate deviations from that skill apply here: ids stay `ObjectId` (D11) and MongoDB runs as a single-node replica set so that an entity write and its audit entry are one transaction (D12).

## Authoritative sources

- [docs/plans/dotnet-rewrite.md](../../../docs/plans/dotnet-rewrite.md): §3.5 persistence, §3.2 ports, D2, D11, D12.
- [docs/huishoudplanner-requirements.md](../../../docs/huishoudplanner-requirements.md) §3: collections, fields and indexes. `apps/server/src/data/db.ts` is the reference index list until the switch.
- `docs/adr/` records on MongoDB, audit and transactions (ADR-0004, ADR-0008, ADR-0015, ADR-0020).
- `hexagonal-arch-dotnet` skill: adapter boundaries and port naming. `xunit-tdd-workflow` skill: test setup.

## When to use this skill

- Writing an adapter that implements a `ForStoringXxx` driven port.
- Mapping a domain type to a document.
- Adding a query, an index, a migration or a transaction.
- Writing an integration test against a real database.

## Core rules

1. **Only `Adapters.Mongo` references `MongoDB.Driver`.** Domain and Application never see a driver type; this is the only place that performs writes. ArchUnitNET enforces it.
2. **`BsonClassMap` only.** No `[BsonElement]`, `[BsonId]` or other attributes on domain types. All maps register in one idempotent `Register()` call from the adapter's `DependencyInjection.cs`.
3. **`SetIgnoreExtraElements(true)`** on every map, so a document written by the Node application (or a newer process) stays readable.
4. **Ids are `ObjectId`** in storage and 24-character hex strings in the API (D11). Never `Guid` or ULID. One converter per adapter boundary maps between the two; existing documents and their ids stay unchanged.
5. **Existing collections and fields stay as they are** (D2). Do not rename a field or change a document shape the Node application cannot read; rollback after the switch depends on it.
6. **Every state change is one transaction: entity write plus audit entry** (D12). The use case runs through `ForRunningTransactions`, which wraps an `IClientSessionHandle`; the stores and the audit writer take part in that session. Nothing writes an entity without its audit entry, and the audit coverage test walks every v2 write endpoint.
7. **A write that changes nothing writes and audits nothing** (ADR-0004). Detect the no-op before the write; do not rely on a replace that matched but modified nothing.
8. **Plan activation is serialisable.** Check and write in one transaction and write a shared guard document (for example a version field on the plan or settings document) in that transaction: snapshot isolation allows write skew, so two activations only conflict when they write the same document (ADR-0008 as amended, ADR-0021).
9. **Write conflicts are values.** Filter a write on the id and on the state the use case read (for example the occurrence status or the activation preview token), and tell `NotFound` apart from `ConflictError` when nothing matched. A transient transaction error is retried by the runner; anything else that is an infrastructure failure becomes `PortError`.
10. **Errors are values.** `NotFound`, `ConflictError`, `PortError` are returned, never thrown. Every catch has a filter that states what an infrastructure failure is, and the resulting `PortError` carries no connection string or other configuration value.
11. **Bounded reads.** Count, filter, sort, project and page on the server with `limit` and `cursor`. Never load a collection to count or filter it in memory, and make sure an index serves every query you add.
12. **Indexes and migrations are idempotent and named.** The index ensurer creates today's index list at startup and drops nothing the Node application relies on. Migrations are recorded in the `migrations` collection; the legacy slot index drop and the occurrence room snapshot backfill are the first entries.
13. **Idempotency keys** (`requestId`) on the three writes that have them (occurrence, one-off, redemption) are stored and replayed with the same semantics as today; a unique index backs each one.
14. **Every call is traced.** One `ActivitySource` for the adapter, one child span per adapter call, and the driver's own `MongoDB.Driver` source is exported. Secrets never appear in spans or logs.

## Collections

The collection names, fields and indexes are defined in requirements §3; keep that the single source and do not maintain a second list here.

## Anti-patterns

| Don't                                                        | Do instead                                                                                                        |
| ------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------- |
| Bson attributes on a domain type                             | `BsonClassMap` registration in the adapter project                                                                |
| Entity write and audit write as two separate operations      | One transaction through `ForRunningTransactions`                                                                  |
| Write and audit an unchanged entity                          | Detect the no-op first; write and audit nothing                                                                   |
| `Guid` or ULID ids                                           | `ObjectId` in storage, 24-hex in the API                                                                          |
| Throw on not-found                                           | Return `NotFound`                                                                                                 |
| Unfiltered `catch (Exception)`                               | Catch with a predicate for infrastructure failures                                                                |
| Load a list to count or sort it                              | Server-side count, filter, sort, projection, `limit` and `cursor`                                                 |
| Class maps registered in several places                      | One idempotent registration call                                                                                  |
| A test container per test class                              | One shared container per test assembly, a uniquely named database per class                                       |
| A test container that is not a replica set                   | `new MongoDbBuilder().WithReplicaSet("rs0")`; transactions only exist on a replica set                            |
| A test container writing to disk                             | Data directory on tmpfs: every majority write waits for the journal, and parallel classes queue behind each other |
| Tests asserting on a developer's database or wall-clock time | Own database, `FakeTimeProvider`, fixtures created through the adapters                                           |

## Completion checklist

- [ ] Driven port defined in `Huishoudplanner.Domain/Ports/Driven/`.
- [ ] Adapter is `internal sealed` in `Huishoudplanner.Adapters.Mongo/`.
- [ ] Class map registered once, with `SetIgnoreExtraElements(true)`; no attributes on domain types.
- [ ] Every state change writes entity and audit entry in one transaction; no-ops write and audit nothing.
- [ ] `NotFound` and `ConflictError` are distinguished; every catch is filtered; no error escapes as an exception.
- [ ] Reads are bounded and an index serves every new query; the index ensurer and any migration are idempotent.
- [ ] Integration tests against a real `mongo:8` replica set through Testcontainers, happy path and every error variant, including the rollback of entity and audit together.
- [ ] DI registration added in the adapter project.
