---
description: React, responsive UI, accessibility, API, and translation rules
applyTo: 'apps/web/**/*.ts,apps/web/**/*.tsx,apps/web/**/*.css'
---

# Web rules

- Keep server state in the existing API/TanStack Query layer; do not fetch ad hoc inside view components.
- Put non-trivial transformations and interaction rules in pure model functions with focused tests.
- Use the existing `t` and `format` helpers. Add every user-facing key to both `nl.ts` and `en.ts`.
- Verify behavior in both mobile and desktop layouts, including role-based navigation.
- Preserve keyboard support, visible focus, semantic labels, and non-color status cues.
- Follow existing optimistic mutation behavior: rollback on failure and refetch after settlement.
- Reuse components from `src/components` and `src/components/ui` before creating new primitives.
- Import shared runtime values from focused subpaths to avoid pulling the full shared index into the bundle.
- Features on `/api/v2` use the generated client (`apiV2`, `unwrap`) from `src/api`; errors are Problem Details, and the code of a problem is the part of `type` after `urn:huishoudplanner:problem:`. After the OpenAPI document changes, run `npm run generate:api -w apps/web` and commit `src/api/v2/schema.d.ts`.
- Entity writes of `/api/v2` (PATCH, PUT and DELETE of users, rooms, tasks, cycle plans and slots, badges, settings and browser notifications) must send `If-Match` (ADR-0022): build the header with `ifMatch(entity)` from the `version` of the entity the person is editing (`params: { path, header: ifMatch(entity) }`; the generated types require it), and put the answered entity back into the cached list with `replaceInList` so a second save needs no re-read. A `412` surfaces as `StaleEntityError` (`isStaleEntity`): re-read the list, keep the unsaved edit in the form, show `app.staleEntity` and let the person save again; a `428` or a `400` on `If-Match` is a programming error and stays a generic error. Entity writes are never queued offline; the offline queue holds only the intent actions of Today (complete, uncomplete, skip), which carry no `If-Match`.
