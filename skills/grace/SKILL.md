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

Use these sibling skills when the task needs a specialized workflow:

| Task | Load |
| ---- | ---- |
| Repeated Codex Code Review Bot findings, review/fix loop monitoring, stabilization ledgers, hard-stop review thresholds | [code-review-stabilizer](../code-review-stabilizer/SKILL.md) |

## Grace Defaults

- Prefer repo evidence over memory, guesses, or old plans.
- Preserve Grace vocabulary: work items, promotion sets, queues, gates, policies, attestations, review reports,
  webhooks, approval policies, approval requests, UploadSessions, FileManifests, ContentBlocks, and
  ManifestContributionWorkflows.
- Keep changes vertically sliced through the nearest public boundary whenever possible.
- For tracked coding issues, write existing issue-detail fields as review-prevention guidance and require worker
  handoffs to include bot-prevention self-review evidence before the pull request review loop.
- When implementing an epic, always use the `epic/<parent-issue>-<slug>` integration branch mode described in
  `references/workflow.md`. Route sub-issue pull requests to that epic branch; do not use direct-to-`main` epic slices.
- Coordinate across `Grace.Types`, `Grace.Shared`, `Grace.Server`, `Grace.Actors`, `Grace.SDK`, `Grace.CLI`, and tests
  when one surface changes another.
- Prefer `pwsh ./scripts/validate.ps1 -Fast`; use `-Full` when Aspire, emulators, Service Bus, storage, Redis,
  deployment/runtime behavior, or cross-service integration is affected.
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
