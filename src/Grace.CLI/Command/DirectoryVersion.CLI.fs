namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Types.Branch
open Grace.Types.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Services
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Globalization
open System.IO
open System.IO.Enumeration
open System.Linq
open System.Security.Cryptography
open System.Threading.Tasks
open System.Text
open System.Text.Json
open Grace.Shared.Parameters.Storage

module DirectoryVersion =
    open Grace.Shared.Validation.Common.Input

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OwnerId)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> Current().OrganizationId)
            )

        let organizationName =
            new Option<String>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                [| "-r" |],
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> Current().RepositoryId)
            )

        let repositoryName =
            new Option<String>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let maxCount =
            new Option<int>(
                OptionName.MaxCount,
                Required = false,
                Description = "The maximum number of results to return.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 30)
            )

        let sha256Hash =
            new Option<String>(
                OptionName.Sha256Hash,
                [||],
                Required = false,
                Description = "The full or partial SHA-256 hash value of the version.",
                Arity = ArgumentArity.ExactlyOne
            )

        let includeDeleted =
            new Option<bool>(OptionName.IncludeDeleted, [| "-d" |], Required = false, Description = "Include deleted branches in the result. [default: false]")

        let directoryVersionIdRequired =
            new Option<DirectoryVersionId>(
                OptionName.DirectoryVersionId,
                [| "-v" |],
                Required = true,
                Description = "The DirectoryVersionId to act on <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

    let private DirectoryVersionValidations parseResult =
        let ``Grace index file must exist`` (parseResult: ParseResult) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (getErrorMessage DirectoryVersionError.IndexFileNotFound) (getCorrelationId parseResult))
            else
                Ok parseResult

        let ``Grace object cache file must exist`` (parseResult: ParseResult) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (getErrorMessage DirectoryVersionError.ObjectCacheFileNotFound) (getCorrelationId parseResult))
            else
                Ok parseResult

        parseResult
        |> ``Grace index file must exist``
        >>= ``Grace object cache file must exist``

    type GetZipFile() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let validateIncomingParameters = parseResult |> CommonValidations >>= DirectoryVersionValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let graceIds = getNormalizedIdsAndNames parseResult
                        let directoryVersionId = parseResult.GetValue(Options.directoryVersionIdRequired)
                        let sha256Hash = parseResult.GetValue(Options.sha256Hash)

                        let sdkParameters =
                            Parameters.DirectoryVersion.GetZipFileParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                DirectoryVersionId = $"{directoryVersionId}",
                                Sha256Hash = sha256Hash,
                                CorrelationId = graceIds.CorrelationId
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = DirectoryVersion.GetZipFile(sdkParameters)
                                            t0.Increment(100.0)

                                            match result with
                                            | Ok returnValue -> AnsiConsole.MarkupLine $"[{Colors.Highlighted}]{returnValue.ReturnValue}[/]"
                                            | Error error -> logToAnsiConsole Colors.Error $"{error}"

                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = DirectoryVersion.GetZipFile(sdkParameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
                        |> renderOutput parseResult

            //match result with
            //| Ok returnValue -> AnsiConsole.MarkupLine $"[{Colors.Highlighted}]{returnValue.ReturnValue}[/]"
            //| Error error -> logToAnsiConsole Colors.Error $"{error}"

            //return result |> renderOutput parseResult
            }

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId
            |> addOption Options.directoryVersionIdRequired

        // Create main command and aliases, if any.`
        let directoryVersionCommand = new Command("directory-version", Description = "Work with directory versions in a repository.")

        directoryVersionCommand.Aliases.Add("dv")
        directoryVersionCommand.Aliases.Add("ver")

        let getZipFileCommand =
            new Command("get-zip-file", Description = "Gets the .zip file for a specific directory version.")
            |> addOption Options.sha256Hash
            |> addCommonOptions

        getZipFileCommand.Action <- GetZipFile()
        directoryVersionCommand.Subcommands.Add(getZipFileCommand)

        directoryVersionCommand
