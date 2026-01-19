# Agent Instructions

Other `AGENTS.md` files exist in subdirectories, refer to them for more specific
context.

## Agent Quickstart (Local)

Prerequisites:

- PowerShell 7.x
- .NET 10 SDK
- Docker Desktop (required for `-Full`)

Commands:

- `pwsh ./scripts/bootstrap.ps1`
- `pwsh ./scripts/validate.ps1 -Fast`

Use `pwsh ./scripts/validate.ps1 -Full` for Aspire integration coverage.
Optional: `pwsh ./scripts/install-githooks.ps1` to add a pre-commit
`validate -Fast` hook.

More context:

- `src/AGENTS.md`
- `src/docs/ASPIRE_SETUP.md`
- `src/docs/ENVIRONMENT.md`

## Issue Tracking

This project uses **bd (beads)** for issue tracking.
Always use `bd` commands to manage your work.
When creating beads, include a description.
Run `bd prime` for workflow context, or install hooks (`bd hooks install`) for auto-injection.

**Quick reference:**

- `bd ready` - Find unblocked work
- `bd create "Title" --type task --priority 2` - Create issue
- `bd close <id>` - Complete work
- `bd sync` - Sync with git (run at session end)

For full workflow details: `bd prime`

## Markdown guidelines

- Follow the MarkdownLint ruleset found at
  `https://raw.githubusercontent.com/DavidAnson/markdownlint/refs/heads/main/doc/Rules.md`.
- Verify updates by running MarkdownLint. Use `npx --yes markdownlint-cli2 ...`. `--help` is available.
- For MD013, override the guidance to allow for 120-character lines.

## Editing Documentation

When updating documentation files, follow these guidelines:

- When writing technical documentation, act as a friendly peer engineer helping other developers to understand Grace as a project.
- When writing product-focused documentation, act as an expert product manager who helps a tech-aware audience understand Grace as a product, and helps end users understand how to use Grace effectively.
- Use clear, concise language; avoid jargon. The tone should be welcoming and informative.
- Structure content with headings and subheadings. Intersperse written (paragraph / sentence form) documentation with bullet points for readability.
- Keep documentation up to date with code changes; review related docs when modifying functionality. Explain all documentation changes clearly, both what is changing, and why it's changing.
- Show all scripting examples in both (first) PowerShell and (then, second) bash/zsh, where applicable. bash and zsh are always spelled in lowercase. Good example:

  PowerShell:
  ```powershell
  $env:GRACE_SERVER_URI="http://localhost:5000"
  ```
  bash / zsh:
  ```bash
  export GRACE_SERVER_URI="http://localhost:5000"
  ```
