# Repository agent guide

These instructions apply to the entire repository. More focused rules live in `.cursor/rules/`, and repeatable workflows live in `.cursor/skills/`.

## Start every task

1. Read `git status` and preserve changes you did not make.
2. Before editing, create and switch to a dedicated feature branch with the `codex/` prefix, unless already on a suitable non-default branch. Never implement changes directly on `main`.
3. Locate the implementation, its nearest tests, and the relevant entries in `docs/DECISIONS.md` before editing.
4. Trace cross-layer changes end to end: shared contract -> server route/domain/data -> web API/UI -> tests.
5. Prefer the smallest coherent change. Do not refactor unrelated code or update dependencies incidentally.
6. Commit every completed coherent change with a Conventional Commit message, even when the user does not ask separately. Keep unrelated user changes out of the commit and leave no finished work uncommitted; these commits are the source for Release Please changelog and release notes.
7. Never push, publish, deploy, restore data, or modify a real installation unless the user explicitly asks.

## Pull requests and release notes

1. Before creating or updating a pull request, compare the branch with its target and identify every distinct release-worthy change.
2. Use a Conventional Commit pull-request title.
3. For a squash-merged pull request with multiple release-note entries, automatically add the documented `BEGIN_COMMIT_OVERRIDE` block to the pull-request description. Include one valid Conventional Commit line per logical change. Consolidate fixup and iteration commits, but never collapse unrelated changes into one vague entry.
4. After publishing or updating the pull request, read its description back and verify that both override markers and all intended entries are present before reporting the pull request as complete.
5. Do not manually edit `version.txt`, `.release-please-manifest.json`, or `CHANGELOG.md`; Release Please owns those files in its release pull request.

## Sources of truth

When sources disagree, use this order and call out the conflict:

1. Executable code and tests.
2. `docs/DECISIONS.md` for intentional architecture and domain choices.
3. `README.md` and `docs/RELEASING.md` for supported operation and release behavior.
4. `docs/huishoudplanner-requirements.md` and `docs/implementation-plan.md` for product intent and historical planning.

Update documentation in the same change when public behavior, configuration, architecture, or an intentional decision changes.

## Repository map

- `packages/shared`: Zod API schemas, shared types, calendar/cycle logic, due logic, and plan validation.
- `apps/server/src/routes`: Fastify HTTP boundary, parsing, permissions, and serialization.
- `apps/server/src/domain`: orchestration and business rules.
- `apps/server/src/data`: MongoDB access and the only permitted location for raw database writes.
- `apps/server/src/audit`: actor context, diffs, and audit recording.
- `apps/server/test`: API, integration, audit, PDF, backup, and configuration tests.
- `apps/web/src/api`: typed HTTP client and shared queries.
- `apps/web/src/features`: feature UI plus colocated component/model tests.
- `apps/web/src/i18n`: Dutch and English message catalogs and language runtime.
- `apps/web/e2e`: isolated Playwright journeys against the real server and a fresh database.
- `docker`, `docker-compose.yml`, `scripts/smoke.mjs`: production image and isolated container smoke test.

## Non-negotiable architecture

- Runtime is Node.js 24+ with strict ESM. Server and shared TypeScript run directly through Node type stripping; keep `.ts` extensions on relative imports and do not introduce a server build output.
- API contracts belong in `packages/shared`. API ids are 24-character hex strings, calendar dates are `YYYY-MM-DD` day keys, and instants are ISO strings. BSON `ObjectId` and `Date` stay server-side.
- Calendar and cycle calculations must use shared day-key helpers. Do not replace them with local `Date` arithmetic; DST behavior is part of the contract.
- All MongoDB writes belong in `apps/server/src/data/`. Every actual state change must have a corresponding audit entry; no-op updates write and audit nothing.
- A selected profile is attribution, not authentication. Keep the documented trusted-network warning. Enforce existing role guards (`requirePlanner`, `requireAdmin`) on protected write routes.
- Do not expose configuration values in validation errors or logs. Never commit `.env`, tokens, database dumps, backups, or generated reports.
- Web runtime values from shared modules should use focused subpath imports. Type-only imports may use `@huishoudplanner/shared`.
- User-facing text goes through the existing i18n helpers and must be added to both Dutch and English catalogs.
- UI changes must work in mobile and desktop layouts, remain keyboard accessible, and not rely on color alone.

## Testing and completion

- Add or update a regression test with every behavior change. Prefer pure model tests for logic, component tests for interaction, server integration tests for HTTP/persistence, and Playwright only for critical cross-stack journeys.
- Server tests use `createTestApp()` with a fresh database and fixed clock. Tests must never call real AI providers, notification endpoints, or a real installation.
- Run focused tests while iterating, then the checks selected by `.cursor/skills/verify-household-planner/SKILL.md`.
- `npm run verify` is the default full quality gate. Use `npm run test:e2e` for affected end-to-end flows and `node scripts/smoke.mjs` only for container/runtime changes.
- Report exactly what was changed and what was verified. If a relevant check could not run, state why.

## Project skills

- `verify-household-planner`: choose and run the proportional validation set.
- `change-server-api`: change shared contracts, Fastify routes, domain logic, persistence, and audit safely.
- `change-web-feature`: change React features, translations, responsive layouts, offline behavior, and UI tests.
- `change-scheduling-domain`: change cycles, slots, occurrences, due calculations, activation, or plan validation.
- `release-household-planner`: change release automation, versions, changelog behavior, or container publishing.
