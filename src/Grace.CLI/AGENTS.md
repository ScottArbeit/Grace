# Grace.CLI

The `grace` command-line tool, built on System.CommandLine and Spectre.Console, mostly calling services through
`Grace.SDK`.

## Commands

- Use named options rather than positional parameters. (`grace connect owner/organization/repository` is the one
  positional shortcut; don't combine it with the explicit owner, organization, or repository options.)
- A deliberately removed command or option stays removed; don't bring it back as a hidden alias.
- When adding or renaming commands, update `rootHelpSections` and the related `*HelpSections` lists in
  `Program.CLI.fs` so the command doesn't fall into "Other".

## Auth

`GRACE_TOKEN` accepts Grace PATs only. Auth0 M2M and interactive login use secure token storage; local token files are
disabled. Print token values only at creation.

## Local state

- Local status and object cache live in `.grace/grace-local.db`. Ignore the SQLite side files (`.db-wal`, `.db-shm`,
  `.db-journal`) in repo scans and Watch change detection, except for status-change coordination.
- `grace history` commands work without a repo `graceconfig.json`, so those flows never call `Configuration.Current()`.

## Libraries

Library support modules live in `Library/`; `Command/Library.CLI.fs` is the entry point. Preserve module names and the
explicit F# compile order when moving files.
