namespace Grace.CLI

open Grace.CLI.Command
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Converters
open Grace.Shared.Resources.Text
open Grace.Shared.Resources.Utilities
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Microsoft.Extensions.Logging
open NodaTime
open NodaTime.Text
open Spectre.Console
open System
open System.Collections
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Help
open System.Diagnostics
open System.Globalization
open System.IO
open System.Linq
open System.Threading.Tasks
open Microsoft.Extensions.Caching.Memory
open System.CommandLine.Help
open FSharpPlus.Control
open System.CommandLine.Invocation

module Configuration =

    type GraceCLIConfiguration = { GraceWatchStatus: GraceWatchStatus }

    let mutable private cliConfiguration = { GraceWatchStatus = GraceWatchStatus.Default }

    let CLIConfiguration () = cliConfiguration
    let updateConfiguration (config: GraceCLIConfiguration) = cliConfiguration <- config

module GraceCommand =

    type OptionToUpdate = { optionAlias: string; display: string; displayOnCreate: string; createParentCommand: string }

    /// Built-in aliases for Grace commands.
    let private aliases =
        let aliases = Dictionary<string, string seq>()
        aliases.Add("aliases", [ "alias"; "list" ])
        aliases.Add("branches", [ "repository"; "get-branches" ])
        aliases.Add("checkpoint", [ "branch"; "checkpoint" ])
        aliases.Add("checkpoints", [ "branch"; "get-checkpoints" ])
        aliases.Add("commit", [ "branch"; "commit" ])
        aliases.Add("commits", [ "branch"; "get-commits" ])
        aliases.Add("dir", [ "maint"; "list-contents" ])
        aliases.Add("ls", [ "maint"; "list-contents" ])
        aliases.Add("promote", [ "branch"; "promote" ])
        aliases.Add("promotions", [ "branch"; "get-promotions" ])
        aliases.Add("rebase", [ "branch"; "rebase" ])
        aliases.Add("refs", [ "branch"; "get-references" ])
        aliases.Add("save", [ "branch"; "save" ])
        aliases.Add("saves", [ "branch"; "get-saves" ])
        aliases.Add("sdir", [ "branch"; "list-contents" ])
        aliases.Add("sls", [ "branch"; "list-contents" ])
        aliases.Add("status", [ "branch"; "status" ])
        aliases.Add("switch", [ "branch"; "switch" ])
        aliases.Add("tag", [ "branch"; "tag" ])
        aliases.Add("tags", [ "branch"; "get-tags" ])
        //aliases.Add("", [""; ""])
        aliases

    /// The character sequences that Grace will recognize as a request for help.
    let helpOptions = [| "-h"; "/h"; "--help"; "-?"; "/?" |]

    /// Prints the aliases for Grace commands.
    let printAliases () =
        let table = Table(Border = TableBorder.DoubleEdge)

        table
            .LeftAligned()
            .AddColumns(
                [| TableColumn($"[{Colors.Important}]Alias[/]")
                   TableColumn($"[{Colors.Important}]Grace command[/]") |]
            )
        |> ignore

        aliases
        |> Seq.iter (fun alias ->
            table.AddRow($"grace {alias.Key}", $"grace {alias.Value.First()} {alias.Value.Last()}")
            |> ignore)

        AnsiConsole.Write(table)

    let internal tryGetTopLevelCommandFromArgs (args: string array) (isCaseInsensitive: bool) =
        if isNull args || args.Length = 0 then
            None
        else
            let comparison =
                if isCaseInsensitive then
                    StringComparison.InvariantCultureIgnoreCase
                else
                    StringComparison.InvariantCulture

            let isOptionWithValue (token: string) =
                token.Equals(OptionName.Output, comparison)
                || token.Equals("-o", comparison)
                || token.Equals(OptionName.CorrelationId, comparison)
                || token.Equals("-c", comparison)

            let rec loop index =
                if index >= args.Length then
                    None
                else
                    let token = args[index]

                    if token = "--" then
                        if index + 1 < args.Length then Some args[index + 1] else None
                    elif token.StartsWith("-", StringComparison.Ordinal) then
                        let nextIndex = if isOptionWithValue token then index + 2 else index + 1
                        loop nextIndex
                    else
                        Some token

            loop 0

    /// Gathers the available options for the current command and all its parents, which are applied hierarchically.
    [<TailCall>]
    let rec gatherAllOptions (command: Command) (allOptions: List<Option>) =
        allOptions.AddRange(command.Options)
        let parentCommand = command.Parents.OfType<Command>().FirstOrDefault()

        if not <| isNull parentCommand then
            gatherAllOptions parentCommand allOptions
        else
            allOptions

    let private replaceDefaultValue (line: string) (defaultValueText: string) =
        let startIndex = line.IndexOf("[default:", StringComparison.OrdinalIgnoreCase)

        if startIndex >= 0 then
            let endIndex = line.IndexOf("]", startIndex)

            if endIndex > startIndex then
                $"{line.Substring(0, startIndex)}[default: {defaultValueText}]{line.Substring(endIndex + 1)}"
            else
                line
        else
            line

    let private rewriteHelpDefaults (helpText: string) (defaultsByAlias: IDictionary<string, string>) =
        let lines = helpText.Split(Environment.NewLine)
        let output = ResizeArray<string>(lines.Length)
        let mutable pendingAlias: string option = None
        let mutable i = 0

        while i < lines.Length do
            let line = lines[i]
            let matchedAlias = defaultsByAlias.Keys |> Seq.tryFind (fun alias -> line.Contains(alias))

            match matchedAlias with
            | Some alias -> pendingAlias <- Some alias
            | None -> ()

            match pendingAlias with
            | Some alias when line.Contains("[default:", StringComparison.OrdinalIgnoreCase) ->
                let startIndex = line.IndexOf("[default:", StringComparison.OrdinalIgnoreCase)
                let endIndex = line.IndexOf("]", startIndex)

                if endIndex > startIndex then
                    output.Add(replaceDefaultValue line defaultsByAlias[alias])
                    pendingAlias <- None
                    i <- i + 1
                else
                    let prefix = line.Substring(0, startIndex)
                    output.Add($"{prefix}[default: {defaultsByAlias[alias]}]")
                    pendingAlias <- None

                    let mutable j = i + 1
                    let mutable foundEnd = false

                    while j < lines.Length && not foundEnd do
                        let continuation = lines[j]
                        let continuationEndIndex = continuation.IndexOf("]")

                        if continuationEndIndex >= 0 then
                            let suffix =
                                if continuationEndIndex < continuation.Length - 1 then
                                    continuation.Substring(continuationEndIndex + 1)
                                else
                                    String.Empty

                            if not <| String.IsNullOrWhiteSpace(suffix) then output.Add(suffix)

                            foundEnd <- true

                        j <- j + 1

                    i <- j
            | Some alias when alias = OptionName.CorrelationId && line.Contains("CorrelationId") ->
                if not <| line.Contains("[default:", StringComparison.OrdinalIgnoreCase) then
                    output.Add($"{line} [default: {defaultsByAlias[alias]}]")
                else
                    output.Add(line)

                pendingAlias <- None
                i <- i + 1
            | _ ->
                output.Add(line)
                i <- i + 1

        String.Join(Environment.NewLine, output)

    let rootCommand =
        // Create the root of the command tree.
        let rootCommand = new RootCommand("Grace Version Control System")

        // Turning this off means getting much more flexible in our input handling, and that's not happening for a while.
        rootCommand.TreatUnmatchedTokensAsErrors <- true

        // Create global options - these appear on every command in the system.
        rootCommand.Options.Add(Options.correlationId)
        rootCommand.Options.Add(Options.output)

        // Add subcommands.
        rootCommand.Subcommands.Add(Connect.Build)
        rootCommand.Subcommands.Add(Watch.Build)
        rootCommand.Subcommands.Add(Branch.Build)
        rootCommand.Subcommands.Add(DirectoryVersion.Build)
        rootCommand.Subcommands.Add(Diff.Build)
        rootCommand.Subcommands.Add(Repository.Build)
        rootCommand.Subcommands.Add(Organization.Build)
        rootCommand.Subcommands.Add(Owner.Build)
        rootCommand.Subcommands.Add(Config.Build)
        rootCommand.Subcommands.Add(History.Build)
        rootCommand.Subcommands.Add(Auth.Build)
        rootCommand.Subcommands.Add(Maintenance.Build)
        rootCommand.Subcommands.Add(PromotionGroupCommand.Build)
        rootCommand.Subcommands.Add(WorkItemCommand.Build)
        rootCommand.Subcommands.Add(ReviewCommand.Build)
        rootCommand.Subcommands.Add(QueueCommand.Build)
        rootCommand.Subcommands.Add(Admin.Build)
        rootCommand.Subcommands.Add(Access.Build)

        let Alias = Command("alias", "Display aliases for Grace commands.")
        let ListAliases = Command("list", "Display aliases for Grace commands.")
        ListAliases.SetAction(fun _ -> printAliases ())
        Alias.Subcommands.Add(ListAliases)
        rootCommand.Subcommands.Add(Alias)
        rootCommand

    ///// Converts tokens to the exact casing defined in their options, enabling case-insensitive parsing on Windows.
    //let caseInsensitiveMiddleware (rootCommand: Command, isCaseInsensitive) =
    //    if isCaseInsensitive then
    //        let parseResult = rootCommand.Parse()
    //        let commandOptions = rootCommand.Options

    //        // Collect all of the aliases for all of the options.
    //        let allAliases = commandOptions |> Seq.collect (fun option -> option.Aliases)

    //        /// Finds tokens that are either aliases for options, or pre-defined valid values for an option (i.e. completions), and converts them to the exact case found in the option definitions.
    //        let getCorrectTokenCase (tokens: string array) =
    //            /// The case-corrected list of tokens.
    //            let newTokens = List<string>(tokens.Length)

    //            // Loop through every token.
    //            for i = 0 to (tokens.Length - 1) do
    //                let token = tokens[i]
    //                // First, check if this is an alias for an option.
    //                match
    //                    allAliases
    //                    |> Seq.tryFind (fun alias -> alias.Equals(token, StringComparison.InvariantCultureIgnoreCase))
    //                with
    //                | Some alias ->
    //                    // We found an alias, so we'll use the alias from the option definition to get the case exactly right.
    //                    newTokens.Add(alias)
    //                | None ->
    //                    // This is either a value, or just an invalid option we didn't find.
    //                    // If it's a known value (i.e. completion) for an option, we want to use the completion value defined in the option to get the case right.
    //                    // If we don't recognize it, we'll just leave it as-is.
    //                    if i > 0 then
    //                        // Check if the previous token is an option. If it is, there's a chance it has completions.
    //                        let previousToken = tokens[i - 1]

    //                        match
    //                            commandOptions
    //                            |> Seq.tryFind (fun option -> option.Aliases.Contains(previousToken, StringComparer.InvariantCultureIgnoreCase))
    //                        with
    //                        | Some option ->
    //                            // We found an option for the previous token, so we'll check if it has completions.
    //                            let dlk = Completions.CompletionContext.Empty

    //                            let completions =
    //                                option.GetCompletions(parseResult.GetCompletionContext())
    //                                |> Seq.map (fun completion -> completion.InsertText)
    //                                |> Seq.toArray

    //                            // If we have completions, and this token is one of them, we'll use the actual completion value to get the case right.
    //                            if completions.Length > 0 then
    //                                newTokens.Add(
    //                                    completions.FirstOrDefault(
    //                                        (fun completion -> token.Equals(completion, StringComparison.InvariantCultureIgnoreCase)),
    //                                        token
    //                                    )
    //                                )
    //                            else
    //                                // There are no completions, so we'll just use the current token as-is.
    //                                newTokens.Add(token)
    //                        | None ->
    //                            // We didn't find an option for the previous token, so we'll just use the current token as-is.
    //                            newTokens.Add(token)
    //                    else
    //                        // This is the first token, so we'll just use it as-is. If this were a valid option, we would have matched it and returned `Some alias`.
    //                        newTokens.Add(token)

    //            newTokens.ToArray()

    //        // Get the text from all tokens on the command-line into an array.
    //        let tokens = parseResult.Tokens.Select(fun token -> token.Value).ToArray()

    //        // Convert the tokens to the correct case.
    //        let newTokens =
    //            match tokens.Count with
    //            | 0 ->
    //                // There are no tokens, so we'll just return an empty array.
    //                Array.empty<string>
    //            | 1 ->
    //                // This could be a command like `grace branch`, which is a valid request for help. I'll convert the token to lower-case to match Grace's command structure.
    //                [| tokens[0].ToLowerInvariant() |]
    //            | _ ->
    //                // We've already converted aliases to their noun-verb form, so we know the first two tokens should be converted to lower-case to match Grace's command structure.
    //                // The rest of the tokens are either options or values, and will be converted to their exact casing based on the option definitions.

    //                // In case someone typed `grace --some-option blah`, which is invalid, I want to leave it alone.
    //                if tokens[0].StartsWith("-") then
    //                    tokens
    //                else
    //                    // Convert the first two tokens to lower-case.
    //                    //Array.append [| tokens[0].ToLowerInvariant(); tokens[1].ToLowerInvariant() |] (getCorrectTokenCase tokens[2..])
    //                    Array.append [| tokens[0].ToLowerInvariant() |] (getCorrectTokenCase tokens[1..])

    //        // Replace the old ParseResult with one based on the updated tokens with exact casing.
    //        rootCommand.Parse(newTokens)
    //    else
    //        rootCommand.Parse()

    /// Checks if the command is a `grace watch` command.
    let isGraceWatch (parseResult: ParseResult) = if (parseResult.CommandResult.Command.Name = "watch") then true else false

    /// Checks if the command is a `grace config` command.
    let isGraceConfig (parseResult: ParseResult) =
        if not <| isNull parseResult.CommandResult.Parent then
            if parseResult.CommandResult.Parent.GetValue("config") then true else false
        else
            false

    type FeedbackSection(action: HelpAction) =
        inherit SynchronousCommandLineAction()

        override _.Invoke(parseResult: ParseResult) =
            let result = action.Invoke(parseResult)
            AnsiConsole.WriteLine()
            AnsiConsole.WriteLine("More help and feedback:")

            AnsiConsole.WriteLine(
                "  For more help, or to give us feedback, please join us in Discussions, or create an issue in our repo, at https://github.com/scottarbeit/grace."
            )

            result

    /// This is the main entry point for Grace CLI.
    [<EntryPoint>]
    let main args =
        let startTime = getCurrentInstant ()
        Auth.configureSdkAuth ()
        Services.resetInvocationCorrelationId ()

        // Create a MemoryCache instance.
        //let memoryCacheOptions = MemoryCacheOptions(TrackStatistics = false, TrackLinkedCacheEntries = false)
        //memoryCache <- new MemoryCache(memoryCacheOptions)

        /// True if the OS is case-insensitive (i.e. Windows), false otherwise.
        let isCaseInsensitive = Environment.OSVersion.Platform = PlatformID.Win32NT
        let argvOriginal = args |> Array.copy

        let normalizeArgsForHistory (args: string array) =
            if args.Length = 0 then
                Array.empty<string>
            else
                let firstToken = if isCaseInsensitive then args[0].ToLowerInvariant() else args[0]

                let properCasedArgs =
                    args
                    |> Array.map (fun arg ->
                        if isCaseInsensitive && arg.StartsWith("--") then
                            arg.ToLowerInvariant()
                        else
                            arg)

                if aliases.ContainsKey(firstToken) then
                    let newArgs = List<string>()

                    for token in aliases[firstToken].Reverse() do
                        newArgs.Insert(0, token)

                    for token in properCasedArgs[1..] do
                        newArgs.Add(token)

                    newArgs.ToArray()
                else
                    properCasedArgs

        let mutable argvNormalized = normalizeArgsForHistory args

        (task {
            let mutable parseResult: ParseResult = null
            let mutable returnValue: int = 0
            let mutable parseSucceeded: bool = false

            try
                try
                    //let commandLineConfiguration = ParserConfiguration rootCommand

                    let argvToParse = if args.Length = 0 then [| helpOptions[0] |] else argvNormalized

                    parseResult <- rootCommand.Parse(argvToParse)
                    parseSucceeded <- parseResult.Errors.Count = 0
                    // Write the ParseResult to Services as global context for the CLI.
                    Services.parseResult <- parseResult

                    match parseResult.Action with
                    | :? HelpAction ->
                        if
                            parseResult.Tokens.Count = 0
                            || (parseResult.Tokens.Count = 1
                                && helpOptions.Contains(parseResult.Tokens[0].Value))
                        then
                            // This is where we configure how help is displayed by Grace.
                            // We want to show the help text for the command, and then the feedback section at the end.
                            let graceFiglet = FigletText($"Grace")
                            graceFiglet.Justification <- Justify.Center
                            graceFiglet.Color <- Color.Green3_1
                            AnsiConsole.Write(graceFiglet)
                            let graceFiglet = FigletText($"Version Control System")
                            graceFiglet.Justification <- Justify.Center
                            graceFiglet.Color <- Color.DarkOrange
                            AnsiConsole.Write(graceFiglet)
                            AnsiConsole.WriteLine()

                            for i in 0 .. rootCommand.Options.Count - 1 do
                                match rootCommand.Options[i] with
                                | :? HelpOption as defaultHelpOption -> defaultHelpOption.Action <- FeedbackSection(defaultHelpOption.Action :?> HelpAction)
                                | _ -> ()

                        //helpAction.Builder.CustomizeLayout(fun layoutContext ->
                        //    HelpBuilder.Default
                        //        .GetLayout()
                        //        .Where(fun section ->
                        //            not
                        //            <| section.Method.Name.Contains("Synopsis", StringComparison.InvariantCultureIgnoreCase))
                        //        .Append(feedbackSection))

                        // We're passing a new List<Option> here, because we're going to be adding to it recursively in gatherAllOptions.
                        let allOptions = gatherAllOptions parseResult.CommandResult.Command (List<Option>())

                        // This section sets the display of the default value for these options in all commands in Grace CLI.
                        // Without setting the display values here, by default, we'd get something like
                        //   "[default: thing-we-said-in-the-Option-definition] [default:e4def31b-4547-4f6b-9324-56eba666b4b2]"
                        //   i.e. whatever the generated Guid value on create might be.
                        let optionsToUpdate =
                            [ { optionAlias = OptionName.CorrelationId
                                display = "new NanoId"
                                displayOnCreate = "new NanoId"
                                createParentCommand = String.Empty }
                              { optionAlias = OptionName.OwnerId; display = "current OwnerId"; displayOnCreate = "new Guid"; createParentCommand = "owner" }
                              { optionAlias = OptionName.OrganizationId
                                display = "current OrganizationId"
                                displayOnCreate = "new Guid"
                                createParentCommand = "organization" }
                              { optionAlias = OptionName.RepositoryId
                                display = "current RepositoryId"
                                displayOnCreate = "new Guid"
                                createParentCommand = "repository" }
                              { optionAlias = OptionName.BranchId; display = "current BranchId"; displayOnCreate = "new Guid"; createParentCommand = "branch" } ]

                        let isCreate = parseResult.CommandResult.Command.Name.Equals("create", StringComparison.OrdinalIgnoreCase)

                        let parentCommands = parseResult.CommandResult.Command.Parents.OfType<Command>()

                        let defaultsByAlias =
                            optionsToUpdate
                            |> Seq.map (fun optionToUpdate ->
                                let useCreateDisplay =
                                    isCreate
                                    && not <| String.IsNullOrWhiteSpace(optionToUpdate.createParentCommand)
                                    && parentCommands.Any(fun parent ->
                                        parent.Name.Equals(optionToUpdate.createParentCommand, StringComparison.OrdinalIgnoreCase))

                                let defaultValueText =
                                    if useCreateDisplay then
                                        optionToUpdate.displayOnCreate
                                    else
                                        optionToUpdate.display

                                optionToUpdate.optionAlias, defaultValueText)
                            |> dict

                        use writer = new StringWriter()
                        let originalOut = Console.Out

                        let invokeResult =
                            try
                                Console.SetOut(writer)
                                parseResult.Invoke()
                            finally
                                Console.SetOut(originalOut)

                        let helpText = writer.ToString()
                        let rewrittenHelpText = rewriteHelpDefaults helpText defaultsByAlias
                        Console.Write(rewrittenHelpText)
                        returnValue <- invokeResult
                    | _ ->
                        if configurationFileExists () then
                            //parseResult <- caseInsensitiveMiddleware (rootCommand, parseResult, isCaseInsensitive)

                            if parseResult |> hasOutput then
                                if parseResult |> verbose then
                                    AnsiConsole.Write((new Rule($"[{Colors.Important}]Started: {formatInstantExtended startTime}.[/]")).RightJustified())

                                //printParseResult parseResult
                                else
                                    AnsiConsole.Write(new Rule())

                            // If this instance isn't `grace watch`, we want to check if `grace watch` is running by trying to read the IPC file.
                            if not <| (parseResult |> isGraceWatch) then
                                let! graceWatchStatus = getGraceWatchStatus ()

                                match graceWatchStatus with
                                | Some status -> Configuration.updateConfiguration { GraceWatchStatus = status }
                                | None -> ()

                            // Now we can invoke the command!
                            let! returnValue = parseResult.InvokeAsync()

                            // Stuff to do after the command has been invoked:

                            // If this instance is `grace watch`, we'll actually delete the IPC file in the finally clause below, but
                            //   we'll write the "we deleted the file" message to the console here, so it comes before the last Rule() is written.
                            if parseResult |> isGraceWatch then
                                logToAnsiConsole Colors.Important (getLocalizedString StringResourceName.InterprocessFileDeleted)

                            // If we're writing output, write the final Rule() to the console.
                            if parseResult |> hasOutput then
                                let finishTime = getCurrentInstant ()
                                let elapsed = (finishTime - startTime).Plus(Duration.FromMilliseconds(110.0)) // Adding 110ms for .NET Runtime startup time.

                                if parseResult |> verbose then
                                    AnsiConsole.Write(
                                        (new Rule(
                                            $"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}. Finished: {formatInstantExtended finishTime}[/]"
                                        ))
                                            .RightJustified()
                                    )
                                else
                                    AnsiConsole.Write(
                                        (new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightJustified()
                                    )

                                AnsiConsole.WriteLine()
                        else
                            // We don't have a config file, so write an error message and exit.
                            AnsiConsole.Write(new Rule())

                            let comparison =
                                if isCaseInsensitive then
                                    StringComparison.InvariantCultureIgnoreCase
                                else
                                    StringComparison.InvariantCulture

                            let allowedCommands = [ "config"; "history"; "auth"; "connect" ]

                            let isAllowed =
                                let command = parseResult.CommandResult.Command

                                Seq.append [ command ] (command.Parents.OfType<Command>())
                                |> Seq.exists (fun cmd ->
                                    allowedCommands
                                    |> List.exists (fun allowed -> cmd.Name.Equals(allowed, comparison)))
                                || (tryGetTopLevelCommandFromArgs argvNormalized isCaseInsensitive
                                    |> Option.exists (fun topLevel ->
                                        allowedCommands
                                        |> List.exists (fun allowed -> topLevel.Equals(allowed, comparison))))

                            if isAllowed then
                                let! invokedReturnValue = parseResult.InvokeAsync()
                                returnValue <- invokedReturnValue
                                ()
                            else
                                AnsiConsole.MarkupLine($"[{Colors.Important}]{getLocalizedString StringResourceName.GraceConfigFileNotFound}[/]")

                            printParseResult parseResult
                            let finishTime = getCurrentInstant ()
                            let elapsed = (finishTime - startTime).Plus(Duration.FromMilliseconds(110.0)) // Adding 110ms for .NET Runtime startup time.

                            AnsiConsole.Write(
                                (new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightJustified()
                            )

                    return returnValue
                with ex ->
                    AnsiConsole.WriteException ex
                    //logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}"
                    //logToAnsiConsole Colors.Error $"{ex.StackTrace}"
                    returnValue <- -1
                    return -1
            finally
                let finishTime = getCurrentInstant ()
                let durationMs = (finishTime - startTime).TotalMilliseconds |> int64

                HistoryStorage.tryRecordInvocation
                    { argvOriginal = argvOriginal
                      argvNormalized = argvNormalized
                      cwd = Environment.CurrentDirectory
                      exitCode = returnValue
                      durationMs = durationMs
                      parseSucceeded = parseSucceeded
                      timestampUtc = startTime
                      source = None }

                // If this was grace watch, delete the inter-process communication file.
                if not <| isNull (parseResult) && parseResult |> isGraceWatch then
                    File.Delete(IpcFileName())
        })
            .Result
