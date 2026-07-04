# Agent Instructions

Other `AGENTS.md` files exist in subdirectories, refer to them for more specific context.

## Agent Quickstart (Local)

Prerequisites:

- PowerShell 7.x
- .NET 10 SDK
- Docker Desktop (required for `-Full`)

Commands:

- `pwsh ./scripts/bootstrap.ps1`
- `pwsh ./scripts/validate.ps1 -Fast`

Use `pwsh ./scripts/validate.ps1 -Full` for Aspire integration coverage.
Optional: `pwsh ./scripts/install-githooks.ps1` to add a pre-commit `validate -Fast` hook.

More context:

- `src/AGENTS.md`
- `skills/grace/SKILL.md` for Agent Skills-compatible clients; use it as the portable Grace context router after
  reading repo-local instructions.
- `src/docs/ASPIRE_SETUP.md`
- `src/docs/ENVIRONMENT.md`

## Issue Tracking

Use GitHub issues and pull requests as the active coordination surface for implementation work.
For non-trivial work, follow `docs/Development process.md`: create or confirm a GitHub issue, declare owned paths,
create an issue-owned branch/worktree, validate in focused slices, commit after each completed slice, and record docs
impact and skipped validation.
For multi-step implementation plans, create an epic parent issue with linked sub-issues for each implementation step,
assign each sub-issue's parent issue relationship to the epic in GitHub Relationships, and include a DAG in the parent
issue that shows dependencies and parallelization opportunities. As sub-issues complete, update the epic checklist so
completed sub-issues are checked. Use the concrete `addSubIssue` GraphQL workflow in `docs/Development process.md`
when creating the native parent/child relationships.
When planning a feature or epic, and when creating issue or pull request bodies, include why the change matters for
Grace and its users. Use that purpose to help implementation agents make better local decisions when the plan leaves a
gap or an acceptance criterion is ambiguous.
When creating the epic and child issues from PowerShell, avoid one giant inline script that embeds every issue body.
For large issue batches, go directly to a short-lived generator script checked into the worktree or written in a temp
directory, run that script to emit one temporary Markdown body per issue, lint those files, then call
`gh issue create --body-file <path>` for each issue. Do not paste large scripts through an interactive shell or pass
them as a single `pwsh -Command` string; that wastes time, floods the transcript, and can hit Windows command-length
limits. After issue creation, patch the epic body with the real child issue numbers and create the native relationships
with GraphQL `addSubIssue`.
When implementing an epic, always use an explicit epic integration branch. Create
`epic/<parent-issue>-<slug>` from `origin/main`, branch sub-issue worktrees from the current `origin/epic/...`, open
sub-issue PRs to the epic branch, keep that branch refreshed from `origin/main`, and use the final epic-to-`main` PR as
the production release candidate. Do not use direct-to-`main` epic slices. Ensure CI or recorded validation covers PRs
targeting `epic/**` before relying on the integration branch flow.
Every pull request must link its related GitHub issue in the PR body. When a PR targets the default branch and should
close an issue, use one of GitHub's supported closing keywords: `close`, `closes`, `closed`, `fix`, `fixes`, `fixed`,
`resolve`, `resolves`, or `resolved`. Use `docs/Development process.md` for default-branch versus epic-branch wording
so links stay traceable without relying on epic-branch auto-close behavior.

## Development Process

- Read the closest `AGENTS.md` before editing. Root guidance applies repo-wide; project guidance applies within that
  subtree.
- When the user says `Plan <work item>`, plan the work in chat. Do not create a GitHub issue unless the user asks for
  an issue, asks to start tracked implementation, or otherwise explicitly requests tracker setup.
- When the user asks to create a GitHub issue, use the Grace agent task template and stop before implementation edits
  unless they also ask you to implement.
- For tracked implementation work, keep one visible task record: the GitHub issue.
- For multi-step implementation plans, use an epic parent issue, linked sub-issues, native GitHub parent relationships,
  and a DAG in the parent issue that shows dependencies and parallelization opportunities. As each sub-issue completes,
  check its box in the epic. Use the concrete `addSubIssue` GraphQL workflow in `docs/Development process.md` when
  creating the native parent/child relationships.
- When planning features or epics, include why the work benefits Grace and its users before decomposing implementation
  steps. Carry that purpose into parent issues, child issues, and PR bodies so implementation agents understand the
  goal behind the requested change, not only the files and tests to touch.
