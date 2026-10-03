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

# Context maintainer: weekly look

## Which commits to read

Read the commits of the last eight days (`git log --since='8 days ago'`), and also check the
files listed in *What you keep current* against the repository as it is now.

For check 6, look at the whole repository and its full history. You may create at most two new
instruction files in this run; list any further candidates in the pull request body.
