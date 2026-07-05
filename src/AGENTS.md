# Grace Repository Agents Guide

Agents operating under `/src` should follow this playbook alongside the existing `AGENTS.md` in the repo root.
Treat this file as the canonical high-level brief; each project folder contains an `AGENTS.md` with deeper context.

## Local Commands

- `pwsh ./scripts/bootstrap.ps1`
- `pwsh ./scripts/validate.ps1 -Fast` (use `-Full` for Aspire integration tests)

Optional: `pwsh ./scripts/install-githooks.ps1` to add a pre-commit `validate -Fast` hook.

## Work Tracking

Use GitHub issues and pull requests as the active coordination surface for implementation work.
For non-trivial work, follow `../docs/Development process.md`: create or confirm a GitHub issue, declare objective,
owned paths, risk surfaces, validation, docs impact, and definition of done before editing. If the write set grows,
update the issue before editing the new paths.

## Core Engineering Expectations

- Make a multi-step plan for non-trivial work, keep edits focused, and leave code cleaner than you found it.
- When the user says `Plan <work item>`, plan the work in chat. Create a GitHub issue only when the user explicitly asks
  for one, asks to start tracked implementation, or otherwise requests tracker setup.
- For tracked multi-step implementation, follow `docs/Development process.md`: create an epic parent issue, link
  sub-issues for each implementation step, assign each sub-issue's parent issue relationship to the epic in GitHub
  Relationships, and include a DAG in the parent issue that shows dependencies and parallelization opportunities. As
  each sub-issue completes, update the epic checklist. Use the concrete `addSubIssue` GraphQL workflow in
  `docs/Development process.md` when creating the native parent/child relationships.
- For non-trivial epics, identify an early tracer-bullet vertical slice before broad parallelization. It should prove
  one narrow user-visible behavior through the closest stable public boundary, crossing the main contract, runtime,
  persistence, validation, and documentation surfaces that later slices are likely to reuse. Use the result to refine
  child issues, owned paths, validation profiles, and parallelization boundaries.
- When implementing an epic, always use an explicit epic integration branch. Create
  `epic/<parent-issue>-<slug>` from `origin/main`, branch sub-issue worktrees from the current `origin/epic/...`, open
  sub-issue PRs to the epic branch, keep that branch refreshed from `origin/main`, and use the final epic-to-`main` PR
  as the production release candidate. Do not use direct-to-`main` epic slices.
- For top-level epics split into mini-epics, each mini-epic gets its own integration branch. Route leaf pull requests to
  their mini-epic integration branches, then merge each mini-epic integration branch into the parent epic integration
  branch. For Operations, leaf pull requests target their WS mini-epic branches and WS mini-epic pull requests target
  `epic/554-grace-operations`.
- Before assigning or starting a coding issue or sub-issue, require the minimum detail gate from
  `docs/Development process.md`: invariant tuple, forbidden implementation shapes, positive/negative/regression/boundary
  tests, high-risk adversarial examples, selected risk-surface traps, and explicit N/A waivers. The issue should be
  contextual enough for an implementation agent to succeed from the issue body alone without hidden project context.
  Write the gate as review-prevention guidance that predicts likely Codex Code Review Bot findings without adding new
  issue-template ceremony.
- Claim the issue with a comment, assign it to the authenticated GitHub user, and create or switch to an issue-owned
  branch/worktree from the selected base before editing implementation files: latest `origin/main` for standalone
  non-epic issues, or current `origin/epic/...` for sub-issues under the required epic integration branch.
- When a task assigns a worktree different from the thread workspace root, every `apply_patch` filename must be an
  absolute path under the assigned worktree. After the first patch, verify git status in both locations.
- Prefer vertical slices that prove one public behavior at a time through the closest stable boundary.
- Validate changes with `pwsh ./scripts/validate.ps1 -Fast` (use `-Full` for Aspire integration coverage).
- Order validation to avoid duplicate builds. Run targeted Fantomas formatting or checks before validation for touched
  F# files, then choose exactly one final build/test gate. If `validate -Fast` or `validate -Full` will run, do not also
  ask workers to routinely run project-specific `dotnet build` plus `dotnet test --no-build`; `validate` is the final
  build/test gate.
- Focused project build/test is appropriate for RED evidence, failure diagnosis, skipped-validate workflows, tests
  outside the selected validate profile, or explicitly focused-only issues. If running commands manually instead of
  `validate`, use `dotnet build --configuration Release` and `dotnet test --no-build`; build the focused project before
  any project-specific `--no-build` test command.
- Freshness or generated-file update workers follow the same validation ladder: formatting or freshness checks first,
  then exactly one final build/test gate. If `validate -Fast` or `validate -Full` runs, do not also run routine focused
  build/test commands.
