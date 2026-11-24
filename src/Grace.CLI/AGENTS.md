# Grace.CLI

The `grace` command-line tool, built on System.CommandLine and Spectre.Console, mostly calling services through
`Grace.SDK`.

## Commands

- Define options in a command's `Options` module and create handlers with `CommandHandler.Create(...)`. Parameter
  classes usually derive from `ParameterBase()`.
- Keep handlers thin; put logic in services or helpers that tests can reach. Keep presentation separate from logic.
- Use named options rather than positional parameters. (`grace connect owner/organization/repository` is the one
  positional shortcut; don't combine it with the explicit owner, organization, or repository options.)
- Don't restore a deliberately removed command or option as a hidden alias.
- When adding or renaming commands, update `rootHelpSections` and the related `*HelpSections` lists in
  `Program.CLI.fs` so the command doesn't fall into "Other".
- Startup builds only the requested top-level command's subtree. A new top-level command or alias goes in
  `topLevelCommands` in `Program.CLI.fs`. In shared helpers, look options up by name with `Services.getResultByName` and
  `Services.getValueByName`: a name that only other commands declare would make `parseResult.GetResult(name)` throw.
- Tests parse through `GraceCommand.fullRootCommand.Value`.
- Add parsing and handler tests with every new command.

## Auth

`GRACE_TOKEN` accepts Grace PATs only. Auth0 M2M and interactive login use secure token storage; local token files are
disabled. Never print token values except at creation.

## Local state

- Local status and object cache live in `.grace/grace-local.db`. Ignore the SQLite side files (`.db-wal`, `.db-shm`,
  `.db-journal`) in repo scans and Watch change detection, except for status-change coordination.
- `grace history` commands work without a repo `graceconfig.json`; don't call `Configuration.Current()` in those flows.

## Libraries

Library support modules live in `Library/`; `Command/Library.CLI.fs` is the entry point. Preserve module names and the
explicit F# compile order when moving files. The Library sync, onboarding, operation, and rename invariants are
documented in `///` comments on those modules. Read them before changing Library code.

## Uploads

Manifest-backed uploads are on by default for eligible large files. Whole-file upload is the fallback only for
ineligible files or recoverable manifest-upload failures, so eligible files are never uploaded twice.
