# ADR-0014 — Badges as derived awards

Status: Accepted

## Context

An administrator defines badges with a rule and a picture, and people earn them from the same audited execution data as the points. Awards must not depend on the wall clock, recomputing must never award a badge twice, and an import must rebuild them. The deployment has no transactions (ADR-0008) and is one application container with one database (ADR-0005). Badge pictures are small, at most a few hundred kilobytes each.

## Decision

**Awards are derived from the executions and recomputed, not stored once when earned.** An award is a pure function of the active badge and the data, keyed per badge and person, and its moment comes from the data that crossed the threshold. It is inserted, moved or removed in place under the same per-database queue that serializes the points ledger (ADR-0011, ADR-0012), by a reconcile that follows the points reconcile and also runs when badges, tasks or statistics change, so recomputing is idempotent and an import rebuilds every award with the same moments.

**Badge images are stored in MongoDB**, as binary in the badge document, not as files on disk or a volume. They then travel with the existing backup and export and add nothing to restore.

## Alternatives considered

- **A running counter per person and badge, or an award stored once when earned,** drifts after an undo, a correction, an import or a badge created later, cannot award retroactively, and would be an extra fact that an import has to carry. The projection is rebuilt from the same data and cannot disagree with it.
- **Images on a volume or in object storage** add a second thing to back up, restore and export to an application that is one container with one database.
- **A separate collection for image bytes** adds an export and consistency rule for no gain, since listings never load the bytes.

## Consequences

- Awards follow the data: an undo, a correction, a purge or deactivating a badge can take an award away. This is a product default that the maintainer can still change at the price of storing awards as facts that must be exported.
- A failed live evaluation is repaired by the next reconcile.
- Pictures make documents, backups and exports larger by up to the per-image limit.
