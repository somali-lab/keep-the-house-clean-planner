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
| `member` | Everything a participant needs: complete, uncomplete, skip, reschedule, assign, claim, and create an ad-hoc occurrence. |
| `planner` | All of the above, plus tasks, cycle plans, promote suggestions, AI operations, and manually triggering jobs. |
| `admin` | All of the above, plus users, rooms, settings, correcting or deleting a completion, purging statistics, importing data, and clearing the audit log. |

Requirements:

- The selected profile persists in the browser across sessions and is switchable from the header in one action, because one device is often shared.
- Every write carries the active profile, and that profile is the actor in the audit entry.
- A completion may be attributed to someone other than the active profile. The audit entry then records both the actor who pressed and the person credited.
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
defaultAssigneeId | null,                // null = either person
active, notes, tags: [string],
lastCompletedAt | null                   // denormalised, maintained on completion
```

- A duration estimate is mandatory. Workload balancing, budget validation and the AI all depend on it.
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
_id, taskId, cycleId, planId | null, date, plannedDate,
assigneeId | null,
status: 'open' | 'done' | 'skipped',
statusBeforeCompletion: 'open' | 'skipped' | null,
completedAt | null, completedBy | null, skipReason | null,
durationMinutesSnapshot, taskNameSnapshot, roomIdSnapshot, roomNameSnapshot,
origin: 'generated' | 'adhoc'
```

- `plannedDate` keeps the original slot date when an occurrence is dragged, so drift is measurable.
- Name, room and duration are snapshotted at creation. Changing a task's duration must never silently rewrite last year's workload statistics.
- `statusBeforeCompletion` exists so that undoing a completion restores the previous status rather than defaulting to open.

### `auditLog`

```
_id, at, actorId,
entity: 'task' | 'cyclePlan' | 'occurrence' | 'user' | 'room' | 'settings',
entityId,
action: 'create' | 'update' | 'delete' | 'complete' | 'uncomplete' | 'skip'
      | 'reschedule' | 'assign' | 'activate' | 'ai-apply' | ...,
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
dismissedPromotions: [ ... ]
```

### Indexes

- `occurrences`: `{ date, assigneeId }`, `{ status, date }`, `{ taskId, completedAt }`
- `tasks`: `{ roomId, active }`
- `auditLog`: `{ entity, entityId, at }` and `{ at }`

## 4. Functional requirements

### 4.1 Rooms and tasks

- Create, update and deactivate rooms; ordering is explicit, not alphabetical.
- Create, update and deactivate tasks, grouped by room.
- Bulk operations per room: deactivate all, reassign all.
- A room that still holds tasks cannot be deleted.

### 4.2 Template editor

- A grid of four weeks by seven days, Monday first, with one column per user.
- Drag a task from an unplanned pool onto a day cell, and drag it between cells.
- Interval validation shows `placed / required` per task and flags any mismatch. This is a warning, not a block: the household may know better than the interval.
- Workload validation sums the planned minutes per user per day against that user's budget, and marks days over the budget and days over the hard ceiling differently.
- A drop onto a weekday the assignee is unavailable on is rejected, with an explanation.
- Per-week totals per user are visible, so imbalance is apparent before the cycle starts.
- The same validation rules run on the server for every plan write, so a plan that the editor would refuse cannot arrive through the API either.

### 4.3 Generation

- Activating a plan generates occurrences for the cycle's 28 days.
- Generation is idempotent. Re-running it produces no duplicates, keyed on cycle, task and planned date.
- Generation never creates an occurrence in the past. A cycle activated midway produces the remainder of the cycle only.
- A nightly job generates the upcoming cycle in advance, so the coming week is always visible.
- Vacation ranges suppress generation on those dates. The due engine keeps counting the days.
- Activating a different plan replaces only future occurrences that are still replaceable — untouched, generated, open ones. Anything completed, skipped, rescheduled, or created ad hoc survives, because it records something that actually happened.

### 4.4 Daily use

- A today view lists the open occurrences for the selected profile, then the other members', then overdue items, and can be filtered per profile.
- The view can browse forward a day or two without leaving the day-oriented layout, and shows which cycle week the day belongs to.
- No backlog is shown from before the cycle anchor date; there is nothing to be behind on yet.
- Complete and undo. Undo restores the previous status.
- Skip with an optional reason. A skipped occurrence does not roll over, but counts as not done for the due engine.
- When the active profile is not the assignee, completing offers an explicit choice between taking the task over and recording it on behalf of the assignee. The two produce different history.
- An unassigned occurrence can be claimed.
- Reschedule by dragging to another day. `plannedDate` is preserved. Dragging to a day the assignee is unavailable on is allowed but warned about, because reality outranks the plan.
- A week overview is the default landing view at every screen width, shows the whole week with drag-to-reschedule, and can collapse past days.
- "Done just now" creates an ad-hoc occurrence for a task that was not planned today. At most one ad-hoc occurrence per task per day, and only within a cycle that has been generated.

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

### 4.6 Promote to template

- When the same occurrence is moved the same way repeatedly, the system offers to update the template slot. The threshold is configurable and is at least two.
- A suggestion can be applied or dismissed. A dismissed suggestion stays dismissed until newer evidence appears.
- Applying a suggestion changes the active plan and is audited like any other plan edit.

