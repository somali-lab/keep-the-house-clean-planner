# ADR-0011 — Points ledger as a projection of executions

Status: Proposed

## Context

Every task gets a configurable points value, and the person who actually did the work receives those points. The points are kept in a ledger with one entry per execution. Undoing an execution, or an administrator's correction, corrects the points. Executions recorded before this decision also earn points, and computing them again must never count anything twice. Week and cycle bonuses (P10b) and currency conversion with redemptions (P10c) will use the same ledger later. This record shapes the ledger for them but does not set their rules.

Four facts of the current system constrain the design.

**`completedBy` already means "performed by", with one gap.** The audit actor is the profile that pressed the button. `completedBy` is the person credited. Several parts of the system show this:

- The completion dialog asks "Who completed this task?". "Check off for {assignee}" sends `completedBy = assigneeId`, and "I took over the task" sends `takeOver`, which sets both `completedBy` and `assigneeId` to the actor.
- The record-work dialog stores "who did it" as `completedBy`.
- The completion management page labels the field as "Uitgevoerd door" (performed by).
- Workload statistics count minutes per `completedBy`.
- The history feed renders a `complete` entry as "actor checked off for doer" whenever `meta.completedBy` differs from the actor.
- Requirements §2 says that the audit entry records both the actor who pressed and the person credited.

The gap is in `completeOccurrence`. When a request names neither `completedBy` nor `takeOver`, `completedBy` falls back to the assignee, even when the actor is someone else. Today and the week overview never send such a request, because they first show the choice. The Due page's "Done now" does send it for an occurrence planned today, so work done by the actor is silently credited to the assignee. The requirement in §4.4 that "completing offers an explicit choice" is already broken on that path, and with points it would also pay the wrong person.

**Executions change in several places:**

- complete and uncomplete;
- an administrator's `edit_completion` (date, time, person) and `DELETE`;
- `retract` and the ad-hoc create with `done: true`;
- the statistics reset, in both of its modes;
- an import.

Generation, activation, reschedule, skip and assign touch only open occurrences.

**There are no transactions** (standalone MongoDB, ADR-0008). Only one application process runs (ADR-0005). Every real state change is audited, and a no-op writes and audits nothing (ADR-0004).

**Task definitions change after the fact.** A task's duration, name or room can change, and a task can even be deleted. Statistics stay stable because occurrences snapshot these values when they are created.

## Decision

### Performed by: `completedBy`, chosen explicitly

`completedBy` stays the one field for "performed by", and points follow it. No new field is added.

The gap is closed on the server. When a `complete` request targets an occurrence whose stored assignee is a person other than the actor, the request must carry the choice. It carries either `completedBy` ("for the assignee", or any named person) or `takeOver`. Without either, the server rejects the request with `400 validation_error`, `{ field: 'completedBy', message: 'completion_choice_required' }`. Sending both is rejected by the schema with `completion_choice_conflict`. For unassigned work, and for work assigned to the actor, the actor stays the default.

The Due page's "Done now" opens the same completion dialog as Today when today's occurrence belongs to someone else. The dialog names who receives the points for each option.

**Older history.** Some done occurrences may have no `completedBy`. Such data can only come from old versions or imports, because every live path sets the field. These occurrences are attributed to their `assigneeId`, which is the rule the old server applied. If that is also `null`, they earn no entry and are counted as `unattributed` in the backfill summary. History itself is never rewritten.

### Task points and the snapshot on the occurrence

- **On the task:** `points` is an integer from `0` to `1000`. `0` means the task earns no points.
- **Default value:** the shared helper `defaultPointsForDuration(minutes)` returns `clamp(minutes, 1, 1000)`, so a task earns one point for every minute it takes. The upper bound (`MAX_TASK_POINTS`) is shared by the task schema, the snapshot, the one-off input and the form fields.
  - A new task gets this value when `points` is omitted on create. The task form fills it in from the duration until the person edits the field.
  - At startup, every existing task without `points` gets the same value. This is a schema migration and is counted in the backfill summary.
  - Changing `points` goes through the existing `PATCH /api/tasks/:id` (`requirePlanner`) and is audited as a task `update`.
- **On the occurrence:** `pointsSnapshot: number | null` is written when the occurrence becomes done. That happens on `complete` and on the ad-hoc create with `done: true`. The value is `task.points`, or for a one-off task its `pointsOverride` when it has one and the one-off rule otherwise. `uncomplete` and the "start over" statistics reset set it back to `null`, so a later check-off takes the value that applies at that moment. `edit_completion` keeps it. It is part of the occurrence audit diff. Missing on older data means not yet snapshotted.

