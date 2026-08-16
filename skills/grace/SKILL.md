---
name: grace
description: Grace repository workflow, architecture, and implementation guidance. Use when working in a Grace repo or on Grace planning, GitHub issue orchestration, F#/.NET code, Orleans actors, Giraffe HTTP APIs, SDK or CLI surfaces, DTOs/events/parameters, authorization, webhooks and approval requests, manifest-backed storage, tests, Aspire/runtime, docs, CONTRIBUTING, or AGENTS updates.
---

# Grace

Use this skill to work in the Grace repository without loading every domain-specific playbook up front.

## Start Here

1. Read the repo-local instructions before editing:
   - `AGENTS.md`
   - the closest nested `AGENTS.md`, usually under `src/`
   - `docs/Development process.md` for non-trivial tracked work
1. Inspect the current code, commands, and tests before answering behavior questions. Grace changes quickly.
1. Keep planning-only requests in chat. Create issues, branches, worktrees, or PRs only when the user asks for tracked
   implementation or tracker setup.
1. For tracked implementation, use the Grace issue-owned workflow and validation profile from
   [workflow.md](references/workflow.md).
1. Load only the reference files needed for the task.

## Reference Router

Read these files on demand:

| Task | Load |
| ---- | ---- |
| Creating or auditing a canonical Grace specification; Grace defaults, propagation surfaces, and readiness traps | [specification-profile.md](references/specification-profile.md) |
| Issue-owned work, epics, DAGs, review loops, branch/worktree cleanup, validation profiles | [workflow.md](references/workflow.md) |
| Finding code, choosing project boundaries, understanding the repo layout | [project-map.md](references/project-map.md) |
| DTOs, domain events, parameters, serializers, shared helpers, role semantics | [contracts-and-shared.md](references/contracts-and-shared.md) |
| HTTP routes, Giraffe handlers, endpoint authorization, SDK, CLI, public command behavior | [public-surfaces.md](references/public-surfaces.md) |
| Orleans grains, event-sourced decisions, idempotency, reminders, durable state transitions | [actors-and-durability.md](references/actors-and-durability.md) |
| Auth, RBAC, PATs, OIDC, TestAuth, path permissions, security review points | [security-and-auth.md](references/security-and-auth.md) |
| Manifest-backed uploads, ContentBlocks, Service Bus, webhooks, Aspire, hosted/runtime work | [runtime-and-storage.md](references/runtime-and-storage.md) |
| Server integration tests, CLI tests, contract tests, authorization tests, validation commands | [tests.md](references/tests.md) |
| README, CONTRIBUTING, AGENTS, Markdown, HTML/process docs, contributor guidance | [docs-and-contributing.md](references/docs-and-contributing.md) |

## Sub-skill Router

Use these installed or sibling skills when the task needs a specialized workflow:

| Task | Load |
| ---- | ---- |
| Canonical specification creation, update, lifecycle audit, traceability, and Plan-ready handoff | Installed `specification` skill plus [specification-profile.md](references/specification-profile.md) |
| Open product/domain/architecture decisions, capability pruning, focused owner interview, or multi-session design map | Installed `design-readiness` skill plus [specification-profile.md](references/specification-profile.md) |
| Implementation plans, spec-to-plan compilation, issues, implementation orchestration, review budgets, merge, and cleanup | Installed `dev-process` skill plus [workflow.md](references/workflow.md) |
| Product V1 capability budgets, algorithm readiness, Factory Run Charters, bounded R1/R2 review, stop conditions, and review recovery | Installed `dev-process` skill plus [workflow.md](references/workflow.md) |

## Grace Defaults

- Prefer repo evidence over memory, guesses, or old plans.
- Use Product V1 as the default quality contract for Grace unless the user or tracked work explicitly chooses another profile.
- Preserve Grace vocabulary: work items, promotion sets, queues, gates, policies, attestations, review reports,
  webhooks, approval policies, approval requests, UploadSessions, FileManifests, ContentBlocks, and
  ManifestContributionWorkflows.
- Keep changes vertically sliced through the nearest public boundary whenever possible.
- For non-trivial epic plans, identify the earliest value-bearing tracer and permit at most two production PRs before
  it. Prefer zero or one. Re-plan later issues from the running tracer instead of pre-creating a horizontal issue forest.
- Apply the Product V1 capability budget by default: one outcome, one primary invariant family, one supported topology,
  one primary authority, at most one new durable partial-state lifecycle, and explicit exclusion of optional automation.
- For stateful, destructive, filesystem, retry, recovery, concurrent, background, or multi-authority work, require the
  `dev-process` Algorithm Readiness Gate before production coding.
