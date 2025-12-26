namespace Grace.CLI.Command

open Grace.CLI
open Grace.CLI.Common
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Client
open Grace.Shared.Utilities
open NodaTime
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Diagnostics
open System.IO
open System.Linq
open System.Threading.Tasks

module History =

    module private Options =
        let limit =
            new Option<int>(
                OptionName.Limit,
                Required = false,
                Description = "Maximum number of history entries to show.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 50)
            )

        let repo =
            new Option<bool>(
                OptionName.Repo,
                Required = false,
                Description = "Filter to entries whose repoRoot matches the current directory.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let failed =
            new Option<bool>(
                OptionName.Failed,
                Required = false,
                Description = "Show only failed commands.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let success =
            new Option<bool>(
                OptionName.Success,
                Required = false,
                Description = "Show only successful commands.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let since =
            new Option<string>(
                OptionName.Since,
                Required = false,
                Description = "Only show entries since a duration (e.g. 10m, 24h, 7d).",
                Arity = ArgumentArity.ExactlyOne
            )

        let contains =
            new Option<string>(
                OptionName.Contains,
                Required = false,
                Description = "Substring match against the stored command line.",
                Arity = ArgumentArity.ExactlyOne
            )

        let showId =
            new Option<bool>(
                OptionName.Id,
                Required = false,
                Description = "Show the stable id column.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let runNumber = new Argument<int>("number", Description = "History number (from most recent = 1).", Arity = ArgumentArity.ZeroOrOne)

        let runId =
            new Option<Guid>(
                OptionName.Id,
                Required = false,
                Description = "Run by stable id instead of history number.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> Guid.Empty)
            )

        let yes =
            new Option<bool>(
                OptionName.Yes,
                Required = false,
                Description = "Skip confirmation prompts.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let useCurrentCwd =
            new Option<bool>(
                OptionName.UseCurrentCwd,
                Required = false,
                Description = "Run from the current working directory instead of the recorded one.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let dryRun =
            new Option<bool>(
                OptionName.DryRun,
                Required = false,
                Description = "Print the resolved command and working directory without executing.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let replace =
            new Option<string[]>(
                OptionName.Replace,
                Required = false,
                Description = "Provide replacements for redacted values (name=value or argIndex=value).",
                Arity = ArgumentArity.OneOrMore
            )

    let private warnOnCorruptConfig (parseResult: ParseResult) (loadResult: UserConfiguration.UserConfigurationLoadResult) =
        if
            loadResult.WasCorrupt
            && not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let message =
                match loadResult.ErrorMessage with
                | Some error -> error
                | None -> "User configuration is invalid; using defaults."

            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]")

    let private formatDuration (durationMs: int64) = $"{(float durationMs) / 1000.0:F3}s"

    let private abbreviatePath (value: string) =
        if String.IsNullOrWhiteSpace(value) then String.Empty
        elif value.Length <= 40 then value
        else "..." + value.Substring(value.Length - 37)

    let private formatRepoName (entry: HistoryStorage.HistoryEntry) =
        match entry.repoName with
        | Some name -> name
        | None ->
            match entry.repoRoot with
            | Some root ->
                try
                    let name = DirectoryInfo(root).Name
                    if String.IsNullOrWhiteSpace(name) then String.Empty else name
                with _ ->
                    String.Empty
            | None -> String.Empty

    let private formatRepoBranch (entry: HistoryStorage.HistoryEntry) = entry.repoBranch |> Option.defaultValue String.Empty

    let private filterEntries
        (entries: HistoryStorage.HistoryEntry list)
        (limit: int)
        (filterRepo: bool)
        (filterFailed: bool)
        (filterSuccess: bool)
        (sinceDuration: Duration option)
        (containsText: string option)
        =
        let mutable filtered = entries

        if filterRepo then
            match HistoryStorage.tryFindRepoRoot Environment.CurrentDirectory with
            | Some repoRoot ->
                let comparer =
                    if runningOnWindows then
                        StringComparer.InvariantCultureIgnoreCase
                    else
                        StringComparer.InvariantCulture

                filtered <-
                    filtered
                    |> List.filter (fun entry ->
                        match entry.repoRoot with
                        | Some entryRoot -> comparer.Equals(entryRoot, repoRoot)
                        | None -> false)
            | None -> filtered <- List.empty

        match sinceDuration with
        | Some duration ->
            let cutoff = getCurrentInstant().Minus(duration)
            filtered <- filtered |> List.filter (fun entry -> entry.timestampUtc >= cutoff)
        | None -> ()

        match containsText with
        | Some text ->
            filtered <-
                filtered
                |> List.filter (fun entry ->
                    entry.commandLine.IndexOf(text, StringComparison.InvariantCultureIgnoreCase)
                    >= 0)
        | None -> ()

        if filterFailed && not filterSuccess then
            filtered <- filtered |> List.filter (fun entry -> entry.exitCode <> 0)
        elif filterSuccess && not filterFailed then
            filtered <- filtered |> List.filter (fun entry -> entry.exitCode = 0)

        let ordered = filtered |> List.sortByDescending (fun entry -> entry.timestampUtc)

        if limit > 0 then
            ordered |> List.truncate (min limit ordered.Length)
        else
            ordered

    let private renderTable (entries: HistoryStorage.HistoryEntry list) (showId: bool) =
        let table = Table(Border = TableBorder.DoubleEdge, ShowHeaders = true)

        let columns =
            if showId then
                [| TableColumn($"[bold]#[/]")
                   TableColumn($"[bold]When[/]")
                   TableColumn($"[bold]Exit[/]")
                   TableColumn($"[bold]Dur[/]")
                   TableColumn($"[bold]Cwd[/]")
                   TableColumn($"[bold]Repo[/]")
                   TableColumn($"[bold]Branch[/]")
                   TableColumn($"[bold]Command[/]")
                   TableColumn($"[bold]Id[/]") |]
            else
                [| TableColumn($"[bold]#[/]")
                   TableColumn($"[bold]When[/]")
                   TableColumn($"[bold]Exit[/]")
                   TableColumn($"[bold]Dur[/]")
                   TableColumn($"[bold]Cwd[/]")
                   TableColumn($"[bold]Repo[/]")
                   TableColumn($"[bold]Branch[/]")
                   TableColumn($"[bold]Command[/]") |]

        table.AddColumns(columns) |> ignore

        entries
        |> List.iteri (fun index entry ->
            let row =
                if showId then
                    [| $"{index + 1}"
                       Markup.Escape(ago entry.timestampUtc)
                       $"{entry.exitCode}"
                       formatDuration entry.durationMs
                       Markup.Escape(abbreviatePath entry.cwd)
                       Markup.Escape(formatRepoName entry)
                       Markup.Escape(formatRepoBranch entry)
                       Markup.Escape(entry.commandLine)
                       $"{entry.id}" |]
                else
                    [| $"{index + 1}"
                       Markup.Escape(ago entry.timestampUtc)
                       $"{entry.exitCode}"
                       formatDuration entry.durationMs
                       Markup.Escape(abbreviatePath entry.cwd)
                       Markup.Escape(formatRepoName entry)
                       Markup.Escape(formatRepoBranch entry)
                       Markup.Escape(entry.commandLine) |]

            table.AddRow(row) |> ignore)

        AnsiConsole.Write(table)

    let private outputEntries (parseResult: ParseResult) (entries: HistoryStorage.HistoryEntry list) (showId: bool) (corruptCount: int) =
        if parseResult |> json then
            let payload = serialize entries
            AnsiConsole.WriteLine(Markup.Escape(payload))
        elif parseResult |> silent then
            ()
        else
            if corruptCount > 0 then
                AnsiConsole.MarkupLine($"[yellow]Skipped {corruptCount} corrupt history entries.[/]")

            renderTable entries showId
            AnsiConsole.WriteLine()

    type HistoryOn() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: Threading.CancellationToken) : Task<int> =
            task {
                let loadResult = UserConfiguration.loadUserConfiguration ()
                warnOnCorruptConfig parseResult loadResult

                let configuration =
                    if loadResult.WasCorrupt then
                        UserConfiguration.UserConfiguration()
                    else
                        loadResult.Configuration

                configuration.History.Enabled <- true

                match UserConfiguration.saveUserConfiguration configuration with
                | Ok _ ->
                    if parseResult |> json then
                        AnsiConsole.WriteLine(Markup.Escape(serialize {| enabled = true |}))
                    elif parseResult |> silent then
                        ()
                    else
                        AnsiConsole.MarkupLine("[green]History recording enabled.[/]")

                    return 0
                | Error error ->
                    if not (parseResult |> silent) then
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]")

                    return -1
            }

    type HistoryOff() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: Threading.CancellationToken) : Task<int> =
            task {
                let loadResult = UserConfiguration.loadUserConfiguration ()
                warnOnCorruptConfig parseResult loadResult

                let configuration =
                    if loadResult.WasCorrupt then
                        UserConfiguration.UserConfiguration()
                    else
                        loadResult.Configuration

                configuration.History.Enabled <- false

                match UserConfiguration.saveUserConfiguration configuration with
                | Ok _ ->
                    if parseResult |> json then
                        AnsiConsole.WriteLine(Markup.Escape(serialize {| enabled = false |}))
                    elif parseResult |> silent then
                        ()
                    else
                        AnsiConsole.MarkupLine("[green]History recording disabled.[/]")

                    return 0
                | Error error ->
                    if not (parseResult |> silent) then
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]")

                    return -1
            }

    type HistoryShow() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: Threading.CancellationToken) : Task<int> =
            task {
                let limit = parseResult.GetValue(Options.limit)
                let filterRepo = parseResult.GetValue(Options.repo)
                let filterFailed = parseResult.GetValue(Options.failed)
                let filterSuccess = parseResult.GetValue(Options.success)
                let sinceText = parseResult.GetValue(Options.since)
                let containsText = parseResult.GetValue(Options.contains)
                let showId = parseResult.GetValue(Options.showId)

                if limit < 0 then
                    AnsiConsole.MarkupLine("[red]Limit must be positive.[/]")
                    return -1
                else
                    let sinceDuration =
                        if String.IsNullOrWhiteSpace(sinceText) then
                            Ok None
                        else
                            match HistoryStorage.tryParseDuration sinceText with
                            | Ok duration -> Ok(Some duration)
                            | Error error -> Error error

                    match sinceDuration with
                    | Error error ->
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]")
                        return -1
                    | Ok since ->
                        let readResult = HistoryStorage.readHistoryEntries ()

                        let filtered =
                            filterEntries
                                readResult.Entries
                                limit
                                filterRepo
                                filterFailed
                                filterSuccess
                                since
                                (if String.IsNullOrWhiteSpace(containsText) then None else Some containsText)

                        outputEntries parseResult filtered showId readResult.CorruptCount
                        return 0
            }

    type HistorySearch() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: Threading.CancellationToken) : Task<int> =
            task {
                let searchText = parseResult.GetValue<string>("text")
                let limit = parseResult.GetValue(Options.limit)
                let filterRepo = parseResult.GetValue(Options.repo)
                let filterFailed = parseResult.GetValue(Options.failed)
                let filterSuccess = parseResult.GetValue(Options.success)
                let sinceText = parseResult.GetValue(Options.since)
                let showId = parseResult.GetValue(Options.showId)

                if limit < 0 then
                    AnsiConsole.MarkupLine("[red]Limit must be positive.[/]")
                    return -1
                else
                    let sinceDuration =
                        if String.IsNullOrWhiteSpace(sinceText) then
                            Ok None
                        else
                            match HistoryStorage.tryParseDuration sinceText with
                            | Ok duration -> Ok(Some duration)
                            | Error error -> Error error

                    match sinceDuration with
                    | Error error ->
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]")
                        return -1
                    | Ok since ->
                        let readResult = HistoryStorage.readHistoryEntries ()

                        let filtered =
                            filterEntries
                                readResult.Entries
                                limit
                                filterRepo
                                filterFailed
                                filterSuccess
                                since
                                (if String.IsNullOrWhiteSpace(searchText) then None else Some searchText)

                        outputEntries parseResult filtered showId readResult.CorruptCount
                        return 0
            }

    let private parseReplacements (values: string array) =
        let replacements = Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
        let errors = ResizeArray<string>()

        if isNull values then
            Ok replacements
        else
            for value in values do
                let trimmed = if isNull value then String.Empty else value.Trim()
                let equalsIndex = trimmed.IndexOf('=')

                if equalsIndex <= 0 then
                    errors.Add($"Invalid replacement '{trimmed}'. Expected name=value.")
                else
                    let name = trimmed.Substring(0, equalsIndex).Trim()
                    let replacement = trimmed.Substring(equalsIndex + 1)

                    if String.IsNullOrWhiteSpace(name) then
                        errors.Add($"Invalid replacement '{trimmed}'. Name cannot be empty.")
                    else
                        replacements[name] <- replacement

            if errors.Count > 0 then
                Error(String.Join(Environment.NewLine, errors))
            else
                Ok replacements

    let private replaceFirst (text: string) (replacement: string) =
        let index = text.IndexOf(HistoryStorage.Placeholder, StringComparison.Ordinal)

        if index < 0 then
            text
        else
            text.Substring(0, index)
            + replacement
            + text.Substring(index + HistoryStorage.Placeholder.Length)

    let private applyReplacements (argv: string array) (redactions: HistoryStorage.Redaction list) (replacements: IDictionary<string, string>) =
        let updated = Array.copy argv
        let missing = ResizeArray<HistoryStorage.Redaction>()

        for redaction in redactions do
            let indexKey = $"{redaction.argIndex}"

            let replacement =
                if replacements.ContainsKey(indexKey) then
                    Some replacements[indexKey]
                elif replacements.ContainsKey(redaction.name) then
                    Some replacements[redaction.name]
                else
                    None

            match replacement with
            | Some value ->
                let currentArg = updated[redaction.argIndex]
                updated[redaction.argIndex] <- replaceFirst currentArg value
            | None -> missing.Add(redaction)

        updated, missing |> Seq.toList

    let private promptForReplacements (missing: HistoryStorage.Redaction list) (replacements: IDictionary<string, string>) =
        for redaction in missing do
            let key = $"{redaction.argIndex}"

            if not <| replacements.ContainsKey(key) then
                let prompt = TextPrompt<string>($"Replacement for {redaction.name} (arg #{redaction.argIndex + 1}):").Secret()

                let value = AnsiConsole.Prompt(prompt)
                replacements[key] <- value

    type HistoryRun() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: Threading.CancellationToken) : Task<int> =
            task {
                let number = parseResult.GetValue(Options.runNumber)
                let byId = parseResult.GetValue(Options.runId)
                let yes = parseResult.GetValue(Options.yes)
                let useCurrentCwd = parseResult.GetValue(Options.useCurrentCwd)
                let dryRun = parseResult.GetValue(Options.dryRun)
                let replacementsInput = parseResult.GetValue(Options.replace)

                let loadResult = UserConfiguration.loadUserConfiguration ()
                warnOnCorruptConfig parseResult loadResult
                let historyConfig = loadResult.Configuration.History

                let readResult = HistoryStorage.readHistoryEntries ()
                let ordered = readResult.Entries |> List.sortByDescending (fun entry -> entry.timestampUtc)

                let target =
                    if byId <> Guid.Empty then
                        ordered |> List.tryFind (fun entry -> entry.id = byId)
                    else if number <= 0 || number > ordered.Length then
                        None
                    else
                        Some ordered[number - 1]

                match target with
                | None ->
                    AnsiConsole.MarkupLine("[red]History entry not found.[/]")
                    return -1
                | Some entry ->
                    match parseReplacements replacementsInput with
                    | Error error ->
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]")
                        return -1
                    | Ok replacements ->
                        let mutable argvToRun = entry.argvNormalized
                        let mutable missingRedactions = List.empty

                        if entry.redactions.Length > 0 then
                            let updated, missing = applyReplacements argvToRun entry.redactions replacements
                            argvToRun <- updated
                            missingRedactions <- missing

                            let canPrompt = not Console.IsInputRedirected && not Console.IsOutputRedirected

                            if missingRedactions.Length > 0 && canPrompt && not yes then
                                promptForReplacements missingRedactions replacements
                                let updatedAfterPrompt, missingAfterPrompt = applyReplacements argvToRun entry.redactions replacements

                                argvToRun <- updatedAfterPrompt
                                missingRedactions <- missingAfterPrompt

                        let stillRedacted = argvToRun |> Array.exists (fun arg -> arg.Contains(HistoryStorage.Placeholder))

                        if missingRedactions.Length > 0 || stillRedacted then
                            let missingKeys =
                                missingRedactions
                                |> List.map (fun redaction -> $"{redaction.name} (arg #{redaction.argIndex + 1})")
                                |> Seq.distinct
                                |> String.concat ", "

                            AnsiConsole.MarkupLine($"[red]Missing replacements for redacted values: {Markup.Escape(missingKeys)}[/]")
                            return -1
                        else
                            let cwd = if useCurrentCwd then Environment.CurrentDirectory else entry.cwd

                            let commandLine = HistoryStorage.buildCommandLine argvToRun

                            AnsiConsole.MarkupLine($"[bold]About to run:[/] grace {Markup.Escape(commandLine)}")

                            AnsiConsole.MarkupLine($"[bold]Working directory:[/] {Markup.Escape(cwd)}")

                            let shouldProceed =
                                if HistoryStorage.isDestructive commandLine historyConfig && not yes then
                                    AnsiConsole.Confirm("This command looks destructive. Re-run?", defaultValue = false)
                                else
                                    true

                            if not shouldProceed then
                                return 1
                            else if dryRun then
                                return 0
                            else
                                let executablePath = Environment.ProcessPath

                                if String.IsNullOrWhiteSpace(executablePath) then
                                    AnsiConsole.MarkupLine("[red]Failed to locate Grace executable.[/]")
                                    return -1
                                else
                                    let startInfo = ProcessStartInfo()
                                    startInfo.FileName <- executablePath
                                    startInfo.WorkingDirectory <- cwd
                                    startInfo.UseShellExecute <- false
                                    startInfo.RedirectStandardInput <- false
                                    startInfo.RedirectStandardOutput <- false
                                    startInfo.RedirectStandardError <- false

                                    for arg in argvToRun do
                                        startInfo.ArgumentList.Add(arg)

                                    use proc = new Process()
                                    proc.StartInfo <- startInfo
                                    proc.Start() |> ignore
                                    proc.WaitForExit()
                                    return proc.ExitCode
            }

    type HistoryDelete() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: Threading.CancellationToken) : Task<int> =
            task {
                match HistoryStorage.clearHistory () with
                | Ok removed ->
                    if parseResult |> json then
                        AnsiConsole.WriteLine(Markup.Escape(serialize {| deleted = true; removed = removed |}))
                    elif parseResult |> silent then
                        ()
                    else
                        AnsiConsole.MarkupLine("[green]History cleared.[/]")

                    return 0
                | Error error ->
                    if not (parseResult |> silent) then
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]")

                    return -1
            }

    let Build =
        let historyCommand = Command("history", Description = "Manage local Grace CLI history.")

        let onCommand = Command("on", Description = "Enable history recording.")
        onCommand.Action <- HistoryOn()

        let offCommand = Command("off", Description = "Disable history recording.")
        offCommand.Action <- HistoryOff()

        let showCommand = Command("show", Description = "Show history entries.")

        showCommand
        |> addOption Options.limit
        |> addOption Options.repo
        |> addOption Options.failed
        |> addOption Options.success
        |> addOption Options.since
        |> addOption Options.contains
        |> addOption Options.showId
        |> ignore

        showCommand.Action <- HistoryShow()

        let searchCommand = Command("search", Description = "Search history entries.")
        let searchText = new Argument<string>("text", Description = "Text to search for.")
        searchCommand.Arguments.Add(searchText)

        searchCommand
        |> addOption Options.limit
        |> addOption Options.repo
        |> addOption Options.failed
        |> addOption Options.success
        |> addOption Options.since
        |> addOption Options.showId
        |> ignore

        searchCommand.Action <- HistorySearch()

        let runCommand = Command("run", Description = "Re-run a prior command.")
        runCommand.Arguments.Add(Options.runNumber)

        runCommand
        |> addOption Options.runId
        |> addOption Options.yes
        |> addOption Options.useCurrentCwd
        |> addOption Options.dryRun
        |> addOption Options.replace
        |> ignore

        runCommand.Action <- HistoryRun()

        let deleteCommand = Command("delete", Description = "Clear history.")
        deleteCommand.Action <- HistoryDelete()

        historyCommand.Subcommands.Add(onCommand)
        historyCommand.Subcommands.Add(offCommand)
        historyCommand.Subcommands.Add(showCommand)
        historyCommand.Subcommands.Add(searchCommand)
        historyCommand.Subcommands.Add(runCommand)
        historyCommand.Subcommands.Add(deleteCommand)

        historyCommand
