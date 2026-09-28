---
# Checks agent instructions after every push to main. The outcome is at most one pull request
# that a human reviews; the workflow never commits to main.
on:
  push:
    branches: [main]

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

imports:
  - shared/context-maintainer.md
---

# Context maintainer: per-release look

## Which commits to read

Read only the commits of this push:
`git log --format='%h %s%n%b' ${{ github.event.before }}..${{ github.event.after }}` and their
diffs. If every commit is a release or only changes the files listed in _What you keep current_,
stop with `noop`.

Do not create new files in this run: name any missing instruction file under its own heading in
the pull request body, and leave creating it to the weekly run.
