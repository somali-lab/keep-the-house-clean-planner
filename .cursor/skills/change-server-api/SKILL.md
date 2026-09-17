---
name: change-server-api
description: Implements or reviews Keep the House Clean server/API changes with shared schemas, Fastify routes, role guards, MongoDB persistence, audit logging, and integration coverage. Use for endpoints, data writes, configuration, jobs, imports, or server business rules.
---

# Change the server API

## Trace the change

Before editing, inspect the matching shared schema, route, domain function, data repository, audit behavior, web consumer, and tests. Search `docs/DECISIONS.md` for the entity or feature name.

## Implementation order

1. Define or update the API input/output contract in `packages/shared/src/schemas/` when the wire shape changes.
2. At the route boundary, parse with shared schemas and `parseOrThrow()`, parse ids with the HTTP helpers, apply the correct role guard, and serialize BSON with `toApi()`.
3. Put orchestration and cross-entity rules in `apps/server/src/domain/`.
4. Put every MongoDB read/write primitive in `apps/server/src/data/`.
5. Pass `AuditContext` to state-changing data functions. Compute a diff, skip unchanged writes, and record each actual change with the correct entity/action/meta.
6. Preserve the standard error shape and status conventions: validation 400, unknown entity 404, state conflict 409, semantically invalid plan 422.
7. Update web API consumers and both translation catalogs when the user-visible contract changes.

## Audit and authorization checklist

- [ ] The route uses `requirePlanner` or `requireAdmin` when required.
- [ ] A missing/invalid actor cannot create an unattributed write.
- [ ] Every database mutation has an audit record, including bulk and derived writes.
- [ ] No-op operations create neither a write nor an audit record.
- [ ] Audit data excludes secrets and remains meaningful after entities become inactive or disappear.
- [ ] The write-route coverage scenario list includes every new non-GET route.

## Tests

Use `createTestApp()` for route and persistence behavior. Cover success, invalid schema/reference, missing permissions, not found/conflict, no-op idempotency, audit content, and write-with-audit coupling as relevant. Use injected fake providers for AI and notifications.

Finish with the `verify-household-planner` skill.
