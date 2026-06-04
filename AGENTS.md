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
completed sub-issues are checked.
During epic planning, choose the merge strategy. Use direct-to-`main` slices only when each sub-issue is independently
production-safe. When `main` is production-bound or intermediate states are not independently production-safe, create an
`epic/<parent-issue>-<slug>` integration branch from `origin/main`, branch sub-issue worktrees from the current
`origin/epic/...`, open sub-issue PRs to the epic branch, keep that branch refreshed from `origin/main`, and use the
final epic-to-`main` PR as the production release candidate. Ensure CI or recorded validation covers PRs targeting
`epic/**` before relying on the integration branch flow.

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
  check its box in the epic.
- Before assigning or starting a sub-issue, apply the minimum detail gate: the issue body must include the invariant
  tuple, forbidden implementation shapes, expected tests, and high-risk adversarial examples. Keep each sub-issue small,
  clear, and contextual enough that an implementation agent could reasonably implement it from the issue body alone
  without hidden project context.
- Declare owned paths, forbidden or sensitive paths, risk surfaces, validation, docs impact, and definition of done
  before editing.
- After the issue exists, claim it and create an issue-owned branch/worktree from the selected base before editing:
  latest `origin/main` for direct-to-`main` slices, or current `origin/epic/...` for sub-issues under an explicitly
  declared epic integration branch.
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
  verify the scoped diff and that no unexpected deletions are present, run the chosen validation gate, then spawn the
  final review-only sibling. Review on a stale branch is exploratory pre-review and does not satisfy the completion
  gate. For sub-issue PRs targeting an epic integration branch, run that freshness gate against the current epic branch;
  for the final epic-to-`main` PR, run it against current `origin/main`.
- Commit after each completed slice and keep pull requests focused and reviewable.
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
- After each review-only subagent pass, persist the findings and "Reviewed And OK" notes to the pull request when one
  exists, or to the GitHub issue before the first pull request exists. Include prior "Reviewed And OK" notes in later
  review prompts so reviewers can build on prior coverage instead of repeating it. If the first code review for a new
  pull request finds no issues, still add a pull request comment with the review output so the review pass is documented
  where code review evidence belongs.
- When adding or updating code-review comments on a pull request, update the pull request body's `Review Status` section
  at the same time. Keep it as a high-level summary of reviews run, open findings, fix commits, final no-issues reviews,
  and where the detailed review/fix comments live.
- Open normal ready-for-review pull requests. Do not open draft pull requests unless the user explicitly asks for a
  draft.
- When the user says a PR is merged, verify the merge, delete the issue branch and worktree, run `git fetch --prune`,
  and `git pull --ff-only` in the local repo so `main` is up to date.
- Update README, CONTRIBUTING, nearby `AGENTS.md`, and other docs when behavior, commands, APIs, or workflow changes.
- When the user asks to address a code review comment, review comment, PR feedback, or similar, treat that as a complete
  review-thread workflow: evaluate the comment, make the appropriate fix or explicitly explain why no code change is
  needed, validate the result, commit and push the branch, reply to the GitHub review comment with the outcome and
  evidence, and resolve the GitHub conversation when the feedback has been satisfied.

## Markdown Guidelines

- Follow the MarkdownLint ruleset found at `https://raw.githubusercontent.com/DavidAnson/markdownlint/refs/heads/main/doc/Rules.md`.
- Verify updates by running MarkdownLint. Use `npx --yes markdownlint-cli2 ...`. `--help` is available.
- For MD013, override the guidance to allow for 120-character lines.

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
