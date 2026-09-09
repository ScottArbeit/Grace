# Grace.CLI Agents Guide

Read `../AGENTS.md` for global expectations before updating CLI code.

## Purpose

- Provide developer tooling entry points that wrap Grace services via
  System.CommandLine and Spectre.Console.
- Surface Grace workflows through friendly commands while keeping logic
  reusable for tests and automation.

## Key Patterns

- Define command arguments and options in dedicated `Options` modules that
  produce `Option<'T>` values.
- Create handlers with `CommandHandler.Create(...)` that delegate to reusable
  services (often via `Grace.SDK`).
- Use Spectre.Console for user interaction, but keep core logic testable and
  separate from console presentation.
- Maintain alignment with DTOs and contracts defined in `Grace.Types`.
- Auth commands support PATs via `GRACE_TOKEN` (PAT-only), Auth0 M2M, and
  Auth0 interactive login with secure token storage. Local token files are
  disabled. Keep token values out of output except on creation.
- Avoid positional parameters; prefer named options for clarity.

## Project Rules

1. Keep handlers thin; move heavier logic into services or helpers that are
   straightforward to unit test.
2. Preserve existing option names and switches unless a tracked decision explicitly replaces the public contract
   without compatibility aliases. Do not restore a deliberately removed command or option as a hidden alias.
3. Capture new command patterns or usage tips in this document to guide future  
   agents.
4. Root and selected subcommand help grouping lives in
   `src/Grace.CLI/Program.CLI.fs` under `rootHelpSections` and the related
   `*HelpSections` lists; update those lists when adding or renaming commands
   so new entries do not silently drift into "Other".

## Local State DB

- Local status and object cache are stored in `.grace/grace-local.db`.
- SQLite side files (`.db-wal`, `.db-shm`, and optional `.db-journal`) are
  internal; ignore them in repo scans and watch change detection except for
  status-change coordination.

## Recent Patterns

- `owner get-directory-version-observation` requires four explicit historical IDs. Its local `JsonElement` projection
  preserves decimal quantity and UTC timestamp strings through the common renderer and `--select`. Keep the registry
  schema/examples synchronized; the shared serializer stays unchanged.

- `grace library sync enable/run/pause/resume/status` owns Windows Library participation in exactly three tables in the existing local database. Keep pause independent from progress. Under the shared root lease, reread pause before capture, synchronization, echo classification or pruning. Only explicit resume clears it; a racing Watch tick skips pause without hiding other failures. Preserve catalog classification and version-control exclusion while paused. Keep frozen requests, materialized edit bases, filesystem guards, and completion transactions in the Library modules. Never acquire the root lease again inside a helper that already holds it. Library work must never create a WDU completion. Watch consumes `LibraryContentAvailable.v1` only as a pull hint.
- `LibraryOperation` stores one typed intent/progress value per operation; derive SQL routing, completion and echo columns from it. Saved-file content must exist as a complete verified immutable object under the configured object directory before SQLite commit. Keep its locator fixed across rename, upload from that object, retain rejected saved work, and block missing/corrupt references instead of substituting working bytes. Operation pruning must not delete shared objects.
- Populated-Library onboarding acquires complete metadata before effects. Repository state retains its baseline selection, operations retain baseline items separately from accepted changes, and materialized items appear only after verified effects. Commit the baseline boundary separately after all required work; gate capture through initial catch-up. Keep incomplete-token restart restricted to an unchanged catalog and empty local root. There is no migration contract for old development-only Library schemas.
- Library synchronization excludes zero-byte input before creating pending operations or preparing uploads. Empty files remain present and protected; retain already-captured positive sources and exact submitted requests. A file rename may remove changed source bytes only against their exact already-accepted pending content. Preserve `ItemTombstoned` rejection and saved input after deletion.
- `grace library rename <path> <new-name>` selects one clean nonempty materialized file in its existing parent. Keep the selected intent, generated operation ID, frozen namespace-only request and genuine receipt in the existing operation row. Apply acceptance through ordered pull; a definitive rejected unprepared rename retires and returns before pulling. Preserve saved-content rejection, exact prepared Watch classification and the four human/JSON outcomes.
- `grace history` commands operate without requiring a repo `graceconfig.json`.
  Avoid `Configuration.Current()` in history-related flows.
- `grace connect` accepts a positional shortcut in the form
  `owner/organization/repository`; do not combine it with explicit owner,
  organization, or repository options.
- Manifest-backed uploads are default-on for eligible large files. Keep whole-file
  fallback limited to ineligible files or recoverable manifest-upload failures so
  eligible files are not uploaded twice.

## Continuous Review Commands

- `grace workitem` (aliases: `work`, `work-item`, `wi`) covers create/show/set-status,
  linking references or promotion sets, and exact attachment add, retrieval, delete, and undelete commands. Do not
  restore the removed bulk `links remove summary|prompt|notes` command paths.
- `grace review` covers inbox/open/checkpoint/delta/resolve/deepen. Inbox and
  delta remain CLI stubs until server endpoints land.
- `grace queue` covers status/enqueue/pause/resume/dequeue; prefer
  `--branch` but `--branch-id`/`--branch-name` still work.

## Validation

- Add option parsing tests and handler unit tests for new functionality.
- Manually exercise impacted commands when practical and ensure
  `dotnet build --configuration Release` stays green.

## Command Modules (`Grace.CLI.Command`)

- Keep Library support modules in `Library/`: local state, filesystem mechanics, manifest upload, baseline acquisition and synchronization. `Command/Library.CLI.fs` remains the command entry point. Preserve module names and explicit F# compile order when moving files.
- Library add/remove optionally read one catalog version and generate one operation ID when omitted. Explicit values bypass their defaults, including invalid values handled by existing validation. Preserve lookup errors and stale mutation results without retries or durable command state.

- Parameter classes usually derive from `ParameterBase()`. Keep them
  lightweight and validated at construction.
- Organize command-specific helpers under `Options` modules and wrap
  invocation with `CommandHandler.Create`.
- Enforce that command parsing remains thin. Push complex behavior into
  services so tests can cover edge cases.
- Add parsing and handler behavior tests in tandem with any new command
  implementation.
