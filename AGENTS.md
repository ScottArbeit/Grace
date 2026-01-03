# Agent Instructions

This project uses **bd** (beads) for issue tracking. The repo is initialized at the root `.beads`, so run `bd` from the repo root.

## Before You Start (MANDATORY)
- Always run `bd ready --json` from repo root before doing anything else.
- You MUST claim or create an issue before editing code:
  - If a suitable issue exists: `bd update <id> --status in_progress`
  - If none exist: create a new issue, then set it in progress.
- Do not make code changes until an issue is in progress.

## Quick Reference

```bash
bd ready --json                 # Find available work (ready issues)
bd list --parent <epic-id>      # List child tasks for an epic (use this; there is no "bd tasks")
bd show <id>                    # View issue details
bd update <id> --status in_progress  # Claim work
bd close <id>                   # Complete work
bd sync                         # Sync with git (when using JSONL sync)
bd --help                       # Command reference (recommended)
```

## Landing the Plane (Session Completion)

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

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
