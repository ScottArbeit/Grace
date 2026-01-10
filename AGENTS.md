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

## Landing the Plane (Session Completion)

**When ending a work session**, you MUST complete ALL steps below. Work is NOT
complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:

   ```bash
   git pull --rebase
   bd sync
   git push
   git status  # MUST show "up to date with origin"
   ```

5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**

- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