- For non-trivial epics, identify an early tracer-bullet vertical slice before broad parallelization. The
  tracer-bullet slice should prove one narrow user-visible behavior through the closest stable public boundary, crossing
  the main contract, runtime, persistence, validation, and documentation surfaces that the rest of the epic is likely to
  reuse. Use what it reveals to refine child issues, owned paths, validation profiles, and parallelization boundaries.
- For epic plus child issue creation, prefer separate temporary Markdown body files and `gh issue create --body-file`
  over a giant inline PowerShell script containing all issue bodies. After the child issue numbers exist, patch the epic
  body with those real numbers, then use GraphQL `addSubIssue` for the native parent relationships.
- Before assigning or starting a coding issue or sub-issue, apply the minimum detail gate from
  `docs/Development process.md`: invariant tuple, forbidden implementation shapes, positive/negative/regression/boundary
  tests, high-risk adversarial examples, selected risk-surface traps, and explicit N/A waivers. Keep each issue
  implementable from its body alone without hidden project context. Write the gate as review-prevention guidance that
  predicts likely Codex Code Review Bot findings without adding new issue-template ceremony.
- Grace is not in production. There is no production data to import, migrate, preserve, or grandfather. Do not weaken
  public contracts, validators, generated clients, runtime behavior, or tests to preserve imaginary old data. Only build
  compatibility behavior when an issue explicitly requires it as current Grace behavior.
- Declare owned paths, forbidden or sensitive paths, risk surfaces, validation, docs impact, and definition of done
  before editing.
- When adding new F# modules, types, functions, methods, members, or meaningful local helper functions, include a
  concise `///` XML documentation comment that explains the declaration's purpose for future maintainers and
  IntelliSense. Avoid generic comments that merely restate the declaration name or use filler such as "for the current
  operation/request"; describe the Grace behavior, invariant, route, command, state transition, or contract role the
  declaration owns.
- After the issue exists, claim it with a comment, assign it to the authenticated GitHub user, and create an
  issue-owned branch/worktree from the selected base before editing: latest `origin/main` for standalone non-epic
  issues, or current `origin/epic/...` for sub-issues under the required epic integration branch.
- When a task assigns a worktree different from the thread workspace root, every `apply_patch` filename must be an
  absolute path under the assigned worktree. After the first patch, verify git status in both locations.
- Prefer vertical slices with focused tests and `pwsh ./scripts/validate.ps1 -Fast` as the normal validation gate.
- Use `pwsh ./scripts/validate.ps1 -Full` when Aspire, emulators, storage, Service Bus, Cosmos DB, Redis,
  deployment/runtime behavior, or cross-service integration is affected.
- Order validation to avoid duplicate builds. Run targeted Fantomas formatting or checks before validation for touched
  F# files, then choose exactly one final build/test gate. If the slice will run
  `pwsh ./scripts/validate.ps1 -Fast` or `pwsh ./scripts/validate.ps1 -Full`, do not also ask workers to routinely run
  project-specific `dotnet build` plus `dotnet test --no-build`; `validate` is the final build/test gate.
- Focused project build/test is appropriate for RED evidence, failure diagnosis, skipped-validate workflows, tests
  outside the selected validate profile, or issues that explicitly require a focused-only gate. When a focused test
  command uses `--no-build`, run the matching Release build for that project after formatting and before the
  `--no-build` test command.
- Freshness or generated-file update workers follow the same validation ladder: formatting or freshness checks first,
  then exactly one final build/test gate. If `validate -Fast` or `validate -Full` runs, do not also run routine focused
  build/test commands.
- Treat parallel work as two separate decisions: product/DAG independence and merge/write-set independence. Run
  branches in parallel only when their write sets are disjoint enough to avoid predictable churn. Serialize or
  merge-queue branches that touch shared project files such as `*.fsproj`, `Startup.Server.fs`, or the same test/helper
  files. For broad waves, consider a preparatory compile-item or file-scaffold slice before later branches edit
  separate files.
- Before the Grace completion review gate, update the branch against current `origin/main`, verify ahead/behind,
  verify the scoped diff and that no unexpected deletions are present, run the chosen validation gate, then wait for
  Codex Code Review Bot to review the refreshed PR head. A bot signal on a stale commit does not satisfy the completion
  gate. For sub-issue PRs targeting an epic integration branch, run that freshness gate against the current epic branch;
  for the final epic-to-`main` PR, run it against current `origin/main`.