- Product/DAG independence is not the same as merge/write-set independence. Parallelize branches only when their write
  sets are disjoint enough to avoid predictable churn. Serialize or merge-queue branches that touch shared project
  files such as `*.fsproj`, `Startup.Server.fs`, or the same test/helper files. For broad waves, consider a
  preparatory compile-item or file-scaffold slice before later branches edit separate files.
- Before the Grace completion review gate, update the branch against current `origin/main`, verify ahead/behind,
  verify the scoped diff and that no unexpected deletions are present, run the chosen validation gate, then wait for
  Codex Code Review Bot to review the refreshed PR head. A bot signal on a stale commit does not satisfy the completion
  gate. For sub-issue PRs targeting an epic integration branch, run that freshness gate against the current epic branch;
  for the final epic-to-`main` PR, run it against current `origin/main`.
- Never sleep or poll for more than 120 seconds in one command. This applies to `Start-Sleep`, `wait_agent`,
  long-polling commands, watch loops, and tool waits; use repeated shorter checks instead, with status updates between
  checks during long workflows.
- Resolve all compilation errors before considering a task complete.
- Run impacted tests for each task and fix failures introduced by your changes.
- Create a new git commit after each completed task to keep review scope clear.
- When acting as the main implementation orchestrator, delegate each coding task and each fix task to a fresh worker
  subagent. The main orchestrator must not implement, repair, inspect or validate code fixes as a substitute for the
  worker, or commit code changes locally. If an earlier worker thread is lost, compacted away, leaves uncommitted work,
  or cannot be resumed, assign the continuation to a fresh worker subagent with the existing worktree/branch context
  and required validation. The main agent coordinates issues, prompts, review ledgers, pull requests, CI/merge status,
  docs/process updates, Codex Code Review Bot monitoring, and final integration evidence. Follow the required bot-review
  loop in `docs/Development process.md`.
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
- For epic-branch pull requests, classify each fresh latest-head finding against the current leaf issue's scope before
  assigning a fix worker. If a finding is valid but explicitly belongs to a named future leaf issue in the same epic,
  reply with that future issue ownership, record the deferred disposition in `Review Status`, resolve the conversation,
  and do not broaden the current PR to absorb that future scope. Update the future sibling issue's detail gate before
  assigning it when the finding reveals missing acceptance criteria, adversarial cases, or risk-surface traps.
- Do not defer a finding to a future leaf issue when it challenges the current leaf's trust contract. If later leaves
  consume a fact, authority signal, persisted field, status flag, or trust predicate produced by the current leaf, the
  current leaf owns making that surface reliable before merge.
- Track substantive Codex Code Review Bot cycles. After one substantive cycle, continue the normal fix loop; after two,
  add a repeated-theme prevention note to `Review Status`; after three, stop one-off patching and post a review
  stabilization ledger to the issue and PR; after four, hard stop until the ledger is implemented, proven, and
  self-reviewed. Start stabilization after two substantive cycles for high-risk surfaces such as Watch state, IPC/status
  contracts, branch-switch safety, storage, actors, authorization, public contracts, persisted shapes, concurrency,
  recovery, or side-effect ordering.
- If a pull request has more than three Codex Code Review Bot review sessions even without three counted substantive
  cycles, pause before assigning another routine fix worker. Audit the timeline, separate stale/duplicate/invalid
  sessions from fresh findings, and decide whether a missing invariant, sibling-issue deferral, or structural ledger is
  needed before continuing.
- After each fix subagent completes a bot-requested fix, reply to the Codex Code Review Bot comment with the outcome,
  fix commit, validation evidence, and the prevention line required by `docs/Development process.md`, resolve the
  GitHub conversation, update the PR body's `Review Status` section, and wait for the next bot review on the new head
  commit.
- When the user asks to address a code review comment, review comment, PR feedback, or similar, complete the full
  review-thread workflow: evaluate the comment, make the appropriate fix or explicitly explain why no code change is
  needed, validate the result, commit and push the branch, reply to the GitHub review comment with the outcome and
  evidence, and resolve the GitHub conversation when the feedback has been satisfied.
- After an agent-owned pull request is merged, or closed because the related issue/sub-issue work is complete, cleanup
  is mandatory: verify the destination contains the change, delete the remote issue branch, delete the local issue
  branch, remove the task worktree, run `git fetch --prune`, and `git pull --ff-only` in the local repo so `main` is up
  to date. Do not wait for a separate user prompt before deleting the remote branch.
- Record skipped validation, docs impact, residual risk, and follow-ups in the task record or pull request.
- Write tests for new features and bug fixes; prioritize critical paths.
- Document new F# modules, types, functions, methods, members, and meaningful local helper functions with concise
  `///` XML comments so future maintainers and IntelliSense users understand their purpose.
- Treat secrets with care, avoid logging PII, and preserve structured logging (including correlation IDs).
- Favor existing helpers in `Grace.Shared` before adding new utilities.

## Test Project Organization

