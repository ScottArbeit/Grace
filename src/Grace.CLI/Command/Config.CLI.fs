namespace Grace.CLI.Command

open DiffPlex
open DiffPlex.DiffBuilder.Model
open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Branch
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Linq
open System.IO
open System.Text.Json
open System.Threading.Tasks

module Config =

    type CommonParameters() =
        inherit ParameterBase()
        member val public Directory: string = "." with get, set
        member val public Overwrite: bool = false with get, set

    module private Options =
        let directory =
            new Option<string>(
                OptionName.Directory,
                Required = false,
                Description = "The root path of the repository to initialize Grace in [default: current directory]",
                Arity = ArgumentArity.ExactlyOne
            )

        let overwrite =
            new Option<bool>(
                OptionName.Overwrite,
                Required = false,
                Description = "Allows Grace to overwrite an existing graceconfig.json file with default values",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

    let private CommonValidations parseResult =
        let ``Directory must be a valid path`` (parseResult: ParseResult) =
            let directory = parseResult.GetValue(Options.directory)

            if Directory.Exists(directory) then
                Ok parseResult
            else
                Error(GraceError.Create (getErrorMessage ConfigError.InvalidDirectoryPath) (getCorrelationId parseResult))

        parseResult |> ``Directory must be a valid path``

    let private renderLine (diffLine: DiffPiece) =
        if not <| diffLine.Position.HasValue then
            $"        {diffLine.Text.EscapeMarkup()}"
        else
            $"{diffLine.Position, 6:D}: {diffLine.Text.EscapeMarkup()}"

    let private getMarkup (diffLine: DiffPiece) =
        if diffLine.Type = ChangeType.Deleted then
            Markup($"[{Colors.Deleted}]-{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Inserted then
            Markup($"[{Colors.Added}]+{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Modified then
            Markup($"[{Colors.Changed}]~{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Imaginary then
            Markup($"[{Colors.Deemphasized}] {renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Unchanged then
            Markup($"[{Colors.Important}] {renderLine diffLine}[/]")
        else
            Markup($"[{Colors.Important}] {diffLine.Text}[/]")

    type WriteParameters() =
        inherit CommonParameters()

    let writeHandler (parseResult: ParseResult) (parameters: WriteParameters) =
        task {
            if parseResult |> verbose then printParseResult parseResult

            let validateIncomingParameters = parseResult |> CommonValidations

            match validateIncomingParameters with
            | Ok _ ->
                // Search for existing .grace directory and existing graceconfig.json
                // If I find them, and parameters.Overwrite is true, then I can empty out the .grace directory and write a default graceconfig.json.
                // If I don't find them, I should create the .grace directory and write a default graceconfig.json.
                // We should use `GraceConfiguration() |> saveConfigFile parameters.Directory` to write the default config
                let graceDirPath = Path.Combine(parameters.Directory, ".grace")
                let graceConfigPath = Path.Combine(graceDirPath, "graceconfig.json")

                let overwriteExisting =
                    parameters.Overwrite
                    && Directory.Exists(graceDirPath)
                    && File.Exists(graceConfigPath)

                if overwriteExisting then
                    // Clear out everything in the .grace directory.
                    if parseResult |> hasOutput then
                        printfn "Deleting contents of existing .grace directory."

                    Directory.Delete(graceDirPath, recursive = true)

                if
                    File.Exists(graceConfigPath)
                    && not parameters.Overwrite
                then
                    if parseResult |> hasOutput then
                        printfn
                            $"Found existing Grace configuration file at {graceConfigPath}. Specify {OptionName.Overwrite} if you'd like to overwrite it.{Environment.NewLine}"
                else
                    let directoryInfo = Directory.CreateDirectory(graceDirPath)

                    if parseResult |> hasOutput then
                        printfn $"Writing new Grace configuration file at {graceConfigPath}.{Environment.NewLine}"

                    GraceConfiguration()
                    |> saveConfigFile graceConfigPath

                return Ok(GraceReturnValue.Create () parameters.CorrelationId)
            | Error error -> return (Error error)
        }

    type Write() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> CommonValidations
                let directory = parseResult.GetValue(Options.directory)
                let overwrite = parseResult.GetValue(Options.overwrite)

                match validateIncomingParameters with
                | Ok _ ->
                    // Search for existing .grace directory and existing graceconfig.json
                    // If I find them, and parameters.Overwrite is true, then I can empty out the .grace directory and write a default graceconfig.json.
                    // If I don't find them, I should create the .grace directory and write a default graceconfig.json.
                    // We should use `GraceConfiguration() |> saveConfigFile parameters.Directory` to write the default config
                    let graceDirPath = Path.Combine(directory, ".grace")
                    let graceConfigPath = Path.Combine(graceDirPath, "graceconfig.json")

                    let overwriteExisting =
                        overwrite
                        && Directory.Exists(graceDirPath)
                        && File.Exists(graceConfigPath)

                    if overwriteExisting then
                        // Clear out everything in the .grace directory.
                        if parseResult |> hasOutput then
                            printfn "Deleting contents of existing .grace directory."

                        Directory.Delete(graceDirPath, recursive = true)

                    if File.Exists(graceConfigPath) && not overwrite then
                        if parseResult |> hasOutput then
                            printfn
                                $"Found existing Grace configuration file at {graceConfigPath}. Specify {OptionName.Overwrite} if you'd like to overwrite it.{Environment.NewLine}"
                    else
                        let directoryInfo = Directory.CreateDirectory(graceDirPath)

                        if parseResult |> hasOutput then
                            printfn $"Writing new Grace configuration file at {graceConfigPath}.{Environment.NewLine}"

                        GraceConfiguration()
                        |> saveConfigFile graceConfigPath

                    return
                        Ok(GraceReturnValue.Create () (getCorrelationId parseResult))
                        |> renderOutput parseResult
                | Error error -> return (Error error) |> renderOutput parseResult
            //let! writeResult = writeHandler parseResult
            //return writeResult |> renderOutput parseResult
            }

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.directory
            |> addOption Options.overwrite

        let configCommand = new Command("config", Description = "Initializes a repository with the default Grace configuration.")

        let writeCommand =
            new Command("write", Description = "Initializes a repository with a default Grace configuration.")
            |> addCommonOptions

        writeCommand.Action <- Write()
        configCommand.Subcommands.Add(writeCommand)

        configCommand
