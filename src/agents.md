# Grace Repository Agents Guide

Agents operating under `D:\Source\Grace\src` should follow this playbook alongside the existing `AGENTS.md` in the repo root. Treat this file as the canonical high-level brief; each project folder contains an `AGENTS.md` with deeper context.

## Issue Tracking with bd (beads)

**IMPORTANT**: This project uses **bd (beads)** for ALL issue tracking. Do NOT use markdown TODOs, task lists, or other tracking methods.

### Why bd?

- Dependency-aware: Track blockers and relationships between issues
- Git-friendly: Auto-syncs to JSONL for version control
- Agent-optimized: JSON output, ready work detection, discovered-from links
- Prevents duplicate tracking systems and confusion

### Quick Start

**Check for ready work:**
```bash
bd ready --json
```

**Create new issues:**
```bash
bd create "Issue title" -t bug|feature|task -p 0-4 --json
bd create "Issue title" -p 1 --deps discovered-from:bd-123 --json
bd create "Subtask" --parent <epic-id> --json  # Hierarchical subtask (gets ID like epic-id.1)
```

**Claim and update:**
```bash
bd update bd-42 --status in_progress --json
bd update bd-42 --priority 1 --json
```

**Complete work:**
```bash
bd close bd-42 --reason "Completed" --json
```

### Issue Types

- `bug` - Something broken
- `feature` - New functionality
- `task` - Work item (tests, docs, refactoring)
- `epic` - Large feature with subtasks
- `chore` - Maintenance (dependencies, tooling)

### Priorities

- `0` - Critical (security, data loss, broken builds)
- `1` - High (major features, important bugs)
- `2` - Medium (default, nice-to-have)
- `3` - Low (polish, optimization)
- `4` - Backlog (future ideas)

### Workflow for AI Agents

1. **Check ready work**: `bd ready` shows unblocked issues
2. **Claim your task**: `bd update <id> --status in_progress`
3. **Work on it**: Implement, test, document
4. **Discover new work?** Create linked issue:
   - `bd create "Found bug" -p 1 --deps discovered-from:<parent-id>`
5. **Complete**: `bd close <id> --reason "Done"`
6. **Commit together**: Always commit the `.beads/issues.jsonl` file together with the code changes so issue state stays in sync with code state

### Auto-Sync

bd automatically syncs with git:
- Exports to `.beads/issues.jsonl` after changes (5s debounce)
- Imports from JSONL when newer (e.g., after `git pull`)
- No manual export/import needed!

### GitHub Copilot Integration

If using GitHub Copilot, also create `.github/copilot-instructions.md` for automatic instruction loading.
Run `bd onboard` to get the content, or see step 2 of the onboard instructions.

### MCP Server (Recommended)

If using Claude or MCP-compatible clients, install the beads MCP server:

```bash
pip install beads-mcp
```

Add to MCP config (e.g., `~/.config/claude/config.json`):
```json
{
  "beads": {
    "command": "beads-mcp",
    "args": []
  }
}
```

Then use `mcp__beads__*` functions instead of CLI commands.

### Managing AI-Generated Planning Documents

AI assistants often create planning and design documents during development:
- PLAN.md, IMPLEMENTATION.md, ARCHITECTURE.md
- DESIGN.md, CODEBASE_SUMMARY.md, INTEGRATION_PLAN.md
- TESTING_GUIDE.md, TECHNICAL_DESIGN.md, and similar files

**Best Practice: Use a dedicated directory for these ephemeral files**

**Recommended approach:**
- Create a `history/` directory in the project root
- Store ALL AI-generated planning/design docs in `history/`
- Keep the repository root clean and focused on permanent project files
- Only access `history/` when explicitly asked to review past planning

**Example .gitignore entry (optional):**
```
# AI planning documents (ephemeral)
history/
```

### CLI Help
Run `bd <command> --help` to see all available flags for any command.
For example: `bd create --help` shows `--parent`, `--deps`, `--assignee`, etc.

### Important Rules
- ✅ Use bd for ALL task tracking
- ✅ Always use `--json` flag for programmatic use
- ✅ Link discovered work with `discovered-from` dependencies
- ✅ Check `bd ready` before asking "what should I work on?"
- ✅ Store AI planning docs in `history/` directory
- ✅ Do NOT create markdown TODO lists
- ✅ Do NOT use external issue trackers
- ✅ Do NOT duplicate tracking systems
- ✅ Do NOT clutter repo root with planning documents

For more details, see README.md and QUICKSTART.md.



## Core Engineering Expectations

- Make a multi-step plan for non-trivial work, keep edits focused, and leave code cleaner than you found it.
- Validate changes with `dotnet build --configuration Release` and `dotnet test --no-build`; run `fantomas .` after touching F# source.
- Treat secrets with care, avoid logging PII, and preserve structured logging (including correlation IDs).
- Favor existing helpers in `Grace.Shared` before adding new utilities; when new helpers are required, keep them composable and well documented.

## F# Coding Guidelines

- Default to F# for new code unless stakeholders specify another language; surface trade-offs if deviation seems necessary.
- Use `task { }` for asynchronous workflows and keep side effects isolated; ensure any awaited operations remain within the computation expression.
- Prefer a functional style with immutable data, small pure functions, and explicit dependencies passed as parameters.
- Apply the modern indexer syntax (`myList[0]`) for lists, arrays, and sequences; avoid the legacy `.[ ]` form.
- Structure modules so that domain types live in `Grace.Types`, shared helpers in `Grace.Shared`, and orchestration in the appropriate project-specific assembly.
- Add lightweight inline comments when control flow or transformations are non-obvious, and log key variable values in functions longer than ten lines to aid diagnostics.
- Format code with `fantomas` and include comprehensive, copy/paste-ready snippets when sharing examples.

## Agent-Friendly Context Practices

- Start with the relevant `AGENTS.md` file(s) to load key patterns, dependencies, and test strategy before exploring the codebase. These files replace the old `instructions.md`.
- Use these summaries to decide which source files actually need inspection; open code only when context is missing or implementation verification is required.
- When documenting new behavior, update the closest `AGENTS.md` to keep future agents informed—living documentation reduces the need for broad code scans.

## Collaboration & Communication

- Summarise modifications clearly, cite file paths with 1-based line numbers, and call out follow-up actions or tests that remain.
- Coordinate cross-project changes across `Grace.Types`, `Grace.Shared`, `Grace.Server`, `Grace.Actors`, and `Grace.SDK` to keep contracts aligned.
- When adding new capabilities, ensure matching tests exist and note any coverage gaps or risks in the final hand-off.


