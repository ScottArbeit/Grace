# Grace CLI Command Conversion Guide

This note bundles the shared knowledge needed to migrate legacy CLI subcommands from `CommandHandler.Create` to the `AsynchronousCommandLineAction` pattern. Use it to plan batches, avoid re-scanning the same F# sources, and keep future prompts short.

---

## 1. Core Pattern

- **Entry point**: Replace each `CommandHandler.Create` assignment with `command.Action <- new Xxx()` where `Xxx` is a new action class in the same module.
- **Class template**:

  ```fsharp
  /// Module.CommandName subcommand definition
  type CommandName() =
      inherit AsynchronousCommandLineAction()

      override _.InvokeAsync(parseResult: ParseResult, ct: CancellationToken) : Tasks.Task<int> =
          task {
              try
                  if parseResult |> verbose then printParseResult parseResult
                  let graceIds = parseResult |> getNormalizedIdsAndNames
                  let validateIncomingParameters = parseResult |> CommonValidations

                  match validateIncomingParameters with
                  | Ok _ ->
                      let parameters =
                          Parameters.Namespace.SomeSdkParameters(
                              // map IDs and options here
                          )

                      if parseResult |> hasOutput then
                          let! result =
                              progress
                                  .Columns(progressColumns)
                                  .StartAsync(fun progressContext ->
                                      task {
                                          let t0 =
                                              progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                          let! response = SdkModule.Operation(parameters)
                                          t0.Increment(100.0)
                                          return response
                                      })

                          return result |> renderOutput parseResult
                      else
                          let! result = SdkModule.Operation(parameters)
                          return result |> renderOutput parseResult
                  | Error error ->
                      return (Error error) |> renderOutput parseResult
              with ex ->
                  return
                      renderOutput
                          parseResult
                          (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
          }
  ```

- **Validation & normalization**:
  - `let graceIds = parseResult |> getNormalizedIdsAndNames`
  - `let validateIncomingParameters = parseResult |> CommonValidations`
  - When existing helpers already normalize parameters, reuse them, but remove obsolete `XxxParameters` types and private handler functions.
- **SDK parameter builders**: Map from `graceIds.*` and `parseResult.GetValue Options.xxx`. Keep correlation IDs: `CorrelationId = getCorrelationId parseResult`.
- **Progress UI**: Preserve any existing `progress.Columns(progressColumns)` blocks and nested tasks exactly; only wrap them with `let! result = ...` and `return result |> renderOutput parseResult`.
- **Output**: Always render using `result |> renderOutput parseResult`. When the old code updated configuration (e.g., `Organization.Create`), keep that logic inside the success branch before returning.
- **Error handling**: Use the `with ex ->` clause shown above to wrap in `GraceResult.Error`.
- **Build hygiene**: Each file should compile independently. Run `dotnet build --configuration Release` after a batch rather than after every command unless you suspect a break in the current file.

---

## 2. Suggested Working Cadence

1. Choose a module (e.g., `Repository.CLI.fs`) and convert 2–4 commands per prompt iteration.
2. After finishing a batch, list which handlers are done and which remain; skip per-command “ready checks” to save tokens.
3. Run `dotnet build --configuration Release` when the current file is green. Report only compile issues tied to the edited file.
4. When pausing, note the next handler you intend to convert to resume smoothly without re-reading the module.

---

## 3. Module Cheat Sheets

Use these summaries to map options to SDK parameters quickly.

### Repository (`Grace.CLI/Command/Repository.CLI.fs`)

| Command | SDK target | Key options / notes |
| --- | --- | --- |
| `Create` | `Repository.Create` | Uses org and owner IDs; mirrors existing config update logic. |
| `Init` | `Repository.Init` | accepts `--graceConfig`; may read defaults from file system. |
| `Get` / `GetBranches` | `Repository.Get*` | Expect pagination flags (check existing handler). |
| `SetVisibility` | `Repository.SetVisibility` | `Options.visibility` maps to `RepositoryType`. |
| `SetStatus` | `Repository.SetStatus` | `--status` from `RepositoryStatus`. |
| `SetRecordSaves` | `Repository.SetRecordSaves` | Boolean `--recordSaves`. |
| `SetSaveDays` / `SetCheckpointDays` / `SetDiffCacheDays` / `SetDirectoryVersionCacheDays` / `SetLogicalDeleteDays` | corresponding storage parameter types | Single-precision floats from options (keep cast). |
| `SetDefaultServerApiVersion` | `Repository.SetDefaultServerApiVersion` | `--defaultServerApiVersion`. |
| `SetName` / `SetDescription` | `Repository.SetName`, `Repository.SetDescription` | `OptionName.NewName` / `OptionName.Description`. |
| `Delete` / `Undelete` | `Repository.Delete` / `Repository.Undelete` | Include `--deleteReason` and `--force` flags as applicable. |
| `SetAnonymousAccess`, `SetAllowsLargeFiles` | `Repository.SetAnonymousAccess`, `Repository.SetAllowsLargeFiles` | Boolean toggles; preserve output text. |