- Commit after each completed slice and keep pull requests focused and reviewable.
- When acting as the main implementation orchestrator, delegate each coding task and each fix task to a fresh worker
  subagent. The main orchestrator must not implement, repair, inspect or validate code fixes as a substitute for the
  worker, or commit code changes locally. If an earlier worker thread is lost, compacted away, leaves uncommitted work,
  or cannot be resumed, assign the continuation to a fresh worker subagent with the existing worktree/branch context
  and required validation. The main agent coordinates issues, prompts, review ledgers, pull requests, CI/merge status,
  docs/process updates, Codex Code Review Bot monitoring, and final integration evidence. Follow the required bot-review
  loop in `docs/Development process.md`.
- When assigning a worker subagent, include an explicit status protocol in the prompt. For Grace work, ask the worker to
  create or update a temp status file outside the repo, for example
  `$env:TEMP\grace-agent-status\<issue-or-pr>-<task>.md`, with `phase`, `lastUpdate`, `changedFiles`, `validation`,
  `blockers`, and `nextStep`. Require updates before code edits, before and after long validation/generation commands,
  before commit/push/handoff steps, and before the final response. Also ask the worker to send a short chat heartbeat
  roughly every five minutes while still working so the orchestrator can distinguish active progress from a stalled or
  lost worker without interrupting it.
- Agents must never sleep or poll for more than 120 seconds in one command. This includes `Start-Sleep`, `wait_agent`,
  long-polling commands, watch loops, and tool waits. Use repeated shorter checks instead, updating the status file
  between checks when the wait is part of a long-running workflow.
- Worker subagents should finish their assigned implementation or fix as soon as they have a validated result and a
  handoff. By default, do not make workers update GitHub issues, pull request bodies, review comments, conversation
  resolution, labels, checklists, or merge/cleanup state. The orchestrator owns those GitHub coordination updates and
  may schedule the next independent worker from the handoff before finishing wrap-up for the previous worker when the
  dependency graph and write sets allow it.
- Before handoff, require the worker to run a bot-prevention self-review over the actual diff, fix likely Codex Code
  Review Bot findings it discovers, and report first-pass review readiness with residual risks in the handoff.
- After the first coding subagent that works on an issue commits and pushes the new branch to origin, open a normal
  ready-for-review pull request. Keep it open while the step is still in progress so Codex Code Review Bot findings,
  fixes, validation evidence, and final no-issues bot state can be recorded on the pull request instead of only on the
  issue.
- For Grace PR code review, do not spawn local review-only subagents by default. Monitor Codex Code Review Bot: 👀 on
  the PR body means it saw the latest commit and is reviewing; 👍🏻 means it found no issues; a bot PR comment or
  inline pull-request-review comment contains findings that must be assigned to a fresh fix subagent. Do not rely on
  top-level PR comments alone; inspect review comments attached to the bot review before merging. For high-risk slices,
  the orchestrator may assign a fresh pre-PR review worker before opening or updating the PR when that is cheaper than a
  likely bot/fix/re-review loop; this does not replace the bot as the blocking review gate.
- For Grace PR review-fix routing, wait for Codex Code Review Bot to finish on the latest head before deciding the
  next fix action set. A fresh finding is one that belongs to the completed bot review for the current head commit, or
  is repeated after that review completes. Review threads from earlier review passes are stale when a newer head exists,
  even if GitHub still maps the thread onto the current diff. Do not assign workers, make code changes, or post fix
  evidence for stale findings; close them only as stale when the maintainer directs that disposition, and say that no
  code change addressed them.
- For epic-branch pull requests, classify each fresh latest-head finding against the current leaf issue's scope before
  assigning a fix worker. If a finding is valid but explicitly belongs to a named future leaf issue in the same epic,
  reply with that future issue ownership, record the deferred disposition in `Review Status`, resolve the conversation,
  and do not broaden the current PR to absorb that future scope. Update the future sibling issue's detail gate before
  assigning it when the finding reveals missing acceptance criteria, adversarial cases, or risk-surface traps.
- Do not defer a finding to a future leaf issue when it challenges the current leaf's trust contract. If later leaves
  consume a fact, authority signal, persisted field, status flag, or trust predicate produced by the current leaf, the
  current leaf owns making that surface reliable before merge.
- Track substantive Codex Code Review Bot cycles. A substantive cycle is a latest-head behavior, correctness,
  concurrency, recovery, durability, authority, contract, or maintainability finding, followed by a worker fix, followed
  by another substantive latest-head finding. Do not count duplicate findings, stale resolved threads, formatting-only
  comments, administrative comments, CI flakes, invalid findings, or maintainer-accepted deferrals.
