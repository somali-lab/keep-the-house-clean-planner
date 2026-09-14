# Huishoudplanner — Requirements

Self-hosted household chore scheduler. Single Docker service + MongoDB, two users by default, 4-week repeating cycle with drag-and-drop planning and an AI planning assistant.

## 1. Decisions taken

| # | Decision | Consequence |
|---|---|---|
| 1 | **Hybrid scheduling** | The 4-week grid drives planning; a due-date engine runs alongside it and surfaces anything that has drifted past its interval. |
| 2 | **Overdue stays put** | An unchecked occurrence remains visible on its original day, marked overdue. It is never auto-moved. The user drags it to another day to reschedule. |
| 3 | **Template model** | The 4-week plan is a reusable template. Each cycle, concrete occurrences are generated from it. Editing an occurrence does not change the template unless explicitly promoted. |

The consequence of 1 + 3 combined is the central rule of the system:

> The template says *when we intend to do it*. The occurrence says *what actually happened*. The due engine compares the two and tells you where reality has drifted.

This must be visible in the UI, or users will not understand why a task appears both on Saturday and in the overdue list.

## 2. Domain model (MongoDB)

Six collections. All documents carry `createdAt`, `updatedAt`.

### `users`
```
_id, name, color, active: bool,
unavailableWeekdays: [int]        // 0=Sun..6=Sat, e.g. [2] = not on Tuesday
dailyBudgetMinutes: { weekday: int, weekend: int }
```

### `rooms`
```
_id, name, sortOrder, active: bool
```
Includes a virtual room for house-wide work (ramen, deuren, radiatoren).

### `tasks` — the definition, not the doing
```
_id, name, roomId, intervalKey, durationMinutes,
defaultAssigneeId: ObjectId | null,   // null = either person
active: bool, notes, tags: [string],
lastCompletedAt: date | null          // denormalised, maintained on completion
```

`intervalKey` references a lookup collection or a config document rather than a hardcoded enum:

| key | label | perCycle | periodDays |
|---|---|---|---|
| `daily` | Dagelijks | 28 | 1 |
| `2w` | 2x per week | 8 | 3 |
| `1w` | 1x per week | 4 | 7 |
| `2wk` | 1x per 2 weken | 2 | 14 |
| `4wk` | 1x per 4 weken | 1 | 28 |
| `quarter` | 1x per kwartaal | — | 91 |

`periodDays` feeds the due engine. Adding `quarter` and `year` later must require no code change — ramen buiten, gordijnen and matras keren will need them.

### `cyclePlans` — the template
```
_id, name, active: bool,
slots: [
  { taskId, weekIndex: 0..3, weekday: 0..6, assigneeId | null, sortOrder }
]
```
One slot = one intended execution. A `2w` task therefore has 8 slots across the cycle. Exactly one plan is active at a time; keep the others for experimentation.

### `occurrences` — the generated, concrete instances
```
_id, taskId, cycleId, date, assigneeId,
plannedDate,                       // original slot date, kept when dragged
status: 'open' | 'done' | 'skipped',
completedAt, completedBy, skipReason,
durationMinutesSnapshot            // copied at generation, so history survives edits
```

Snapshotting name and duration at generation time matters: if you halve a task's duration next year, last year's workload statistics should not silently change.

### `auditLog` — append-only history of every change
```
_id, at: date, actorId: ObjectId,
entity: 'task' | 'cyclePlan' | 'occurrence' | 'user' | 'settings',
entityId: ObjectId,
action: 'create' | 'update' | 'delete' | 'complete' | 'uncomplete'
      | 'skip' | 'reschedule' | 'assign' | 'activate' | 'ai-apply',
before: {}, after: {},            // changed fields only, not whole documents
source: 'ui' | 'api' | 'ai' | 'system'
```

Never updated, never deleted by the application. `source: 'system'` covers nightly generation, so a mysterious occurrence can always be traced back to the job that made it.

### `settings`
```
cycleAnchorDate,                   // which Monday is week 1
weekStartsOn: 1,
timezone: 'Europe/Amsterdam',
vacationRanges: [{ from, to }],
aiProvider: { type, endpoint, model }
```

### Indexes
- `occurrences`: `{ date: 1, assigneeId: 1 }`, `{ status: 1, date: 1 }`, `{ taskId: 1, completedAt: -1 }`
- `tasks`: `{ roomId: 1, active: 1 }`
- `cyclePlans.slots`: `{ weekIndex: 1, weekday: 1 }`

## 3. Functional requirements

### 3.1 Task management
- CRUD on tasks, grouped by room.
- Every task requires an interval and a duration estimate. Duration is not optional — the workload balancer and the AI both depend on it.
- Deactivate rather than delete; occurrence history must stay intact.
- Bulk operations per room (deactivate all, reassign all).

### 3.2 Cycle template editor
- Grid of 4 weeks × 7 days, Monday first, one column per user.
- Drag a task from an unplanned pool onto a day cell.
- **Interval validation:** the editor shows, per task, `placed / required` slots for the cycle and flags mismatches. A `2w` task with 5 slots is a warning, not a hard block — the user may know better.
- **Workload validation:** per day per user, the summed `durationMinutes` against that user's daily budget, with a visual over-budget state.
- **Availability enforcement:** a drop onto a weekday listed in the assignee's `unavailableWeekdays` is rejected with an explanation.
- Per-week totals per user, so imbalance is visible before the cycle starts.