Special considerations:
- Many commands rely on the shared `graceIds.RepositoryIdString`. When options are optional, fetch `graceIds` first and rely on defaults set in parsing logic.
- Some handlers adjust configuration or display multi-step progress; keep any post-call mutations (e.g., updating `Current()`).

### Owner (`Grace.CLI/Command/Owner.CLI.fs`)

| Command | Notes |
| --- | --- |
| `Create`, `Get` | Similar to Organization pattern; ensure new owner IDs fall back to defaults when implicit. |
| `SetName`, `SetType`, `SetSearchVisibility`, `SetDescription` | Mirror `Organization.SetType` example for structure; options map to string enums validated via `.AcceptOnlyFromAmong`. |
| `Delete`, `Undelete` | Include `--deleteReason` / `--force` handling if present. |

### Branch (`Grace.CLI/Command/Branch.CLI.fs`)

- Command list: `Create`, `Switch`, `Status`, `Promote`, `Commit`, `Checkpoint`, `Save`, `Tag`, `CreateExternal`, `Rebase`, `ListContents`, `GetRecursiveSize`, `Get` (and event wrappers), `GetReferences`, `GetPromotions`, `GetCommits`, `GetCheckpoints`, `GetSaves`, `GetTags`, `GetExternals`, `Assign`, `SetName`, `Delete`, plus any feature toggles still using `CommandHandler.Create`.
- Highlights:
  - Many handlers compose multiple SDK calls with shared progress tasks; reproduce nested tasks exactly.
  - Option modules contain GUID validation using `validateGuid`; keep parse-time validation as-is.
  - Several commands stream console output (`Console.WriteLine`, `AnsiConsole.Write`) in addition to returning results; leave those statements untouched.
  - `Switch`/`Create` update local configuration; ensure config updates stay before returning.

### Reference (`Grace.CLI/Command/Reference.CLI.fs`)

- Commands mirror branch operations (`Promote`, `Commit`, `Checkpoint`, `Save`, `Tag`, `CreateExternal`, `Get`, `Delete`, `Assign`).
- Shares many options via `Branch` helper modules; focus on using `graceIds` for repository context and explicit branch/reference IDs supplied via options.

### Maintenance (`Grace.CLI/Command/Maintenance.CLI.fs`)

- Commands: `Test`, `UpdateIndex`, `Scan`, `Stats`, `ListContents`.
- Usually take only common parameters plus optional filters.
- Each handler may print structured diagnostics; keep logging intact.
- `ListContents` uses a custom `ListContentsParameters`; delete the type after inlining option reads.

### DirectoryVersion (`Grace.CLI/Command/DirectoryVersion.CLI.fs`)

- All current `CommandHandler.Create` wrappers (`Get`, `Save`, `GetZipFile`, etc.) must convert.
- Parameters often rely on directory paths and recursion flags; map options to `Grace.Shared.Parameters.DirectoryVersion` builders.
- Some commands interact with file system (e.g., writing zip). Keep `use` bindings and `Directory.CreateDirectory` calls.

---

## 4. Tracking Progress

- Use `rg "CommandHandler.Create" Grace.CLI/Command` to confirm remaining legacy handlers.
- Maintain a simple checklist (e.g., in your prompt or local notes) marking each handler as converted. Reference this doc instead of re-opening every file.
- When resuming work, note which `command.Action` assignments are still old-style; they provide a quick diff target.

---

## 5. Verification

- After finishing each file:
  - `dotnet build --configuration Release` (captures errors across the solution).
  - Optionally run targeted tests if the module has coverage (`dotnet test --no-build --filter FullyQualifiedName~Grace.CLI`).
- Ensure Fantomas formatting if required: `dotnet tool run fantomas Grace.CLI/Command/<File>.fs`.

---

By sticking to this reference and working in batches, you can minimize token usage per iteration while migrating all CLI subcommands to the new action pattern. Keep this document open during conversion sessions and update it if new patterns emerge (e.g., additional validation helpers or SDK parameter changes).

