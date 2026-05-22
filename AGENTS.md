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
- `src/docs/ASPIRE_SETUP.md`
- `src/docs/ENVIRONMENT.md`

## Issue Tracking

Use GitHub issues and pull requests as the active coordination surface for implementation work.
For non-trivial work, follow `docs/Development process.md`: create or confirm a GitHub issue, declare owned paths,
create an issue-owned branch/worktree, validate in focused slices, commit after each completed slice, and record docs
impact and skipped validation.

## Development Process

- Read the closest `AGENTS.md` before editing. Root guidance applies repo-wide; project guidance applies within that
  subtree.
- When the user says `Plan <work item>`, plan the work in chat. Do not create a GitHub issue unless the user asks for
  an issue, asks to start tracked implementation, or otherwise explicitly requests tracker setup.
- When the user asks to create a GitHub issue, use the Grace agent task template and stop before implementation edits
  unless they also ask you to implement.
- For tracked implementation work, keep one visible task record: the GitHub issue.
- Declare owned paths, forbidden or sensitive paths, risk surfaces, validation, docs impact, and definition of done
  before editing.
- After the issue exists, claim it and create an issue-owned branch/worktree from latest `origin/main` before editing.
- Prefer vertical slices with focused tests and `pwsh ./scripts/validate.ps1 -Fast` as the normal validation gate.
- Use `pwsh ./scripts/validate.ps1 -Full` when Aspire, emulators, storage, Service Bus, Cosmos DB, Redis,
  deployment/runtime behavior, or cross-service integration is affected.
- Commit after each completed slice and keep pull requests focused and reviewable.
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
