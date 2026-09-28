# Keep the House Clean — Claude instructions

The authoritative agent guide for this repository is [AGENTS.md](AGENTS.md). Read it first and follow it exactly; it owns the branching workflow, commit and pull-request rules, the repository map, the non-negotiable architecture constraints, and the testing and completion policy.

Path-specific rules live in `.github/instructions/*.instructions.md`; each file declares the paths it applies to in its `applyTo` frontmatter. Repeatable procedures live in `.agents/skills/*/SKILL.md`.

Do not duplicate the content of `AGENTS.md` here. When a rule changes, change it in `AGENTS.md` (general rules) or in the matching `.github/instructions/*.instructions.md` file (path-specific rules).

Here is a list of instruction files that contain rules for modifying or creating new code.
These files are important for ensuring that the code is modified or created correctly.
Please make sure to follow the rules specified in these files when working with the codebase.
If you have not already read the file, use the `view` tool to acquire it.
Make sure to acquire the instructions before making any changes to the code.
| Pattern | File Path | Description |
| ------- | --------- | ----------- |
| .github/workflows/*.yml, .github/workflows/*.yaml, release-please-config.json, .release-please-manifest.json, CHANGELOG.md, version.txt, docs/RELEASING.md, docker/**, docker-compose.yml | '.github/instructions/release.instructions.md' | Release Please, GitHub Actions, version, and container publication rules |
| apps/server/**/*.ts | '.github/instructions/server.instructions.md' | Server layering, authorization, persistence, and audit rules |
| packages/shared/**/*.ts | '.github/instructions/shared-domain.instructions.md' | Shared API schema, time, cycle, and validation constraints |
| **/*.test.ts, **/*.test.tsx, **/*.spec.ts, **/*.spec.tsx | '.github/instructions/tests.instructions.md' | Deterministic regression-test conventions |
| apps/web/**/*.ts, apps/web/**/*.tsx, apps/web/**/*.css | '.github/instructions/web.instructions.md' | React, responsive UI, accessibility, API, and translation rules |
