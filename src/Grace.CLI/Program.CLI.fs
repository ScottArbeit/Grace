namespace Grace.Cli

open Grace.Cli.Command
open Grace.Cli.Common
open Grace.Cli.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Converters
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

    let private aliases = 
        let aliases = Dictionary<string, string[]>()
        aliases.Add("merge", [| "branch"; "merge" |])
        aliases.Add("commit", [| "branch"; "commit" |])
        aliases.Add("tag", [| "branch"; "tag" |])
        aliases.Add("checkpoint", [| "branch"; "checkpoint" |])
        aliases.Add("save", [| "branch"; "save" |])
        aliases.Add("rebase", [| "branch"; "rebase" |])
        aliases.Add("merges", [| "branch"; "get-merges" |])
        aliases.Add("commits", [| "branch"; "get-commits" |])
        aliases.Add("checkpoints", [| "branch"; "get-checkpoints" |])
        aliases.Add("saves", [| "branch"; "get-references" |])
        aliases.Add("refs", [| "branch"; "get-references" |])
        aliases.Add("status", [| "branch"; "status" |])
        aliases.Add("switch", [| "branch"; "switch" |])
        aliases.Add("branches", [| "repository"; "get-branches" |])
        //aliases.Add("", [| ""; "" |])
        aliases
        
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
        rootCommand.AddCommand(Maintenance.Build)

        let feedbackSection: HelpSectionDelegate = HelpSectionDelegate(fun context -> 
            context.Output.WriteLine()
            context.Output.WriteLine("More help and feedback:")
            context.Output.WriteLine("  For more help, or to give us feedback, please create an issue in our repo at https://github.com/github/grace."))

        let aliasHandler (context: InvocationContext) =
            let tokens = context.ParseResult.Tokens.Select(fun token -> token.Value).ToList()
            if tokens.Count > 0 then
                let firstToken = tokens[0]
                if aliases.ContainsKey(firstToken) then
                    tokens.RemoveAt(0)
                    for token in aliases[firstToken].Reverse() do
                        tokens.Insert(0, token)
                context.ParseResult <- context.Parser.Parse(tokens)

        // Build the whole thing into a command system.        
        let commandLineBuilder = CommandLineBuilder(rootCommand)
        commandLineBuilder
            .UseDefaults()
            .AddMiddleware(aliasHandler, MiddlewareOrder.ExceptionHandler)
            .UseHelp(fun helpContext ->
                let helpOptions = [| "-h"; "/h"; "--help"; "-?"; "/?" |]
                if helpContext.ParseResult.Tokens.Count = 0 || 
                        (helpContext.ParseResult.Tokens.Count = 1 && helpOptions.Contains(helpContext.ParseResult.Tokens[0].Value)) then
                    let graceVCS = FigletText("Grace Version Control System")
                    graceVCS.Color <- Color.Green3
                    graceVCS.Alignment <- Justify.Center
                    AnsiConsole.Write(graceVCS)
                    AnsiConsole.WriteLine()

                    // Skip the description section if we printed FigletText.
                    helpContext.HelpBuilder.CustomizeLayout(fun layoutContext ->
                        HelpBuilder.Default.GetLayout().Where(fun section -> not <| section.Method.Name.Contains("Synopsis", StringComparison.InvariantCultureIgnoreCase))
                    )

                let rec gatherAllOptions (command: Command) (allOptions: List<Option>) = 
                    allOptions.AddRange(command.Options)
                    let parentCommand = command.Parents.OfType<Command>().FirstOrDefault()
                    if not <| isNull parentCommand then
                        gatherAllOptions parentCommand allOptions
                    else
                        allOptions

                let allOptions = gatherAllOptions helpContext.Command (List<Option>())

                //logToConsole "Children:"
                //for xx in helpContext.Command.Children do
                //    logToConsole $"{xx.Name}; {xx.Description}"

                //AnsiConsole.WriteLine()
                //logToConsole "All options:"
                //for xx in allOptions do
                //    logToConsole $"{xx.Name}; {xx.Description}"

                for option in allOptions.Where(fun opt -> opt.Name = "correlationId") do
                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = "new Guid")

                for option in allOptions.Where(fun opt -> opt.Name = "branchId") do
                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = "current branch, or new Guid on create")

                for option in allOptions.Where(fun opt -> opt.Name = "organizationId") do
                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = "current organization, or new Guid on create")

                for option in allOptions.Where(fun opt -> opt.Name = "ownerId") do
                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = "current owner, or new Guid on create")

                for option in allOptions.Where(fun opt -> opt.Name = "repositoryId") do
                    helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = "current repository, or new Guid on create")

                // Add the feedback section at the end.
                helpContext.HelpBuilder.CustomizeLayout(fun layoutContext ->
                    HelpBuilder.Default.GetLayout().Append(feedbackSection)
                )
            )
            .Build()

    let isGraceWatch(parseResult: ParseResult) =
        if (parseResult.CommandResult.Command.Name = "watch") then true else false

    [<EntryPoint>]
    let main args =
        (task {
            let mutable parseResult: ParseResult = null
            try
                let startTime = getCurrentInstant()
                //use logListener = new TextWriterTraceListener(Path.Combine(Current().ConfigurationDirectory, "grace.log"))
                //Threading.ThreadPool.SetMinThreads(Constants.ParallelOptions.MaxDegreeOfParallelism, Constants.ParallelOptions.MaxDegreeOfParallelism) |> ignore
                let command = Build

                //let n = Constants.JsonSerializerOptions.Converters.Count
                //Constants.JsonSerializerOptions.Converters.Add(BranchDtoConverter())
                //logToAnsiConsole Colors.Important $"Was {n}, now {Constants.JsonSerializerOptions.Converters.Count}."

                parseResult <- command.Parse(args)
                if parseResult |> showOutput then
                    //AnsiConsole.Write((new Rule($"[{Colors.Important}]Started: {startTime.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}.[/]")).RightAligned())
                    AnsiConsole.Write(new Rule())

                if not <| isGraceWatch parseResult then
                    let! graceWatchStatus = getGraceWatchStatus()
                    match graceWatchStatus with
                    | Some status ->
                        //logToAnsiConsole Colors.Verbose $"Opened IPC file."
                        Configuration.updateConfiguration {GraceWatchStatus = status}
                    | None ->
                            //logToAnsiConsole Colors.Verbose $"Couldn't open IPC file."
                            ()

                let! returnValue = command.InvokeAsync(args)

                // We'll tell the user now, before the Rule(), but we actually delete the inter-process communication file in the finally clause.
                if isGraceWatch parseResult then
                    logToAnsiConsole Colors.Important $"Inter-process communication file deleted."

                if parseResult |> showOutput then
                    AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {(getCurrentInstant() - startTime).TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightAligned())
                    AnsiConsole.WriteLine()

                return returnValue
            finally
                // If this was grace watch, delete the inter-process communication file.
                if isGraceWatch parseResult then
                    File.Delete(IpcFileName())
        }).Result
