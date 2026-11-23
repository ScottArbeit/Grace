# Grace.CLI.Tests

CLI behavior tests that run without a server.

- Match test files to command files: `Grace.CLI/Command/WorkItem.CLI.fs` -> `WorkItem.CLI.Tests.fs`. Root command
  behavior goes in `Program.CLI.Tests.fs`.
- Pure parser tests go in `*.CLI.Parsing.Tests.fs` files, which may run in parallel. They must not touch invocation,
  console, environment, filesystem, current directory, local config or history, or SDK identity.
- Everything stateful stays in the non-parsing files, which run serialized. Keep auth tests in their own files.
- Back up and restore anything touched under `~/.grace`. Use fixed timestamps for history tests.
- Use FsUnit for assertions and FsCheck where properties add value.
