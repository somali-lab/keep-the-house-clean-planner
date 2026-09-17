---
name: change-web-feature
description: Implements or reviews Keep the House Clean React features across mobile and desktop layouts, API/query state, Dutch and English translations, accessibility, offline behavior, and UI tests. Use for pages, components, navigation, forms, styling, interactions, or web regressions.
---

# Change a web feature

## Workflow

1. Inspect the feature page, its API module, pure model helpers, colocated tests, both layouts, and relevant E2E journey.
2. Keep remote state and mutations in the existing TanStack Query/API layer. Keep derived domain/UI behavior in pure functions when it can be tested without React.
3. Reuse the existing UI primitives and visual language. Check narrow mobile and wide desktop behavior, not only the currently visible layout.
4. Add all user-facing copy to both `apps/web/src/i18n/nl.ts` and `apps/web/src/i18n/en.ts`; use existing `t`/`format` helpers in components.
5. Preserve role-based navigation and server authorization. Hiding a control is not an authorization boundary.
6. For optimistic actions, update the cache consistently, roll back on error, show actionable feedback, and refetch after settlement.
7. Preserve offline queue semantics for completion actions: replay with the original profile and handle conflicts visibly.

## Interaction checklist

- [ ] Reachable and usable by keyboard.
- [ ] Controls have semantic names and errors are associated with fields.
- [ ] Status is not conveyed through color alone.
- [ ] Touch targets and drag alternatives remain usable on mobile.
- [ ] Loading, empty, error, and disabled states are explicit.
- [ ] Dutch and English both render without hard-coded copy.
- [ ] Mobile and desktop navigation still expose the correct features for the active role.

## Tests

Prefer pure `.test.ts` coverage for models and Testing Library for user interaction. Use `renderWithProviders()` and existing fixtures. Add or update Playwright only when the change spans routing, the real API/database, offline recovery, PDF/downloads, or another critical journey.

Finish with the `verify-household-planner` skill.