- For tracked coding issues, use the compact Issue Readiness and Factory Run Charter from `dev-process`. Do not paste
  broad risk checklists, require fixed worker heartbeats, or start a fresh worker for every review finding.
- Choose each epic child's delivery mode separately from its issue hierarchy. Default to a mainline slice when the
  child is independently correct and safe or inert until consumed. Use the integration-branch exception only after
  the owner records the composition need and proof conditions described in `references/workflow.md`.
- Coordinate across `Grace.Types`, `Grace.Shared`, `Grace.Server`, `Grace.Actors`, `Grace.SDK`, `Grace.CLI`, and tests
  when one surface changes another.
- Prefer the smallest focused proof. GitHub `Validate` is the required broad current-head gate. Use local `-Fast` only
  as an explicit broad preflight and `-Full` for local integration reproduction or diagnosis.
- Use PowerShell examples before bash / zsh in docs.

## PowerShell Text Editing and Quoting

Use PowerShell deliberately when writing or updating text. Most Grace orchestration and GitHub body updates run from
PowerShell, so quoting mistakes can silently flatten Markdown, expand variables, or pass malformed arguments.

- Prefer file-based edits for multiline GitHub issue, pull request, or Markdown bodies. Write a temporary `.md` file,
  validate it, then pass it with `--body-file` or the relevant file argument.
- Use single-quoted here-strings (`@' ... '@`) for literal Markdown, JSON, GraphQL, code, and command text that should
  not expand `$variables`, backticks, or quotes.
- Use double-quoted here-strings (`@" ... "@`) only when interpolation is required. Keep the interpolated values small
  and inspect the generated text before sending it to GitHub or another tool.
- Put here-string headers and footers on their own lines. PowerShell rejects characters after `@'` / `@"` and treats
  leading spaces before the closing marker as content.
- Avoid capturing multiline Markdown through `gh ... --jq .body` into a string and rewriting it directly; this can lose
  line breaks depending on command shape. Prefer `ConvertFrom-Json` on `gh ... --json body`, or write/read explicit
  body files.
- Use `Set-Content -Encoding utf8NoBOM -NoNewline` when you already control the final newline. Otherwise,
  `Set-Content` can add an extra newline that triggers MarkdownLint blank-line findings.
- Normalize external text before linting or rewriting: convert CRLF/CR to LF, remove whitespace-only lines, collapse
  three or more blank lines, then add exactly one final newline.
- Escape only for PowerShell, not for bash. Do not use bash-style `\"`; choose single quotes, doubled single quotes
  inside single-quoted strings, backtick escapes in double-quoted strings, or here-strings instead.
- When replacing text, first verify the exact anchor with `.Contains()`, `.IndexOf()`, or `Select-String`. If the anchor
  fails, inspect nearby text and switch to a section-based replacement instead of guessing.
- For arguments containing `|`, `&`, `?`, JSON, GraphQL, SAS URLs, or Markdown tables, prefer files or arrays of
  arguments over one large inline command string.

## Output Habits

- Cite concrete files and commands in final answers.
- State skipped validation and the reason.
- For reviews, lead with findings and include file/line references.
- For docs-only work, validate Markdown or explain why validation was skipped.

## Specification And Planning Mode

Use this mode when the user asks to design a feature, create or review a product specification, produce an implementation
plan, or audit an issue packet.

1. Read the applicable repo guidance and this skill's relevant references.
2. Load the installed `dev-process` quality contract.
3. Load the installed `specification` skill and
   [specification-profile.md](references/specification-profile.md).
4. Inspect the current source surface enough to verify paths, symbols, contracts, tests, generated artifacts, and docs.
5. When product, domain, architecture, authority, state, failure, feasibility, or scope decisions remain, use the
   installed `design-readiness` skill. Propagate every resolution into one canonical specification.
6. Use the shared `specification` audit to classify the artifact Exploratory, Design-ready, or Plan-ready. Do not
   maintain a separate Grace lifecycle checklist in this router.
7. Before production planning for Tier 2 behavior, use `dev-process` and `prototype` to pass the Algorithm Readiness
   Gate. Propagate the witness verdict into the canonical specification.
8. Only after Plan-ready and algorithm-ready verdicts, use `dev-process` to compile the specification into the earliest
   value-bearing tracer, no more than two enabling PRs, and only the later slices current evidence justifies.
9. Before coding each slice, apply the compact Issue Readiness Gate and freeze a Factory Run Charter.
10. Create GitHub issues, branches, worktrees, or pull requests only when the user explicitly requests tracked work or
    implementation.

The canonical specification owns product behavior and proof traceability. GitHub issues own the executable slice
contract once tracked work begins. Repository guidance owns branch, validation, review, merge, and cleanup mechanics.
