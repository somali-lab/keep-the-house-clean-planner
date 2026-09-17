---
name: change-scheduling-domain
description: Changes or reviews four-week cycles, plan slots, generation, occurrences, rescheduling, due calculations, activation, workload validation, or planner drag/drop while preserving calendar and audit invariants. Use for scheduling-domain behavior anywhere in shared, server, or web code.
---

# Change scheduling behavior

## Model invariants

- A cycle is 28 days, begins on the configured Monday anchor, and contains week indexes `0..3`.
- Weekday values use `0=Sunday..6=Saturday`; UI ordering is Monday first.
- Calendar calculations use day keys and shared helpers. Convert to instants only at the server boundary with the configured timezone.
- `validatePlan()` is the shared authority for slot errors, workload summaries, interval counts, availability, and budget warnings. Client and server must not drift.
- A task may occur at most once per calendar day. Assigned unavailable days are hard plan errors; unassigned slots do not count toward a person's budget.
- Generated occurrences retain `plannedDate` when moved so drift stays measurable. Ad-hoc occurrences remain distinguishable from generated ones.
- Activation/re-generation must preserve completed, skipped, moved, and ad-hoc work according to the existing replacement rules.
- Due state is independent of grid placement: it derives from `lastCompletedAt` or creation date and the interval's `periodDays`.

## Change workflow

1. Search `docs/DECISIONS.md` for cycles, slots, generation, occurrences, due, or activation.
2. Write boundary cases as shared pure tests where possible: anchor edges, negative cycle indexes, Sunday ordering, DST transitions, interval mismatch, unavailable users, and duplicate task/day.
3. Update shared validation/time logic first when both server and web need the rule.
4. Update server generation/persistence with idempotency, deterministic ordering, and full audit coverage.
5. Update planner/today/week UI models and translated issue messages together.
6. Test activation and occurrence transitions through the API; use E2E only for the critical user journey.

Finish with the `verify-household-planner` skill across every affected workspace.
