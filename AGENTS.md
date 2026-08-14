# Agent Instructions

Other `AGENTS.md` files exist in subdirectories, refer to them for more specific context.

## Agent Quickstart (Local)

Prerequisites:

- PowerShell 7.x
- .NET 10 SDK
- Docker Desktop (required for `-Full`)

Commands:

- `pwsh ./scripts/bootstrap.ps1`
- Run the smallest meaningful focused proof for the changed slice.

Use `pwsh ./scripts/validate.ps1 -Fast` as an optional broad local preflight and `-Full` for local integration
reproduction or diagnosis. GitHub `Validate` is the required broad gate for the current pull-request revision.
Optional: `pwsh ./scripts/install-githooks.ps1` adds a lightweight pre-commit staged-diff check; focused tests and
formatting remain explicit responsibilities.

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
For multi-step implementation plans, create an epic parent issue, but initially create only the earliest tracer and
prerequisites proven necessary by current evidence. Use native GitHub parent relationships and a compact dependency
map for created work. Do not pre-create a broad horizontal issue forest. Re-plan and create later child issues after the
tracer runs. Use the concrete `addSubIssue` GraphQL workflow in `docs/Development process.md` for each created child.
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
- For multi-step implementation plans, use an epic parent issue and native GitHub parent relationships, but initially
  create only the earliest tracer and prerequisites proven necessary by current evidence. Keep a compact dependency map
  for created work, re-plan after the tracer, and avoid pre-creating a horizontal issue forest. Use the concrete
  `addSubIssue` GraphQL workflow in `docs/Development process.md` for each created child.
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
- Before assigning behavior-changing work, require the compact Issue Readiness gate in
  `docs/Development process.md`: one user-visible outcome, supported world, Product V1 capability budget, primary
  invariant and authority, explicit non-goals, algorithm-witness result when required, focused proof, and owner stop
  conditions. Do not paste broad adversarial checklists or require exhaustive N/A inventories.

- Before turning a product spec or implementation plan into tracked work, record decision closure. Name product
  decisions that are accepted, recommended, deferred, or waived; include the recommended default when the plan makes a
  best-effort assumption. Do not leave audience, visibility, ownership, lifecycle, publication timing, failure behavior,
  or accepted-but-unimplemented inputs implicit.
- For any issue that touches public, durable, generated, or cross-project behavior, include a contract propagation map:
  shared DTOs/parameters/events, persisted shapes, HTTP routes, CLI, SDK, OpenAPI/static/generated artifacts, docs, and
  tests. Every surface must be updated or explicitly marked N/A with a reason.
- For runtime, storage, materialization, watch, eventing, or authorization work, include a stale-authority preflight:
  the authoritative source before the decision, the revalidation point before mutation/materialization/publication, the
  failure/abort state, retry/cleanup behavior, and the proof that stale snapshots cannot win.

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
- Prefer vertical slices with focused local proof: RED where applicable, formatting, the smallest relevant test or
  parser/lint/rendered-artifact check, required freshness checks, and `git diff --check` before each commit.
- Do not routinely run local Fast or Full and then repeat the same broad proof in required CI. Fast is an optional
  broad preflight; Full is for local integration reproduction or diagnosis. Use either only for concrete escalation,
  such as unavailable CI, broad compile fan-out, a requested gate, or local investigation.
- When a focused `dotnet test` command uses `--no-build`, run the matching Release build first. If Fast or Full is
  intentionally selected, it replaces, rather than supplements, the routine focused build/test gate for that checkpoint.
- Treat parallel work as two separate decisions: product/DAG independence and merge/write-set independence. Run
  branches in parallel only when their write sets are disjoint enough to avoid predictable churn. Serialize or
  merge-queue branches that touch shared project files such as `*.fsproj`, `Startup.Server.fs`, or the same test/helper
  files. For broad waves, consider a preparatory compile-item or file-scaffold slice before later branches edit
  separate files.
