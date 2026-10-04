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
