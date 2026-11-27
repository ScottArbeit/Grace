# Agent Instructions

Grace is a version control system written in F# on .NET 11, with an Orleans and Aspire backend, a CLI, and an SDK.
Nested `AGENTS.md` files add project-specific rules; `src/AGENTS.md` covers all source work.

## Setup

- PowerShell 7.x, the .NET SDK selected by `global.json`, and Docker Desktop for integration tests.
- `pwsh ./scripts/bootstrap.ps1`
- The code is the documentation. `skills/grace/SKILL.md` routes cross-cutting rules; `src/docs/ENVIRONMENT.md` lists
  environment variables.

## Rules that apply everywhere

- **Grace is pre-production.** There is no production data to migrate, import, preserve, or grandfather. Write
  contracts, validators, and tests for the behavior Grace should have now. Add compatibility behavior only when an issue
  asks for it. Resetting development data never excuses broken serialization, replay, authorization, or truthful
  failure.
- **A request to plan stays in chat.** Create GitHub issues, branches, or PRs only when asked to track or implement the
  work.
- **Say why.** Issues, PRs, and plans explain how the change helps Grace and its users, so whoever implements it can make
  good calls where the plan is silent.
- **Stop and ask** before work grows a new domain term, durable state machine, authority boundary, public contract, or
  product rule that the issue did not already decide. Bring a recommendation.
- **Don't write docs the code already says.** Add no new Markdown files; record decisions and their reasons in the issue
  and PR. Edit README, CONTRIBUTING, `src/docs/ENVIRONMENT.md`, or an `AGENTS.md` only when your change makes something
  in them wrong.

## Tracked work

Follow `CONTRIBUTING.md` for issues, epics, branches, validation, review, merge, and cleanup. The short
version: one issue per outcome, a branch from current `origin/main`, focused tests locally, GitHub `Validate` as the
broad gate, one independent review, Scott approves the merge, then clean up the branch and worktree.

When asked to address PR review feedback: decide whether it is right, fix it or explain why not, validate, push, reply
on the thread with the outcome, and resolve it.

## Writing docs

- Technical docs: write as a friendly peer engineer. Product docs: write as a product manager explaining Grace to a
  tech-aware reader. Prefer plain words over jargon.
- Show PowerShell examples first, then bash / zsh (always lowercase) when they apply:

PowerShell:

```powershell
$env:GRACE_SERVER_URI="http://localhost:5000"
```

bash / zsh:

```bash
export GRACE_SERVER_URI="http://localhost:5000"
```

- Markdown follows `.markdownlint.jsonc`. Check it with `npx --yes markdownlint-cli2 <files>`.
