---
# Checks agent instructions after a Release Please pull request is merged. Updates to the open
# release branch do not match `main`, and ordinary merges do not change the filtered release files.
# The outcome is at most one pull request that a human reviews; the workflow never commits to main.
on:
  push:
    branches: [main]
    paths:
      - version.txt
      - .release-please-manifest.json

permissions:
  contents: read
  issues: read
  pull-requests: read
  # Copilot inference with the workflow's own token, billed to the owning organization.
  copilot-requests: write

engine:
  id: copilot
  # Judging whether instructions and skills still cover the implementation needs more reasoning
  # than Haiku showed on release 1.7.0 (it missed the new rewards domain); Sonnet is the cheapest step up.
  model: claude-sonnet-5
strict: true
timeout-minutes: 20
# 1 AI credit is $0.01.
max-ai-credits: 200
max-daily-ai-credits: 400

imports:
  - shared/context-maintainer.md
---

# Context maintainer: per-release look

## Which commits to read

This run starts when Release Please metadata reaches `main`, identifying a merged release pull
request. Exclude that release commit itself. Find the previous release tag reachable from its
first parent with
`git describe --tags --abbrev=0 "${{ github.event.after }}^1"`.

Read the commit messages and diffs from that tag through the release commit's first parent. For
example, use
`git log --format='%h %s%n%b' <previous-tag>..${{ github.event.after }}^1`.
If no previous release tag exists, read all commits through `${{ github.event.after }}^1` instead.
If the resulting range is empty or only changes the files listed in _What you keep current_, stop
with `noop`.

Do not create new files in this run: name any missing instruction file under its own heading in
the pull request body, and leave creating it to the weekly run.
