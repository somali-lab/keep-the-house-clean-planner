# Build

The implementation plan for the work that is running right now, written as vertical slices that each end in something demonstrable. Every slice carries a checkbox, so this file is both the plan and the current state.

Current assignment: slice 0.2 of [docs/plans/dotnet-rewrite.md](plans/dotnet-rewrite.md), skills and agent context for `apps/api`.

- [x] Copy `hexagonal-arch-dotnet`, `mongodb-persistence` and `xunit-tdd-workflow` from dark-factory into `.agents/skills/` and adapt them (ObjectId ids, replica-set transactions with audit, plan section 3.1 layout, test stack, no MediatR, OneOf errors).
- [x] AGENTS.md: .NET rules, repository map entry for `apps/api`, scoped instruction index row, skills list, `next` as base branch for rewrite work.
- [x] `.github/instructions/api.instructions.md` and its Cursor mirror `.cursor/rules/api.mdc`; tests rule extended to `apps/api/tests/**/*.cs` in both mirrors.
- [x] `verify-household-planner`: short pointer to the .NET commands, marked "once apps/api exists".
- [x] Tick 0.2 in the plan; prettier check on the changed files.
