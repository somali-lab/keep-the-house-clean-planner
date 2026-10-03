# ADR-0012 — Week and cycle bonuses as derived ledger entries

Status: Proposed

## Context

A person earns a bonus per calendar week and per cycle when they did everything, and an additional bonus when they did everything on time. Both amounts are configurable. Planned and ad-hoc work count; skipped work is not done. On time means completed before the end of that week for the week bonus, and before the end of that cycle for the cycle bonus. A bonus is only finalised after its period has ended. It must be reproducible with a fixed clock and an isolated database, and it must never be awarded twice.

ADR-0011 built the points ledger as a keyed projection of the occurrences and reserved room for these bonuses as entries of their own kinds. Several facts of the current system shape the rules.

**Weeks and cycles align.** Settings fix `weekStartsOn: 1`. The cycle anchor is always a Monday and a cycle is 28 days, so every cycle is exactly four calendar weeks and a week never straddles two cycles. The shared helpers (`mondayOf`, `addDays`, `cycleIndexFor`, `cycleStart`, `cycleEnd`, `fromDayKey`) do all calendar arithmetic on day keys in the household timezone; DST only matters where a day key becomes an instant.

**Cycles are drawn from the current anchor.** The nightly job re-dates every stored cycle with `cycleStart(index, anchor)`, so moving the anchor redraws past cycles too. ADR-0011 therefore stores no cycle index.

**An occurrence has two days.** `plannedDate` is the slot day and survives a reschedule. `date` is where the occurrence stands now: a drag moves it, and an administrator's `edit_completion` sets it. Overdue work stays on its own day until somebody drags it, and dragging overdue work to today is the documented daily practice (requirements §1, §4.4).

**Who an occurrence belongs to depends on its state.** Open and skipped work has an `assigneeId`, possibly `null` ("anyone"). Done work has a credited person, `completedBy ?? assigneeId` (ADR-0011). Completing unassigned work claims it, and a take-over makes the actor the assignee, so the occurrence forgets who it was planned for. "Check off for {assignee}" credits the assignee. A named third person is credited without changing the assignee. Only the audit log keeps earlier assignees, and business rules do not read it.

**Completion data can change long after the fact.** A late check-off, an uncomplete, an administrator's correction or deletion, a statistics reset and an import all change the occurrences of a period that has already ended.

## Decision

### The set of a person for a period

A **period** is a calendar week (Monday to Sunday) or a cycle (`cycleStart(i, anchor)` to `cycleEnd(i, anchor)`, any index, also negative). It is identified by its first day.

The **period day** of an occurrence decides which week and which cycle it belongs to:

- `plannedDate` for planned work (`recordedDone !== true`), generated or ad hoc;
- `date` for recorded work (`recordedDone: true`), which has no plan; an administrator's correction of its date moves it.

The **period owner** of a planned occurrence is the person it was planned for: `periodOwnerId` when set, else `assigneeId`. `periodOwnerId` is a new optional occurrence field that is **frozen** once: the first time an assign, claim, take-over or complete happens on an occurrence whose planned week (`plannedDate`) has already ended, it is set to the assignee at that moment, before the change (`null` when the work was unassigned), unless it is set already. The freeze is part of the same audited occurrence update, with no entry of its own, and goes through the data layer like every other field. A missing field means the assignee, so there is no migration, and nothing is frozen while the planned week is still running: the assignee at the end of the week is the owner. Assigning the person who already has the work changes nothing and freezes nothing.

The **set** of a person for a period is every occurrence placed with them whose period day lies in that period:

- **Recorded work** is placed with the credited person, `completedBy ?? assigneeId`. It never blocks (see below).
- **Open or skipped planned work** is the period owner's open item. Work without an owner (`null`) is in nobody's set.
- **Done planned work** is the owner's done item when the credited person (`completedBy ?? assigneeId`, the rule of the execution points) is the owner. When somebody else did it, it is **not done for the owner**, so it blocks both "done" and "on time" for them, and for the person who did it it is **non-blocking**: treated like recorded work, never needed, never late, and unable to make them eligible. Without an owner it is only non-blocking for the person who did it.

