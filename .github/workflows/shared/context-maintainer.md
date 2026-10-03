---
checkout:
  fetch-depth: 0

tools:
  edit:
  bash: ['git:*']
  github:
    toolsets: [repos, pull_requests]

network: defaults

safe-outputs:
  create-pull-request:
    draft: false
    max: 1
    title-prefix: '[context] '
    # A proposal nobody took up in a week is stale; the next weekly run makes a fresh one.
    expires: 7d
    if-no-changes: 'ignore'
    # Instruction files are protected by default; they are the only ones this workflow may touch.
    protected-files: allowed
    allowed-files:
      # The root file is listed on its own: "**/AGENTS.md" does not match a top-level AGENTS.md here.
      - AGENTS.md
      - "**/AGENTS.md"
      - CLAUDE.md
      - README.md
      - .github/copilot-instructions.md
      - .github/instructions/*.instructions.md
      - .github/instructions/**/*.instructions.md
      - .cursor/rules/*.mdc
      - .agents/skills/**/SKILL.md
  noop:
---

# Context maintainer: keep the agent instructions current

You check whether the instructions agents read in this repository still match the repository,
and whether an instruction file is missing where agents need one. Which commits to read, and
whether you may create new files, is said in the section *Which commits to read*.

## What you keep current

- `AGENTS.md` (English) is the authoritative repository-wide guide: workflow, architecture,
  repository map, and testing policy.
- `CLAUDE.md` and `.github/copilot-instructions.md` (English) are thin entry points that
  delegate to `AGENTS.md`.
- `.github/instructions/**/*.instructions.md` (English) contains path-specific GitHub Copilot
  rules selected by `applyTo`.
- `.cursor/rules/*.mdc` (English) contains the matching Cursor rules selected by `globs` or
  `alwaysApply`. These are manually maintained counterparts of `.github/instructions`; no sync
  script exists. Keep paired rule bodies equivalent while preserving their different frontmatter.
- `.agents/skills/**/SKILL.md` (English) contains repeatable procedures for verification,
  releases, server/API changes, web changes, and scheduling-domain changes.
- `README.md` (English) documents setup, operation, and configuration: quick start, releases,
  configuration, scheduled jobs, backups, integrations, security, local development, and
  project structure. The section **What it does** and every other description of product
  features and behaviour belong to the `docs-maintainer` workflow, together with
  `docs/huishoudplanner-requirements.md`. Do not edit them; if one is stale, mention it in the
  pull request body.

No `.claude/skills/`, `.github/skills/`, `.github/prompts/`, or `.cursor/skills/` directory
currently exists.

Nothing else. Do not touch other docs, changelogs, application or test code, configuration,
workflow files, generated files, or release files, even when you see something wrong there:
mention it in the pull request body instead. In particular, never change `docs/**`,
`CHANGELOG.md`, `version.txt`, `.release-please-manifest.json`, `release-please-config.json`,
or `.github/workflows/**`.

## What to check

1. **Everything named still exists.** Every path, folder, project, script, command, workflow,
   job, and skill the files mention exists and does what the file says. Read the source to
   confirm; do not run builds or tests.
2. **Every rule still matches what is built.** Where code and text disagree, the code is what
   is and the text is what you change — unless the text states a rule the code breaks; then
   leave the text and name the break in the pull request body.
3. **Nothing new is missing.** A new folder, project, script, workflow, command, or convention
   an agent needs to know, that none of the files mentions.
4. **Nothing is said twice.** The same rule, list, or command in two files, or twice in one:
   keep it where it belongs and refer to it from the other place.
5. **A skill still fits its trigger.** Its `description` still says when to use it, and its
   body still matches the code it describes.
6. **No instruction file is missing.** Look for:
   - a folder with its own stack or conventions (its own package manifest, project file,
     test framework or build) that differ from the root, without a nested `AGENTS.md`;
   - a task that recurs in the history — the same kind of change in at least three commits,
     with a clear sequence of steps — and no skill for it: propose a new skill with `name`
     and `description` frontmatter, the description saying when to use it;
   - a new business domain with invariants an agent can break without noticing — signs are a
     new collection or derived data, a recomputation or single-writer rule, its own ADR, or
     review fixes in the history for the same kind of mistake — that no skill or scoped
     instruction covers: propose a skill that states those invariants and points to the ADR
     and requirements sections instead of repeating them.
   Only propose a file that tells an agent something it cannot read straight from the code.
   No file is better than a thin one. Each new file cites its evidence (paths, commits) in
   the pull request body.

## How to write the changes

- Change as little as possible: fix what is wrong or missing, do not rewrite what is right.
- Follow the repository's own writing rules, and keep each file in English.
- Commit with Conventional Commits, English, imperative mood (for example,
  `docs(agents): name the new api project`). Put in the body which commit made the text stale.

## The outcome

- If anything changed, open **one** pull request. Title: a plain English sentence, not a
  Conventional Commit. Body: per change, what was wrong, which commit caused it, what now
  stands; then, under its own heading, anything wrong you saw outside the files you may change.
- If nothing needs to change, finish with `noop` and one sentence saying what you checked.
