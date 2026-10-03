# ADR-0011 — Points ledger as a projection of executions

Status: Proposed

## Context

Every execution earns points for the person who did the work, and the points are kept in a ledger. Executions change after the fact: they are undone, corrected, deleted, reset or imported, and history recorded before the ledger existed must earn points without ever counting anything twice. Some ledger entries are facts that no occurrence says, such as a redemption of points for a payout. The deployment has no MongoDB transactions (ADR-0008) and runs one application process (ADR-0005). Every real state change is audited and a no-op writes nothing (ADR-0004).

## Decision

**The ledger is a recomputable projection, keyed per execution.** An entry of a derived kind is a pure function of one occurrence and carries a unique key such as `execution:<occurrenceId>`. It is inserted, updated in place or deleted to match that occurrence, and never compensated by a second entry, so there is at most one entry per execution however often it is computed, and a balance is always the sum of what is true now.

**One reconcile is the single recomputation path.** A single function makes the whole ledger match the occurrences: it inserts what is missing, updates what differs and deletes what is orphaned. It runs at startup, in the nightly job and after an import, under one per-database lock, so reconciliations never overlap and the same code serves backfill, repair and rebuild. Live writes sync only the entry of the execution they changed, in the same lock.

**Booked entries live in the same ledger.** An entry that is a fact in its own right, such as a redemption, is stored in the same collection as a non-derived kind. The reconcile only loads, compares and deletes derived kinds, so a booked kind is never touched by a recomputation. A signed amount lets one balance be computed over every kind.

## Alternatives considered

- **Append-only signed entries** (`+n` on complete, `-n` on undo, a pair on a correction) need the current net value per execution, read and then written, which can race without transactions and would double or lose points on a retry. No unique key says that a delta was already applied, so backfill and import would need special cases, and purging history would leave deltas behind. The audit log already holds the append-only story.
- **No ledger, balances computed from occurrences on every read** is simple, but gives bonuses and redemptions no place to live.
- **A separate collection for booked entries** would split one balance over two sources, and every reader of the ledger would have to merge them.

## Consequences

- Ledger and occurrences can drift for a short time, because an occurrence write can succeed while its ledger write is lost. The nightly reconcile repairs that within a day and records what it changed.
- The ledger is rebuilt on import rather than exported; only booked entries cannot be rebuilt and must travel in the export.
- Entries of a new derived kind need no new writer, only a new expectation in the reconcile; entries of a new booked kind need their own writer and no change to it.
- Removing history (a statistics reset) must remove the entries derived from it.