### 4.7 Statistics

- A period is selected either as a number of recent cycles or as a range of calendar weeks.
- Reports: an overview, fairness between users, workload per user over time, completion rate per task, room and user, configured versus actually achieved intervals, and deviations between planned and actual days.
- Every chart has an equivalent table, so the same numbers are available without interpreting a graphic.
- An administrator can reset statistics completely, or purge only the completion data before a chosen date.

### 4.8 Completion management

- An administrator can correct a recorded completion — its date, its timestamp, and who is credited — or delete it entirely.
- Both are audited, including the values before the correction.

### 4.9 Audit trail

Every state change is recorded with who, when, which entity, which action, the changed fields before and after, and the origin of the change.

- Completing, undoing, skipping, rescheduling, assigning and claiming are each their own entry. Undoing is a new entry, never the removal of the original.
- Task, room, user, plan and settings changes record old and new values per changed field.
- Applying an AI proposal is recorded with an AI origin, so a machine-made plan is always distinguishable from a hand-made one.
- Generation is recorded with a system origin, so an unexpected occurrence can be traced to the run that created it.
- A change that changes nothing writes nothing and records nothing.
- The log is append-only. No interface path edits an entry. An optional retention job removes entries older than a configured age and is the only exception.
- History is viewable per entity and as a global chronological feed, filterable by actor, entity type, action and date range, with a panel showing the referenced entity.

### 4.10 Notifications and scheduled jobs

- A nightly job generates upcoming occurrences and, when configured, applies audit retention.
- A morning notification summarises the day: what is planned per person and what is overdue. It is suppressed when there is nothing to report.
- Supported channels are none, an ntfy topic, and a Home Assistant webhook. The channel and its credentials come from the environment.
- Generation, the morning notification and audit retention can each be triggered manually from the settings screen, which is also how an installation is verified after a change.
- The scheduler can be disabled entirely, which is required for reproducible tests.

### 4.11 Data management

- Full JSON export of the dataset, and import of such an export.
- Import validates the entire file against both the API shape and the storage shape before writing anything, reports what it will replace, and requires explicit confirmation.
- A nightly database dump is written to a mounted backup path by a separate container.

## 5. AI assistance

### 5.1 Use cases

1. Propose a cycle plan for the selected tasks.
2. Rebalance an existing plan for fairness, spread and budget overruns.
3. Suggest tasks that are missing for a given room.
4. Explain a plan in a short rationale per week.

### 5.2 Contract

- Input: active tasks with room, interval and duration; users with availability and budgets; the current template when rebalancing; and optional free-text constraints.
- Output: strict JSON matching the plan slot schema, plus a rationale per week.
- The response is always a draft. It is stored as an inactive plan and presented as a diff against the active one. The user applies or discards it; nothing is ever activated automatically.
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
- It exports generated occurrences, not the template, so rescheduled items appear where they actually sit.
- Only weeks that have already been generated can be exported. The interface states this instead of producing an empty sheet.
- Paper and application do not synchronise. One line on the sheet says so.
- The output is black-and-white safe: no information is carried by colour alone.
- The download has a predictable file name that names the period it covers.

## 7. Web application

### 7.1 Structure

- A compact overview is the default at every screen width: the week grid, the day view, the overdue list and the task list.
- Management screens — planner, tasks, distribution, statistics, history, completions and settings — live behind a separate management area and are reachable from anywhere.
- The settings screen is organised in tabs so that cycle, intervals, AI, notifications, appearance and maintenance stay separable.

### 7.2 Interaction

- Mobile-first. Ticking things off happens one-handed on a phone; planning happens on a larger screen. Both must work, and drag-and-drop must work with touch as well as a mouse.
- The control used to complete an occurrence is configurable between a circle and a thumbs-up, because the same gesture reads differently to different people.
- Every interactive element is reachable and operable from the keyboard.
- No state is communicated by colour alone; overdue, over-budget and completed states also differ in shape, weight or label.

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
GET    /api/rooms                           POST /api/rooms            PATCH /api/rooms/:id
DELETE /api/rooms/:id
GET    /api/tasks                           POST /api/tasks            PATCH /api/tasks/:id
DELETE /api/tasks/:id                       POST /api/rooms/:id/tasks/bulk

GET    /api/cycles
GET    /api/cycle-plans                     GET  /api/cycle-plans/active
GET    /api/cycle-plans/:id                 GET  /api/cycle-plans/:id/diff
POST   /api/cycle-plans                     PATCH /api/cycle-plans/:id
DELETE /api/cycle-plans/:id                 PUT  /api/cycle-plans/:id/slots
POST   /api/cycle-plans/:id/activate
POST   /api/cycle-plans/:id/apply-proposal  POST /api/cycle-plans/:id/discard

GET    /api/occurrences                     POST /api/occurrences
PATCH  /api/occurrences/:id                 POST /api/occurrences/:id/claim
DELETE /api/occurrences/:id
GET    /api/due

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

`PATCH /api/occurrences/:id` is a single endpoint carrying an explicit action: complete, uncomplete, edit a completion, skip, reschedule or assign. The action is part of the request, so history records intent rather than an inferred difference.

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