**A changed task value never rewrites past points**, not even during a recomputation. A recomputation reads the snapshot, not the task. This is the same rule that keeps the workload statistics stable when a duration changes.

**Points of one-off tasks** (`taskId: null`). The record-work dialog has a points field for a one-off task, from `0` to `1000`, filled in with the default for the entered duration until it is edited by hand. `POST /api/occurrences/one-off` takes the optional integer `points` (the same bounds as a task). Without it, the one-off rule applies: `defaultPointsForDuration(durationMinutesSnapshot)`, so a one-off task of 30 minutes earns 30 points.

A chosen value is stored on the occurrence as `pointsOverride: number | null`. It is written when the one-off task is created and never changes afterwards, whether the work is recorded as done at once or planned and checked off later. `pointsSnapshot` is still the value that counts: it is set from `pointsOverride` (else the duration rule) when the occurrence becomes done, and an uncomplete followed by a new check-off therefore gives the same points. A missing `pointsOverride`, as on older data and on extra executions of a task, means the rule. The override is part of the occurrence's create audit entry and of the export; the reconciliation reads it when it fills in a missing snapshot. The value is snapshotted when the work is done, so a household setting can still replace the default rule later without touching past entries.

### The ledger: a keyed projection, not a stream of deltas

A new collection, `pointEntries`, holds the ledger:

```ts
interface PointEntryDoc {
  _id: ObjectId;
  key: string;                  // unique; 'execution:<occurrenceId>'
  kind: 'execution';            // P10b and P10c add their own kinds
  personId: ObjectId;
  amount: number;               // signed integer; an execution is >= 1
  date: Date;                   // local midnight of the occurrence's date
  weekStart: Date;              // local midnight of that week's Monday (mondayOf)
  occurrenceId: ObjectId | null;
  taskId: ObjectId | null;      // null for a one-off task
  titleSnapshot: string;        // the occurrence's taskNameSnapshot, so the entry stays readable
  source: 'live' | 'backfill' | 'recompute';  // the path that wrote the current value
  createdAt: Date; updatedAt: Date;
}
// indexes: { key: 1 } unique; { personId: 1, date: -1 }; { date: 1 }
```

**Derived entries.** An entry of kind `execution` is a pure function of one occurrence. The expected entry exists exactly when all of the following hold:

- the occurrence has `status: 'done'`;
- a person can be attributed (`completedBy ?? assigneeId`);
- `pointsSnapshot >= 1`.

The entry then holds that person, that amount and the occurrence's `date`. The `key` makes the ledger idempotent: there is at most one entry per execution, no matter how often it is computed. A correction changes the entry in place, and undoing the execution removes the entry. The balance is therefore always the sum of what is true now. No chain of compensating entries has to be added up, or kept consistent without transactions.

**Kinds that are not derived.** Entries that are facts in their own right, such as the redemptions in P10c, will carry their own kind and key and are never touched by a recomputation. The recomputation only manages derived kinds. A period bonus in P10b is derived again and will be keyed by person and period, for example `week_bonus:<personId>:<weekStart>`. `amount` is signed so that redemptions fit. `date` and `weekStart` give P10b its period keys without an extra calendar calculation. No cycle index is stored, because the cycle anchor can move, and a moved anchor would make every stored index stale.

**When entries change.** One domain function, `syncExecutionPoints(ctx, occurrenceId, reason)`, compares the expected entry with the stored one and then inserts, updates or deletes that one entry through the data layer. Each real change is audited, and a no-op writes and audits nothing. A duplicate key during an insert, caused by a concurrent sync, falls back to the update path. The function runs after every successful write that can change an execution.

| Event | Ledger effect | Audit `meta.reason` |
| --- | --- | --- |
| `complete` (any choice) | create for `completedBy` | `complete` |
| ad-hoc / one-off create with `done: true` | create | `recorded` |
| replay of a known `requestId` | sync runs, normally no-op | — |
| second `complete` (409) | nothing | — |
| `uncomplete` | delete | `uncomplete` |
| `retract` | delete | `retract` |
| admin `edit_completion` | update person, date or week, or no-op | `correction` |
| admin `DELETE /occurrences/:id` | delete | `correction` |
| statistics reset | bulk delete (see below) | in the reset entry |
| task `points` change | nothing; past snapshots stand | — |

