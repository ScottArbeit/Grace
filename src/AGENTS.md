# Grace Repository Agents Guide

Agents operating under `D:\Source\Grace\src` should follow this playbook
alongside the existing `AGENTS.md` in the repo root. This project uses **bd
(beads)** for issue tracking.
Run `bd prime` for workflow context, or install hooks (`bd hooks install`) for
auto-injection.

Treat this file as the canonical high-level brief; each project folder contains
an `AGENTS.md` with deeper context.

## Local Commands

- `pwsh ./scripts/bootstrap.ps1`
- `pwsh ./scripts/validate.ps1 -Fast` (use `-Full` for Aspire integration tests)

Optional: `pwsh ./scripts/install-githooks.ps1` to add a pre-commit
`validate -Fast` hook.

## Core Engineering Expectations

- Make a multi-step plan for non-trivial work, keep edits focused, and leave
  code cleaner than you found it.
- Validate changes with `pwsh ./scripts/validate.ps1 -Fast` (use `-Full` for
  Aspire integration coverage). If running commands manually, use
  `dotnet build --configuration Release` and `dotnet test --no-build`.
- Each task cannot be considered complete until it builds without errors; all
  compilation errors much be resolved.
- When each task is done, run all tests and ensure they pass; fix any broken
  tests before marking complete.
- Create a new `git commit` after each task is done to ensure ease of review.
- Write unit/integration tests for new features and bug fixes; aim for high
  coverage on critical paths.
- Document new public APIs with XML comments; update relevant READMEs and
  `AGENTS.md` files to reflect behavior changes.
- Treat secrets with care, avoid logging PII, and preserve structured logging
  (including correlation IDs).
- Favor existing helpers in `Grace.Shared` before adding new utilities; when
  new helpers are required, keep them composable and well documented.

## F# Coding Guidelines

- Default to F# for new code unless stakeholders specify another language;
  surface trade-offs if deviation seems necessary.
- Use `task { }` for asynchronous workflows and keep side effects isolated;
  ensure any awaited operations remain within the computation expression.
- Prefer a functional style with immutable data, small pure functions, and
  explicit dependencies passed as parameters.
- Prefer collections from `System.Collections.Generic` (e.g., `List<T>`,
  `Dictionary<K,V>`) over F#-specific collections unless pattern matching or
  discriminated unions are needed.
- Apply the modern indexer syntax (`myList[0]`) for lists, arrays, and
  sequences; avoid the legacy `.[ ]` form. When procesing those collections,
  prefer LINQ-style syntax over F# list.
- Structure modules so that domain types live in `Grace.Types`, shared helpers
  in `Grace.Shared`, and orchestration in the appropriate project-specific
  assembly.
- Add lightweight inline comments when control flow or transformations are
  non-obvious, and log key variable values in functions longer than ten lines
  to aid diagnostics.
- Format code with `dotnet tool run fantomas --recurse .` (from `./src`) and
  include comprehensive, copy/paste-ready snippets when sharing examples.

### Avoid FS3511 in resumable computation expressions (`task { }`, `backgroundTask { }`)

FS3511 warnings indicate the compiler could not statically compile a resumable
state machine. These warnings are treated as regressions (and will fail the
build with warnings-as-errors enabled).

1. **Do not define `let rec` inside `task { }`.**

   Bad:

   ```fsharp
   task {
       let rec poll () = task { return! poll () }
       return! poll ()
   }
   ```

   Good:

   ```fsharp
   task {
       let mutable finished = false
       while not finished do
           finished <- true
       return ()
   }
   ```

2. **Avoid `for ... in ... do` loops inside `task { }`.**

   Bad:

   ```fsharp
   task {
       for item in items do
           doSomething item
   }
   ```

   Good:

   ```fsharp
   task {
       items |> Seq.iter doSomething
   }
   ```

3. **Keep `try/with` minimal inside `task { }`.**

   Bad:

   ```fsharp
   task {
       try
           let! result = doWork ()
           return result
       with ex ->
           return Error ex.Message
   }
   ```

   Good:

   ```fsharp
   let doWorkImpl () = task { return! doWork () }

   let doWork () =
       task {
           try
               return! doWorkImpl ()
           with ex ->
               return Error ex.Message
       }
   ```

4. **Move complex pure work out of `task { }`.**

   Bad:

   ```fsharp
   task {
       return { bundle with FieldA = valueA; FieldB = valueB }
   }
   ```

   Good:

   ```fsharp
   let updateBundle bundle valueA valueB =
       { bundle with FieldA = valueA; FieldB = valueB }

   task {
       return updateBundle bundle valueA valueB
   }
   ```

5. **Prefer `let!` + `match` over `match!` in large orchestration blocks.**

   Bad:

   ```fsharp
   task {
       match! fetch () with
       | Ok value -> return value
       | Error err -> return failwith err
   }
   ```

   Good:

   ```fsharp
   task {
       let! result = fetch ()
       match result with
       | Ok value -> return value
       | Error err -> return failwith err
   }
   ```

6. **Policy:** FS3511 warnings are treated as regressions.

## Agent-Friendly Context Practices

- Start with the relevant `AGENTS.md` file(s) to load key patterns,
  dependencies, and test strategy before exploring the codebase. These files
  replace the old `instructions.md`.
- Use these summaries to decide which source files actually need inspection;
  open code only when context is missing or implementation verification is
  required.
- When documenting new behavior, update the closest `AGENTS.md` to keep future
  agents informed—living documentation reduces the need for broad code scans.

## Collaboration & Communication

- Summarize modifications clearly, cite file paths with 1-based line numbers,
  and call out follow-up actions or tests that remain.
- Coordinate cross-project changes across `Grace.Types`, `Grace.Shared`,
  `Grace.Server`, `Grace.Actors`, `Grace.CLI` and `Grace.SDK` to keep contracts
  aligned.
- When adding new capabilities, ensure matching tests exist and note any
  coverage gaps or risks in the final hand-off.
