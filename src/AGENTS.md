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
  each sub-issue completes, update the epic checklist.
- When implementing an epic, always use an explicit epic integration branch. Create
  `epic/<parent-issue>-<slug>` from `origin/main`, branch sub-issue worktrees from the current `origin/epic/...`, open
  sub-issue PRs to the epic branch, keep that branch refreshed from `origin/main`, and use the final epic-to-`main` PR
  as the production release candidate. Do not use direct-to-`main` epic slices.
- Before assigning or starting a sub-issue, require the minimum detail gate: invariant tuple, forbidden implementation
  shapes, expected tests, and high-risk adversarial examples. The issue should be contextual enough for an
  implementation agent to succeed from the issue body alone without hidden project context.
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
  verify the scoped diff and that no unexpected deletions are present, run the chosen validation gate, then spawn the
  final review-only sibling. Review on a stale branch is exploratory pre-review and does not satisfy the completion
  gate. For sub-issue PRs targeting an epic integration branch, run that freshness gate against the current epic branch;
  for the final epic-to-`main` PR, run it against current `origin/main`.
- Resolve all compilation errors before considering a task complete.
- Run impacted tests for each task and fix failures introduced by your changes.
- Create a new git commit after each completed task to keep review scope clear.
- When acting as the main implementation orchestrator, delegate all coding and fixing tasks to worker subagents and use
  fresh review-only subagents for code review. The main orchestrator must not implement, repair, inspect or validate
  code fixes as a substitute for the worker, or commit code changes locally. If an earlier worker thread is lost,
  compacted away, leaves uncommitted work, or cannot be resumed, assign the continuation to a fresh worker subagent with
  the existing worktree/branch context and required validation. The main agent coordinates issues, prompts, review
  ledgers, pull requests, CI/merge status, docs/process updates, and final integration evidence. Follow the required
  subagent review loop in `docs/Development process.md`.
- After the first coding subagent that works on an issue commits and pushes the new branch to origin, open a normal
  ready-for-review pull request. Keep it open while the step is still in progress so subsequent code-review findings,
  fixes, and "Reviewed And OK" notes can be recorded on the pull request instead of only on the issue.
- Persist each review-only subagent report, including "Reviewed And OK" notes, to the pull request when one exists, or
  to the issue before the first pull request exists. Later review prompts should include prior OK notes and ask the
  reviewer to re-check them only when the new diff affects those areas. If the first code review for a new pull request
  finds no issues, still add a pull request comment with the review output so the review pass is documented where code
  review evidence belongs.
- When adding or updating code-review comments on a pull request, update the pull request body's `Review Status` section
  at the same time. Keep it as a high-level summary of reviews run, open findings, fix commits, final no-issues reviews,
  and where the detailed review/fix comments live.
- When the user asks to address a code review comment, review comment, PR feedback, or similar, complete the full
  review-thread workflow: evaluate the comment, make the appropriate fix or explicitly explain why no code change is
  needed, validate the result, commit and push the branch, reply to the GitHub review comment with the outcome and
  evidence, and resolve the GitHub conversation when the feedback has been satisfied.
- When the user says a PR is merged, verify the merge, delete the issue branch and worktree, run `git fetch --prune`,
  and `git pull --ff-only` in the local repo so `main` is up to date.
- Record skipped validation, docs impact, residual risk, and follow-ups in the task record or pull request.
- Write tests for new features and bug fixes; prioritize critical paths.
- Document new public APIs with XML comments and update nearby `AGENTS.md`/docs when behavior changes.
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
- Add lightweight comments only where control flow or transformations are non-obvious.
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
