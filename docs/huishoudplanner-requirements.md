# Huishoudplanner — Requirements

Self-hosted household chore scheduler for a small household. One application container plus MongoDB, a repeating four-week cycle with drag-and-drop planning, a due-date engine alongside it, printable schedules, and an optional AI planning assistant.

This document describes what the system must do. It contains no history and no planning. Architectural choices and their rationale live in [docs/adr](adr/README.md); repository conventions live in [AGENTS.md](../AGENTS.md).

## 1. Scheduling model

Three concepts carry the whole system.

| Concept | Meaning |
| --- | --- |
| Template | A reusable four-week plan. One slot describes one intended execution of one task on one weekday of one cycle week. |
| Occurrence | A concrete, generated instance of a slot on a real calendar day, carrying what actually happened. |
| Due engine | An interval-based comparison that surfaces anything that has drifted past its interval, independent of the grid. |

> The template says when we intend to do it. The occurrence says what actually happened. The due engine compares the two and reports where reality has drifted.

Rules that follow from this:

- Editing an occurrence never changes the template unless the change is explicitly promoted.
- An unchecked occurrence stays on its own day, marked overdue. Nothing is moved automatically; a user reschedules by dragging.
- A task can legitimately appear both on its planned day and in the overdue list. The interface must make that relationship visible, or the duplication reads as a bug.

A cycle is 28 days, always starting on a Monday, counted from a configured anchor date. Weeks start on Monday.

## 2. Roles and identity

There is no authentication. A user picks a profile; that profile is attribution, not proof of identity. The installation is expected to run on a trusted network.

| Role | May do |
| --- | --- |
| `member` | Everything a participant needs: complete, uncomplete, skip, reschedule, assign, claim, create an ad-hoc occurrence or an extra execution of a task (also already done), and retract that recorded work. |
| `planner` | All of the above, plus tasks, cycle plans, promote suggestions, AI operations, and manually triggering jobs. |
| `admin` | All of the above, plus users, rooms, settings, badges, correcting or deleting a completion, purging statistics, importing data, and clearing the audit log. |

Requirements:

- The selected profile persists in the browser across sessions and is switchable from the header in one action, because one device is often shared.
- Every write carries the active profile, and that profile is the actor in the audit entry.
- A completion records the person credited, `completedBy` ("performed by"), who may be someone other than the active profile. The audit entry records both the actor who pressed and the person credited. The credited person is never implied for work of someone else: the request must name them or take the work over (see 4.4). Points follow the credited person (see 4.12).
- Role checks are enforced on the server. Hiding a control in the interface is a convenience, not the control.
- The identity layer is a single replaceable module so that adding real authentication later does not touch every endpoint.

## 3. Domain model

All documents carry `createdAt` and `updatedAt`.

### `users`

```
_id, name, color, active, role: 'admin' | 'planner' | 'member',
unavailableWeekdays: [0..6]              // 0 = Sunday
dailyBudgetMinutes: { weekday, weekend } // target load
maxDailyMinutes:    { weekday, weekend } // hard ceiling
browserNotifications: { enabled, times: ['HH:mm', ...] } // at most 6, unique, sorted, household timezone; absent reads as disabled
```

The number of users is configuration, not an assumption in the code. A fresh installation seeds the configured set.

### `rooms`

```
_id, name, sortOrder, active
```

Includes a room for house-wide work that belongs to no single space.

### `tasks`

The definition, not the doing.

```
_id, name, roomId, intervalKey, durationMinutes,
points: integer 0..1000,                 // missing on older data = default for the duration
defaultAssigneeId | null,                // null = either person
active, notes, tags: [string],
lastCompletedAt | null                   // denormalised, maintained on completion
```

- A duration estimate is mandatory. Workload balancing, budget validation and the AI all depend on it.
- `points` is what one execution of the task earns (see 4.12). `0` means the task earns no points.
- Tasks are deactivated, never deleted, so their history stays intact. Deletion is refused while the task is referenced by a plan or an occurrence.

### Intervals

Intervals are data in `settings`, not a hardcoded enum, so a household can add its own without a code change.

| key | label | perCycle | periodDays |
| --- | --- | --- | --- |
| `daily` | Dagelijks | 28 | 1 |
| `3w` | 3x per week | 12 | 2 |
| `2w` | 2x per week | 8 | 3 |
| `1w` | 1x per week | 4 | 7 |
| `2wk` | 1x per 2 weken | 2 | 14 |
| `4wk` | 1x per 4 weken | 1 | 28 |
| `quarter` | 1x per kwartaal | — | 91 |

`perCycle` is the number of slots the template editor expects per cycle; `null` means the task is not planned on the grid at all and is tracked by the due engine only. `periodDays` drives the due engine. An interval key that is still referenced by a task cannot be removed.

### `cyclePlans`

```
_id, name, active,
slots: [{ taskId, weekIndex: 0..3, weekday: 0..6, assigneeId | null, sortOrder }]
```

Exactly one plan is active at a time. Inactive plans are kept, so alternatives and AI proposals can be compared against the active one.

### `occurrences`

```
_id, taskId | null, cycleId, planId | null, date, plannedDate,   // taskId null = one-off task
assigneeId | null,
status: 'open' | 'done' | 'skipped',
statusBeforeCompletion: 'open' | 'skipped' | null,
completedAt | null, completedBy | null, skipReason | null,
durationMinutesSnapshot, taskNameSnapshot, roomIdSnapshot, roomNameSnapshot,
origin: 'generated' | 'adhoc',
recordedDone: boolean,           // missing on older data = false
requestId: string | null,        // missing on older data = null
pointsSnapshot: number | null,   // points of this execution, fixed when it became done; missing = not yet snapshotted
pointsOverride: number | null,   // points a one-off task was recorded with (0..1000); missing/null = the duration rule
periodOwnerId: id | null         // the assignee frozen when work of an ended planned week first changed hands; missing = the assignee
```

- `plannedDate` keeps the original slot date when an occurrence is dragged, so drift is measurable.
- Name, room and duration are snapshotted at creation. Changing a task's duration must never silently rewrite last year's workload statistics.
- `statusBeforeCompletion` exists so that undoing a completion restores the previous status rather than defaulting to open.
- `recordedDone` marks an occurrence that was created directly in the done state (an extra execution that already happened). It has no planned state to return to, so undoing it deletes it instead of reopening it.
- `requestId` is the client's idempotency key of an ad-hoc creation; a repeated request with the same key does not create a second occurrence.
- `taskId` is `null` for a one-off task (see 4.4): it has no task record, and its name, duration and room live only in the snapshot fields, which are always written (`null` for a missing room).
- `pointsSnapshot` is written when the occurrence becomes done and set back to `null` when it is uncompleted (see 4.12). It is part of the occurrence's audit diff.
- `periodOwnerId` is written once, in the same audited update, the first time an assign, claim, take-over or complete happens on a planned occurrence whose planned week (`plannedDate`) has already ended: it then holds the assignee of that moment before the change (`null` when it was unassigned). Week and cycle bonuses count the work for that person (see 4.12). Nothing is frozen while the planned week is running, and a missing value means the assignee, so no migration is needed.

### `pointEntries`

The points ledger: one entry per execution that earned points, one entry per week or cycle bonus that was earned, and one entry per redemption that was booked (see 4.12).

```
_id,
key: string,                     // unique; 'execution:<occurrenceId>', '<kind>:<personId>:<periodStart>' or 'redemption:<_id>'
kind: 'execution' | 'bonus_week_done' | 'bonus_week_ontime' | 'bonus_cycle_done' | 'bonus_cycle_ontime' | 'redemption',
personId,                        // the person credited, or the person who redeemed
amount: integer,                 // signed; an execution or a bonus is >= 1, a redemption is <= -1
date, weekStart,                 // local midnight of the occurrence's date (a bonus: the last day of its period; a redemption: the day it was booked) and of that week's Monday
periodStart | null,              // local midnight of the first day of the week or cycle of a bonus; null for an execution
occurrenceId | null, taskId | null,   // taskId null = one-off task; both null for a bonus
titleSnapshot: string,           // the occurrence's task name, so the entry stays readable; empty for a bonus or a redemption
source: 'live' | 'backfill' | 'recompute',
note | null,                     // redemption only: free text of at most 200 characters
centsPerPointSnapshot,           // redemption only: cents one point was worth when it was booked
currencyCodeSnapshot,            // redemption only: the household currency when it was booked (missing on a booking from before it was kept)
requestId | null                 // redemption only: idempotency key of the booking request
```

- An entry of kind `execution` is a pure function of one occurrence; it is changed in place or removed, never compensated by a second entry.
- An entry of one of the four bonus kinds is a pure function of the occurrences of one person in one period, the cycle anchor, the timezone, today and the bonus schedule (see 4.12). It is only inserted or deleted, never updated; a cycle is keyed by its first day, not by an index, because the anchor can move.
- An entry of kind `redemption` is booked, not derived (ADR-0013): a person gave up points on a day, which no occurrence says. A reconciliation never reads, updates or deletes it. It is created by a booking and removed by an undo or a statistics reset, and it keeps the conversion factor and the currency of its own moment. The request key is bookkeeping for retries and never appears in an audit entry.

### `badges`

Badge definitions (see 4.13).

