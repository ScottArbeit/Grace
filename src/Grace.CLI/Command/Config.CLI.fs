namespace Grace.Cli.Command

open DiffPlex
open DiffPlex.DiffBuilder.Model
open FSharpPlus
open Grace.Cli.Common
open Grace.Cli.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Config
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
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
        let directory = new Option<string>("--directory", IsRequired = false, Description = "The root path of the repository to initialize Grace in [default: current directory]", Arity = ArgumentArity.ExactlyOne)
        let overwrite = new Option<bool>("--overwrite", IsRequired = false, Description = "Allows Grace to overwrite an existing graceconfig.json file with default values", Arity = ArgumentArity.ZeroOrOne, getDefaultValue = (fun _ -> false))

    let private CommonValidations (parseResult, parameters) =
        let ``Directory must be a valid path`` (parseResult: ParseResult, parameters: CommonParameters) =
            if Directory.Exists(parameters.Directory) then
                Ok (parseResult, parameters)
            else
                Error (GraceError.Create (ConfigError.getErrorMessage InvalidDirectoryPath) (parameters.CorrelationId))

        (parseResult, parameters)
            |> ``Directory must be a valid path``
   
    let private renderLine (diffLine: DiffPiece) = 
        if not <| diffLine.Position.HasValue then $"        {diffLine.Text.EscapeMarkup()}"
        else $"{diffLine.Position,6:D}: {diffLine.Text.EscapeMarkup()}"

    let private getMarkup (diffLine: DiffPiece) = 
        if diffLine.Type = ChangeType.Deleted then Markup($"[{Colors.Deleted}]-{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Inserted then Markup($"[{Colors.Added}]+{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Modified then Markup($"[{Colors.Changed}]~{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Imaginary then Markup($"[{Colors.Deemphasized}] {renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Unchanged then Markup($"[{Colors.Important}] {renderLine diffLine}[/]")
        else Markup($"[{Colors.Important}] {diffLine.Text}[/]")
    
    type WriteParameters() =
        inherit CommonParameters()

    let private writeHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: WriteParameters) ->
            task {
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = (parseResult, parameters) |> CommonValidations
                match validateIncomingParameters with
                | Ok _ ->
                    // Search for existing .grace directory and existing graceconfig.json
                    // If I find them, and parameters.Overwrite is true, then I can empty out the .grace directory and write a default graceconfig.json.
                    // If I don't find them, I should create the .grace directory and write a default graceconfig.json.
                    // We should use `GraceConfiguration() |> saveConfigFile parameters.Directory` to write the default config
                    let graceDirPath = Path.Combine(parameters.Directory, ".grace")
                    let graceConfigPath = Path.Combine(graceDirPath, "graceconfig.json")

                    let overwriteExisting = parameters.Overwrite && Directory.Exists(graceDirPath) && File.Exists(graceConfigPath)

                    if overwriteExisting then
                        // Clear out everything in the .grace directory.
                        if parseResult |> showOutput then printfn "Deleting contents of existing .grace directory."
                        Directory.Delete(graceDirPath, recursive = true)

                    if File.Exists(graceConfigPath) && not parameters.Overwrite then
                        if parseResult |> showOutput then printfn $"Found existing Grace configuration file at {graceConfigPath}. Specify --overwrite if you'd like to overwrite it.{Environment.NewLine}"
                    else
                        let directoryInfo = Directory.CreateDirectory(graceDirPath)
                        if parseResult |> showOutput then printfn $"Writing new Grace configuration file at {graceConfigPath}.{Environment.NewLine}"
                        GraceConfiguration() |> saveConfigFile graceConfigPath
                    return 0
                | Error error ->
                    return (Error error) |> renderOutput parseResult
            } :> Task)

    let Build = 
        let addCommonOptions (command: Command) =
            command |> addOption Options.directory
                    |> addOption Options.overwrite

        let configCommand = new Command("config", Description = "Initializes a repository with the default Grace configuration.")

        let writeCommand = new Command("write", Description = "Initializes a repository with a default Grace configuration.") |> addCommonOptions
        writeCommand.Handler <- writeHandler
        configCommand.AddCommand(writeCommand)
        
        configCommand