A per-entry change is audited as `entity: 'points'` with `create`, `update` or `delete`. The entry's fields are recorded before and after, with `meta: { occurrenceId, reason }`. This follows the same pattern as the separately audited `lastCompletedAt` refresh. A check-off therefore writes at most one points entry.

**Statistics reset.** The ledger follows the history it is derived from.

- **Purging before a date** deletes the execution entries with `date` before the boundary.
- **Starting over** deletes every execution entry, and the reset also clears `pointsSnapshot`.

The existing single reset audit entry gains `removedPointEntries`, and no audit entry is written per point entry.

### Retroactive points: one reconciliation, idempotent

`reconcilePoints(ctx, trigger)` makes the whole ledger match the occurrences. It runs in three steps:

1. **Migrate the fields.** Tasks without `points` get the default value. Done occurrences without `pointsSnapshot` get `task.points`; when the task no longer exists, or for a one-off task without `pointsOverride`, they get the duration rule; a one-off task with `pointsOverride` gets that value. Both writes use filters on missing fields, so a second run matches nothing.
   - *Deliberate refinement:* a task whose points were filled in by this very migration never had a value of its own, so its current duration says nothing about the past. Its historical executions get `defaultPointsForDuration(durationMinutesSnapshot)`, the duration the occurrence had, instead of the points just derived from the task's current duration. A task that already had an explicit value keeps using it.
2. **Compute the expected entries.** The function loads every stored `execution` entry first, and then computes the expected execution entry for every done occurrence. An occurrence that cannot be read (an invalid date, for example) is skipped and counted as `skipped`, and its stored entry is left alone.
3. **Apply the differences.** It inserts the missing entries, updates the entries that differ and deletes the orphans, as bulk writes. Updates and deletes are compare-and-set on the entry that was read (`_id`, person, amount and date), so an entry that a live sync changed in the meantime is left for the next run. Reconciliations never overlap within the process: a second one waits for the first.

When anything changed, the run writes **one** summary audit entry: `entity: 'points'`, a fixed ledger id (like `SETTINGS_ID`), `action: 'recompute'`, the system or admin actor, and `meta`:

```ts
{ trigger, tasksDefaulted, snapshotsSet, created, updated, removed, unattributed, skipped, corrections, correctionsTotal, correctionsTruncated }
```

`corrections` lists, for each updated or removed entry, its key and its old and new person and amount, at most 100 of them; `correctionsTotal` counts all of them and `correctionsTruncated` says whether the list was cut. Such a change means the ledger had drifted from the occurrences, so it is worth keeping in detail. Entries that are only created are counted. When nothing changed, the run writes nothing and audits nothing.

The reconciliation runs in four places:

- **At startup,** after `ensureIndexes`, the room-snapshot backfill and `seed`, and before the HTTP server and the scheduler start. A failing run is logged and never keeps the application from starting. When settings do not exist yet, there is nothing to do. On the first start after the upgrade, it awards all existing history its points, with one audit entry. Every later start is a no-op.
- **After an import,** in the same request (`trigger: 'import'`). The ledger is not part of the export; see below.
- **In the scheduled nightly job** (`trigger: 'nightly'`). The manual `POST /api/jobs/nightly`, which planners may call, does not reconcile; that is the administrator's `POST /api/points/recompute`. Without transactions, an occurrence write can succeed while its ledger write is lost, for example on a crash or in an interleaving of two syncs. The nightly run repairs such drift within a day and reports it in `corrections`.
- **On request:** `POST /api/points/recompute` (`requireAdmin`, no body) runs it with `trigger: 'admin'` and answers `200` with the counts. It is covered by the write-route coverage test, which accepts either the audit entry or a provable no-op.

### API

All shapes live in `packages/shared/src/schemas/points.ts`. The server keeps BSON types, and the API uses ids, day keys and ISO instants (ADR-0002). Reads need no profile, as everywhere else.

**`GET /api/points/balances?from&to`.** Both parameters are optional day keys, and `from` must not be after `to`. The response is:

```ts
{ from, to, balances: [{ personId, points, executions }] }
```

It lists every active user, including those at `0`, and every inactive user with entries in the range. The order is stable: the order of the user list.

**`GET /api/points/entries?personId&from&to`.** `personId` is required, and so are both day keys. The range is at most 371 days (`range_too_large`). The response is `{ entries: PointEntryView[] }`, newest `date` first, then `_id`. The view is:

```ts
{ _id, key, kind, personId, amount, date: DayKey, weekStart: DayKey, occurrenceId,
  taskId, titleSnapshot, source, createdAt, updatedAt }
```