```
_id, name,                        // 1..60 characters, trimmed
description,                      // at most 200 characters; '' when none
rule: { type: 'executions' | 'minutes', taskIds: [id], threshold }   // taskIds empty = every task, a one-off task included
    | { type: 'onTimeWeeks', threshold },
active: boolean,
exampleKey | null,                // set on an example badge; unique when a string
image: { data: Binary, contentType: 'image/png' | 'image/jpeg' | 'image/webp', size, hash } | null   // at most 256 KB; hash is SHA-256 in hex
```

- `threshold` is an integer from 1 to 100000 (1000 for `onTimeWeeks`) and a rule holds at most 500 tasks. The tasks are stored once and in sorted order, and compared as a set when a change is audited. There are at most 100 badges. A task id that does not exist is dropped when a badge is saved, and a rule that named tasks and names none is refused. An example badge keeps its `exampleKey` when it is renamed or edited.
- An image is stored as bytes, never as a path. The API never returns the bytes in a badge: a badge view carries `image: { contentType, size, hash, url }`, and `url` ends in `?v=<first 12 characters of the hash>`, so a changed picture has a new address.

### `badgeAwards`

The derived awards: one entry per badge and person who earned it (see 4.13).

```
_id,
key: string,                      // unique; 'badge:<badgeId>:<personId>'
badgeId, personId,
awardedAt                         // the moment the data first crossed the threshold
```

- An award is a pure function of the active badge, the done executions credited to the person (see 4.12) and, for `onTimeWeeks`, their on-time week bonuses. It is created, moved in time or removed in place and never compensated, so recomputing never awards twice. It is not exported; an import rebuilds it.

### `auditLog`

```
_id, at, actorId,
entity: 'task' | 'cyclePlan' | 'occurrence' | 'user' | 'room' | 'settings' | 'cycle' | 'import' | 'points' | 'badge' | 'badgeAward',
entityId,
action: 'create' | 'update' | 'delete' | 'complete' | 'uncomplete' | 'skip'
      | 'reschedule' | 'assign' | 'activate' | 'ai-apply' | 'reset' | 'recompute',
before, after,                  // changed fields only
source: 'ui' | 'api' | 'ai' | 'system'
```

### `settings`

```
cycleAnchorDate,                // a Monday
weekStartsOn: 1,
timezone,
vacationRanges: [{ from, to }],
intervals: [ ... ],
aiProvider: { type, endpoint, model, timeoutSeconds },
aiPrompts, aiPromptTemplates,
completionControl: 'circle' | 'thumb',
promoteThreshold,
dismissedPromotions: [ ... ],
bonusSchedule: [{ from, weekDone, weekOnTime, cycleDone, cycleOnTime }],  // sorted by `from`, unique days; amounts are integers 0..1000; missing = []
bonusFloor: day key,               // boundary of the last statistics reset; missing = none
currencyCode,                   // ISO 4217 with exactly two fraction digits; missing = 'EUR'
centsPerPoint,                  // integer 0..10000, cents one point is worth; missing = 0 (no money is shown)
rewardGoals: { weekPoints, cyclePoints }  // each an integer 0..100000 or null (automatic); missing = both null
```

- `bonusSchedule` holds the amounts of the week and cycle bonuses over time (see 4.12). The amounts of a period are those of the last row whose `from` is on or before the period's last day; before the first row every amount is `0`, so the default `[]` leaves bonuses disabled. `bonusFloor` is written by a statistics reset (see 4.7) and only moves forward: a period that starts before it is never evaluated for a bonus.
- `currencyCode` and `centsPerPoint` convert points to money (see 4.12). Money is always whole cents; it is shown with `Intl.NumberFormat` in the active locale. Only currencies with exactly two fraction digits are accepted (`Intl` `maximumFractionDigits` 2: EUR and USD yes, JPY and KWD no). With `centsPerPoint` 0 no amount of money is shown anywhere.
- `rewardGoals` holds the goals of the reward meter (see 4.12). `null` means the goal is automatic, `0` means no goal for that period. The goals travel in the export as part of the settings; an older file imports with both automatic.

### Indexes

- `occurrences`: `{ date, assigneeId }`, `{ status, date }`, `{ taskId, completedAt }`, `{ completedBy, status }`, `{ assigneeId, status }`, `{ plannedDate }`; unique `{ cycleId, taskId, plannedDate }` for generated occurrences only; unique `{ requestId }` where `requestId` is a string
- `tasks`: `{ roomId, active }`
- `pointEntries`: unique `{ key }`, `{ personId, date }` (date descending), `{ date }`; unique `{ requestId }` where `requestId` is a string
- `badges`: unique `{ exampleKey }` where `exampleKey` is a string
- `badgeAwards`: unique `{ key }`, `{ personId }`, `{ badgeId }`
- `auditLog`: `{ entity, entityId, at }` and `{ at }`

## 4. Functional requirements

### 4.1 Rooms and tasks

- Create, update and deactivate rooms; ordering is explicit, not alphabetical.
- Create, update and deactivate tasks, grouped by room.
- A task has a points value, an integer from 0 to 1000. When it is omitted on create, the server sets it to the default for the duration: one point per minute, between 1 and 1000 (`clamp(minutes, 1, 1000)`). A task from before points existed shows the same default. Points are changed through the existing task update and audited as a task update; a value outside 0 to 1000 or a fraction is rejected with a `validation_error` on `points`. The task form has a points field that is filled in from the duration, one point per minute, until it is edited by hand; clearing the field hands it back to the duration.
- Bulk operations per room: deactivate all, reassign all.
- A room that still holds tasks cannot be deleted.

### 4.2 Template editor

- A grid of four weeks by seven days, Monday first, with one column per user.
- Drag a task from an unplanned pool onto a day cell, and drag it between cells.
- Interval validation shows `placed / required` per task and flags any mismatch. This is a warning, not a block: the household may know better than the interval.
- Workload validation sums the planned minutes per user per day against that user's budget, and marks days over the budget and days over the hard ceiling differently.
- A drop onto a weekday the assignee is unavailable on is rejected, with an explanation.
- Per-week totals per user are visible, so imbalance is apparent before the cycle starts.
- The editor shows each person's planned minutes and the household total for every cycle week. A task-name search matches a case- and accent-insensitive substring and only changes what is visible in the editor; it never changes saved slots.
- The same validation rules run on the server for every plan write, so a plan that the editor would refuse cannot arrive through the API either.
- The editor identifies an inactive plan as a draft and explains that its slots do not appear in the week overview or My tasks until the plan is activated.

### 4.3 Generation

- Activating a plan generates occurrences for the cycle's 28 days.
- Generation is idempotent. Re-running it produces no duplicate generated occurrences, keyed on cycle, task and planned date. Only generated occurrences occupy a slot: an ad-hoc occurrence on a slot day does not suppress the generated one.
- Generation never creates an occurrence in the past. A cycle activated midway produces the remainder of the cycle only.
- A nightly job generates the upcoming cycle in advance, so the coming week is always visible.
- Saving slots in the active plan synchronizes future generated occurrences immediately. The resulting tasks appear on their assigned dates and for their assigned people when those dates are within the selected range in the week overview or My tasks, including after a page reload. Saving slots in an inactive draft does not change those overviews.
- Vacation ranges suppress generation on those dates. The due engine keeps counting the days.
- Activating a different plan replaces only future occurrences that are still replaceable — untouched, generated, open ones. Anything completed, skipped, rescheduled, or created ad hoc survives, because it records something that actually happened.
- Before a person activates a plan, show an inspectable preview for the current and next cycle: the open generated occurrences to replace, the occurrences expected from the new plan, and separate groups for completed, skipped, manually moved, and ad-hoc occurrences that remain (extra executions and one-off tasks; a one-off task has no task id and is shown by its name). Show counts plus each task's date and assignee. Previewing makes no changes. Both kinds of ad-hoc occurrence survive activation.
- An activation confirmation is tied to the state that was previewed. At confirmation, the server recomputes the preview; if the plan, relevant tasks, settings, or occurrences differ, it rejects the confirmation before any activation writes and requires a fresh review.

### 4.4 Daily use

- A today view lists the open occurrences for the selected profile, then the other members', then overdue items, and can be filtered per profile.
- When the today view shows everyone, its groups sit in two columns on screens wide enough for two readable columns; narrower screens keep one column.
- The view can browse forward a day or two without leaving the day-oriented layout, and shows which cycle week the day belongs to.
- No backlog is shown from before the cycle anchor date; there is nothing to be behind on yet.
- Complete and undo. Undo restores the previous status.
- Skip with an optional reason. A skipped occurrence does not roll over, but counts as not done for the due engine.
- When the active profile is not the assignee, completing needs an explicit choice, and the server enforces it: the request carries either `completedBy` (on behalf of the assignee, or of any named active person) or `takeOver` (the actor does it and becomes the assignee). Without either, the server rejects it with `400 validation_error` on `completedBy` (`completion_choice_required`); both together are rejected with `completion_choice_conflict`. Unassigned work and work of the actor itself default to the actor. The choices produce different history, and the person credited receives the points. Today, the week overview and the Due page's "Done now" for work planned today for someone else all ask this choice with the same dialog, which names who receives the points for each option.
  - *Deliberate change:* earlier versions credited the assignee when a request carried neither choice (ADR-0011).
