---
# Checks the requirements and the README feature description after a Release Please pull request
# is merged. Updates to the open release branch do not match `main`, and ordinary merges do not
# change the filtered release files. The outcome is at most one pull request that a human reviews;
# the workflow never commits to main.
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
  # Comparing described behaviour with code and tests needs more reasoning than the Haiku model
  # of the context maintainer offers; a Sonnet-class model is the cheapest step up.
  model: claude-sonnet-5
strict: true
timeout-minutes: 30
# 1 AI credit is $0.01. A release run reads the diffs of a whole release and the requirement
# sections they touch, so it gets twice the budget of the context maintainer. The daily cap lets
# a release run and the weekly run of the same day both finish, with room for one manual run.
max-ai-credits: 200
max-daily-ai-credits: 400

imports:
  - shared/docs-maintainer.md
---

# Docs maintainer: per-release look

## Which commits to read

This run starts when Release Please metadata reaches `main`, identifying a merged release pull
request. Exclude that release commit itself. Find the previous release tag reachable from its
first parent with
`git describe --tags --abbrev=0 "${{ github.event.after }}^1"`.

Read the commit messages and diffs from that tag through the release commit's first parent. For
example, use
`git log --format='%h %s%n%b' <previous-tag>..${{ github.event.after }}^1`.
If no previous release tag exists, read all commits through `${{ github.event.after }}^1`
instead, and do a full audit as described in *Full audit*.
If the resulting range is empty, only changes the files listed in *What you keep current*, or
only changes things the requirements and README do not describe, stop with `noop`.
