---
# Keeps the instructions agents read in step with the repository. A quick look after every
# push to main, a thorough one every week, and either by hand. The outcome is at most one
# pull request that a human reviews; the workflow never commits to main.
on:
  push:
    branches: [main]
  schedule: weekly
  workflow_dispatch:

permissions:
  contents: read
  issues: read
  pull-requests: read
  # Copilot inference with the workflow's own token, billed to the owning organization.
  copilot-requests: write

engine:
  id: copilot
  # Maintenance reading and small text edits do not need a frontier model.
  model: claude-haiku-4.5
strict: true
timeout-minutes: 20
# 1 AI credit is $0.01.
max-ai-credits: 100
max-daily-ai-credits: 300

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
      - AGENTS.md
      - CLAUDE.md
      - README.md
      - .github/copilot-instructions.md
      - .github/instructions/**/*.instructions.md
      - .cursor/rules/*.mdc
      - .agents/skills/**/SKILL.md
  noop:
---

# Context maintainer: keep the agent instructions current

You check whether the instructions agents read in this repository still match the repository,
and propose the smallest set of changes that makes them match again.

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
- `README.md` (English) documents the supported product, setup, operation, and configuration.

No `.claude/skills/`, `.github/skills/`, `.github/prompts/`, or `.cursor/skills/` directory
currently exists.

Nothing else. Do not touch other docs, changelogs, application or test code, configuration,
workflow files, generated files, or release files, even when you see something wrong there:
mention it in the pull request body instead. In particular, never change `docs/**`,
`CHANGELOG.md`, `version.txt`, `.release-please-manifest.json`, `release-please-config.json`,
or `.github/workflows/**`.

## Which commits to read

This run was started by `${{ github.event_name }}`.

- **`push`** — a quick look. Read only the commits of this push:
  `git log --format='%h %s%n%b' ${{ github.event.before }}..${{ github.event.after }}` and their
  diffs. If every commit is a release or only changes the files listed above, stop with `noop`.
- **`schedule` or `workflow_dispatch`** — a thorough look. Read the commits of the last eight
  days (`git log --since='8 days ago'`), and also check the files above against the repository
  as it is now.

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
