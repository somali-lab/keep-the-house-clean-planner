# ADR-0009 — Extra executions and one-off tasks as ad-hoc occurrences

Status: Proposed

## Context

An occurrence is the only record of work that was planned or done. History, statistics, the due engine, the PDF, export and import, promotion suggestions and the activation preview all read occurrences, and gamification will award points per occurrence to the person who completed it.

Two kinds of real work have no place in that model yet:

- an **extra execution** of an existing task, possibly several on the same day and on a day where the plan already has that task;
- a **one-off task** that is done once and must not appear in the central task list, the due list, the planner or the AI input.

Two rules block the first kind. The unique index `(cycleId, taskId, plannedDate)` covers every occurrence, so a second record of the same task on the same day is rejected, and an ad-hoc record on a slot day even suppresses the generated occurrence of that slot. Generation and nightly reconciliation depend on that index to stay idempotent. The second kind is blocked because `taskId` is required.

Removing the uniqueness exposes a third problem. "Done now" is today two requests: create, then complete. A repeated click or a retried request would no longer fail on the index; it would silently register the same work twice. The supported deployment is a standalone MongoDB without transactions (ADR-0008), and the web app often runs on a plain-HTTP household address where `crypto.randomUUID()` is unavailable.

## Decision

### Records

Both kinds are ordinary occurrences with `origin: 'adhoc'`. Plan activation and reconciliation replace only open, untouched `origin: 'generated'` occurrences, so both kinds survive a plan switch without new rules.

- An extra execution keeps its `taskId`.
- A one-off task has `taskId: null`. Its name, duration and room live only in the existing snapshot fields (`taskNameSnapshot`, `durationMinutesSnapshot`, `roomIdSnapshot`, `roomNameSnapshot`), which are always written, with `null` for a missing room. No task document is created.
- `recordedDone: boolean` marks an occurrence that was created directly in the done state. It has no planned state to return to. Missing on older data means `false`.
- `requestId: string | null` holds the client's idempotency key. Missing on older data means `null`.

Both new fields and the nullable `taskId` are part of the shared occurrence schema, so they appear in API views and survive export and import.

### Indexes

The slot uniqueness applies to generated occurrences only, and a second index makes creation idempotent:

```ts
{ key: { cycleId: 1, taskId: 1, plannedDate: 1 }, name: 'occurrences_generated_slot_unique',
  unique: true, partialFilterExpression: { origin: 'generated' } }
{ key: { requestId: 1 }, name: 'occurrences_request_id_unique',
  unique: true, partialFilterExpression: { requestId: { $type: 'string' } } }
```

The data layer migrates the legacy index on startup, inside `ensureIndexes` and before the occurrence indexes are created:

1. List the indexes of `occurrences`; if the collection does not exist yet, skip the migration.
2. Drop every index whose key is exactly `{ cycleId: 1, taskId: 1, plannedDate: 1 }` and that is not the partial index named above. This includes the legacy default name `cycleId_1_taskId_1_plannedDate_1`.
3. Create the indexes from `INDEXES` as before.

A second run finds only the new index and changes nothing. The migration is safe on an existing database for three reasons. It runs before the HTTP server and the scheduler start, and the deployment runs a single application process (ADR-0005), so no write falls between the drop and the create. The new constraint is strictly weaker than the old one, so creating it cannot fail on existing data. No existing document has a `requestId`. Like the room-snapshot backfill, an index change is a schema change, not a state change, and it is not audited.

`insertOccurrencesIdempotent`, which ignores duplicate-key errors, accepts generated occurrences only. Ad-hoc creation uses a separate insert that reports a duplicate `requestId` instead of ignoring it. Every place that predicts or checks the generated slot key considers generated occurrences only: the occupied-key set in the activation preview and the expected-key check in nightly reconciliation. An ad-hoc occurrence on a slot day therefore no longer suppresses that slot's generated occurrence.

### API

`POST /api/occurrences` creates an ad-hoc occurrence of an existing task. The existing body stays valid, and two optional fields are added:

```ts
{ taskId: ObjectId, date: DayKey, assigneeId?: ObjectId | null,
  done?: boolean,          // default false
  requestId?: RequestKey } // /^[A-Za-z0-9_-]{16,64}$/
```

`POST /api/occurrences/one-off` creates a one-off task:

```ts
{ name: string /* trimmed, 1..120 */, roomId?: ObjectId | null, durationMinutes: int >= 1,
  date: DayKey, assigneeId?: ObjectId | null, done?: boolean, requestId?: RequestKey }
```

Both routes require a selected profile (`requireActor`), as creating an ad-hoc occurrence does today.

- **`done: false`** creates an open occurrence in a generated cycle. For an existing task, an omitted assignee means the task's default assignee. For a one-off task, an omitted assignee means unassigned. When the task already has an open occurrence that day, the response carries a non-blocking warning `task_already_planned`.
- **`done: true`** requires `date` to be today in the household timezone. It writes one document that is already done, with `recordedDone: true`, `statusBeforeCompletion: null`, `completedAt` set to now and `completedBy` set to the assignee. An omitted assignee means the actor, and `null` is rejected because someone did the work. For an existing task, the task's `lastCompletedAt` is refreshed.
- **Responses:** `201` with the occurrence view and `warnings` on creation. A repeated `requestId` whose stored record has the same `taskId` (or, for a one-off task, the same snapshot name), the same `date` and the same `recordedDone` returns `200` with the current state of that record. It writes nothing and audits nothing.
- **Errors:**
  - `400 validation_error`, with field messages `unknown_task`, `inactive_task`, `unknown_room`, `inactive_room`, `unknown_user`, `inactive_user`, `done_requires_today` or `done_requires_person`;
  - `409 cycle_not_generated`;
  - `409 idempotency_key_conflict`, when a `requestId` is reused for a different request.

  `409 occurrence_exists` is no longer produced.

