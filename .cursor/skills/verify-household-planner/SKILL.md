---
name: verify-household-planner
description: Selects and runs proportional checks for Keep the House Clean changes. Use after implementing, fixing, refactoring, or reviewing repository code, tests, Docker files, or release automation.
---

# Verify Keep the House Clean

## Workflow

1. Inspect `git status --short` and `git diff --stat` to identify the changed surfaces.
2. Run the narrowest relevant tests first so failures remain attributable.
3. Run type checking for every affected workspace.
4. Run the broader quality gate when the change crosses layers or is ready to hand off.
5. Report commands and outcomes; never imply an unrun check passed.

## Check selection

### Shared logic or schemas

```powershell
npx vitest run packages/shared/src/<file>.test.ts
npm run typecheck --workspace packages/shared
```

Also test affected server and web consumers when an exported contract changes.

### Server/API/data

```powershell
npx vitest run apps/server/test/<area>.test.ts
npm run typecheck --workspace apps/server
```

Include `apps/server/test/audit-coverage.test.ts` and `apps/server/test/write-routes-coverage.test.ts` when a write route or persistence operation changes.

### Web UI/model

```powershell
npx vitest run apps/web/src/features/<feature>/<file>.test.tsx
npm run typecheck --workspace apps/web
```

Use the `.test.ts` variant for pure models. Run `npm run test:e2e --workspace apps/web -- --grep "<journey>"` when routing, persistence, offline behavior, export, or a critical user journey changes.

### Full repository gate

```powershell
npm run verify
npm run build
```

Run both for cross-workspace changes. `npm run verify` is mandatory before calling a substantial implementation complete unless the user asks for a narrower check.

### Container/runtime

For Docker, browser/PDF runtime, backup, static-serving, or Compose changes:

```powershell
docker build --file docker/Dockerfile --tag keep-the-house-clean-planner:verify .
node scripts/smoke.mjs
```

The smoke test uses an isolated Compose project, port, backup directory, and volumes, then removes them. Do not substitute a real installation.

## Failure handling

- Fix failures caused by the current change and rerun the failed check.
- Distinguish pre-existing failures with evidence; do not rewrite unrelated code to hide them.
- If a check needs unavailable Docker, Chromium, MongoDB downloads, or network access, state the exact limitation and run the remaining local checks.