- `Grace.Server.Tests` is the Aspire-backed server integration project. Put HTTP flows, emulator/resource coverage,
  Service Bus validation, storage route behavior, and server-surface actor behavior there.
- `Grace.Server.Unit.Tests` is the no-Aspire server-adjacent project. Put pure helper, deterministic contract, and
  unit-shaped server coverage there only when it does not need HTTP hosting, emulators, blob storage, Service Bus,
  Redis, `Grace.Server.Tests.Services`, or `Grace.Aspire.AppHost`.
- `Grace.CLI/Command/*.CLI.fs` should be primarily covered by `Grace.CLI.Tests/*.CLI.Tests.fs`. Pure parser coverage
  belongs in dedicated `*.CLI.Parsing.Tests.fs` files that can be parallelized. Tests that touch command invocation,
  local config/history, environment variables, console output, filesystem/current-directory state, or SDK identity
  remain serialized in the non-parsing CLI test files.
- `Grace.Types/*.Types.fs` should be covered by `Grace.Types.Tests/*.Types.Tests.fs`.
- Keep auth-focused suites separate for now (`Grace.Authorization.Tests`, plus auth-specific files inside other test
  projects).
- Prefer server-surface integration tests for actor behavior; avoid duplicating deep actor internals in server test files.

## Test Parallelization And Validation

- `pwsh ./scripts/validate.ps1 -Fast` and `-Full` run one solution-level `dotnet test "src/Grace.slnx"` command with
  selection filters. Fast selects Authorization, CLI, Types, and Server.Unit tests. Full adds Server integration tests.
- Do not reintroduce custom per-project process fan-out into validation unless a future issue owns that runner change.
- Assembly-level NUnit parallel defaults are intentionally limited. `Grace.Authorization.Tests` and `Grace.Types.Tests`
  have bounded defaults. `Grace.Server.Unit.Tests` is deferred while process-static approval-store mutation remains in
  the project. `Grace.CLI.Tests` is deferred while global/current-process mutations remain. `Grace.Server.Tests` stays
  integration-controlled because it shares Aspire-hosted resources and setup state.
- If running a project-specific `dotnet test --no-build` command, run the matching Release build for that project first
  so the test assembly exists and reflects current source.

## F# Coding Guidelines

- Default to F# for new code unless stakeholders specify another language.
- Use `task { }` for asynchronous workflows and keep side effects isolated.
- Prefer immutable data, small pure functions, and explicit dependencies passed as parameters.
- Prefer collections from `System.Collections.Generic` (for example `List<T>`, `Dictionary<K,V>`) over F#-specific
  collections unless pattern matching or discriminated unions are needed.
- Apply the modern indexer syntax (`myList[0]`) for lists, arrays, and sequences; avoid the legacy `.[ ]` form.
- Structure modules so domain types live in `Grace.Types`, shared helpers in `Grace.Shared`, and orchestration in the
  project-specific assembly.
- Treat `///` XML documentation comments as required for new F# declaration surfaces: modules, types, functions,
  methods, members, and meaningful local helper functions. Keep these comments concise and purpose-focused.
- Do not satisfy the XML documentation rule with boilerplate such as "performs X", "returns X", "creates X", or
  "for the current operation/request" when that repeats the declaration name. Describe the concrete Grace behavior,
  invariant, route, command, state transition, storage operation, validation rule, or contract role instead.
- Add ordinary inline comments only where control flow or transformations are non-obvious.
- Run Fantomas formatting or a targeted Fantomas check before build and test validation. Avoid the slow loop where tests
  pass, Fantomas then changes files, and the same build/tests must be repeated. For broad F# edits, format with
  `dotnet tool run fantomas --recurse .` from `./src`; for narrow fixes, run targeted Fantomas on the touched files
  before validation.

## Avoid FS3511 in Resumable Computation Expressions

These rules apply to `task { }` and `backgroundTask { }`.

1. **Do not define `let rec` inside `task { }`.**
2. **Avoid `for ... in ... do` loops inside `task { }`.**
3. **Treat FS3511 warnings as regressions; do not suppress them.**

## Agent-Friendly Context Practices

- Start with relevant `AGENTS.md` files to load patterns, dependencies, and test strategy before broad code exploration.
- When Agent Skills are available, load `../skills/grace/SKILL.md` after the relevant `AGENTS.md` files and use its
  references as the on-demand router for Grace-specific workflow, architecture, testing, and public-surface guidance.
- Use these summaries to target only source files needed for implementation or verification.
- When documenting new behavior, update the closest `AGENTS.md` so future agents inherit context quickly.

## Collaboration and Communication

- Summarize modifications clearly, cite file paths with 1-based line numbers, and call out remaining follow-ups/tests.
- Coordinate cross-project changes across `Grace.Types`, `Grace.Shared`, `Grace.Server`, `Grace.Actors`, `Grace.CLI`,
  and `Grace.SDK`.
- When adding capabilities, ensure matching tests exist and note any residual risk.
