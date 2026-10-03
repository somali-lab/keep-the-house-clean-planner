# Build

The implementation plan for the work that is running right now, written as vertical slices that each end in something demonstrable. Every slice carries a checkbox, so this file is both the plan and the current state.

A multi-step assignment starts by writing its slices here and ticks them off while it runs. A single small change does not need a plan and leaves this file untouched.

Once the work is finished, this file is emptied. Completed work lives in the Git history and in [CHANGELOG.md](../CHANGELOG.md); it is not archived here.

## Running: slice 0.3a, .NET solution skeleton

Part of [the .NET rewrite plan](plans/dotnet-rewrite.md), phase 0. Slice 0.3 itself completes after 0.3b (architecture rules).

- [x] Solution `apps/api/Huishoudplanner.slnx` with the nine `src` projects and four `tests` projects of plan §3.1, project references as specified.
- [x] `Directory.Build.props` (net10.0, nullable, warnings as errors, analyzers, version from `version.txt`), `Directory.Packages.props` (central versions), `global.json`.
- [x] One smoke test per test project; `dotnet build` and `dotnet test` green with zero warnings.
- [x] CI job `dotnet-build-test` in `ci.yml`.
- [x] `apps/api/README.md`, `bin/` and `obj/` in `.gitignore`.
