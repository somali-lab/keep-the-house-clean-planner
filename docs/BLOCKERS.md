# Blockers

Open questions for the maintainer, and anything else that stops the work from continuing: a missing permission, an unavailable dependency, a choice only the maintainer can make.

This file is for work that cannot ask. An agent in a live conversation asks the question directly; an agent running unattended writes it here and stops at that point instead of guessing.

An entry names what is blocked, what is needed to unblock it, and who can supply that. The answer is written to its permanent home, the entry is removed, and this file is empty again.

## Slice 7.2: task points on update, and deleting a task

- **Reset points to the default on update.** The web app no longer holds `defaultPointsForDuration` (plan §4.3), and `PATCH /api/v2/tasks/{id}` cannot ask for the default (a missing `points` keeps the old value, an explicit `null` is a 400). The task form therefore no longer shows the default as placeholder, and on an existing task an empty points field is refused with "Enter the points." Requirements 4.12 still say that clearing the field hands the points back to the duration. Needed: the maintainer decides whether v2 gets that (for example `points: null` on PATCH meaning "default for the duration", then the form can offer it again) or whether requirements 4.12 is changed to "a new task defaults, an existing task needs a value". Supplied by: maintainer.
- **Permanent delete of a task.** `apps/api` has no `DELETE /api/v2/tasks/{id}` yet (the Http adapter says it arrives with the plans and badges it must be removed from). The delete button on the task list therefore still calls the Node `DELETE /api/tasks/{id}`. Needed: a v2 endpoint (an API slice, not a web slice) before 7.7; until then slice 7.2 is not ticked in the plan. Supplied by: the API work of phases 2 and 4.
