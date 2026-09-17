---
name: release-household-planner
description: Changes or reviews Keep the House Clean release automation, Conventional Commit metadata, Release Please, changelog/version handling, GitHub Actions, GHCR image publication, or release recovery. Use for releases, CI publication, versioning, and container tags.
---

# Release Keep the House Clean

Read `docs/RELEASING.md`, the three workflows in `.github/workflows/`, `release-please-config.json`, and `.release-please-manifest.json` before changing release behavior.

## Normal change flow

- Use a Conventional Commit pull-request title. `fix` creates a patch, `feat` a minor, and `feat!` a major release.
- For several user-visible entries in one squash merge, prepare the documented `BEGIN_COMMIT_OVERRIDE` block with one valid Conventional Commit line per entry.
- Do not manually edit `version.txt`, `.release-please-manifest.json`, or `CHANGELOG.md`; the Release Please pull request owns them.
- Do not publish or merge anything unless the user explicitly asks.

## Automation constraints

- Keep third-party actions pinned to full commit SHAs.
- Grant the smallest job/workflow permissions possible.
- Use the repository-scoped `GITHUB_TOKEN`; never add a long-lived registry or homelab credential.
- Preserve immutable `sha-<full-commit>` plus semantic, rolling minor/major, and `latest` image tags.
- Keep OCI source/version/revision labels, SBOM, and provenance enabled.
- The documented production target is `linux/amd64`.
- A failed publication can be safely retried for the same released SHA and version through the publish workflow.

## Verification

Run `npm run verify`, `npm run build`, and a Docker image build for release automation or runtime image changes. Run `node scripts/smoke.mjs` when Compose, the runtime image, browser/PDF support, backup tooling, health checks, or static serving changes.

Report proposed release type and release-note entries, but leave the actual version decision to Release Please unless explicitly instructed otherwise.