- An unassigned occurrence can be claimed.
- Reschedule by dragging to another day. `plannedDate` is preserved. Dragging to a day the assignee is unavailable on is allowed but warned about, because reality outranks the plan.
- A week overview is the default landing view at every screen width, shows the whole week with drag-to-reschedule, and can collapse past days.
- The week overview can search by part of a task name and optionally show the cycle-week number on its cards.
- My tasks groups its sliding 1-, 2-, or 4-week period into seven-day blocks starting today. Each block shows its date range; each task shows its own cycle-week number even when a block crosses a cycle boundary.
- Filter choices throughout the app survive a hard reload. A single icon button in the top header, directly left of the language switch, resets the filters of the screen the person is on (today, week, my tasks, planner, tasks, statistics, completions and history) to their defaults and leaves the saved filters of every other screen untouched. It is disabled when the current screen has no filters or all of them are at their defaults, it has an accessible name and tooltip, and it announces the reset to assistive technology. One household member's saved choices are not silently applied to another member.
- An extra execution of an existing task is an ad-hoc occurrence, planned or already done, and only within a cycle that has been generated. Several executions of one task on one day coexist, next to the generated occurrence of that day. Planning or recording a task on a day where it already has an open occurrence is allowed and returns the non-blocking warning `task_already_planned`, also when it is recorded as done; the Extra Task dialog then offers to check off the planned occurrence first.
  - *Deliberate change:* earlier versions allowed at most one ad-hoc occurrence per task per day, and an ad-hoc occurrence on a slot day suppressed that slot's generated occurrence. Both rules are gone (ADR-0009).
- A one-off task is work that is done once and has no place in the central task list. It is an ad-hoc occurrence with `taskId: null`, created by `POST /api/occurrences/one-off` with a name (trimmed, 1 to 120 characters), an optional active room, a duration in whole minutes (at least 1), a date, an optional assignee (unassigned when omitted, the actor when it is recorded as done), `done`, `requestId` and optional `points` (a whole number from 0 to 1000, see 4.12). No task record is created, so a one-off task never appears in the task list, the due list, the planner or the AI input, and it does not take part in the due engine. The same rules as for an extra execution apply to `done`, the idempotency key and retract. An inactive or unknown room is rejected (`inactive_room`, `unknown_room`).
- "Done just now" is one request that records an extra execution already done: `done: true` is only allowed for today, completes it for the given person (the actor when omitted; "anyone" is rejected), and refreshes the task's `lastCompletedAt`. If the task is already planned today, the Due page completes that occurrence instead, asking the choice above when it is planned for someone else.
- Creating an ad-hoc occurrence takes an optional idempotency key (`requestId`). A repeat of the same request with the same key returns `200` with the stored record and writes and audits nothing; the same key for a different request is rejected with `409 idempotency_key_conflict`. The web client creates one key per user action with `crypto.getRandomValues`, keeps it across retries of that action, and does not queue these requests offline.
- Undoing recorded work is a separate action, `retract`, because there is no planned state to return to. It is an undo of today's work: it is only allowed while the record's date is today in the household timezone (`409 retract_not_today` otherwise), and the clients show the undo of recorded work only on that day. Deleting an older completion stays an administrator's correction. The occurrence is deleted, audited with the reason `retract`, and `lastCompletedAt` falls back to the newest remaining completion. A second retract answers `404`, which clients treat as already undone. Uncomplete on recorded work is rejected with `409 retract_required`; an ad-hoc occurrence that was planned and completed later still uses uncomplete. Today and the week overview mark recorded extra executions with an "Extra" badge (icon and text).
- Entry points. Today and the Tasks overview ("My Tasks") have an "Extra Task" action that opens one dialog with two clearly separated choices: an extra execution of an existing task (task) and a one-off task that does not appear on the task list (name, optional room, duration, points). The points field of a one-off task is a number field from 0 to 1000, filled in with the default for the entered duration (one point per minute) until it is edited by hand; an empty field hands the choice back to the default, and a value outside the range is refused next to the field. A second choice, "Already Done (Today)" (default) or "Plan", says when: already done is recorded as done today by the chosen person ("Done By"), who defaults to the active profile; plan creates an open ad-hoc occurrence (`done` omitted) on a chosen day, today or later, for a chosen active person or "Anyone" (unassigned). A day outside the generated cycles is refused by the server (`409 cycle_not_generated`), and the dialog shows that, and any other 4xx refusal, as a message under the date field. The hint that the task is still planned today and can be checked off applies to already done only. The dialog creates one request key per intent, ignores a repeated click while the request is pending, shows validation per field, is keyboard accessible, and is usable on mobile and desktop; both choices are shown with an icon, a radio button and text, not by colour alone. After recording, Today shows the record under finished and offers undo, which retracts; after planning, a confirmation "planned for {date}" is shown without undo, because a planned occurrence is ordinary open work, and the occurrence lists (Today, week overview, Tasks) refresh at once. The Due page offers the same dialog per task, opened on "extra" with that task chosen, next to "Schedule" and "Done now".

### 4.5 Due engine

For every active task:

```
daysSince = today - (lastCompletedAt ?? first known day)
ratio     = daysSince / intervalPeriodDays
```

- `ratio >= 1.0` marks the task due.
- `ratio >= 1.5` marks it overdue and surfaces it prominently.
- The result is a ranked list independent of the grid, which is what catches the task that has been quietly skipped for three cycles while the grid kept looking tidy.
- A task that has never been completed is measured from the start of the first cycle rather than from the moment the task record was created, so importing a task list does not immediately report everything as overdue.
- A recorded extra execution counts as a completion: it refreshes `lastCompletedAt` and restarts the due clock. Retracting it restores the previous value.

### 4.6 Promote to template

- When the same occurrence is moved the same way repeatedly, the system offers to update the template slot. The threshold is configurable and is at least two.
- A suggestion can be applied or dismissed. A dismissed suggestion stays dismissed until newer evidence appears.
- Applying a suggestion changes the active plan and is audited like any other plan edit.

### 4.7 Statistics

- A period is selected either as a number of recent cycles or as a range of calendar weeks.
- Reports: an overview, fairness between users, workload per user over time, completion rate per task, room and user, configured versus actually achieved intervals, deviations between planned and actual days, and points (see 4.12).
- Under the entries of the chosen person the Points report also shows that person's badges: the earned ones with the day they were earned, and the others with their progress (see 4.13).
- The Points report shows, of every person, the net points in the selected period, the number of executions, the bonus points, the redeemed points and the balance over the whole ledger (and, when a point is worth money, the value of the net and of the balance), and a table with the ledger entries of one chosen person. A redemption is listed with an icon and the text "Ingewisseld" (Redeemed), its note and, when it was booked while a point was worth money, what the points were worth then, in the currency of that booking. A bonus entry is labelled by its kind and period, for example "Weekbonus: alles op tijd, week 40" or "Cyclusbonus: alles gedaan, 7 sep – 4 okt", with an icon and the text, not by colour alone. It covers the period selected for the other reports: the current week and the weeks before it, or the current cycle and the cycles before it, both ends included. A person who is no longer active is listed when they earned points in the period.
- Every chart has an equivalent table, so the same numbers are available without interpreting a graphic.
- One-off tasks count like any other occurrence in workload, fairness, the overview and the completion totals per user. Per task, all one-off tasks share one combined row labelled "One-off Task". Per room they count under the room recorded on the occurrence, and one-off tasks without a room share one row without a room. They are left out of the interval report (they have no configured interval) and out of the deviation report, as are recorded extra executions, because nothing was planned.
- An administrator can reset statistics completely, or purge only the completion data before a chosen date. Starting over deletes recorded extra executions instead of reopening them, because they have no planned state to return to.
- A reset removes the points that belong to the history it removes (see 4.12). Starting over deletes every derived ledger entry, the executions and the four bonus kinds, and clears the points snapshots of the occurrences it reopens; it also deletes every redemption. Purging before a date deletes the derived entries dated before that date and the redemptions dated before it, and leaves the others (a purge can therefore leave a negative balance when a later redemption spent points that were earned before the boundary); a bonus is dated on the last day of its period, so these are the bonuses of the periods that ended before the boundary, and a period that straddles the boundary keeps its entries until the next reconciliation, which removes them. The reset stores its boundary as `bonusFloor` in the settings, in the same audited reset entry (before and after), moving it forward only; starting over sets it to today. A period that starts before the floor never earns a bonus, so work dragged forward out of a purged period cannot pay that period out from what remains. The number of removed entries is recorded as `removedPointEntries` in the one reset audit entry, with the redemptions among them as `removedRedemptions`; no audit entry is written per removed ledger entry. The badge awards are rebuilt from what remains (see 4.13): a purge revokes the awards that depended on the purged history and starting over revokes every award, with one `badgeAward` summary entry when anything changed. The badge definitions are never touched.

### 4.8 Completion management

- An administrator can correct a recorded completion — its date, its timestamp, and who is credited — or delete it entirely.
- Both are audited, including the values before the correction.
- The points follow the correction (see 4.12). Changing who is credited or the date moves the ledger entry of that execution in place, to the other person and to the week of the new date; deleting the completion removes the entry. Each is audited as a points entry with the reason `correction`, and a correction that changes neither person nor date writes nothing.

### 4.9 Audit trail

Every state change is recorded with who, when, which entity, which action, the changed fields before and after, and the origin of the change.

