# Agentic workflow checks

This repository uses [GitHub Agentic Workflows](https://github.github.com/gh-aw/)
to keep the instructions read by coding agents aligned with the repository. The
workflow is a maintenance checker: it reviews the agent-context layer and either
opens one pull request for human review or reports that no change is needed. It
never commits directly to `main`.

## Workflow layout

GitHub Agentic Workflows use Markdown source files with YAML frontmatter. The
`gh aw compile` command validates that source and generates the executable GitHub
Actions `.lock.yml` files.

| File | Purpose |
| --- | --- |
| `.github/workflows/context-maintainer.md` | Release-triggered entry point and commit range |
| `.github/workflows/context-maintainer-weekly.md` | Weekly and manually dispatched entry point |
| `.github/workflows/shared/context-maintainer.md` | Shared scope, checks, edit rules, and output contract |
| `.github/workflows/context-maintainer.lock.yml` | Generated release-triggered GitHub Actions workflow |
| `.github/workflows/context-maintainer-weekly.lock.yml` | Generated weekly GitHub Actions workflow |
| `.github/workflows/agentics-maintenance.yml` | Generated cleanup and maintenance workflow |

Edit the Markdown source, not a generated `.lock.yml` or
`agentics-maintenance.yml` file.

## When checks run

### After a release

The release checker runs on a push to `main` that changes `version.txt` or
`.release-please-manifest.json`. Release Please owns both files, so in the normal
repository flow this happens when its release pull request is merged.

Updates to the still-open release pull request happen on its own branch and do
not match the `main` branch filter. Ordinary pull-request merges do not change
the filtered release metadata and therefore do not start this checker.

The checker finds the previous release tag from the release commit's first
parent. It reviews the commits after that tag through the first parent, excluding
the generated release commit itself. This makes the check cover the work shipped
in the release instead of only the version and changelog update.

### Weekly or on demand

The weekly checker reviews commits from the last eight days and compares the
current repository with the maintained instructions. It also performs the full
repository and history check for missing instruction files. It can be started
manually from the **Context maintainer: weekly look** workflow in the GitHub
Actions tab.

The weekly run may create at most two missing instruction files. The release run
does not create new instruction files; it lists candidates in the pull-request
body for the weekly run instead.

## What the checker reviews

The shared checker verifies that:

1. Named files, folders, commands, workflows, and skills still exist and behave
   as documented.
2. Instructions still match the implementation and intentional repository
   rules.
3. New repository structures or recurring procedures needed by agents are not
   missing from the context layer.
4. Rules are not duplicated across instruction files.
5. Skills still match their triggers and the code they describe.
6. A distinct stack or recurring task does not require a new instruction file
   or skill.

Its editable scope is restricted to the agent-context files listed in the
shared workflow, including `AGENTS.md`, the Copilot and Cursor instruction files,
agent skills, and `README.md`. It cannot change application code, tests, release
metadata, workflows, `CHANGELOG.md`, or other files under `docs/`. Findings
outside its editable scope are reported in the pull-request body.

The checker only reads source and history; its instructions explicitly prohibit
running builds or tests. Any proposed change still goes through normal pull-request
review and CI before a person merges it.

## Security, output, and cost limits

- The agent receives read-only repository, issue, and pull-request permissions.
- GitHub Copilot inference is the only additional agent permission.
- Repository writes are performed by the separate `create-pull-request` safe
  output, not by the AI agent itself.
- The safe output allows at most one pull request, prefixes its title with
  `[context]`, and restricts which files may be changed.
- A run that finds nothing calls the `noop` output instead of creating a pull
  request.
- Runs use strict compilation, a 20-minute agent timeout, at most 100 AI Credits
  per run, and at most 300 AI Credits across scheduled runs in 24 hours.
- The configured engine is GitHub Copilot using `claude-haiku-4.5`.

Context-maintainer pull requests expire after seven days. That setting causes
`gh aw compile` to generate `agentics-maintenance.yml`, which runs daily to close
expired safe outputs. Maintainers can also dispatch that workflow manually for
supported GH AW maintenance operations, including replaying a failed safe output.

## Changing or validating the workflows

Install the GitHub CLI and the `gh aw` extension as described in the
[official quick start](https://github.github.com/gh-aw/setup/quick-start/). After
changing either checker or the shared imported instructions, compile the affected
workflow:

```powershell
gh aw compile .github/workflows/context-maintainer.md
gh aw compile .github/workflows/context-maintainer-weekly.md
```

Compile every agentic workflow when changing shared behavior or upgrading GH AW:

```powershell
gh aw compile
```

Commit the Markdown sources and all generated changes together. Compilation may
update the corresponding `.lock.yml`, action pins, or
`agentics-maintenance.yml`. Never hand-edit those generated files.

Useful checks are:

```powershell
gh aw validate
git diff --check
```

The repository's normal completion gate remains:

```powershell
npm run verify
npm run build
```

## Diagnosing a run

- No run after an ordinary merge is expected; the release checker only watches
  Release Please metadata on `main`.
- No run while Release Please updates its open pull request is expected; that
  update is not on `main`.
- No pull request after a successful run usually means the checker intentionally
  returned `noop`.
- A stale-lock error means the source and generated workflow disagree; compile
  and commit the generated changes.
- Inspect the run in GitHub Actions, or use `gh aw status`, `gh aw logs`, and
  `gh aw audit <run-id-or-url>` for CLI diagnostics.
- If only applying a safe output failed, dispatch **Agentic Maintenance** with
  operation `safe_outputs` and the original run URL or run ID.

For GH AW behavior beyond this repository-specific process, use the official
[overview](https://github.github.com/gh-aw/introduction/overview/),
[compilation reference](https://github.github.com/gh-aw/reference/compilation-process/),
and [safe outputs reference](https://github.github.com/gh-aw/reference/safe-outputs/).