| Situation | Effect |
| --- | --- |
| Completed by the assignee, or "check off for {assignee}" | The owner's done item. |
| Take-over inside the planned week | The actor becomes the assignee and so the owner. It is the actor's done item and the old assignee has no item. |
| Take-over of overdue work from an ended week | The owner stays the person it was planned for: not done for them, and non-blocking for the actor. |
| Credited to a named third person | Not done for the owner, non-blocking for the third person. |
| Unassigned "anyone" work, open or skipped | Blocks nobody (**DEFAULT**). |
| Unassigned work completed while its week runs | It is claimed: the doer is the assignee, the owner and the credited person. |
| Unassigned overdue work, claimed or completed after the week ended | The frozen owner is `null`: non-blocking for the doer, blocks nobody. |
| Reassigned while open, inside the planned week | Counts for the assignee at the end of the week. |
| Reassigned while open, after the planned week ended | The week stays with the frozen owner. It does not move into the new assignee's ended week. |
| Extra execution or one-off task, recorded as done | In the credited person's set, never blocks and is always on time. |
| Planned extra execution or planned one-off task | Like any planned work of its assignee; unassigned blocks nobody. |
| Rescheduled from week N to week N+1 | Stays in week N and in week N's cycle (**DEFAULT**, see below). |

A person earns nothing for a period whose set is empty, or whose set holds only recorded work and non-blocking items. At least one planned occurrence of their own is needed (**DEFAULT**); recorded work still counts inside the set, but cannot make a person eligible on its own.

**Why the plan decides the period.** Measuring by `date` would let anyone erase a missed task from an ended week by dragging it to today, which is exactly what the interface asks people to do with overdue work. Measuring by `plannedDate` makes a period's set immune to moves of open work, so a finalised week only changes when its completion data changes. Pulling work earlier costs nothing, because completing early is on time. Pushing work into the next week costs the on-time bonus of the week it was planned in, and still earns "everything done" once it is completed.

**Why the owner is frozen.** **Decision (orchestrator, review of P10b):** helping with overdue work from an ended period must not penalise the helper or reward the person who missed it. Without the freeze, taking over Bram's missed task after the week ended would hand the work to Anna's set of that week, where it is late and would take back a bonus she had already earned, while Bram's set would no longer hold it and he would be paid for a week he did not finish. Remembering who the work was planned for, once, at the moment the week is over and the work first changes hands, keeps both finalised weeks as they were. It is listed as an open product question below.

### Done and on time

For a person, a period and its set:

- **Everything done**: every occurrence in the set has `status: 'done'`, no matter when it was completed. Open and skipped work is not done. Skipped work that is completed later is done.
- **Everything on time**: everything is done and every `completedAt` lies before the end of the period: `completedAt < fromDayKey(addDays(lastDay, 1), timezone)`, local midnight after the period's last day in the household timezone. Planned done work without `completedAt` (old data only) is done but not on time. Recorded work, and work that is only non-blocking for the person who did it, never blocks and always counts as on time, whatever its `completedAt` says: an administrator can correct the date of recorded work to before its completion instant.

The cut-off is an instant computed from day keys by the shared helper, so a DST week is 167 or 169 hours long and Sunday 23:59 local time is always on time. A week's cut-off and a cycle's cut-off differ, so work planned in week 1 and completed in week 2 of the same cycle is late for the week but on time for the cycle.

A period is **ended** when its last day is before today in the household timezone. A period that has not ended earns nothing, however complete it already is.

### Ledger entries

Four derived kinds join `execution` in `pointEntries`:

| Kind | Condition | Amount |
| --- | --- | --- |
| `bonus_week_done` | week ended, eligible, everything done | `weekDone` |
| `bonus_week_ontime` | week ended, eligible, everything on time | `weekOnTime` |
| `bonus_cycle_done` | cycle ended, eligible, everything done | `cycleDone` |
| `bonus_cycle_ontime` | cycle ended, eligible, everything on time | `cycleOnTime` |

Each kind is evaluated on its own; an amount of `0` produces no entry. The on-time condition implies the done condition, so the on-time bonus is additional to the done bonus.