- Completing, undoing, skipping, rescheduling, assigning and claiming are each their own entry. Undoing is a new entry, never the removal of the original.
- Creating an extra execution writes one `create` entry with its final fields, including status and completion, and `meta: { origin: 'adhoc', kind: 'extra' | 'one_off', recordedDone, requestId }` (a one-off task uses `kind: 'one_off'`). Retracting recorded work is the one exception to the previous rule: the occurrence is deleted, so the entry is a `delete` with `meta.reason: 'retract'` that keeps the removed fields. A replayed request writes no entry.
- Task, room, user, plan and settings changes record old and new values per changed field. Setting the bonus amounts is a settings `update` that records the bonus schedule before and after.
- Applying an AI proposal is recorded with an AI origin, so a machine-made plan is always distinguishable from a hand-made one.
- Generation is recorded with a system origin, so an unexpected occurrence can be traced to the run that created it.
- A change to a ledger entry (see 4.12) is its own entry, entity `points` with `create`, `update` or `delete`, the entry's fields before and after, and `meta: { occurrenceId, reason }` where the reason is `complete`, `recorded`, `uncomplete`, `retract` or `correction`. A check-off therefore writes at most three entries: the occurrence, the task's `lastCompletedAt` and the points entry. The history feed renders a points entry as what the person gained or lost. Booking a redemption is a `points` `create` entry with `meta: { reason: 'redemption' }` and taking one back is a `points` `delete` entry with `meta: { reason: 'redemption_undone' }` that keeps the removed fields; the feed reads them as points exchanged, with the note and who booked it for whom, and as the redemption being undone. Changing the currency or the cents per point is a settings `update` with the old and new value. A redemption entry carries no request key in its audit fields.
- A badge (see 4.13) is its own entity: creating one is a `badge` `create` entry with its fields, a change is an `update` with the changed fields only (the rule's fields nested) and deleting one is a `delete` that keeps the removed fields. An image appears in these entries as `{ contentType, size, hash }` and never as bytes, so a changed picture is visible without storing it in the log. A change to an award is its own `badgeAward` entry with `create`, `update` (the moment moved) or `delete` and `meta: { reason, badgeName }`, the reason of the points sync that caused it (`complete`, `recorded`, `uncomplete`, `retract` or `correction`) and the name of the badge at that moment; the history feed reads it as a person earning or losing a badge. A bulk evaluation of the awards (a reconciliation, a change of a rule or of the active flag, a deletion of a badge, adding example badges, a statistics reset) that changes anything records one `badgeAward` summary entry with a fixed id, action `recompute` and `meta: { trigger, created, updated, removed, changes, changesTotal, changesTruncated }`, where `trigger` is `startup`, `nightly`, `import`, `admin`, `badge` or `reset` and `changes` lists at most 100 changes with their key, badge, badge name (so the history still names a deleted badge), person and `created`, `updated` or `removed`. Deleting a task removes it from the badge rules that name it as an audited `badge` `update` with `meta: { reason: 'task_deleted' }`. A run that changes nothing writes and records nothing.
- A reconciliation of the ledger (see 4.12) that changes anything records one summary entry: entity `points`, a fixed ledger id, action `recompute`, the actor that started it (the system for startup and the scheduled nightly run, the requesting profile for an import, a manual nightly run and the recompute endpoint), and `meta: { trigger, tasksDefaulted, snapshotsSet, created, updated, removed, unattributed, skipped, corrections, correctionsTotal, correctionsTruncated, bonusesCreated, bonusesRemoved, bonusChanges, bonusChangesTotal, bonusChangesTruncated }`. `trigger` is `startup`, `nightly`, `import` or `admin`. `corrections` lists, for the entries that were changed or removed, its key and its old and new person and amount, because such a change means the ledger had drifted from the occurrences; it holds at most 100 items, `correctionsTotal` counts all of them and `correctionsTruncated` says whether it was cut. `skipped` counts occurrences that could not be read and were left as they are. `bonusChanges` lists, for each week or cycle bonus that was created or removed, its key, person and amount and `created` or `removed`, with the same limit of 100 items, `bonusChangesTotal` and `bonusChangesTruncated`, so the history shows who earned or lost which bonus. Entries that are only created are counted. No entry is written per ledger entry, and a run that changes nothing writes and records nothing.
- A change that changes nothing writes nothing and records nothing.
- The log is append-only. No interface path edits an entry. An optional retention job removes entries older than a configured age and is the only exception.
- History is viewable per entity and as a global chronological feed, filterable by actor, entity type, action and date range, with a panel showing the referenced entity.

### 4.10 Notifications and scheduled jobs

- A nightly job generates upcoming occurrences, reconciles the points ledger with the occurrences (see 4.12), which also finalises the week and cycle bonuses of the periods that ended (the run at Monday 03:00 finalises the week that ended at midnight) and then makes the badge awards match (see 4.13), and, when configured, applies audit retention.
- A morning notification summarises the day: what is planned per person and what is overdue. It is suppressed when there is nothing to report.
- Supported server channels are none, an ntfy topic, and a Home Assistant webhook. The channel and its credentials come from the environment. These reach the household whether or not a browser is open, and stay a separate setting from browser notifications.
- Generation, the morning notification and audit retention can each be triggered manually from the settings screen, which is also how an installation is verified after a change.
- The scheduler can be disabled entirely, which is required for reproducible tests.

Browser notifications (ADR-0010) are a second, personal channel:

- They are shown only while the planner is open in a browser tab, also when that tab is not active. There is no service worker push and no delivery to a closed browser.
- Each person sets their own moments: up to six unique `HH:mm` times in the household timezone, plus an on/off switch. A person changes their own moments; an administrator can change anyone's. Users without stored moments read as disabled with no times.
- A notification summarises that person's open tasks for today and their overdue tasks, listing up to five task names and the number of others. When nothing is open, no notification is shown.
- A moment is delivered when the tab is open at that time or within ten minutes after it; earlier moments are not caught up. With several tabs open, at most one summary is shown per person, day and moment.
- The browser permission belongs to the device. The notifications page asks for it with an explicit button, shows whether it is not yet asked, allowed, blocked or unsupported, and can send a test notification. Notifications need a secure origin (HTTPS or localhost); on a plain-HTTP address the page reports that they cannot work and disables the permission and test buttons.
- Changes to the moments are audited like other user changes; a change that changes nothing is neither written nor audited.

### 4.11 Data management

- Full JSON export of the dataset, and import of such an export. The export carries `schemaVersion: 6`. Version 2 added `recordedDone`, `requestId` and a nullable occurrence `taskId` (extra executions and one-off tasks; ADR-0009); version 3 adds `tasks.points` and `occurrences.pointsSnapshot` (ADR-0011); version 4 adds the bonus schedule `settings.bonusSchedule` (ADR-0012); version 5 adds the redemptions in `collections.pointEntries` and `settings.currencyCode` and `settings.centsPerPoint` (ADR-0013); version 6 adds the badge definitions with their images in `collections.badges`, the images as extended-JSON binary (ADR-0014). The derived part of the points ledger is not exported: an import replaces the ledger, puts the redemptions of the file back and rebuilds the executions and bonuses from the imported occurrences, which also fills in the points of an older file with the same defaults as at startup (see 4.12). Import accepts versions 1 to 6; an older file is valid unchanged, has no redemptions (so the redemptions it replaces are dropped like every other replaced collection) and a file without a bonus schedule rebuilds without bonuses. A version 5 file without `collections.pointEntries` and a version 6 file without `collections.badges` are rejected. The badge awards are not exported: an import clears them and rebuilds them from the imported executions, with the same moments. A file older than version 6 has no badges, so the badges and awards it replaces are removed like every other replaced collection. Task ids in a badge rule that the file does not have are dropped (a rule that named tasks and names none is deactivated) and the others are sorted. A badge whose image size or hash does not match its bytes, whose image is not a PNG, JPEG or WebP of the declared type (an SVG included), or that repeats an example key, is rejected before anything is written (`image_size_mismatch`, `image_hash_mismatch`, `unsupported_image_type`, `image_type_mismatch`, `duplicate_example_key`). Importing a file older than version 6 while badges exist needs an explicit acknowledgement as well, `acknowledgeBadges=true`; without it the import is refused with `409 badges_would_be_removed` (with `count`) before anything is written, the number removed is `removedBadges` in the import result and audit entry, and the import screen reads the number from `GET /api/badges` and asks for its own tick. Importing a file older than version 5 while redemptions exist needs an explicit acknowledgement, `acknowledgeRedemptions=true`; without it the import is refused with `409 redemptions_would_be_removed` (with `count`) before anything is written. The number of removed redemptions is `removedRedemptions` in the import result and in the import audit entry. The import screen reads the number from `GET /api/points/redemptions/count`, names it in a warning when the chosen file is older than version 5, and enables the existing confirmation only after the person ticks that they understand those redemptions are lost. A redemption of a person who is not in the file, a duplicate redemption key and a duplicate redemption request key are rejected before anything is written (`unknown_user`, `duplicate_key`, `duplicate_request_id`). A file whose bonus schedule has a row that starts after today, or whose `bonusFloor` lies after today, is rejected with `validation_error` (`bonus_schedule_in_future`, `bonus_floor_in_future`) before anything is written. A later version is rejected.
- Before anything is deleted, import checks the file for duplicates on the unique indexes: two generated occurrences with the same `(cycleId, taskId, plannedDate)` and two occurrences with the same `requestId`. Such a file is rejected as a whole with `validation_error` (`duplicate_slot`, `duplicate_request_id`) and nothing is written, because the replacement would otherwise fail halfway, after the collections were emptied.
- Import validates the entire file against both the API shape and the storage shape before writing anything, reports what it will replace, and requires explicit confirmation.
- A nightly database dump is written to a mounted backup path by a separate container.

### 4.12 Points

Every execution of a task earns the points of that task for the person who did the work. The points are kept in a ledger with one entry per execution (ADR-0011).

- **Who is credited.** The person who performed the work: `completedBy`, or the assignee for older data without it. An occurrence that is not done, whose points are 0, or whose person cannot be attributed earns no entry.
- **Snapshot.** When an occurrence becomes done (complete, or recorded as done) it snapshots `pointsSnapshot`: the task's points, or for a one-off task, and for a task that no longer exists, the default for the occurrence's duration (one point per minute: a one-off task of 30 minutes earns 30 points). A one-off task recorded with its own points keeps them in `pointsOverride`, also when it is planned first and checked off later, and snapshots that value instead of the default. Uncompleting clears the snapshot, so a later check-off takes the value that applies at that moment. A correction of the completion keeps it. Changing a task's points never rewrites points that were already earned.
- **One entry per execution.** The entry `execution:<occurrenceId>` holds the person, the amount, the date and the week of the execution. Completing twice, replaying a request key or repeating a correction never produces a second entry.
- **Corrections follow the history.** Check-off and recording create the entry; uncomplete, retract and an administrator's deletion remove it; an administrator's correction of the person or the date updates it in place. A task points change does nothing to past entries.
- **Audit.** Every real change of the ledger is audited (see 4.9); syncing an entry that already matches writes and audits nothing.

- **Retroactive points.** One reconciliation makes the whole ledger match the occurrences, idempotently. It first migrates the fields: a task without points gets the default for its duration, and a done occurrence without a snapshot gets the task's points, or the default for its duration when the task no longer exists, it is a one-off task without `pointsOverride` (one with `pointsOverride` gets that value), or the task's points were just filled in by this migration (the duration the occurrence had then counts, not the task's current one). It then computes the expected entry of every done occurrence, and inserts the missing entries, updates the entries that differ and deletes the entries without an occurrence, as bulk writes. A done occurrence without `completedBy` is credited to its assignee; without an assignee too, it earns no entry and is counted as `unattributed`. History itself is never rewritten.
- **When it runs.** At startup before the server accepts requests, where a failing run is logged and never keeps the application from starting (on the first start after the upgrade it awards all existing history its points; every later start writes nothing), in the scheduled nightly job, which repairs drift between an occurrence and its ledger entry, for example after a crash, within a day (the manual nightly run, which planners may start, does not reconcile; only an administrator can, through the recompute endpoint), after an import, and on request through `POST /api/points/recompute` (administrators only). A run that changes something records one summary entry (see 4.9); a run that changes nothing writes and audits nothing, so running it twice never counts anything twice.
- **Balances.** The balance of a person is the sum of their entries in a period, and the entries of a person are listed for a period, newest first (see 8). Reading needs no profile.
- **Conversion to money.** An administrator sets the currency (`currencyCode`, ISO 4217, default `EUR`) and the cents one point is worth (`centsPerPoint`, an integer from 0 to 10000, default 0) in the settings (the Points value card next to the bonuses). Money is always whole cents and is shown with `Intl.NumberFormat` in the active locale. With 0 no money is shown. The balances show money at the factor in force now; a redemption keeps the factor of the day it was booked.

#### Redemptions

A person redeems points for a payout or a reward by booking a redemption (ADR-0013). There is no approval step: the booking itself is the record, and it is audited.

- **A booked entry.** A redemption is a ledger entry of kind `redemption` with a negative amount, dated today in the household timezone, with an optional note of at most 200 characters (trimmed; empty is none) and the conversion factor of that moment. A reconciliation never touches it: recomputing keeps every redemption, also when the points it spent are no longer earned.
- **Who books.** A person books for themselves (the active profile); an administrator can book for any active person. A member who names someone else gets `403 permission_denied`.
- **Never below zero.** The points of a booking are at least 1 and at most the person's balance over the whole ledger, not only the range on screen. A booking above that is refused with `409 insufficient_balance`. The check and the insert run in the same per-database queue as the reconciliation and the live execution sync, so within the one process two bookings never overdraw the balance and a sync never lands between the check and the insert. A balance can still turn negative afterwards when earned work that was redeemed against is undone; the redemption is never rewritten (see ADR-0013).
- **Idempotent.** A booking can carry a request key. A repeat with the same key, person, points and note answers `200` with the stored booking and writes nothing; the same key for anything else answers `409 idempotency_key_conflict`. The web client says "Deze inwisseling was al geboekt" instead of reporting a new booking when it gets `200`, and keeps an unfinished request key for ten minutes, so a deliberate identical redemption later is a new request.
- **Undo.** The person a redemption belongs to can undo it on the day it was booked (household timezone); an administrator can undo any redemption at any time. Later the owner gets `403 redemption_locked`, anybody else `403 permission_denied`.
- **Reading.** `earned` is the points of executions and bonuses in the range, `redeemed` the points redeemed in it, and the balance `points` is earned minus redeemed. When a point is worth money the balances also carry the money of each of the three. A redemption is listed among the entries with its note and the factor it was booked at.
- **Web.** The Points tab has a "Inwisselen" (Redeem) action for the active profile, where an administrator picks a person. The dialog shows the available balance, the amount with the money it is worth, and a note, refuses more than the balance, and is safe against a double click. Redemptions are listed with an icon and text, with an undo button where the profile may undo.

#### Bonuses

A person earns a bonus per calendar week and per cycle for doing everything, and a further bonus for doing everything on time. The bonuses are derived ledger entries that only the reconciliation writes (ADR-0012).

- **The set.** A period is a calendar week (Monday to Sunday) or a cycle (28 days from a Monday; any index, also negative). The set of a person for a period is every occurrence placed with them whose period day lies in it. The period day is the planned day (`plannedDate`) for planned work and the date for recorded work (extra executions and one-off tasks recorded as done), so dragging overdue work to today never removes it from the week it was planned in. The period owner of planned work is the person it was planned for: `periodOwnerId` when set, else the assignee. Open or skipped work is the owner's open item; unassigned work is in nobody's set, and completing it claims it for the person who did it. Done work is the owner's done item when the owner did it (or it was checked off on their behalf). When somebody else did it, by a take-over, a claim or a named third person, it is not done for the owner, who stays blocked until it is finished, and it is non-blocking for the person who did it: it never blocks them, is never late and cannot make them eligible. A take-over inside the planned week makes the actor the assignee and so the owner. Recorded work is placed with the credited person and never blocks.
- **Eligibility.** A person earns nothing for a period whose set is empty or holds only recorded work and non-blocking items; at least one planned occurrence of their own is needed. A partial period (the first cycle, a plan activated midway, a vacation) counts what exists and earns the full amount.
- **Done and on time.** Everything done: every occurrence in the set is done, whenever it was completed; open and skipped work is not done, skipped work that is completed later is done. Everything on time: everything is done and every completion lies before local midnight after the period's last day in the household timezone (so a DST week is 167 or 169 hours long); planned done work without a completion instant is done but not on time. Recorded work and non-blocking items are always on time, whatever their completion instant says (an administrator can correct the date of recorded work to before it). Work planned in week 1 and completed in week 2 of the same cycle is late for the week and on time for the cycle.
- **Ended.** A period has ended when its last day is before today in the household timezone; a period that has not ended earns nothing, however complete it is.
- **Entries.** `bonus_week_done`, `bonus_week_ontime`, `bonus_cycle_done` and `bonus_cycle_ontime`, each evaluated on its own; an amount of 0 produces no entry, and the on-time bonus is in addition to the done bonus. The key is `<kind>:<personId>:<periodStart>`; the entry is dated on the last day of the period, so a balance range that includes that day includes the bonus.
- **Schedule.** The amounts are a schedule over time, not a snapshot: the amounts of a period are those in force on its last day. An administrator sets the four amounts (integers from 0 to 1000) with `PATCH /api/settings` field `periodBonuses`; they apply from today in the household timezone, as a new schedule row or by replacing the row that already starts today, and equal amounts write and audit nothing. The write is a compare-and-set on the schedule it was computed from, so two administrators saving at once cannot overwrite each other: one wins and the other gets `409 bonus_schedule_conflict`. The amounts therefore apply to the current week and cycle and everything after, never to a period that has ended, and turning bonuses on never awards the past; a rebuild after an import gives the same amounts. An import rejects a schedule row that starts after today (`bonus_schedule_in_future`). The default is that every amount is 0, which disables the bonuses.
- **Finalisation.** The reconciliation gains a fourth step that inserts the missing bonus entries and deletes the stored entries that are no longer expected, under the same exclusive run: the deletes as one ordered bulk write, compare-and-set on the entry that was read, then the inserts as one unordered write. What was written is read back, so the summary only counts and lists the writes that happened. If the step fails, the summary of the other steps and of what it already wrote is still recorded before the failure is rethrown. No live write path touches a bonus: a late check-off or a correction inside an ended period shows at the next nightly run, at startup, after an import, or through the recompute endpoint. An occurrence that cannot be read is counted in `skipped`, and the bonus entries of everyone it could count for (assignee, period owner, and the person who did it) are left as they are in that run.
- **Corrections follow the history.** An uncomplete, an administrator's deletion or a correction of the completion past the cut-off removes the affected bonus; a late check-off of the last open item adds the done bonus but not the on-time bonus; helping with overdue work from an ended period (a take-over, a claim or finishing it for someone else) neither takes back a bonus the helper earned nor pays the person who missed it, because the work stays with the period owner; reassigning open overdue work after its week ended does not move it into the new assignee's ended week; moving the cycle anchor removes and recomputes the cycle bonuses of ended cycles, while week bonuses do not depend on the anchor.
- **Reading.** `bonusPoints` in the balances is the sum of the bonus entries in the range; `points` stays the total and `executions` counts only executions. The Points report shows the bonus column and the bonus entries (see 4.7). The history feed lists, for a reconciliation, who received or lost which bonus (person, kind and period) from `bonusChanges`, and shows a change of the bonus schedule as readable rows.

#### Reward meter

A tab shows how far a person is towards the goal of the current week or cycle (ADR-0015). Nothing about it is stored: it is read from the ledger and the occurrences.

- **Period.** The calendar week (Monday to Sunday) or the cycle (28 days from the Monday anchor) of today in the household timezone, built with the shared day-key and cycle helpers, so a DST week is 167 or 169 hours long and still starts and ends on local midnight.
- **Earned.** The person's points of executions and bonuses dated in the period. A redemption never lowers it. A week bonus is dated on the last day of its week and only written after that day, so it counts in the cycle meter, not in the week meter of its own week.
- **Goal.** An administrator can set a goal in points for the week and for the cycle (0 to 100000; the settings card "Beloningsdoelen"); it applies to everybody and 0 means no goal for that period. Without an explicit goal the goal is automatic: the sum of the points of the work planned for the person in the period, at least 1. Planned work is every occurrence that was not recorded as done, placed in a period by the day it was planned for and with the person it was planned for as its owner, by the rule of the bonuses (the frozen period owner, else the assignee). Work that somebody else did stays in the owner's goal; skipped work and recorded extra work are left out. The points of an occurrence are its snapshot when done, else what its task is worth now, else the duration rule. Nothing planned and no explicit goal means no goal: the meter says "Geen doel deze periode" instead of 0 of 0, with the reason (nothing is planned in this period, or the administrator switched the goal off with an explicit 0).
- **Percent and eggs.** `percent` is the earned share of the goal, a whole number rounded down and capped at 100, so it only reaches 100 when the goal is met. The basket holds one egg per full 10% (10 eggs are 100%).
- **Money.** When a point is worth money the earned points and the goal are also shown as money at the factor in force now.
- **Completion animation.** When the meter is full, a short animation plays once per person and period: the eggs bounce into the basket. It is remembered in the browser under `khc.rewardCelebrated.<personId>.<period>.<startDayKey>`, so a reload does not play it again and a new period plays it again; a copy is also kept in memory, so a blocked or failing storage cannot make a remount (the week/cycle toggle) play it again. The animation is over when its last egg has landed, so undoing and redoing the last task never replays it. With reduced motion (`prefers-reduced-motion`) it never plays and nothing is remembered; the static text "Doel gehaald!" is shown instead, and also whenever the goal is met.
- **Web.** The fifth overview tab "Beloning" shows the active profile: a week/cycle toggle (remembered per profile), an inline SVG scene with a chicken that walks to the current percentage with a CSS transform and the basket with its eggs (an image with a text alternative; missing eggs are dashed outlines), a progress bar (`role="progressbar"` with `aria-valuenow`, `aria-valuemin`, `aria-valuemax` and `aria-valuetext`), and always the text "{earned} van {goal} punten ({percent}%)" with the money and the number of eggs. "Doel gehaald!" appears in a live region (`role="status"`) that is in the page all the time, so the change is announced. The open tab reads the progress again on every window focus and every five minutes, and at once when the device's day lies outside the week or cycle that was read (a rollover). The earned badges of the person are shown below it. The layout is simple and accessible and will be fine-tuned later.

### 4.13 Badges

An administrator creates badges that reward doing a lot or doing it often (ADR-0014). A badge has a name, a picture the administrator uploads and a rule; people earn it automatically from the same audited execution data as the points.

- **Rules.** `executions`: the number of done executions credited to the person; `minutes`: the sum of the `durationMinutesSnapshot` of those executions; `onTimeWeeks`: the number of the person's `bonus_week_ontime` ledger entries (see 4.12). `executions` and `minutes` look at the chosen tasks, or at every task when none is chosen; a one-off task has no task, so it only counts for a rule on every task, and recorded extra work counts like any execution. The person credited is the one of the points (see 4.12): `completedBy`, else the assignee, so a check-off on behalf of the assignee credits the assignee and a take-over credits the actor. Work nobody can be credited for counts for nobody. Changing a task's duration never rewrites what was done. The `onTimeWeeks` rule only ever fires while bonus amounts are configured and after the week ended; the editor says so.
- **The award.** A person holds a badge exactly while the data reaches the threshold of an active badge. Its moment, `awardedAt`, is the moment the threshold was first crossed according to the data: the executions are ordered by completion instant (the date when there is none) and then by id, and the threshold-th one (for minutes the one that makes the running sum reach the threshold) decides; for `onTimeWeeks` it is the date of the threshold-th bonus entry, the last day of that week. It is never the wall clock, so recomputing and importing give the same moment. Awards appear and disappear with the data: undoing work below the threshold, deleting or correcting a completion, purging history, deactivating or deleting the badge, or changing its rule revokes or moves them. This is a product default (see ADR-0014).
- **One award, never twice.** The key `badge:<badgeId>:<personId>` is unique. Evaluating again changes nothing, writes nothing and records nothing.
- **When awards are evaluated.** After every execution sync (a check-off, an undo, a recording, a retract, a correction or a deletion of a completion) for the person the execution is credited to now and the person its ledger entry belonged to, or for everybody when the previous holder is unknown (an undo, a retract or a correction of work that earned no ledger entry); as the last step of the points reconciliation (startup, the nightly run, an import, `POST /api/points/recompute`); after a badge is created, deleted, or changed in its rule or active flag, and after the example badges were added; and after a statistics reset. After a task is deleted the awards are evaluated again too, since the task leaves the rules that named it (a rule left without tasks is deactivated, because an empty list means every task). All of it runs in the same per-database queue as the points reconciliation and the redemption bookings. The evaluation after a check-off only looks at the badges whose rule covers the task of the execution, and reads no executions at all for a person without an active executions or minutes badge. A failure after a check-off, a badge change, a task deletion or a statistics reset is logged and never fails that request; the nightly run repairs it. In the points reconciliation the badge step runs even when the bonus step failed, and a failure of either is rethrown after the points summary is recorded.
- **Images.** At most 256 KB, PNG, JPEG or WebP. The server decodes the base64 of the request and refuses text that is not base64, bytes over the limit, bytes whose signature is not a PNG, JPEG or WebP (an SVG, which can carry script, is never accepted) and a declared type that differs from the real one, each as `400 validation_error` (`invalid_base64`, `image_too_large`, `unsupported_image_type`, `image_type_mismatch`). `GET /api/badges/:id/image` serves the bytes without a profile with the checked content type, an `ETag` of the hash, `Cache-Control: public, max-age=31536000, immutable`, `X-Content-Type-Options: nosniff` and a restrictive `Content-Security-Policy`, and answers `304` to a matching `If-None-Match`; a badge without an image answers `404`. The long cache applies only to an address whose `v` parameter is a prefix (at least 12 characters) of the hash of the stored bytes, which is the `url` of the badge view; any other address answers `Cache-Control: no-cache` and revalidates. `If-None-Match` is read as a list of entity tags, weak tags and `*` included. A badge without an image shows a standard medal in the interface.
- **Examples.** An administrator can add example badges: "Alles op tijd" (`onTimeWeeks`, 4), "Toiletjuffrouw" (10 executions of the toilet task) and "Dweilkampioen" (300 minutes of the mopping task), in Dutch or English as chosen. They are created once, by their stable `exampleKey`, only on request and never at startup; adding them again, or after one was renamed, creates nothing. The tasks of an example are found by name; an example that finds no task is created inactive, because an empty task list would count every task. Names, descriptions, thresholds and tasks stay editable.
- **Who may do what.** Reading badges, awards, progress and images needs no profile. Creating, changing, deleting and adding examples needs an administrator (`403 permission_denied` for others, `400 profile_required` without a profile). A rule that names only unknown tasks is rejected (`validation_error` `unknown_task`), and creating a badge or adding examples beyond 100 badges is rejected with `409 badge_limit`. A change that changes nothing writes and audits nothing.
- **Web.** Administrators get a Badges page under management, with a list (picture, name, rule, an "inactive" label in words), an editor with name, description, rule, threshold, task choice with search, active flag and picture upload with a preview, a remove button, and the size and type checked before sending (also against the first bytes of the file), delete after a confirmation, and an "add example badges" action that says what it did. A person's badges are shown in the Points tab and in a small "Mijn badges" section on the Today page: the picture has the badge name as its alternative text, an earned badge says when it was earned, and a badge that is not earned yet says so with its progress as "7/10"; no state is shown by colour or dimming alone. Both sections are left out while there are no active badges.

## 5. AI assistance

### 5.1 Use cases

1. Propose a cycle plan for the selected tasks.
2. Rebalance an existing plan for fairness, spread and budget overruns.
3. Suggest tasks that are missing for a given room.
4. Explain a plan in a short rationale per week.

### 5.2 Contract

- Input: active tasks with room, interval and duration; users with availability and budgets; the current template when rebalancing; and optional free-text constraints.
- Output: strict JSON matching the plan slot schema, plus a rationale per week.
- The response is always a draft. It is stored as an inactive plan; the user reviews it in plan management and activates or deletes it there. Nothing is ever activated automatically.
- After a proposal or rebalance succeeds, the new draft opens in plan management by itself: it is selected, the result is announced, and keyboard focus moves to an AI card above the plan. The draft is then reviewed like any other plan and can be edited, deleted or activated there. The card states that the active plan does not change until the draft is activated, shows the stored rationale per week, and, right after creation, the validation warnings. A rejected proposal (validation failure) changes no selection and its error stays visible.
- Activating an AI draft always goes through the same activation preview as any other plan.
- The prompt asks, as a soft preference ranked below availability, the intervals and the hard daily limits, to keep recurring activities on the same weekdays and in a recognizable rhythm. It never outranks a hard rule.
- The proposal is validated on the server against exactly the same rules as the manual editor. On failure the model is re-prompted once, after which the error is surfaced rather than a broken plan silently accepted.

### 5.3 Provider

- Pluggable: no provider, a deterministic mock for testing, Anthropic, an OpenAI-compatible endpoint, or a local Ollama instance.
- Endpoint, model and request timeout are settings; the API key is an environment variable only and is never returned by the API.
- Prompts are editable per use case, with the default text restorable.
- The application remains fully usable with AI disabled. It is an assistant, not a dependency.

## 6. Print and PDF export

The fridge is a legitimate output device. The schedule must work without a phone.

### 6.1 Range

- One week, two weeks, or the full four-week cycle, starting from any week in the cycle.
- Also a single-day sheet, a standalone overdue list, and a task list per room.

### 6.2 Layout

- One week per page; a two-week export is two pages, the full cycle four.
- Portrait A4, with landscape available only for the two-week side-by-side variant.
- Days as rows, Monday first, one column per user. Empty days stay visible, because a gap on the sheet is information.
- Each line shows the task name, its room, and a checkbox large enough to tick with a pen.
- A header per page names the cycle week and the calendar dates it covers; a footer carries the generation date, so nobody works from a sheet that is three cycles old.
- Per-day totals in minutes per user are available as an option.

### 6.3 Content rules

- The sheet is a blank checklist. It shows what is planned and nothing about status — no completion marks, no "done by" column.
- It exports generated occurrences, not the template, so rescheduled items appear where they actually sit. One-off tasks are included, with their room taken from the occurrence (no room when they have none).
- Only weeks that have already been generated can be exported. The interface states this instead of producing an empty sheet.
- Paper and application do not synchronise. One line on the sheet says so.
- The output is black-and-white safe: no information is carried by colour alone.
- The download has a predictable file name that names the period it covers.

## 7. Web application

### 7.1 Structure

- A compact overview is the default at every screen width: the week grid, the day view, the overdue list, the task list and the reward meter, each a tab of the bottom menu. The five tabs keep fitting, each label on one line, and stay keyboard accessible at a width of 320 px.
- Management screens — planner, tasks, distribution, statistics, history, completions, badges, notifications and settings — live behind a separate management area and are reachable from anywhere. The notifications page is available to every role, because each person sets their own browser notifications.
- The overview and the management area switch with a button in the same top-right spot: a management button in the overview, and a Home button in management that always returns to the week overview. The management side menu stays available.
- The settings screen is organised in tabs so that cycle, intervals, AI, scheduled jobs (including the ntfy and Home Assistant morning notification), appearance and maintenance stay separable. The calendar tab holds a "Bonussen" card, visible to administrators only, in which the four bonus amounts (integers from 0 to 1000, 0 turns a bonus off) are set; it says that the amounts apply to the current week and cycle and everything after, since when they apply, and lists the schedule rows, marking a row that has not started yet with an icon and text. Under the bonuses are the "Puntenwaarde" card (currency and what a point is worth) and the "Beloningsdoelen" card (the goal of the reward meter per week and per cycle, an empty field meaning automatic), both visible to administrators only.
- An About page, reachable for every role from the management menu, shows the running version, the date and time of the latest release labelled as such, and links to the license and the changelog that belong to the running build. A local build that is not a release shows no release date.

### 7.2 Interaction

- Mobile-first. Ticking things off happens one-handed on a phone; planning happens on a larger screen. Both must work, and drag-and-drop must work with touch as well as a mouse.
- The control used to complete an occurrence is configurable between a circle and a thumbs-up, because the same gesture reads differently to different people.
- Every interactive element is reachable and operable from the keyboard.
- No state is communicated by colour alone; overdue, over-budget and completed states also differ in shape, weight or label.
- Motion is decorative and never carries information: with `prefers-reduced-motion` the reward meter's chicken stands still, the completion animation never plays, and the same progress is given as text.

### 7.3 Offline behaviour

- The application installs as a PWA and keeps working without a connection for reading and for completing occurrences.
- Actions taken offline are queued and replayed on reconnect, attributed to the profile that was active when the action was taken, not the one active when the queue drains.
- A new application version prompts the user to reload rather than swapping itself out underneath an open screen.

### 7.4 Language

- The interface is available in Dutch, which is the default, and English. The choice is stored in the browser.
- All user-facing text comes from the message catalogues; both languages carry the same keys.
- Week numbers are ISO week numbers and weeks start on Monday in both languages.
- The version and build identity of the running instance are visible in the interface.

## 8. API surface

All endpoints live under `/api`. Identifiers are 24-character hexadecimal strings, calendar dates are `YYYY-MM-DD` day keys, and timestamps are ISO instants.

```
GET    /api/health

GET    /api/users                           POST /api/users            PATCH /api/users/:id
PUT    /api/users/:id/browser-notifications (own moments, or any person's for an admin)
GET    /api/rooms                           POST /api/rooms            PATCH /api/rooms/:id
DELETE /api/rooms/:id
GET    /api/tasks                           POST /api/tasks            PATCH /api/tasks/:id
DELETE /api/tasks/:id                       POST /api/rooms/:id/tasks/bulk

GET    /api/cycles
GET    /api/cycle-plans                     GET  /api/cycle-plans/active
GET    /api/cycle-plans/:id                 GET  /api/cycle-plans/:id/diff
POST   /api/cycle-plans                     PATCH /api/cycle-plans/:id
DELETE /api/cycle-plans/:id                 PUT  /api/cycle-plans/:id/slots
GET    /api/cycle-plans/:id/activation-preview
POST   /api/cycle-plans/:id/activate        (body: { previewToken })
POST   /api/cycle-plans/:id/apply-proposal  POST /api/cycle-plans/:id/discard

GET    /api/occurrences                     POST /api/occurrences
PATCH  /api/occurrences/:id                 POST /api/occurrences/:id/claim
POST   /api/occurrences/one-off             POST /api/occurrences/:id/retract
DELETE /api/occurrences/:id
GET    /api/due

GET    /api/points/balances                  GET  /api/points/entries
GET    /api/points/progress
POST   /api/points/recompute
POST   /api/points/redemptions               DELETE /api/points/redemptions/:id

GET    /api/badges                           POST /api/badges
PATCH  /api/badges/:id                       DELETE /api/badges/:id
GET    /api/badges/:id/image                 POST /api/badges/examples
GET    /api/badges/awards                    GET  /api/badges/progress

GET    /api/promote-suggestions
POST   /api/promote-suggestions/apply       POST /api/promote-suggestions/dismiss

GET    /api/stats/workload                  GET  /api/stats/completion
GET    /api/stats/intervals                 GET  /api/stats/deviations
DELETE /api/stats

GET    /api/audit                           DELETE /api/audit
GET    /api/settings                        PATCH  /api/settings

GET    /api/ai/prompt-info
POST   /api/ai/test                         POST /api/ai/propose-plan
POST   /api/ai/rebalance                    POST /api/ai/suggest-tasks
POST   /api/ai/explain

POST   /api/jobs/nightly                    POST /api/jobs/morning-notify
POST   /api/jobs/audit-retention

GET    /api/export/pdf                      GET  /api/export/pdf/day
GET    /api/export/pdf/due                  GET  /api/export/pdf/tasks
GET    /api/export/json                     POST /api/import/json
```

`POST /api/occurrences` takes `{ taskId, date, assigneeId?, done?, requestId? }` and answers `201` with the occurrence and its `warnings`, or `200` when a repeated `requestId` replays the stored record. Errors: `400 validation_error` (`unknown_task`, `inactive_task`, `unknown_user`, `inactive_user`, `done_requires_today`, `done_requires_person`), `409 cycle_not_generated`, `409 idempotency_key_conflict`. `POST /api/occurrences/:id/retract` answers `200 { retracted: true, id }`, `409 not_retractable` for anything but recorded extra work, and `404` when it is already gone.

`POST /api/occurrences/one-off` takes `{ name, roomId?, durationMinutes, date, assigneeId?, done?, points?, requestId? }` and answers like `POST /api/occurrences`: `201` with the occurrence (`taskId: null`) and `warnings`, or `200` on a replay. Errors: `400 validation_error` (`unknown_room`, `inactive_room`, `unknown_user`, `inactive_user`, `done_requires_today`, `done_requires_person`), `409 cycle_not_generated`, `409 idempotency_key_conflict`. A repeated key matches when the stored one-off task has the same name, date and `recordedDone`.

`PATCH /api/occurrences/:id` is a single endpoint carrying an explicit action: complete, uncomplete, edit a completion, skip, reschedule or assign. The action is part of the request, so history records intent rather than an inferred difference. A `complete` action takes `completedBy` or `takeOver: true`, never both (`completion_choice_conflict`), and work of someone else without either is rejected with `400 completion_choice_required` (see 4.4). Occurrence views carry `pointsSnapshot`.

`POST /api/tasks` and `PATCH /api/tasks/:id` take an optional integer `points` from 0 to 1000; task views always carry `points`.

`GET /api/points/balances?from&to` takes two optional day keys (`from` must not be after `to`) and answers `{ from, to, currencyCode, centsPerPoint, balances: [{ personId, points, earned, redeemed, money, executions, bonusPoints }] }`, with `from` and `to` `null` when absent. `points` is the balance (earned minus redeemed), `earned` the points of executions and bonuses, `redeemed` the redeemed points as a positive number, and `money` is `{ earned, redeemed, balance }` in cents at the factor in force now, or `null` while `centsPerPoint` is 0. It lists every active user, also at 0, and every inactive user with entries in the range, in the order of the user list. `GET /api/points/entries?personId&from&to` requires all three, allows a range of at most 371 days, both days included, and answers `{ entries }`, newest `date` first and then by id; an entry carries `_id`, `key`, `kind` (`execution`, one of the four bonus kinds, or `redemption`), `personId`, `amount`, `date`, `weekStart`, `periodStart` (a day key for a bonus, `null` for an execution), `occurrenceId`, `taskId`, `titleSnapshot`, `note`, `centsPerPointSnapshot` and `currencyCodeSnapshot` (all `null` unless the entry is a redemption; the currency also `null` on a booking from before it was kept), `source`, `createdAt` and `updatedAt`. Errors: `400 validation_error` with `from_after_to` on `from`, `range_too_large` on `to`, or the field of a missing or malformed parameter. Neither read needs a profile. `POST /api/points/recompute` takes no body, requires an administrator and answers `200` with `{ trigger: 'admin', tasksDefaulted, snapshotsSet, created, updated, removed, unattributed, skipped, corrections, correctionsTotal, correctionsTruncated, bonusesCreated, bonusesRemoved, bonusChanges, bonusChangesTotal, bonusChangesTruncated }`; it audits and writes nothing when the ledger already matches.

`GET /api/points/progress?personId&period=week|cycle` requires both parameters and answers `{ personId, period, start, end, earnedPoints, goalPoints, goalSource, percent, currencyCode, centsPerPoint, money }` for the week or cycle of today in the household timezone (see 4.12): `start` and `end` are the first and last day, `earnedPoints` the points of executions and bonuses dated in it (redemptions are not subtracted), `goalPoints` the goal or `null` when there is none, `goalSource` `explicit` or `automatic`, `percent` a whole number from 0 to 100 (0 without a goal), and `money` is `{ earned, goal }` in cents at the factor in force now (`goal` `null` without a goal), or `null` while `centsPerPoint` is 0. Errors: `400 validation_error` for a missing or malformed `personId` or `period`. It needs no profile and writes and audits nothing.

`GET /api/points/redemptions/count` answers `{ count }`, the number of redemptions, without a profile. `POST /api/points/redemptions` takes `{ personId?, points, note?, requestId? }` (`personId` defaults to the active profile, `points` is an integer from 1, `note` at most 200 characters, `requestId` the same key shape as for occurrences) and needs a profile. It answers `201` with the entry (a redemption view with its `currencyCodeSnapshot`, without the request key), or `200` when a repeated `requestId` replays the stored booking. Errors: `400 validation_error` (`unknown_user` and `inactive_user` on `personId`, or the field of a malformed value), `403 permission_denied` (a member booking for someone else), `409 insufficient_balance` with `balance` and `requested`, `409 idempotency_key_conflict`. `DELETE /api/points/redemptions/:id` needs a profile and answers `200 { deleted: true }`; errors: `403 redemption_locked` (the owner after the day it was booked), `403 permission_denied` (another member), `404` for an unknown id or an entry that is not a redemption.

`GET /api/badges` takes an optional `active=true|false` and answers `{ badges }`, oldest first; a badge carries `_id`, `name`, `description`, `rule`, `active`, `exampleKey` (`null` unless it is an example), `image` (`null` or `{ contentType, size, hash, url }`), `createdAt` and `updatedAt`, never the bytes. `POST /api/badges` takes `{ name, description?, rule, active?, image? }` (`rule` is `{ type: 'executions' | 'minutes', taskIds, threshold }` or `{ type: 'onTimeWeeks', threshold }`; `image` is `{ contentType, data }` with the bytes as base64) and answers `201` with the badge; `PATCH /api/badges/:id` takes any of those, `image: null` removes the picture, and answers `200` with the badge or `404`; `DELETE /api/badges/:id` answers `200 { deleted: true }` or `404`. All three need an administrator. Errors: `409 badge_limit` on create (and when adding examples) at 100 badges; `400 validation_error` (`name`, `description`, `rule.threshold`, `rule.taskIds` with `unknown_task` when the rule names only unknown tasks, and `image.data` or `image.contentType` with the image messages of 4.13). `POST /api/badges/examples` takes `{ language?: 'nl' | 'en' }` (default `nl`), needs an administrator and answers `{ created, skipped }`: the badges it created and the number of examples that already existed. `GET /api/badges/awards` takes an optional `personId` and answers `{ awards: [{ _id, badgeId, personId, awardedAt }] }`, oldest first, for badges that exist. `GET /api/badges/progress?personId` requires `personId` and answers `{ personId, items: [{ badgeId, current, threshold, awardedAt }] }` for the active badges, evaluated on the data (`awardedAt` is `null` while it is not earned, and `current` can exceed `threshold`). `GET /api/badges/:id/image` is described in 4.13. Reads need no profile.

`GET /api/settings` always returns `bonusSchedule` (`[]` when none), `currencyCode` (`EUR` when unset), `centsPerPoint` (`0` when unset) and `rewardGoals` (`{ weekPoints: null, cyclePoints: null }` when unset). `PATCH /api/settings` (administrators only) takes the optional `periodBonuses: { weekDone, weekOnTime, cycleDone, cycleOnTime }`, four integers from 0 to 1000, which the server turns into a schedule row from today (see 4.12); a client never sends the schedule itself, amounts that equal the ones in force write and audit nothing, and a write that races another one answers `409 bonus_schedule_conflict`. It also takes the optional `currencyCode` (an ISO 4217 code that the runtime knows, `validation_error` `invalid_currency_code` otherwise, and `currency_not_two_decimals` for a known currency without exactly two fraction digits, such as JPY or KWD) and `centsPerPoint` (an integer from 0 to 10000); a value equal to the one in force writes and audits nothing, and a change is one settings `update`. It also takes the optional `rewardGoals: { weekPoints, cyclePoints }` (administrators only): both required, each an integer from 0 to 100000 or `null`, `validation_error` otherwise; goals equal to the ones in force write and audit nothing.

Every error response uses one envelope with a stable machine-readable code and optional field-level details. Validation failures, permission failures and conflicts are distinguishable by status and code, and configuration values never appear in an error message.

## 9. Configuration

All configuration is supplied through environment variables; nothing is baked into the image.

| Variable | Purpose |
| --- | --- |
| `MONGO_URL` | Database connection. Required. |
| `PORT` | HTTP port. |
| `NODE_ENV` | Runtime mode. |
| `TZ_APP` | Household timezone used for all calendar reasoning. |
| `SEED_USERS` | Profiles created on an empty database. |
| `LOG_LEVEL` | Log verbosity. |
| `AUDIT_RETENTION_DAYS` | Age after which audit entries are removed. Unset means indefinite. |
| `AI_API_KEY` | Credential for the configured AI provider. |
| `NOTIFY_TYPE` | `none`, `ntfy` or `homeassistant`. |
| `NOTIFY_URL` | Target for notifications. Required unless the type is `none`. |
| `NOTIFY_TOKEN` | Credential for the notification target. |
| `DISABLE_SCHEDULER` | Disables background jobs. |
| `WEB_DIST_DIR` | Location of the built frontend. |

Invalid configuration fails at startup with a message naming the offending variables and never echoing their values.

## 10. Non-functional requirements

### Deployment

- One application container serving both the API and the frontend, plus MongoDB, described by a single compose file, with a separate container for nightly backups.
- A named volume for the database and a mounted path for backups.
- A health endpoint, and structured logs to stdout so the same output works in a terminal and in a container.

### Data

- All calendar reasoning happens in the configured timezone on day keys, so cycle and week arithmetic is unaffected by daylight saving.
- Snapshotted history is never rewritten by later edits to the definitions it came from.
- No secrets, dumps, backups or generated reports are stored in the repository.

### Quality

- Every behaviour change carries a regression test at the cheapest level that can prove it.
- Server tests run against a fresh database and a fixed clock, and never reach a real AI provider, a real notification endpoint or a real installation.
- A small number of end-to-end journeys cover the cross-stack paths that unit tests cannot.
