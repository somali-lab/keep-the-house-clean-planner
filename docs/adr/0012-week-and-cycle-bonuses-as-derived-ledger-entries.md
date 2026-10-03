# ADR-0012 — Week and cycle bonuses as derived ledger entries

Status: Accepted

## Context

A person earns a bonus for a finished week or cycle. A bonus is only final after its period has ended, and a late check-off, an undo, an administrator's correction, a statistics reset or an import can all change the occurrences of a period that has already ended. The result must be reproducible from the stored data and must never be awarded twice. ADR-0011 made the points ledger a keyed projection and gave it one reconcile as its single recomputation path. The deployment has no transactions (ADR-0008) and one application process (ADR-0005).

## Decision

Bonuses are derived ledger entries, keyed per person and period, and **only the reconcile writes them**. It evaluates every ended period after the execution entries, inserts the missing bonus entries and deletes the ones that are no longer expected, under the same lock as the execution entries (ADR-0011). No live write path touches a bonus, so a bonus has exactly one writer and needs no race handling beyond that lock.

## Alternatives considered

- **Live updates on every completion** would add a second writer that races the reconcile without transactions, and extra work on every check-off, to save at most a day of delay. A bonus is only final after its period ended, so the nightly run is the natural moment.
- **Freezing a bonus once it is written** is stable, but an undo or correction would leave a bonus the history no longer supports, and an import could not rebuild it. That is why ADR-0011 made the ledger a projection.

## Consequences

- A check-off never writes a bonus. A bonus appears after the next reconcile (the night after its period ends), and a correction inside an ended period shows at the next run, or at once through the administrator's recompute.
- A reconcile reads every occurrence, not only done ones, which is a few thousand small documents a year for a household.
- Because bonuses are a pure function of the data, the same inputs give the same bonuses after an import, a recompute or a run on another day.