- **Key:** `<kind>:<personId>:<periodStart>`, for example `bonus_week_done:65f0…:2026-09-28`. A cycle is keyed by its first day, not its index, following ADR-0011.
- **Fields:** `personId`, `amount`; `date` is local midnight of the period's last day, because the bonus is earned when the period completes, so a balance range that includes that day includes the bonus; `weekStart` is the Monday of that day; `periodStart` (new, `null` for executions) is local midnight of the period's first day; `occurrenceId` and `taskId` are `null`; `titleSnapshot` is empty, because the client labels a bonus by its kind and period; `source` is `recompute`.

A bonus entry is a pure function of the occurrences, the anchor, the timezone, today, the bonus schedule and the reset floor. It is inserted or deleted, never updated in place: person and period are part of its key, and the amount of an ended period cannot change (see Settings).

### Finalisation: only `reconcilePoints` writes bonuses

`reconcilePoints` gains a fourth step after the execution entries:

1. Read the stored bonus entries, before the occurrences, as for executions.
2. Read every occurrence with the fields above (`status`, `plannedDate`, `date`, `recordedDone`, `assigneeId`, `completedBy`, `completedAt`) and map it to the shared model.
3. Compute the expected bonus entries for every ended week and cycle that holds at least one occurrence.
4. Insert the missing entries and delete the stored entries that are no longer expected, as two writes: the deletes as one ordered bulk write with compare-and-set on the entry that was read, then the inserts as one unordered write. What was written is read back, so the summary only counts and lists the writes that happened: a delete that missed its compare-and-set and an insert that hit a duplicate key are left out.

An occurrence that cannot be read is counted in `skipped`. The stored bonus entries of every person it could count for (its assignee, its period owner and the person who did it) are left as they are in that run, and nothing is inserted for them, because their sets cannot be evaluated reliably. If step 4 fails, the summary of steps 1 to 3 and of whatever step 4 already wrote is still recorded, and the failure is rethrown after it.

No live write path touches bonus entries. A late check-off or a correction inside an ended period is reflected by the next nightly run (Monday 03:00 finalises the week that ended at midnight), at startup, after an import, or by an administrator's `POST /api/points/recompute`. Reconciliations never overlap (ADR-0011), so bonus entries have exactly one writer and need no race handling beyond the existing mutex. A run that changes nothing writes and audits nothing, so a bonus is never awarded twice.

**Corrections after finalisation.** The ledger follows the history:

- An uncomplete, an administrator's deletion or a correction of `completedAt` past the cut-off removes the affected bonus at the next run.
- A late check-off of the last open item creates the "everything done" bonus, but not the on-time one.
- A take-over or reassignment of overdue work moves the blocker to the other person.
- Moving the cycle anchor redraws the ended cycles. Their cycle bonuses are removed and recomputed for the new boundaries. Week bonuses do not depend on the anchor.

**Audit.** A reconciliation that changes anything still writes one summary entry (`points` / `recompute`). Its `meta` and the recompute response gain `bonusesCreated`, `bonusesRemoved` and `bonusChanges`: for each created or removed bonus entry, its key, person, amount and `created` or `removed`, at most 100 items (`MAX_POINTS_CORRECTIONS`), with `bonusChangesTotal` and `bonusChangesTruncated`. A nightly run touches a handful of entries, so the list normally holds all of them, and the history feed can show who earned or lost which bonus. Per-entry audit entries are not written, for the reason ADR-0011 gives: an import would otherwise bury the history.

**Statistics reset.** The reset deletes the derived kinds, `execution` and the four bonus kinds, never a non-derived kind (P10c):

- Starting over deletes all of them.
- Purging before a date deletes those dated before it. These are the bonuses of periods that ended before the boundary.
- The reset stores its boundary as `bonusFloor` in the settings, in the same audited reset entry (before and after), and the floor only moves forward. The expected entries never include a period that starts before the floor, so work that was dragged forward out of a purged period cannot pay that period out from what remains. Starting over sets the floor to today.
- A period that straddles the boundary keeps its entries until the next run, which removes them because the period starts before the floor.