### 3.3 Cycle generation
- On activation of a cycle, generate occurrences for that cycle's 28 days from the active template.
- Generation is idempotent: re-running must not duplicate. Key on `(cycleId, taskId, plannedDate)`.
- Generate the next cycle automatically ahead of time (nightly job), so the coming week is always visible.
- Vacation ranges suppress generation for those dates; the due engine still counts the days.

### 3.4 Daily use
- Today view: open occurrences for the current user, then the other user's, then overdue.
- Check off / undo. Undo must restore the previous status, not just flip to open.
- Skip with an optional reason. A skipped occurrence does not roll over, but it does count as "not done" for the due engine.
- Overdue occurrences remain on their original date, visually marked, and additionally appear in a dedicated overdue list.
- Drag an occurrence to another day to reschedule it. `plannedDate` is preserved so the drift is measurable.
- **Promote to template:** when the same occurrence gets dragged the same way repeatedly, offer to update the template slot. This is what keeps the plan honest over time.

### 3.5 Due engine (the "hybrid" half)
Runs nightly and on demand. For each active task:

```
daysSince = today - (lastCompletedAt ?? task.createdAt)
ratio     = daysSince / intervalPeriodDays
```

- `ratio >= 1.0` → due
- `ratio >= 1.5` → overdue, surfaced prominently
- Produces a ranked "achterstand" list independent of the grid.

This is what catches the badkamer that got skipped three cycles running while the grid kept looking tidy.

### 3.6 Users
- Seed two users on first run; the count is configuration, not an assumption in the code.
- Per-user availability and daily time budgets.
- Fairness view: minutes per user per week and per cycle, planned vs. actually completed.

### 3.7 Statistics
- Completion rate per task, per room, per user.
- Actual interval vs. configured interval per task — the report that tells you which intervals were wishful thinking.
- Workload trend per user over cycles.

### 3.8 Audit trail
Every state change is recorded, by whom and when. This covers more than checking off:

- **Completion:** who ticked it, at what timestamp, and whether they were the assignee. Unchecking is its own entry, not a deletion of the original.
- **Skipping:** who skipped it and the reason given.
- **Rescheduling:** who dragged it, from which date to which date.
- **Assignment:** who assigned or reassigned an occurrence, and who set the default assignee on the task.
- **Task definition:** who created the task, and every later change to its name, interval, duration or room, with old and new values.
- **Planning:** who placed a task in the template, who activated a cycle plan, and who applied an AI proposal — recorded with `source: 'ai'` plus the proposal id, so an AI-made plan is always distinguishable from a hand-made one.
- **Generation:** which nightly run created which occurrences.

Requirements:
- Append-only. No UI path deletes or edits an audit entry.
- Every write path goes through one audit helper. A change that bypasses it is a bug, and that is worth a test.
- Viewable per entity ("history of this task") and as a global chronological feed, filterable on actor, entity type and date range.
- `before`/`after` hold changed fields only. Storing whole documents makes the log unreadable within a month.
- Deleting a task does not remove its audit entries or its occurrences. This is the main reason tasks are deactivated rather than deleted.
- Retention indefinite by default; volume for two people is trivial. Make it configurable anyway.

Index on `{ entity: 1, entityId: 1, at: -1 }` and `{ at: -1 }`.

## 4. AI integration

### 4.1 Use cases
1. **Propose a cycle plan** from the selected tasks.
2. **Rebalance** an existing plan (fairness, spread, budget overruns).
3. **Suggest missing tasks** for a given room, based on what is already defined.
4. **Explain the plan** — a short rationale per week.

### 4.2 Contract
- Input: active tasks (name, room, interval, duration), users with availability and budgets, current template if rebalancing, plus optional free-text constraints ("geen nat werk doordeweeks", "zaterdag maximaal een uur per persoon").
- Output: strict JSON matching the `cyclePlans.slots` schema, plus a `rationale` array of 4 strings.
- The response is **always a draft**. It is stored as an inactive `cyclePlan` and presented as a diff against the active one. The user applies or discards. Nothing is auto-activated.
- Server-side validation of the proposal against the exact same rules as the manual editor (interval counts, availability, budgets). Reject and re-prompt once on failure, then surface the error rather than silently accepting a broken plan.

### 4.3 Provider
- Pluggable behind an interface: Anthropic API, OpenAI-compatible endpoint, or a local Ollama instance.
- Configuration in `settings`, secrets via environment variables only.
- The app must remain fully usable with AI disabled. It is an assistant, not a dependency.

## 5. Print / PDF export

The fridge is a legitimate output device. The app must produce a printable schedule that works without a phone.

### 5.1 Range
- Selectable range: 1 week, 2 weeks, or the full 4-week cycle.
- Start from any week in the cycle, not only week 1.
- Also: a single-day sheet, and a standalone "achterstand" list.