**`POST /api/points/recompute`** is described above.

Task create and update gain `points` (integer from `0` to `1000`), `POST /api/occurrences/one-off` gains the optional `points`, and the occurrence view gains `pointsSnapshot` and `pointsOverride`. `patchOccurrenceInputSchema` rejects `completedBy` together with `takeOver`. The audit schema gains the entity `points` and the action `recompute`.

### Transfer

The ledger is **rebuilt, not exported**. Every entry in P10a is derived. The data it is derived from (`tasks.points`, `occurrences.pointsSnapshot`, `completedBy`) travels in the export, and the audit log carries the history of the ledger. The import replaces the collections and then reconciles, as described above. Export `schemaVersion` becomes `3`, because tasks and occurrences gain fields. Import accepts versions 1, 2 and 3. For files of versions 1 and 2, the reconciliation fills in the missing fields with the same defaults as at startup. P10c introduces non-derived entries, so it will have to add those entries to the export.

## Alternatives considered

- **Append-only signed entries,** with `+n` on complete, `−n` on undo, and `−n A / +n B` on a correction. Each correction would need the current net value per execution, read and then written, which can race without transactions. A retried or interleaved write would double the points or lose them. Rebuilding the ledger from occurrences cannot reproduce such a stream, because there is no unique key that says "this delta has already been applied", so backfill and import would need special cases. Deleting history in a statistics purge would leave the deltas behind, or need compensating entries for history that no longer exists. The audit log already holds the append-only story of every value.
- **No ledger, only balances computed from occurrences on every read.** This is simple for P10a, but P10b and P10c need a place for bonuses and redemptions. A ledger with keys can hold those as kinds of their own, and a computed balance cannot.
- **A new `performedBy` field next to `completedBy`.** `completedBy` already means the doer in the user interface, statistics, history and requirements. A second field would split them and would need a migration that copies one into the other. The real problem was the implicit default, and that is fixed at its source.
- **Keeping the implicit "credit the assignee" default and fixing only the Due page.** Any other client, including the next feature, could then pay the wrong person without noticing. The server is where role and attribution rules are enforced.
- **Computing points from the current task value every time.** A planner who changed a task's points would then silently rewrite last year's balances, and a recomputation would no longer be stable. The snapshot repeats the existing duration rule.
- **Exporting the ledger.** Import would then have to validate that the ledger matches the occurrences, or accept a file where it does not. Rebuilding is shorter and cannot disagree.
- **An audit entry per entry during backfill or reset.** On the first start this would write one audit entry for every completion ever made, and it would bury the real history. Import and the statistics reset already summarise a bulk change in one entry.
- **Storing a cycle index on each entry.** The cycle anchor can move, and a moved anchor would make every stored index stale. P10b can derive the cycle from `date`, or key its own entries.

## Consequences

- **Explicit choice in the API.** A check-off of someone else's work without an explicit choice now fails with `400 completion_choice_required`, where it used to credit the assignee. The server test that asserts the old default is replaced by one for the new rule. A complete that waits in the offline queue, for an occurrence that was reassigned in the meantime, gets this `400` and is dropped like any other `4xx`.
- **More audit entries per check-off.** A check-off now writes up to three audit entries: the occurrence, the task's `lastCompletedAt` and the points entry. The history feed renders the points entry as one line.
- **Uncomplete and re-complete use today's value.** The re-completed work earns the task's current points, not the earlier snapshot. This is deliberate: the snapshot belongs to one completion.
- **Points disappear with the history they came from.** A statistics purge before a date also removes the points earned before that date. This is a product default; see the open questions.
- **Drift between occurrences and the ledger is possible** for at most a day, for example after a crash or racing requests. The nightly reconciliation repairs it and reports it in the audit log.
- **The first start after the upgrade** writes all historical points and one summary audit entry. Every later start writes nothing.
- **Version 3 exports.** An application older than this decision cannot import a version-3 export. Downgrading was never supported.
- **Open product questions,** answered here with defaults:
  - **One-off task points.** Decided by the maintainer: they are entered in the record-work dialog and default to the duration rule (see above). A fixed household setting remains possible later.
  - **Statistics purge.** The default is that the purge removes the points. The alternative is one non-derived `carry_over` entry per person that preserves the balances. That fits once P10c makes balances spendable.
  - **The default points formula and its bounds.** Decided by the maintainer: one point per minute, and the bounds are `0` to `1000`. An earlier draft of this record used one point per ten minutes and a maximum of `100`.