`removedPointEntries` counts both kinds.

### Settings: a bonus schedule, not a snapshot

Settings gain `bonusSchedule: { from: DayKey, weekDone, weekOnTime, cycleDone, cycleOnTime }[]`. The amounts are integers from `0` to `1000`, the list is sorted by `from` and has unique `from` days, and a missing list means `[]`. The amounts of a period are those of the last row whose `from` is on or before the period's last day; before the first row, every amount is `0`. The default `[]` therefore leaves bonuses disabled, and `0` disables one kind.

`PATCH /api/settings` (`requireAdmin`, as today) accepts `periodBonuses: { weekDone, weekOnTime, cycleDone, cycleOnTime }`, the amounts that apply from now on:

- When they differ from the row in force today, the server writes a row with `from` = today in the household timezone, or replaces the row that already starts today.
- When they are equal, it writes and audits nothing.
- The change is audited as a settings `update` with the schedule before and after.

`GET /api/settings` returns `bonusSchedule`.

A row starts today and a period is only ended after its last day, so **a settings change never alters an ended period**. It needs no recompute, it is not retroactive, and turning bonuses on never awards the past. Unlike a snapshot taken when the nightly job happens to run, the schedule keeps every bonus a pure function of the stored data. A rebuild after an import, a recompute after a correction, and a run on another day all produce the same amounts.

The schedule travels in the settings of the export, together with `bonusFloor`. Export `schemaVersion` becomes `4`, and import accepts versions 1 to 4. An older file has no schedule, so it rebuilds without bonuses. Import rejects a schedule row whose `from` lies after today in the timezone of the file (`bonus_schedule_in_future`) and a floor after today (`bonus_floor_in_future`), because a later run would otherwise apply a row that nobody had set. `PATCH periodBonuses` is a compare-and-set on the schedule the new one was computed from: when two administrators save at the same moment, one wins and the other gets `409 bonus_schedule_conflict`. The settings card says that new amounts apply to the current week and cycle and everything after, and marks a row that has not started.

### Shared model

`packages/shared/src/bonuses.ts` holds the rules as pure functions over day keys, ISO instants and string ids, with no BSON:

- the period of a day (week and cycle);
- whether a period has ended;
- the on-time cut-off;
- the period day and the period owner of an occurrence, and where it counts (the sets it is placed in);
- the evaluation of a set;
- the amounts in force on a day;
- `expectedBonusEntries(items, { anchor, timezone, today, schedule, floor })`.

The server maps documents to this model, and the web client can reuse it for progress.

### Reading

- **`GET /api/points/balances`.** Each balance gains `bonusPoints`, the sum of the bonus entries in the range. `points` stays the total. `executions` counts only entries of kind `execution`; it currently counts every entry, which would be wrong once bonuses exist.
- **`GET /api/points/entries`.** The `kind` enum gains the four bonus kinds, and the view gains `periodStart: DayKey | null`.
- **The Points tab.** It shows a bonus column in the balance table. Bonus rows in the entry table are labelled by kind and period, for example "Weekbonus: alles op tijd, week 40" or "Cyclusbonus: alles gedaan, 7 sep – 4 okt", with an icon and text, not colour alone. Both catalogs get the labels.
- **Settings.** A "Bonussen" card for administrators sets the four amounts and shows since when they apply.
- **Progress for the current week and cycle.** `GET /api/points/progress` (no profile) evaluates the current week and cycle with the same shared model and writes nothing. Per active person it returns `{ total, planned, done, open, skipped, eligible }` and the amounts in force. The current week lies inside the current cycle, so this is one query. The Points tab shows it as "7 of 9 done this week". It is cheap, but it is the last slice and may move to P12 without affecting the rest.

## Alternatives considered