- Use repeated-review stabilization thresholds: after the first substantive cycle, continue the normal fix loop; after
  the second cycle, add a short repeated-theme prevention note to `Review Status`; after the third cycle, stop one-off
  patching and post a review stabilization ledger to the issue and PR before assigning more fix work; after the fourth
  cycle, hard stop until the ledger is implemented, proven, and self-reviewed.
- Start the stabilization pass after two substantive cycles for high-risk surfaces, including Watch state, IPC/status
  contracts, branch-switch safety, local working-tree mutation, runtime timers, storage, actors, retries,
  idempotency, authorization, public contracts, persisted shapes, concurrency, recovery, or side-effect ordering.
- If a pull request has more than three Codex Code Review Bot review sessions even without three counted substantive
  cycles, pause before assigning another routine fix worker. Audit the review timeline, separate stale/duplicate/invalid
  sessions from fresh findings, and decide whether the issue needs a missing invariant, sibling-issue deferral, or
  structural stabilization ledger before the next review request.
- Serialize review-fix workers for a single Grace pull request unless the completed latest-head review contains multiple
  fresh findings with provably disjoint write sets. Do not overlap workers that touch the same branch, files, tests, or
  review surface. After a fix worker pushes, reply to and resolve only the fresh findings it addressed, update
  `Review Status`, then wait for Codex Code Review Bot to finish on the new head before assigning another fix worker.
- Never manually trigger Codex Code Review Bot while 👀 is present for the current pull request head. A manual trigger is
  allowed only through the documented missed-ack exception after verifying that no 👀, no 👍🏻, no bot review, and no bot
  comment exists for the current head commit.
- After each fix subagent completes a bot-requested fix and hands off, the orchestrator replies to the Codex Code Review
  Bot comment with the outcome, fix commit, and validation evidence, resolves the GitHub conversation, updates the PR
  body's `Review Status` section, includes the required prevention line from `docs/Development process.md`, and waits
  for the next bot review on the new head commit.
- Open normal ready-for-review pull requests. Do not open draft pull requests unless the user explicitly asks for a
  draft.
- After an agent-owned pull request is merged, or closed because the related issue/sub-issue work is complete, cleanup
  is mandatory: verify the destination contains the change, delete the remote issue branch, delete the local issue
  branch, remove the task worktree, run `git fetch --prune`, and `git pull --ff-only` in the local repo so `main` is up
  to date. Do not wait for a separate user prompt before deleting the remote branch.
- Update README, CONTRIBUTING, nearby `AGENTS.md`, and other docs when behavior, commands, APIs, or workflow changes.
- When the user asks to address a code review comment, review comment, PR feedback, or similar, treat that as a complete
  review-thread workflow: evaluate the comment, make the appropriate fix or explicitly explain why no code change is
  needed, validate the result, commit and push the branch, reply to the GitHub review comment with the outcome and
  evidence, and resolve the GitHub conversation when the feedback has been satisfied.

## Markdown Guidelines

- Follow the MarkdownLint ruleset found at `https://raw.githubusercontent.com/DavidAnson/markdownlint/refs/heads/main/doc/Rules.md`.
- Verify updates by running MarkdownLint. Use `npx --yes markdownlint-cli2 ...`. `--help` is available.
- For MD013, override the guidance to allow for 120-character lines.
- When appending generated Markdown sections, especially to GitHub issue or pull request bodies, normalize the insertion
  boundary before writing the file: trim trailing whitespace, preserve exactly one blank line after the previous block,
  and ensure every new heading is preceded and followed by a blank line. Do not append a heading immediately after a
  list item; this creates repeat MD022/MD032 failures.

## Editing Documentation

When updating documentation files, follow these guidelines:

- When writing technical documentation, act as a friendly peer engineer helping other developers to understand Grace as
  a project.
- When writing product-focused documentation, act as an expert product manager who helps a tech-aware audience
  understand Grace as a product, and helps end users understand how to use Grace effectively.
- Use clear, concise language; avoid jargon. The tone should be welcoming and informative.
- Structure content with headings and subheadings. Intersperse written documentation with bullet points for readability.
- Keep documentation up to date with code changes; review related docs when modifying functionality. Explain what is
  changing, and why it's changing.
- Show all scripting examples in both PowerShell first, and then bash/zsh when applicable. bash and zsh are always
  spelled in lowercase.

PowerShell:

```powershell
$env:GRACE_SERVER_URI="http://localhost:5000"
```

bash / zsh:

```bash
export GRACE_SERVER_URI="http://localhost:5000"
```
