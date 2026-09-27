---
# Thoroughly checks the instructions agents read every week or when started by hand. The outcome
# is at most one pull request that a human reviews; the workflow never commits to main.
on:
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

imports:
  - shared/context-maintainer.md
---

# Context maintainer: weekly look

## Which commits to read

Read the commits of the last eight days (`git log --since='8 days ago'`), and also check the
files listed in *What you keep current* against the repository as it is now.

For check 6, look at the whole repository and its full history. You may create at most two new
instruction files in this run; list any further candidates in the pull request body.
