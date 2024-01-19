namespace Grace.CLI

open Grace.CLI.Command
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Converters
open Grace.Shared.Resources.Text
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open NodaTime.Text
open Spectre.Console
open System
open System.Collections
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Builder
open System.CommandLine.Help
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Diagnostics
open System.Globalization
open System.IO
open System.Linq
open System.Threading.Tasks
open Grace.Shared.Validation
open Grace.Shared.Resources.Text
open System.ComponentModel.DataAnnotations

module Configuration =
    
    type GraceCLIConfiguration =
        {
            GraceWatchStatus: GraceWatchStatus
        }

    let mutable private cliConfiguration = {GraceWatchStatus = GraceWatchStatus.Default}
    let CLIConfiguration() = cliConfiguration
    let updateConfiguration (config: GraceCLIConfiguration) =
        cliConfiguration <- config

module GraceCommand =

    type OptionToUpdate = {optionName: string; command: string; display: string; displayOnCreate: string}

    /// Built-in aliases for Grace commands.
    let private aliases = 
        let aliases = Dictionary<string, string seq>()
        aliases.Add("branches", ["repository"; "get-branches"])
        aliases.Add("checkpoint", ["branch"; "checkpoint"])
        aliases.Add("checkpoints", ["branch"; "get-checkpoints"])
        aliases.Add("commit", ["branch"; "commit"])
        aliases.Add("commits", ["branch"; "get-commits"])
        aliases.Add("promote", ["branch"; "promote"])
        aliases.Add("promotions", ["branch"; "get-promotions"])
        aliases.Add("rebase", ["branch"; "rebase"])
        aliases.Add("refs", ["branch"; "get-references"])
        aliases.Add("save", ["branch"; "save"])
        aliases.Add("saves", ["branch"; "get-references"])
        aliases.Add("status", ["branch"; "status"])
        aliases.Add("switch", ["branch"; "switch"])
        aliases.Add("tag", ["branch"; "tag"])
        aliases.Add("tags", ["branch"; "get-tags"])
        //aliases.Add("", [""; ""])
        aliases

    /// The character sequences that Grace will recognize as a request for help.
    let helpOptions = [| "-h"; "/h"; "--help"; "-?"; "/?" |]

    /// Gathers the available options for the current command and all its parents, which are applied hierarchically.
    [<TailCall>]
    let rec gatherAllOptions (command: Command) (allOptions: List<Option>) = 
        allOptions.AddRange(command.Options)
        let parentCommand = command.Parents.OfType<Command>().FirstOrDefault()
        if not <| isNull parentCommand then
            gatherAllOptions parentCommand allOptions
        else
            allOptions
        
    let Build =
        // Create the root of the command tree.
        let rootCommand = new RootCommand("Grace Version Control System", Name = "grace")

        // Turning this off means getting much more flexible in our input handling, and that's not happening for a while.
        rootCommand.TreatUnmatchedTokensAsErrors <- true

        // Create global options - these appear on every command in the system.
        rootCommand.AddGlobalOption(Options.correlationId)
        rootCommand.AddGlobalOption(Options.output)

        // Add subcommands.
        rootCommand.AddCommand(Connect.Build)
        rootCommand.AddCommand(Watch.Build)
        rootCommand.AddCommand(Branch.Build)
        rootCommand.AddCommand(Diff.Build)
        rootCommand.AddCommand(Repository.Build)
        rootCommand.AddCommand(Organization.Build)
        rootCommand.AddCommand(Owner.Build)
        rootCommand.AddCommand(Config.Build)
        rootCommand.AddCommand(Maintenance.Build)

        /// The feedback section is printed at the end of any CLI request for help.
        let feedbackSection: HelpSectionDelegate = HelpSectionDelegate(fun context -> 
            context.Output.WriteLine()
            context.Output.WriteLine("More help and feedback:")
            context.Output.WriteLine("  For more help, or to give us feedback, please create an issue in our repo at https://github.com/scottarbeit/grace."))

        /// Handles command aliases by removing the alias and substituting the full Grace command before execution.
        let aliasHandlerMiddleware (context: InvocationContext) =
            let tokens = context.ParseResult.Tokens.Select(fun token -> token.Value).ToList()
            if tokens.Count > 0 then
                let firstToken = 
                    if Environment.OSVersion.Platform = PlatformID.Win32NT then
                        tokens[0].ToLowerInvariant()
                    else
                        tokens[0]
                if aliases.ContainsKey(firstToken) then
                    tokens.RemoveAt(0)
                    // Reverse the full command so we insert them in the right order.
                    for token in aliases[firstToken].Reverse() do
                        tokens.Insert(0, token)
                context.ParseResult <- context.Parser.Parse(tokens)

        /// Converts tokens to exact casing from commands and options, to allow for case-insensitive parsing on Windows.
        let caseInsensitiveMiddleware (context: InvocationContext) =
            if Environment.OSVersion.Platform = PlatformID.Win32NT then
                let commandOptions = context.ParseResult.CommandResult.Command.Options |> Seq.append context.ParseResult.RootCommandResult.Command.Options
                let allAliases = commandOptions |> Seq.collect (fun option -> option.Aliases)
                
                /// Finds tokens that are either aliases for options, or completions, and converts them to the exact case found in the option definitions.
                let processTokens (tokens: string array) =
                    let newTokens = List<string>()
                    for i = 0 to (tokens.Length - 1) do
                        let token = tokens[i]
                        let foundAlias = allAliases.FirstOrDefault((fun alias -> alias.Equals(token, StringComparison.InvariantCultureIgnoreCase)), String.Empty)
                        if String.IsNullOrEmpty(foundAlias) then
                            // This is either an invalid option, or a value for an option.
                            // If it's a known value (i.e. completion) for an option, we want to use the actual completion value to get the case right.
                            if i > 0 then
                                let previousToken = tokens[i - 1]
                                let option = commandOptions.FirstOrDefault(fun option -> option.Aliases.Contains(previousToken, StringComparer.InvariantCultureIgnoreCase))
                                if not <| isNull option then
                                    let completions = option.GetCompletions() |> Seq.map(fun completion -> completion.InsertText) |> Seq.toArray
                                    if completions.Length > 0 then
                                        // This is a known value for an option, so we need to get the actual value to get the correct case.
                                        newTokens.Add(completions.FirstOrDefault((fun completion -> token.Equals(completion, StringComparison.InvariantCultureIgnoreCase)), token))
                                    else
                                        newTokens.Add(token)
                                else
                                    newTokens.Add(token)
                            else
                                newTokens.Add(token)
                        else
                            newTokens.Add(foundAlias)
                    newTokens.ToArray()

                let tokens = context.ParseResult.Tokens.Select(fun token -> token.Value).ToArray()
                let newTokens =
                    if tokens.Length >= 2 then
                        Array.append [| tokens[0].ToLowerInvariant(); tokens[1].ToLowerInvariant() |] (processTokens tokens[2..])
                    elif tokens.Length = 1 then
                        [| tokens[0].ToLowerInvariant() |]
                    else
                        tokens
                context.ParseResult <- context.Parser.Parse(newTokens)

        // Build the whole thing into a command system.        
        let commandLineBuilder = CommandLineBuilder(rootCommand)

        commandLineBuilder
            .UseHelp(fun helpContext ->
                // This is where we configure how help is displayed by Grace.

                // This checks to see if help was requested at the parent `grace` level, or if no commands or parameters were specified.
                // If so, we'll show the Figlet text and top-level commands and options.
                if helpContext.ParseResult.Tokens.Count = 0 || 
                        (helpContext.ParseResult.Tokens.Count = 1 && helpOptions.Contains(helpContext.ParseResult.Tokens[0].Value)) then
                    let graceFiglet = FigletText("Grace Version Control System")
                    graceFiglet.Color <- Color.Green3
                    graceFiglet.Justification <- Justify.Center
                    AnsiConsole.Write(graceFiglet)
                    AnsiConsole.WriteLine()

                    // Skip the description section if we printed FigletText.
                    helpContext.HelpBuilder.CustomizeLayout(fun layoutContext ->
                        HelpBuilder.Default.GetLayout().Where(fun section -> not <| section.Method.Name.Contains("Synopsis", StringComparison.InvariantCultureIgnoreCase))
                    )

                // We're passing a new List<Option> here, because we're going to be adding to it recursively in gatherAllOptions.
                let allOptions = gatherAllOptions helpContext.Command (List<Option>())

                //logToConsole "Children:"
                //for xx in helpContext.Command.Children do
                //    logToConsole $"{xx.Name}; {xx.Description}"

                //AnsiConsole.WriteLine()
                //logToConsole "All options:"
                //for xx in allOptions do
                //    logToConsole $"{xx.Name}; {xx.Description}"

                // This section sets the display of the default value for these options in all commands in Grace CLI.
                // Without setting the display values here, by default, we'd get something like "[default: thing-we-said-in-the-Option-definition] [default:e4def31b-4547-4f6b-9324-56eba666b4b2]" 
                //   i.e. whatever the generated Guid value on create might be.
                let optionsToUpdate = [
                    {optionName = "correlationId"; command = String.Empty; display = "new Guid"; displayOnCreate = "new Guid"}
                    {optionName = "branchId"; command = "Branch"; display = "current branch"; displayOnCreate = "new Guid"}
                    {optionName = "organizationId"; command = "Organization";  display = "current organization"; displayOnCreate = "new Guid"}
                    {optionName = "ownerId"; command = "Owner"; display = "current owner"; displayOnCreate = "new Guid"}
                    {optionName = "repositoryId"; command = "Repository"; display = "current repository"; displayOnCreate = "new Guid"}
                    {optionName = "parentBranchId"; command = "Branch"; display = "current branch, empty if parentBranchName is provided"; displayOnCreate = "current branch, empty if parentBranchName is provided"}
                ]

                optionsToUpdate |> List.iter(fun optionToUpdate ->
                     allOptions |> Seq.where(fun opt -> opt.Name = optionToUpdate.optionName)
                                |> Seq.iter(fun option -> 
                                                if helpContext.Command.Aliases.Any(fun alias -> alias = "create") && 
                                                    helpContext.Command.Parents.Any(fun parent -> parent.Name.Equals(optionToUpdate.command, StringComparison.InvariantCultureIgnoreCase)) then
                                                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = optionToUpdate.displayOnCreate)
                                                else
                                                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = optionToUpdate.display)))

                // Add the feedback section at the end.
                helpContext.HelpBuilder.CustomizeLayout(fun layoutContext ->
                    HelpBuilder.Default.GetLayout().Append(feedbackSection)
                )
            )
            .AddMiddleware(aliasHandlerMiddleware, MiddlewareOrder.ExceptionHandler)
            .AddMiddleware(caseInsensitiveMiddleware, MiddlewareOrder.ExceptionHandler)
            .UseDefaults()
            .UseParseErrorReporting()
            .Build()

    /// Checks if the command is a `grace watch` command.
    let isGraceWatch (parseResult: ParseResult) =
        if (parseResult.CommandResult.Command.Name = "watch") then true else false

    /// Checks if the command is a `grace config` command.
    let isGraceConfig (parseResult: ParseResult) =
        if not <| isNull parseResult.CommandResult.Parent then
            if (parseResult.CommandResult.Parent.Symbol.Name = "config") then true else false
        else
            false

    /// This is the main entry point for Grace CLI.
    [<EntryPoint>]
    let main args =
        let startTime = getCurrentInstant()
        (task {
            let mutable parseResult: ParseResult = null
            let mutable returnValue: int = 0
            try
                try
                    //use logListener = new TextWriterTraceListener(Path.Combine(Current().ConfigurationDirectory, "grace.log"))
                    //Threading.ThreadPool.SetMinThreads(Constants.ParallelOptions.MaxDegreeOfParallelism, Constants.ParallelOptions.MaxDegreeOfParallelism) |> ignore
                    let command = Build

                    // Right now, in order to handle default values, we need to read the Grace configuration file as the commands are being built, and before they're executed.
                    // For instance, when the command is `grace repo create...`, the `repo` command is built by System.CommandLine in-memory, and that causes `graceconfig.json` to be read
                    //    to get default values for things like OwnerId, OrganizationId, etc.
                    // If grace is being run in a directory where there's no config, that obviously won't work, so, first, we check if we have a config file.
                    // If we do have a config file, great! we can parse it and use it.
                    // If we don't, we're just going to print an error message and exit.
                    if configurationFileExists() then
                        parseResult <- command.Parse(args)
                        if parseResult |> hasOutput then
                            if parseResult |> verbose then
                                AnsiConsole.Write((new Rule($"[{Colors.Important}]Started: {startTime.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}.[/]")).RightJustified())
                            else
                                AnsiConsole.Write(new Rule())

                        // If this instance isn't `grace watch`, we want to check if `grace watch` is running by trying to read the IPC file.
                        if not <| (parseResult |> isGraceWatch) then
                            let! graceWatchStatus = getGraceWatchStatus()
                            match graceWatchStatus with
                            | Some status ->
                                Configuration.updateConfiguration {GraceWatchStatus = status}
                            | None ->
                                ()

                        // Now we can invoke the command!
                        let! returnValue = command.InvokeAsync(args)

                        // Stuff to do after the command has been invoked:

                        // If this instance is `grace watch`, we'll actually delete the IPC file in the finally clause below, but
                        //   we'll write the "we deleted the file" message to the console here, so it comes before the last Rule() is written.
                        if parseResult |> isGraceWatch then
                            logToAnsiConsole Colors.Important (getLocalizedString StringResourceName.InterprocessFileDeleted)

                        // If we're writing output, write the final Rule() to the console.
                        if parseResult |> hasOutput then
                            let finishTime = getCurrentInstant()
                            let elapsed = finishTime - startTime
                            if parseResult |> verbose then
                                AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}. Finished: {finishTime.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}[/]")).RightJustified())
                            else
                                AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightJustified())

                            AnsiConsole.WriteLine()
                    else
                        // We don't have a config file, so write an error message and exit.
                        AnsiConsole.Write(new Rule())

                        if args.Any(fun arg -> arg = "config") then
                            let! returnValue = command.InvokeAsync(args)
                            ()
                        else
                            AnsiConsole.MarkupLine($"[{Colors.Important}]{getLocalizedString StringResourceName.GraceConfigFileNotFound}[/]")
                        printParseResult parseResult
                        let finishTime = getCurrentInstant()
                        let elapsed = finishTime - startTime
                        AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightJustified())

                    return returnValue
                with ex ->
                    logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}"
                    logToAnsiConsole Colors.Error $"{ex.StackTrace}"
                    return -1
            finally
                // If this was grace watch, delete the inter-process communication file.
                if not <| isNull(parseResult) && parseResult |> isGraceWatch then
                    File.Delete(IpcFileName())
        }).Result
