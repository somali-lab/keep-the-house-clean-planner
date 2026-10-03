---
# Checks the requirements and the README feature description every week or when started by hand.
# A manual run can audit the whole documents against the code. The outcome is at most one pull
# request that a human reviews; the workflow never commits to main.
on:
  schedule: weekly
  workflow_dispatch:
    inputs:
      full:
        description: 'Audit the whole requirements document and README feature description against the current code instead of only the last eight days'
        type: boolean
        required: false
        default: false

permissions:
  contents: read
  issues: read
  pull-requests: read
  # Copilot inference with the workflow's own token, billed to the owning organization.
  copilot-requests: write

engine:
  id: copilot
  # Comparing described behaviour with code and tests needs more reasoning than the Haiku model
  # of the context maintainer offers; a Sonnet-class model is the cheapest step up.
  model: claude-sonnet-5
strict: true
timeout-minutes: 30
# 1 AI credit is $0.01. A full audit reads the code behind every section of the requirements, so
# a run gets twice the budget of the context maintainer. The daily cap lets a scheduled run and a
# manual full audit of the same day both finish. A run that reaches its cap must report the
# sections it did not reach instead of guessing.
max-ai-credits: 200
max-daily-ai-credits: 400

imports:
  - shared/docs-maintainer.md
---

# Docs maintainer: weekly look

## Which commits to read

The `full` input of this run is `${{ github.event.inputs.full }}`. A scheduled run has no input,
so it is empty and counts as `false`.

- **`full` is `false` or empty:** read the commits of the last eight days
  (`git log --since='8 days ago'`) and follow the diff-based approach in *How to work*. If there
  are no commits in that window that change behaviour, stop with `noop`.
- **`full` is `true`:** do a full audit as described in *Full audit*, not limited to recent
  commits.
