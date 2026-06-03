# Grace Workflow

Load this reference for non-trivial planning, issue orchestration, implementation, review, PR, or cleanup work.

## Current Source Of Truth

- Root instructions: `AGENTS.md`
- Code instructions: `src/AGENTS.md`
- Process details: `docs/Development process.md`
- Contributor docs: `CONTRIBUTING.md`

Re-read these files during the current session before relying on older memory or this summary.

## Planning Versus Tracked Work

- When the user says `Plan <work item>`, plan in chat and stop. Do not create GitHub issues unless asked.
- When asked to create issues, use the Grace agent task shape and stop before implementation unless asked to implement.
- For tracked multi-step work, create an epic parent issue, one linked sub-issue per implementation step, native GitHub
  parent relationships, a DAG in the parent issue, and an epic checklist that is updated as sub-issues complete.
- Before assigning a sub-issue, require the minimum detail gate:
  - invariant tuple
  - forbidden implementation shapes
  - expected tests
  - high-risk adversarial examples

## Issue-Owned Implementation

For non-trivial tracked work:

1. Confirm or create the GitHub issue.
1. Declare objective, context, owned paths, forbidden paths, risk surfaces, validation, docs impact, and definition of
   done.
1. Claim the issue and create an issue-owned branch/worktree from latest `origin/main`.
1. Inspect `git status --short --branch` before editing.
1. Work in vertical slices through the closest stable public boundary.
1. Run the focused validation first, then the selected repo validation profile.
1. Commit after each completed slice.
1. Open a normal ready-for-review PR. Do not open draft PRs unless the user asks for a draft.

## Orchestration And Review

When acting as the main implementation orchestrator, follow the repo policy:

- Delegate coding and fixing tasks to GPT-5.5 Medium worker subagents when the available tools and user authorization
  allow delegation.
- Use GPT-5.4-mini xhigh fresh local review-only sibling subagents for code review when available.
- Do not replace the worker by locally implementing or validating code fixes from the orchestrator role.
- Persist each review report, including "Reviewed And OK" notes, to the issue or PR before starting another review.
- Include prior OK notes in follow-up review prompts so reviewers do not repeat work without cause.

If subagent tools are unavailable or cannot be used under the active tool policy, state that limitation and preserve the
rest of the Grace workflow as far as possible.

## Validation Profiles

Use the profile from `docs/Development process.md`:

| Profile | Use For |
| ------- | ------- |
| `docs-only` | Markdown, HTML, static guidance, workflow docs |
| `domain-contract` | Types, DTOs, validators, serializers, hashes, shared helpers |
| `cli-command` | Grace CLI command behavior |
| `server-api` | HTTP handlers, server services, auth, persistence boundaries, API contracts |
| `actor-workflow` | Orleans actor behavior and durable state transitions |
| `sdk-client` | SDK surface or client contract changes |
| `deployment-runtime` | Aspire, emulators, Docker, Azure resources, scripts, runtime config |

Default commands:

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
npx --yes markdownlint-cli2 "**/*.md"
git diff --check
```

Run `-Full` for Aspire integration, emulators, storage, Cosmos DB, Service Bus, Redis, runtime delivery, or
cross-service behavior.

## Merge Cleanup

When the user says a PR is merged:

1. Verify the destination branch contains the change.
1. Confirm no uncommitted or unpushed task work is stranded.
1. Remove task worktrees that are no longer needed.
1. Delete the issue branch locally and remotely when appropriate.
1. Run `git fetch --prune`.
1. Run `git pull --ff-only` in the main repo.
1. Update the task record with final status and follow-ups.