### 5.2 Layout
- One week per page. A 2-week export is two pages; the full cycle is four.
- Portrait A4. Landscape as an option for the 2-week side-by-side variant.
- Grid: days as rows (Monday first), one column per user. Empty days stay visible — a gap on the sheet is information.
- Each task line shows name, room, and an empty checkbox large enough to tick with a pen (minimum 5mm).
- Header per page: week number within the cycle, the calendar dates it covers, and the week's theme.
- Footer: generation date, so nobody works from a sheet that is three cycles old.

### 5.3 Content rules
- The PDF is a **blank checklist**. It shows which tasks are planned for that period and nothing about their status — no strikethrough, no completed marks, no "done by" column. It is meant to be ticked with a pen.
- Export the **generated occurrences**, not the template, so items that were rescheduled appear where they actually sit.
- Only weeks that have already been generated can be exported. The UI states this rather than silently exporting an empty sheet.
- Paper and app do not sync. Anything ticked on paper stays unticked in the app until someone enters it. Say so on the sheet in one line, so nobody assumes otherwise.
- Per-day totals in minutes per user, optional via a toggle.

### 5.4 Technical
- Server-side rendering to PDF, not browser print. The output must be identical regardless of who exports it.
- HTML/CSS template rendered headless (Playwright or Puppeteer) is the pragmatic route; a dedicated PDF library is more work than this needs.
- Black and white safe: no information carried by color alone. Use weight, borders and strikethrough instead.
- Endpoint returns a downloadable file with a predictable name, e.g. `huishoudschema-2026-w38-w39.pdf`.
- Nice to have: a monthly scheduled export mailed or pushed via the same notification channel.

## 6. Non-functional requirements

### Deployment
- Two containers: app + MongoDB, one `docker-compose.yml`.
- Named volume for MongoDB data.
- All configuration via environment variables; no config file baked into the image.
- Healthcheck endpoint, structured JSON logging to stdout.

### Frontend
- Mobile-first PWA. Ticking things off happens one-handed on a phone; planning happens on a desktop. These are two different layouts, not one responsive compromise.
- Drag-and-drop must work with touch, not only mouse.
- Offline-tolerant check-off with sync on reconnect is a want, not a need.

### Identity — profile selection, no login
There is no authentication. The app runs behind the homelab reverse proxy and is not publicly exposed.

- On first open, the user picks a profile from the available users. That choice is stored client-side and persists across sessions.
- A visible profile switcher in the header, one tap. Switching is frequent — one phone may be used by both people.
- The active profile is sent with every write and is what lands in `auditLog.actorId`.
- The profile is a claim, not a verified identity. The audit trail therefore records *which profile was active*, not *who the person was*. This is fine for a household and must not be treated as security.
- A check-off can be attributed to a profile other than the active one ("afgevinkt door" selector), for when one person enters what the other did. The audit entry records both: `actorId` (who pressed) and the occurrence's `completedBy`.
- Design the identity layer as a single replaceable module, so adding real auth later does not touch every endpoint.

### Data
- JSON export and import of the full dataset.
- Nightly `mongodump` to a mounted backup path.
- Timezone-aware date handling throughout; store UTC, render Europe/Amsterdam.

### Localisation
- Dutch UI, English codebase and API.
- Week starts Monday, ISO week numbers.

## 7. API sketch

```
GET    /api/rooms
GET    /api/tasks?roomId=&active=
POST   /api/tasks
PATCH  /api/tasks/:id
GET    /api/cycle-plans/active
PUT    /api/cycle-plans/:id/slots
POST   /api/cycle-plans/:id/activate
GET    /api/occurrences?from=&to=&assigneeId=
PATCH  /api/occurrences/:id        # status, date (drag), assigneeId
GET    /api/due                    # ranked overdue list
POST   /api/ai/propose-plan        # returns draft cyclePlan id
POST   /api/ai/rebalance
GET    /api/stats/workload?cycles=
GET    /api/export/pdf?fromWeek=&weeks=1|2|4
GET    /api/audit?entity=&entityId=&actorId=&from=&to=
```

## 8. Phasing

| Phase | Scope |
|---|---|
| 1 | Domain model, task CRUD, manual template editor, generation, today view with check-off |
| 1b | Audit log wired into every write path from the start |
| 1c | PDF export for 1, 2 and 4 weeks |
| 2 | Due engine, overdue list, drag-to-reschedule, promote-to-template |
| 3 | AI propose and rebalance with draft/diff flow |
| 4 | Statistics, fairness view, notifications (ntfy or Home Assistant webhook) |

## 9. Open questions

1. Does a `daily` task need an occurrence per day at all, or is a simple streak counter enough? 28 occurrences per cycle per daily task is most of your data volume for the least interesting information.
2. When a task's interval changes, do existing generated occurrences for the current cycle get regenerated, or does the change take effect next cycle?
3. Does dragging an occurrence to a day the assignee is unavailable get blocked, or allowed with a warning? The template editor blocks it; the daily view arguably should not.
4. Should two users be able to claim the same occurrence, or is the assignee fixed at generation? "Either" tasks suggest a claim model.
