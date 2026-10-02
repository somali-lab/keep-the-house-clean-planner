# ADR-0008 — Optimistic activation preview

Status: Accepted

## Context

Changing the active four-week plan can replace many upcoming occurrences. A person needs to inspect those changes before confirming. Persisting a preview would create another mutable state record, while MongoDB transactions would require a replica set that the supported standalone deployment does not provide.

## Decision

The preview is read-only and projects the same current-and-next-cycle replacement rules used by activation. It returns inspectable removals, expected additions, and protected completed, skipped, moved, and ad-hoc occurrences, plus an opaque token for the relevant plan, settings, tasks, and occurrence state.

Activation recomputes the projection immediately before its first write and rejects a different token with HTTP 409. The client fetches a new preview and requires the person to review it again. Generated replacements remain limited to the current and next cycle.

## Consequences

- No preview collection or cleanup process is needed, and opening a preview creates no audit entries.
- A change committed before the activation recheck invalidates the token and causes no activation writes.
- The recheck and subsequent writes are not one MongoDB transaction. A concurrent edit after the recheck can still race with activation. This mechanism prevents confirming an already stale preview; it does not provide serializable activation across processes.
