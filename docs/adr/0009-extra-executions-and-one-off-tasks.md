# ADR-0009 — Extra executions and one-off tasks as ad-hoc occurrences

Status: Proposed

## Context

An occurrence is the only record of work that was planned or done. History, statistics, the due engine, the PDF, export and import, and the points ledger all read occurrences. Two kinds of real work have no place in that model: an extra execution of a task that the plan already has on the same day, and a one-off task that has no task record at all.

Two existing mechanisms block them. A unique index on `(cycleId, taskId, plannedDate)` covers every occurrence, and generation and nightly reconciliation rely on it to stay idempotent. And `taskId` is required. Relaxing the index also removes the accidental protection against a repeated click or retried request registering the same work twice, and the supported deployment has no MongoDB transactions to compensate (ADR-0008).

## Decision

Both kinds are ordinary occurrences with `origin: 'adhoc'`, so plan activation and reconciliation, which only replace untouched generated occurrences, leave them alone.

**The slot index becomes partial.** The unique key `(cycleId, taskId, plannedDate)` applies to generated occurrences only. A second partial unique index on a client-supplied `requestId` makes creating an ad-hoc occurrence idempotent. The data layer migrates the legacy index on startup, before the server and the scheduler start; with a single application process (ADR-0005) no write falls between the drop and the create.

**A one-off task is an occurrence with `taskId: null`.** Its name, duration and room live only in the snapshot fields that every occurrence already carries. No task document exists.

## Alternatives considered

- **Dropping or relaxing the unique index without a replacement** makes generation non-idempotent and lets a double click register work twice.
- **A sequence number in the unique key** needs a read-then-write counter that races without transactions, and still cannot tell a retry from a deliberate second execution.
- **A server-side time window that rejects a repeated identical create** confuses a quick deliberate second execution with a double click and does not survive a lost response.
- **A hidden task record for each one-off task** keeps `taskId` required, but every reader of tasks (task list, due engine, planner, plan validation, AI input, interval statistics, transfer) then needs a filter, and a missed filter leaks the one-off task into the central list. A nullable `taskId` limits the change to readers of occurrences, and the type system lists them all.
- **A separate executions collection** would force history, statistics, PDF, export and the points ledger to merge two sources.

## Consequences

Several executions of a task can coexist with its one generated occurrence on a day, while generation stays idempotent because its key is still unique where it matters. `taskId` becomes nullable in the shared contract, so every consumer must handle a one-off task, and the compiler finds the places that do not. The index change is a schema change, not a state change, and it is not audited. Rolling back to an older image after the upgrade needs a pre-upgrade export or backup, because the old full unique index cannot be rebuilt once extra executions share a slot (see `docs/RELEASING.md`).
