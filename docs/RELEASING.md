# Releasing

This repository uses [Release Please](https://github.com/googleapis/release-please-action) to create semantic versions, release notes, GitHub Releases, and versioned container images.

## Release flow

1. Merge changes into `main` using a Conventional Commit title and the release-note rules below.
2. The **Release** workflow opens or updates a release pull request.
3. Review the generated version and `CHANGELOG.md`, then merge the release pull request.
4. The workflow creates the Git tag and GitHub Release.
5. The released commit is built and published to GHCR.

Merging the release pull request also starts the release-triggered agent-context
check because Release Please updates `version.txt` and
`.release-please-manifest.json` on `main`. Ordinary merges and updates to the open
release pull request do not start that check. See
[Agentic workflow checks](AGENTIC-WORKFLOWS.md) for the complete process.

Normal pull requests run linting, type checking, tests, the Playwright end-to-end suite, and a container build before they are merged. The release workflow does not repeat those checks; it only manages the release and builds the image that is actually published.

The end-to-end suite (`npm run test:e2e`) runs in its own CI job in parallel with the other jobs, with four workers and one retry, against the .NET host on a throwaway MongoDB replica set in Docker; it blocks the merge like every other job. On failure the job uploads the Playwright report and traces as an artifact. The container smoke test (`node scripts/smoke.mjs`) deliberately stays out of CI because it needs a real container runtime; run it locally when a change touches the container or runtime.

Both **CI** and **Release** also accept a manual `workflow_dispatch` run from the Actions tab, which is how a release pull request is re-created after an interrupted run without pushing an empty commit.

No repository secret is needed. The workflows publish with GitHub's short-lived `GITHUB_TOKEN`; runtime or homelab credentials do not belong in this repository.

## Commit and pull-request titles

Release Please derives the next semantic version from Conventional Commit messages. When pull requests are squash-merged, use a Conventional Commit title for the pull request:

Repository agents first fetch `origin/main`, fast-forward local `main`, and create a dedicated feature branch named with a Conventional Commit type prefix followed by the agent tool's name and a short description (e.g. `feat/codex-add-export`, `fix/copilot-due-date-bug`) from that updated source. They commit every completed coherent change using this format, without requiring a separate branch or commit request. This keeps `main` current and protected, and keeps the unreleased history usable as the input for generated changelog and release-note entries. Agents still never push, publish, merge, or deploy without explicit permission.

- `fix: correct overdue task calculation` creates a patch release.
- `feat: add a monthly planning view` creates a minor release.
- `refactor: simplify route handling` creates a patch release.
- `feat!: replace the cycle configuration format` creates a major release.
- `docs: explain backup recovery` creates a patch release.
- `chore: update dependencies` creates a patch release.

Every type configured in `release-please-config.json` is visible in the changelog and bumps the version. `docs`, `chore`, `test`, `style`, `ci` and `build` are therefore releasable too, each as a patch. A documentation-only change to this repository produces a new patch version, and that is intentional: the published image and the version shown in the app then match the state of the repository they were built from.

Scopes are optional, for example `feat(planner): add keyboard controls`.

### Several release-note entries in one pull request

One pull request per commit is not required. Prefer one coherent pull request and squash-merge it. When that pull request contains several changes that each deserve their own release-note entry, add this block to the pull-request description before merging:

```text
BEGIN_COMMIT_OVERRIDE
fix(planner): keep the week overview in sync
feat(planner): filter scheduled tasks by person
test(planner): cover plan synchronization regressions
END_COMMIT_OVERRIDE
```

List every intended changelog entry as a valid Conventional Commit line. Release Please then uses those lines instead of reducing the pull request to only its squash title. Review the generated release pull request and confirm that each line appears under the configured section in `CHANGELOG.md`.

This works because the repository sets the default squash commit message to the pull-request title and description (`squash_merge_commit_title: PR_TITLE`, `squash_merge_commit_message: PR_BODY` under Settings, Pull Requests). Without that setting GitHub fills the commit body with the individual branch commits, the override block never reaches `main`, and the extra entries are silently lost.

Repository agents add this override automatically whenever a squash-merged pull request contains multiple release-worthy changes. They derive the logical entries from the complete branch diff and commit history, consolidate fixup or iteration commits, and keep distinct changes separate. After creating or updating the pull request, they read the published description back to verify the exact markers and entries.

Commit overrides only work for squash merges. Do not use a plain merge for a pull request that depends on this block. If the pull request has one release-note entry, a Conventional Commit pull-request title is sufficient.

## Published image tags

For a release such as `v1.4.2`, the workflow publishes:

- `ghcr.io/somali-lab/keep-the-house-clean-planner:1.4.2`
- `ghcr.io/somali-lab/keep-the-house-clean-planner:1.4`
- `ghcr.io/somali-lab/keep-the-house-clean-planner:1`
- `ghcr.io/somali-lab/keep-the-house-clean-planner:latest`
- `ghcr.io/somali-lab/keep-the-house-clean-planner:sha-<full-commit-sha>`

Production can follow `latest` for automatic updates or pin the full semantic version or commit SHA for deterministic rollbacks.

The version shown in the app also identifies its origin. Published GHCR images pass `APP_OFFICIAL_BUILD=true` and show the exact Release Please version, for example `1.3.0`. A build made directly from local source uses the same base version with a UTC build timestamp, for example `1.3.0-local-20260920-093245Z`. The timestamp is generated once when Vite starts the build, so every asset from that build reports the same identifier.

Published images also pass `APP_RELEASE_DATE`: the commit timestamp of the release commit, read by the publish workflow with `git log -1 --format=%cI`. The About page shows it as the latest release. Republishing an existing release reads the same commit, so the moment does not change, and no GitHub request is needed at runtime. A local build has no release moment and says so instead of inventing one. The About page links to `LICENSE` and `CHANGELOG.md` at the release tag for a published image, and at `main` for a local build.

The image currently targets `linux/amd64`, matching the Proxmox VM deployment target. It also contains OCI source, version, and revision labels, a software bill of materials, and build provenance.

### Rolling back across the occurrence index change

The release that introduced extra executions and one-off tasks (ADR-0009) replaces the unique occurrence index on `(cycleId, taskId, plannedDate)` with one that only covers generated occurrences, and it writes data that older images cannot read (`taskId: null`, `recordedDone`, `requestId`, export `schemaVersion: 2`). Once extra executions share a slot, an older image can no longer rebuild its full unique index, so it fails on startup. Rolling back to an image from before that release therefore requires restoring a pre-upgrade export or database backup first. Take a JSON export (Settings, Data) or a database dump before upgrading, and keep it until the new version has proven itself.

## Parallel run of the .NET rewrite

Until the switch release, the .NET application is verified next to the Node application on a copy of the production data, with the Compose profile `parallel`, a database copy script and a parity script. The procedure, the verification checklist, the switch and the rollback are in [PARALLEL-RUN.md](PARALLEL-RUN.md); the switch release itself (the Dockerfile replaced, `BREAKING CHANGE` footer) is slice 8.3 of the [rewrite plan](plans/dotnet-rewrite.md).

## One-time GitHub settings

In **Settings → Actions → General → Workflow permissions**, enable **Allow GitHub Actions to create and approve pull requests**. The workflow declares only the permissions needed to create the release pull request, GitHub Release, and GHCR package.

The built-in `GITHUB_TOKEN` intentionally does not start a second workflow for events it creates. Normal pull requests receive both CI checks; the generated release pull request does not. That generated pull request only updates release metadata such as `CHANGELOG.md` and `version.txt`. If strict branch protection must also require checks on the generated release pull request, configure Release Please with a separate automation token; keep that token in the repository's GitHub Actions secrets (and in a password manager such as 1Password), never in Git.

The first package publication normally inherits repository access. Choose the required package visibility in the package settings after the first image has been published.

## Recover a failed image publication

If the GitHub Release exists but the image publication failed, rerun only the failed job. Alternatively, manually run **Publish container image** with the release's full commit SHA and its version without the `v` prefix. The operation is safe to repeat for the same release.