Undoing recorded work uses a separate action. `POST /api/occurrences/:id/retract`, with `requireActor`, deletes an occurrence only when it is `origin: 'adhoc'`, `recordedDone: true` and `status: 'done'`. The deletion is audited as `delete` with `meta.reason: 'retract'`, and `lastCompletedAt` is refreshed. The response is `200 { retracted: true, id }`. Any other occurrence is rejected with `409 not_retractable`. A second retract returns `404 not_found`, which the client treats as already undone. `uncomplete` on a recorded-done occurrence returns `409 retract_required` rather than leaving an open record behind. Ad-hoc occurrences that were planned and completed later still use `uncomplete`.

Creation writes one `create` audit entry that contains the final fields, including status and completion, with `meta: { origin: 'adhoc', kind: 'extra' | 'one_off', recordedDone, requestId }`. One write produces one entry.

### The web client

The client derives a request key from `crypto.getRandomValues`, which also works on plain HTTP. It creates one key for each user intent: when the action renders or the dialog opens. Every retry and every rapid repeat click reuses that key, and a new key is created only after the request settles. Ad-hoc creation is not placed in the offline queue.

### Readers of occurrences

| Reader | Extra execution | One-off task (`taskId: null`) |
| --- | --- | --- |
| Due engine | A recorded completion refreshes `lastCompletedAt` and restarts the due clock. | Skipped; it has no due state. `nextOccurrence` ignores it. |
| Task list, planner, AI input | No change; these read tasks and plans. | Never present, because no task document exists. |
| Completion statistics | Count under their task. | By task: one row with key `null`, labelled in the client as one-off tasks. By room: `roomId` is `$ifNull: [task.roomId, roomIdSnapshot]`. By user: no change. |
| Workload, fairness, overview | Count like any occurrence. | Count like any occurrence. |
| Interval statistics | Count as real completions. | Excluded with `taskId: { $ne: null }`. |
| Deviation statistics | Excluded when `recordedDone`, because nothing was planned. | Excluded. |
| PDF | No change. | Uses the room snapshot, and the task lookup is guarded for `null`. |
| Promotion suggestions | Not affected; they read `origin: 'generated'` only. | Not affected. |
| Activation preview | Listed under the preserved ad-hoc group. The token includes it. | The same; the preview item's `taskId` becomes nullable. |
| Statistics reset, restart from today | `recordedDone` records are deleted instead of reset to open. | The same. |
| Room-snapshot backfill | No change. | Never matches, because snapshots are always written. The mapping is still guarded for `null`. |

The export `schemaVersion` becomes `2`. Import accepts versions `1` and `2`; a version-1 file is valid unchanged, because the new fields are optional and its `taskId` values are all set. Before anything is written, import rejects a file that contains duplicate generated slot keys or duplicate `requestId` values. Such a file would otherwise fail partway through the replacement, after the collections were already emptied.

## Alternatives considered

- **A hidden task record for each one-off task** keeps `taskId` required. However, every reader of tasks would need a filter: task list, due engine, planner pool, plan validation, AI prompts, interval statistics and transfer. A missed filter puts the one-off task in the central list. The nullable `taskId` limits the change to readers of occurrences, and the type system lists all of them.
- **A separate executions collection** leaves occurrences untouched. However, history, statistics, PDF, export and gamification would then have to merge two sources, and an extra execution would no longer be part of the week it belongs to.
- **Adding a sequence number to the unique key**, as in `(cycleId, taskId, plannedDate, seq)`, needs a read-then-write counter. Without transactions that counter can race, and it still does not tell a retried click from a deliberate second execution.
- **A server-side window that rejects a second identical create for a few seconds** confuses a deliberate quick second execution with a double click. It also does not survive a retry after a lost response.
- **`uncomplete` on recorded work** would leave an open occurrence on a past or current day, which later counts as missed. **Marking it skipped** would count as not done in statistics.
- **A `kind` value in `origin`** (`'oneoff'`) would touch every check of `origin` for no gain. `taskId: null` already identifies a one-off task, and `origin: 'adhoc'` already carries the survival rule.

## Consequences

- Several real executions of a task can coexist with its one generated occurrence on the same day. Generation stays idempotent, because its key is still unique where it matters.
- A planned ad-hoc occurrence no longer suppresses the generated occurrence on the same slot day. Both exist, and the client is warned. This changes earlier behaviour and is covered by a regression test.
- Each execution is one document with its own identifier, `completedBy` and audit trail, which gamification can award points to.
- `taskId` is nullable in the API contract. Every consumer has to handle a one-off task, and the compiler finds the places that do not.
- A key that is retried after its record was retracted creates the record again, because the deletion also removes the key. This needs a delayed retry after an undo; the audit log shows both events.
- Clients that call the API without a `requestId` get no protection against duplicates. The web client always sends one.
- An application version older than this decision cannot import a version-2 export, which is rejected on `schemaVersion`. Downgrade was never supported.