- Before tracked implementation, freeze a Factory Run Charter from `dev-process/TEMPLATES.md`: issue and outcome,
  base SHA, supported world, quality contract, algorithm-witness result, agent topology, review protocol, and stop
  conditions. Do not change these rules inside the run. A material process change stops the run and starts a new charter.
- For stateful, destructive, filesystem, retry, recovery, concurrent, background, or multi-authority work, require the
  `dev-process` Algorithm Readiness Gate before production coding. Use a disposable executable witness that injects
  failure at meaningful effect boundaries and restarts from captured state. Do not use review to discover the algorithm.
- Default to one active high-risk epic or factory-calibration stream. Parallelize only when the owner explicitly approves
  independent outcomes, authority models, write sets, and integration paths.
- Use one short-lived controller per issue or pull request. The controller is the only agent allowed to spawn subagents.
  Workers and reviewers must not spawn agents. Do not keep one root controller alive across an entire epic.
- Use one issue-owner implementation worker. That worker owns the implementation, focused proof, self-review, and all
  accepted in-scope repairs. Do not start a fresh worker for each finding. Use one replacement only when the original
  worker is genuinely unavailable or its context is unusable, and record why.
- The controller may inspect code, diffs, logs, tests, and validation evidence and may run read-only or validation
  commands. It coordinates GitHub state, CI, review ledgers, merge, and cleanup. It must not silently replace the issue
  owner's implementation work.
- Do not require temp status files or fixed-interval worker heartbeats. Require concise updates before a long-running
  command, when a material finding or blocker appears, and at handoff. Never sleep or poll for more than 120 seconds in
  one command.
- Before handoff, require the issue owner to inspect the actual diff against the outcome, non-goals, quality contract,
  authority and effect model, contract propagation, proof truth, and owned paths. The worker must not start an independent
  review agent.
- Open one coherent ready-for-review pull request after the first validated candidate. Do not use the PR as a scratchpad
  for discovering the architecture.
- Run one independent **R1 discovery review** of the complete candidate using `dev-process/CODE_REVIEW.md` in a
  fresh, read-only review context supplied only with the complete run packet and repository evidence needed for the
  review. It must produce a finite Review Discovery Ledger. If R1 passes and current-head GitHub `Validate` passes,
  the PR may proceed without R2.
- Classify the R1 ledger once. Reject unsupported-path hardening, stale or duplicate findings, and product decisions
  presented as defects. Freeze accepted findings for the run.
- Route the accepted ledger back to the same issue-owner worker for one consolidated repair pass. Do not restart broad
  discovery review after each repair commit.
- When repairs were required, run one independent **R2 closure review** on the repaired current head. R2 verifies accepted
  ledger items, repair hunks, direct repair regressions, current-head proof, required CI, and scope preservation. R2 must
  not reopen untouched parts of the original diff for another unconstrained search.
- There is no automatic R3. If R2 incidentally exposes a supported-world merge blocker outside the frozen ledger and
  direct repair-regression scope, record one `DISCOVERY ESCAPE` and stop. Do not ignore it or keep searching. A new
  material invariant, authority boundary, state machine, product semantic, or scope expansion is likewise an owner stop
  and requires a new charter, split, or supersession.
- Required GitHub `Validate` must pass on the final head. R1 remains valid as the finite discovery ledger; R2 and final CI
  certify the repaired head. A repair commit does not by itself authorize another whole-diff review.
- When a required check fails, inspect the newest failed workflow for the final head and classify actual owned-diff
  failures separately from unrelated repository or environment failures before assigning repair work.
- For epic-branch pull requests, defer a finding only when it is outside the current leaf and a named future issue owns
  the exact behavior and proof. Do not defer a prerequisite that makes the current leaf's produced facts untrustworthy.
- Stop regardless of review count when the issue gains another primary invariant, durable state machine, authority
  boundary, or product semantic; when a third enabling PR is proposed before the tracer; when process rules must change;
  or when review is writing the product model. Preserve a salvage map before superseding.
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
