# ADR-0007 — Release automation and build identity

Status: Accepted

## Context

The application updates itself in place on somebody's home server. When something breaks there, the first two questions are "which version is this?" and "how do I go back?". Hand-maintained version numbers answer neither reliably, and a version string alone cannot distinguish a published image from a build somebody made locally while debugging.

## Decision

Versioning, changelog, tags, releases, and the published image are derived from commit history by automation. Version metadata files are owned by that automation and are not edited by hand.

Conventional commit messages are the input. A pull request that contains several release-worthy changes declares them explicitly, so squashing does not collapse distinct changes into one entry.

Publication uses short-lived credentials issued to the workflow. No long-lived token exists in the repository.

Each release publishes the exact version, the major and minor aliases, a moving latest tag, and the commit digest, so an installation can follow updates or pin exactly.

The version shown in the interface identifies its origin. A published image shows the plain release version; a build made from local source shows the same base version marked as local, with the moment it was built.

A published image also carries the moment of its release, taken from the release commit at build time rather than fetched from GitHub while running. A local build carries no release moment, because it is not a release.

## Consequences

- The changelog is a by-product of committing properly rather than a separate chore.
- A rollback is selecting a different tag.
- "It works on my machine" is visible in the interface, because a local build says so.
- Commit message discipline is load-bearing. A wrong type produces a wrong version, and that is why the rules are written down rather than assumed.
