# Contributing to Grace

Thanks for considering a contribution to Grace. The repo is mostly F# on .NET 11. This file covers setup and how work
moves from an idea to merged code, for people and agents alike. Scale the process to the change: a typo fix needs a
branch and a PR, not an epic.

## Setup

You need:

- the .NET SDK selected by `global.json` (<https://dotnet.microsoft.com/download>);
- PowerShell 7+ (<https://learn.microsoft.com/powershell/>);
- Docker Desktop or Podman, for the local emulators and the Aspire integration tests.

From the repo root:

```powershell
pwsh ./scripts/bootstrap.ps1     # checks tools, restores .NET tools and packages; -SkipDocker if you have no Docker yet
dotnet build ./src/Grace.slnx
```

F# is formatted with Fantomas, and CI doesn't check it, so format the files you touch. From `./src`:

```powershell
dotnet tool run fantomas <files>
```

Build, test, and project-placement rules for source work are in `src/AGENTS.md`.

### Running Grace locally

`pwsh ./scripts/dev-local.ps1` starts Grace Server and its emulators through the Aspire AppHost and prints a personal
access token, along with the commands that set `GRACE_SERVER_URI` and `GRACE_TOKEN`. The README's "Running Grace
locally" section walks through a first repository. `pwsh ./scripts/start-debugazure.ps1` runs against real Azure
resources instead.

Keep local secrets in .NET user-secrets for `Grace.Server`; keys use `__` for hierarchy:

```powershell
dotnet user-secrets set --project ./src/Grace.Server/Grace.Server.fsproj "grace__azurecosmosdb__connectionstring" "<value>"
```

Environment variables, run modes, and test toggles are listed in `src/docs/ENVIRONMENT.md`.

## The lifecycle

```text
plan -> issue -> branch -> slices (test, change, commit) -> PR -> Validate + review -> fix pass -> approval -> merge -> cleanup
```

Outside contributors use the usual GitHub fork and pull request flow; the rest of this file still applies. Include any
AI prompts that helped with diagnosis or implementation in the PR description.

## Planning

A request to plan stays in chat until someone asks for tracked work. A good plan states:

- the goal: what a user or operator will be able to do or see, and why it matters for Grace;
- done when: checks anyone can run on the finished PR (a command and its expected output, a test that fails on `main`
  today, an HTTP call, a screenshot);
- what is deliberately left out;
- the risky parts: durable state, effect order, authority, public or persisted contracts.

Ask about real product decisions and bring a recommendation. Look up facts yourself.

### Stateful or destructive work: prove the algorithm first

When correctness depends on effect order, commit points, partial failure, restart, retries with unknown outcomes,
concurrency, or more than one authoritative store, build a small throwaway executable witness before writing production
code. It should model the states and effects, inject failure at each meaningful step, restart from the captured state,
and end with a verdict: proven, simplified, or blocked. Keep it off the production branch and carry its decisions into
the issue. A diagram is not proof.

## Issues

Create issues with `gh issue create --body-file <file>`, copying the sections below the front matter of
`.github/ISSUE_TEMPLATE/task.md`. One issue owns one outcome. List the surfaces it changes so reviewers can check
propagation (see `src/AGENTS.md`). Decisions and their reasons live in the issue and its PR.

### Epics

For multi-step work, create an epic issue that holds the outcome, the decisions made, and a short dependency list. Then
create only the first child: a **tracer**, the thinnest slice that proves one user-visible behavior end to end through
the real public boundary. Plan the next children after the tracer lands, using what it taught. If more than two enabling
PRs are needed before the tracer, the slice or scope is too big.

Link children with GitHub's native sub-issue relationship (body checklists and comments are not enough):

```powershell
$query = @'
mutation($parent: ID!, $child: ID!) {
  addSubIssue(input: { issueId: $parent, subIssueId: $child, replaceParent: true }) {
    subIssue { number parent { number } }
  }
}
'@
$parentId = gh issue view <epic-number> --json id --jq .id
$childId = gh issue view <child-number> --json id --jq .id
gh api graphql -f query=$query -F parent=$parentId -F child=$childId
```

Each child normally branches from and merges to `main`, and must be safe on `main` by itself (inert until used is fine).
Use a long-lived `epic/<number>-<slug>` integration branch only when Scott agrees that children cannot land
independently; then the final epic-to-`main` PR is the release candidate.

## Branches and worktrees

Assign the issue to yourself, then branch from current `origin/main`:

```powershell
git fetch --prune origin
git worktree add ../Grace-gh-184 -b agent/184-short-slug origin/main
```

Leave unrelated local changes alone. If the work needs files well outside what the issue described, say so on the issue
before continuing.

Don't run parallel branches that edit the same `.fsproj` compile list, `Startup.Server.fs`, or shared test helpers; F#
compile order makes those conflicts predictable. Land them one after another.

## Building in slices

Work in vertical slices. For each behavior change:

1. Write a focused test that fails for the right reason.
2. Make the smallest change that passes it.
3. Refactor while it stays green.
4. Format, run the focused proof, run `git diff --check`, and commit.

Mechanical edits and Markdown changes don't need a failing test first; their proof is the build, lint, or a rendered
check.

## Verification

The issue's "Done when" list is the contract, and the PR shows evidence for each item on its head. Choose the proof by
the kind of change; a change gets every row that applies.

| Change | Proof |
| --- | --- |
| Markdown and agent guidance | `npx --yes markdownlint-cli2 <files>` and `git diff --check` |
| Mechanical code: rename, move, formatting | Build and existing tests; no new tests |
| Logic or contract types | Focused tests from the issue's examples; FsCheck properties where an invariant matters |
| User-visible CLI, HTTP route, or SDK behavior | Tests plus a **demo** (below) |
| Durable state, effect order, retries, restart | The algorithm witness first, then restart and retry tests through the server |
| Authorization or hidden resources | A test that an unauthorized caller can't tell hidden from missing |
| Scripts or workflows | Parse them, then a meaningful dry run |
| Performance | Measured numbers before and after |

**Red-on-revert.** Every new or changed test must fail when the behavior it covers is absent. Run it against the base,
or against the head with the change's logic reverted or stubbed out, and record "red without the change, green with it"
in the PR. A compile error isn't red: for new API, stub the new member (throw or return a default) and show the test
failing on an assertion. A test that can't go red only re-derives the code; rewrite it or drop it. Tests edited only by
a mechanical change are exempt.

**Demo.** For a change a user can see, stop any Grace already running on `:5000` (the script reuses a healthy server,
which may be an old build), start local Grace with `pwsh ./scripts/start-debuglocal.ps1`, and set the environment it
prints. Run the command or call the route the way a user would; for an SDK change, run a CLI command that goes through
the changed path, or a short `dotnet fsi` script that calls the SDK. Paste the command and trimmed output under the
"Done when" item it proves, with tokens redacted, and stop the AppHost with the `Stop-Process` command the script
printed. Changes users can't see skip this.

Focused local proof shows a slice is correct; GitHub `Validate` certifies the pull request across the whole repository,
so don't run the full suite locally just to repeat it. `pwsh ./scripts/validate.ps1 -Fast` is an optional broad local
check (no Aspire). `-Full` runs everything, including the Aspire integration tests; use it to reproduce or diagnose
integration failures, or when CI is unavailable.

When `Validate` fails, read the newest run for the current head and separate failures your change caused from unrelated
ones before fixing anything.

## Pull requests

Open a normal, ready-for-review PR once the first complete version is pushed and its focused proof passes. Use
`.github/PULL_REQUEST_TEMPLATE.md`. Link the issue with `Closes #123` (or `Part of #123` for a PR into an epic branch).
Keep the body short: what changed, the evidence for each "Done when" item on this head, the review outcome, and what
wasn't verified.

## Review

Every PR gets one independent review of the whole change, from a reviewer with fresh context (a different model family
is preferred when available). Brief the reviewer with the issue and the diff, not the implementer's summary. The rubric
is `.github/code_review_instructions.md`: check the evidence first, then look for what can be removed, then for defects.
A defect finding comes with a failing test, a reproduction, or direct static evidence; speculative hardening is not a
finding.

Decide once which findings to accept, fix them together in one pass, then have the same reviewer (or a fresh one) check
only those fixes and anything they directly affect. Do not restart a whole-diff review because the head changed.
Concerns outside the issue's goal become new issues, not fixes in this PR. If the fix check turns up a new blocker
outside the fixes, or the review shows the issue needs a new product decision, stop and ask Scott.

For changes to persisted, wire, or public types, Scott may also ask for a **Shape Review**: a short HTML report that
explains the changed types and what states they allow.

## Merge and cleanup

Scott approves every merge, and the approval covers the exact head he reviewed. The PR needs a green `Validate` on that
head. After merge:

1. Confirm `main` contains the change and the issue is closed.
2. Delete the remote branch, remove the worktree, and delete the local branch.
3. Run `git fetch --prune` and `git pull --ff-only` on local `main`.

For an epic, update its dependency list and choose the next child.

## Escapes

When a bug reaches `main`, or Scott catches one at approval, open an issue labeled `escape` that names the check that
would have caught it. Fix the bug, then add that one check to the Verification table or the review rubric. The process
grows from real escapes, not imagined ones.

## Working with subagents

Subagents are optional. Use them for independent review and for parallel read-only investigation. One issue has one
writer at a time. Give a subagent a fresh context and a complete brief: the issue, the exact revision, what to do, and
what to return.

## Earlier docs

Grace used to keep design notes, decision records, and feature write-ups under `docs/`. They restated the code and
drifted from it, so they were removed. The `docs-archive-2026-11-27` tag keeps them:

```powershell
git show "docs-archive-2026-11-27:docs/Libraries.md"
```
