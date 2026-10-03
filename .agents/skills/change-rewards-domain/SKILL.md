---
name: change-rewards-domain
description: Changes or reviews the points ledger, redemptions, week/cycle bonuses, the reward meter, or badge awards while preserving the single-writer reconcile and idempotent-key invariants. Use for any change to execution points, bonuses, redemptions, badges, or the reward meter anywhere in server or web code.
---

# Change the rewards domain

## Model invariants

- The points ledger, the week/cycle bonuses, and the badge awards are recomputable projections of the audited occurrence/execution data, not facts stored once (ADR-0011, ADR-0012, ADR-0014). A derived entry is keyed uniquely per execution, period, or badge-and-person (for example `execution:<occurrenceId>`, `<kind>:<personId>:<periodStart>`, `badge:<badgeId>:<personId>`) so recomputing never duplicates or loses it.
- **Two writers, one lock.** Execution entries are written live by `syncExecutionPoints` in `apps/server/src/domain/points.ts` on every complete, undo, correction, admin delete, retract and done-create, which then re-evaluates the badges of the person concerned (`apps/server/src/domain/badgeAwards.ts`). The reconcile (`reconcilePoints`/`reconcilePointsSafely`) recomputes execution entries, bonuses and badge awards from the data and repairs any drift; it runs at startup, in the nightly job (`apps/server/src/jobs/nightly.ts`), after an import, and through the administrator-only `POST /api/points/recompute`. **Bonuses have a single writer: only the reconcile writes them** (ADR-0012). Never add a third path that writes derived entries, and never let a live path write a bonus.
- Redemptions are a **booked** kind in the same ledger collection, never touched by the reconcile; they are facts in their own right and must travel in exports (ADR-0011, requirements 4.12).
- Every reconcile run, redemption booking, and live execution sync for the same database runs through the single in-process queue (`exclusively()` in `apps/server/src/domain/points.ts`), because there are no MongoDB transactions (ADR-0008) and one application process (ADR-0005). Do not add a write path to the ledger or badge-award collections that bypasses this queue.
- A run that changes nothing writes and audits nothing; a run that changes something writes one summary audit entry, never one per entry (ADR-0004 no-op rule applies here too).
- Bonuses and badge awards are evaluated after their period ended or their threshold data changed; no live check-off path writes a bonus directly, and a badge award's `awardedAt` always comes from the data (the threshold-crossing execution or bonus entry), never the wall clock.
- The reward meter (requirements 4.12 "Reward meter") stores nothing; it is read live from the ledger and occurrences on every request.

## Change workflow

1. Read requirements 4.12 (Points, Redemptions, Bonuses, Reward meter) and 4.13 (Badges) in `docs/huishoudplanner-requirements.md`, and ADR-0011, ADR-0012, and ADR-0014, before changing any rule.
2. Decide whether the change belongs in the reconcile (derived, recomputed data) or in a dedicated writer (booked facts like redemptions). Never make a derived kind writable outside the reconcile, and never route a booked kind through the reconcile.
3. If the change touches what counts toward a ledger entry, a bonus, or a badge rule, update the pure evaluation function first (`expectedExecutionEntry`, the bonus period logic, `evaluateBadgeRule`) with shared/unit tests for the boundary cases: undo, correction, statistics reset, and import rebuild all giving the same result as a fresh reconcile.
4. Keep every ledger/badge write inside `exclusively()` and give every new derived or booked kind a unique, stable key.
5. Update the reconcile summary shape and the audited `meta` together when a new kind of change needs to be reported (`bonusChanges`, `changes` in the badge summary).

Finish with the `verify-household-planner` skill; include `apps/server/test` cases for reconcile idempotency (run twice, same result) and for redemption balance/idempotency-key handling.