- **The period of `date`.** This is natural for planning ahead, but dragging an overdue item out of an ended week is the documented daily practice, and it would award that week's bonus afterwards. Allowing only moves made before the period ended needs the reschedule history, which only the audit log holds. Business rules do not read the audit log.
- **Done work stays in the original assignee's set, read from the audit log.** Business rules do not read the audit log. Copying the original assignee into a new field at every change would add a migration and split the "credit follows `completedBy`" rule that execution points use. The decision takes a narrower form of the same idea: one optional field, frozen once and only for work whose planned week had already ended, where a missing value means the assignee, so nothing is migrated and the credit rule of the execution points is unchanged.
- **Neutral for the owner when somebody else did the work.** This was the first default. It lets a person who missed work be rewarded for a week they did not finish, and lets the helper's late completion count against the helper. The frozen owner replaces it.
- **"Done" means done before the period ends.** "Everything done" would then equal "everything on time" for the week, and the two bonuses would not differ. Done at any time keeps an incentive to clear a backlog after the deadline has passed.
- **Freezing a bonus once it is finalised.** This is stable, but an uncomplete or an administrator's correction would leave a bonus that the history no longer supports, and an import could not rebuild it. ADR-0011 made the ledger a projection for exactly this reason.
- **Snapshotting the amount at finalisation.** The amount would depend on when the nightly job ran. An import or a full rebuild would re-award every period at today's amount, and enabling bonuses would pay out all of history. The schedule is deterministic and costs one small list in settings.
- **Live bonus sync after every write**, as for executions. That means a second writer racing the reconciliation without transactions, and work on every check-off, for at most one day less delay. The confirmed rule already finalises only after the period, so the nightly run is the natural moment.
- **Unassigned work blocks everyone.** "Anyone" would then punish the whole household for a task that nobody owned. It stays a product question.
- **One audit entry per bonus entry.** This is fine at night, but an import would write one per person, period and kind. The summary with `bonusChanges` gives the same detail at night and stays one entry for a bulk rebuild.

## Consequences

- A check-off never writes a bonus. The bonus of a week appears in the night after it ends, and a correction to an ended period shows by the next night, or at once through the administrator's recompute.
- A reconciliation now reads every occurrence, not only the done ones. For a household this means a few thousand small documents a year, read with a projection of seven fields.
- Bonus entries carry `periodStart`. `executions` in the balances counts executions only, and `bonusPoints` is new. Export `schemaVersion` becomes `4`.
- Moving the cycle anchor rewrites the cycle bonuses of past cycles, and the summary audit entry shows which. Week bonuses are unaffected.
- A statistics purge never awards a bonus: periods that start before its boundary are not evaluated any more, so a straddling period loses its bonus at the next run.
- Occurrences gain the optional `periodOwnerId`, and settings gain `bonusFloor`; both travel in the export (schema version 4). A person who is credited with work of somebody else's week has not "done" it for that person, also inside a running week: the owner is still blocked until they or somebody on their behalf finishes it.
- A partial period counts what exists: the first cycle after installation, a plan activated midway, or a vacation. A shorter set earns the full amount; nothing is pro-rated.
- **Open product questions,** answered here with defaults:
  - **Unassigned "anyone" work.** The default is that it blocks nobody. The alternative is that it blocks every member, or only the people available that day.
  - **Rescheduling into the next week.** The default is that work stays in the week of its plan. The alternative is that a move made before the original week ended moves the work, which needs reschedule history.
  - **Helping with overdue work (decision, to be confirmed by the maintainer).** The default is that taking over, claiming or finishing overdue work from an ended period does not penalise the helper and does not reward the person who missed it: the work stays with the period owner it was planned for, who is blocked until they finish it, and it is non-blocking for the helper. Alternatives: it counts for the person who finished it (which can take back a bonus the helper already earned), or it blocks nobody.
  - **Work done by someone else.** The default is that it is not done for the person it was planned for and non-blocking for the person who did it. The alternative is that it is neutral for the original assignee, which loses the incentive to finish one's own work.
  - **Eligibility.** The default is that at least one planned occurrence is needed. The alternatives are that any non-empty set qualifies, including recorded extras only, or that a minimum number of planned occurrences or minutes is needed.
  - **Partial periods.** The default is the full amount. The alternative is pro-rating by the number of days with generated work.
  - **Amount bounds.** The default bounds are `0` to `1000`, and every amount is `0`, which disables bonuses until an administrator sets them.
