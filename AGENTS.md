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
Run `bd prime` for workflow context, or install hooks (`bd hooks install`) for auto-injection.

**Quick reference:**

- `bd ready` - Find unblocked work
- `bd create "Title" --type task --priority 2` - Create issue
- `bd close <id>` - Complete work
- `bd sync` - Sync with git (run at session end)

For full workflow details: `bd prime`
