namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors.DirectoryVersion
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
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

    type CommonParameters() =
        inherit ParameterBase()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set
        member val public DirectoryVersionId: string = String.Empty with get, set

    module private Options =
        let ownerId =
            new Option<String>(
                "--ownerId",
                IsRequired = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                getDefaultValue = (fun _ -> $"{Current().OwnerId}")
            )

        let ownerName =
            new Option<String>(
                "--ownerName",
                IsRequired = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<String>(
                "--organizationId",
                IsRequired = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue = (fun _ -> $"{Current().OrganizationId}")
            )

        let organizationName =
            new Option<String>(
                "--organizationName",
                IsRequired = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<String>(
                [| "--repositoryId"; "-r" |],
                IsRequired = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue = (fun _ -> $"{Current().RepositoryId}")
            )

        let repositoryName =
            new Option<String>(
                [| "--repositoryName"; "-n" |],
                IsRequired = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let maxCount =
            new Option<int>("--maxCount", IsRequired = false, Description = "The maximum number of results to return.", Arity = ArgumentArity.ExactlyOne)

        maxCount.SetDefaultValue(30)

        let sha256Hash =
            new Option<String>(
                [| "--sha256Hash" |],
                IsRequired = false,
                Description = "The full or partial SHA-256 hash value of the version.",
                Arity = ArgumentArity.ExactlyOne
            )

        let includeDeleted =
            new Option<bool>([| "--include-deleted"; "-d" |], IsRequired = false, Description = "Include deleted branches in the result. [default: false]")

        let directoryVersionIdRequired =
            new Option<String>(
                [| "--directoryVersionId"; "-v" |],
                IsRequired = true,
                Description = "The DirectoryVersionId to act on <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: DirectoryVersionError) =
        let mutable guid = Guid.Empty

        if
            parseResult.CommandResult.FindResultFor(option) <> null
            && not <| String.IsNullOrEmpty(value)
            && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
        then
            Error(GraceError.Create (DirectoryVersionError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: DirectoryVersionError) =
        if
            parseResult.CommandResult.FindResultFor(option) <> null
            && not <| Constants.GraceNameRegex.IsMatch(value)
        then
            Error(GraceError.Create (DirectoryVersionError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let oneOfTheseOptionsMustBeProvided (parseResult: ParseResult) (parameters: CommonParameters) (options: Option array) (error: DirectoryVersionError) =
        match
            options
            |> Array.tryFind (fun opt -> not <| isNull (parseResult.CommandResult.FindResultFor(opt)))
        with
        | Some opt -> Ok(parseResult, parameters)
        | None -> Error(GraceError.Create (DirectoryVersionError.getErrorMessage error) (parameters.CorrelationId))

    let private CommonValidations parseResult commonParameters =
        let ``OwnerId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.ownerId commonParameters.OwnerId InvalidOwnerId

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.ownerName commonParameters.OwnerName InvalidOwnerName

        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.organizationId commonParameters.OrganizationId InvalidOrganizationId

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.organizationName commonParameters.OrganizationName InvalidOrganizationName

        let ``RepositoryId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.repositoryId commonParameters.RepositoryId InvalidRepositoryId

        let ``RepositoryName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.repositoryName commonParameters.RepositoryName InvalidRepositoryName

        let ``DirectoryVersionId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.directoryVersionIdRequired commonParameters.DirectoryVersionId InvalidDirectoryVersionId

        let ``Grace index file must exist`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (DirectoryVersionError.getErrorMessage IndexFileNotFound) commonParameters.CorrelationId)
            else
                Ok(parseResult, commonParameters)

        let ``Grace object cache file must exist`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (DirectoryVersionError.getErrorMessage ObjectCacheFileNotFound) commonParameters.CorrelationId)
            else
                Ok(parseResult, commonParameters)

        (parseResult, commonParameters)
        |> ``OwnerId must be a Guid``
        >>= ``OwnerName must be a valid Grace name``
        >>= ``OrganizationId must be a Guid``
        >>= ``OrganizationName must be a valid Grace name``
        >>= ``RepositoryId must be a Guid``
        >>= ``RepositoryName must be a valid Grace name``
        >>= ``Grace index file must exist``
        >>= ``Grace object cache file must exist``

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user, or should be taken from default values.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, then we should only send the name, and we set the id to String.Empty.
        if
            parseResult.CommandResult.FindResultFor(Options.ownerId).IsImplicit
            && not <| isNull (parseResult.CommandResult.FindResultFor(Options.ownerName))
            && not <| parseResult.CommandResult.FindResultFor(Options.ownerName).IsImplicit
        then
            parameters.OwnerId <- String.Empty

        if
            parseResult.CommandResult.FindResultFor(Options.organizationId).IsImplicit
            && not
               <| isNull (parseResult.CommandResult.FindResultFor(Options.organizationName))
            && not
               <| parseResult.CommandResult.FindResultFor(Options.organizationName).IsImplicit
        then
            parameters.OrganizationId <- String.Empty

        if
            parseResult.CommandResult.FindResultFor(Options.repositoryId).IsImplicit
            && not <| isNull (parseResult.CommandResult.FindResultFor(Options.repositoryName))
            && not
               <| parseResult.CommandResult.FindResultFor(Options.repositoryName).IsImplicit
        then
            parameters.RepositoryId <- String.Empty

        parameters

    /// Populates OwnerId, OrganizationId, RepositoryId, and BranchId based on the command parameters and the contents of graceconfig.json.
    let getIds<'T when 'T :> CommonParameters> (parameters: 'T) =
        let ownerId =
            if
                not <| String.IsNullOrEmpty(parameters.OwnerId)
                || not <| String.IsNullOrEmpty(parameters.OwnerName)
            then
                parameters.OwnerId
            else
                $"{Current().OwnerId}"

        let organizationId =
            if
                not <| String.IsNullOrEmpty(parameters.OrganizationId)
                || not <| String.IsNullOrEmpty(parameters.OrganizationName)
            then
                parameters.OrganizationId
            else
                $"{Current().OrganizationId}"

        let repositoryId =
            if
                not <| String.IsNullOrEmpty(parameters.RepositoryId)
                || not <| String.IsNullOrEmpty(parameters.RepositoryName)
            then
                parameters.RepositoryId
            else
                $"{Current().RepositoryId}"

        (ownerId, organizationId, repositoryId)

    type GetZipFileParameters() =
        inherit CommonParameters()
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set

    let getZipFileHandler (parseResult: ParseResult) (getZipFileParameters: GetZipFileParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult getZipFileParameters

                match validateIncomingParameters with
                | Ok _ ->
                    let normalizedParameters = getZipFileParameters |> normalizeIdsAndNames parseResult

                    let sdkParameters =
                        Parameters.DirectoryVersion.GetZipFileParameters(
                            OwnerId = normalizedParameters.OwnerId,
                            OwnerName = normalizedParameters.OwnerName,
                            OrganizationId = normalizedParameters.OrganizationId,
                            OrganizationName = normalizedParameters.OrganizationName,
                            RepositoryId = normalizedParameters.RepositoryId,
                            RepositoryName = normalizedParameters.RepositoryName,
                            DirectoryVersionId = normalizedParameters.DirectoryVersionId,
                            Sha256Hash = normalizedParameters.Sha256Hash,
                            CorrelationId = normalizedParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = DirectoryVersion.GetZipFile(sdkParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! DirectoryVersion.GetZipFile(sdkParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private GetZipFile =
        CommandHandler.Create(fun (parseResult: ParseResult) (getZipFileParameters: GetZipFileParameters) ->
            task {
                let! result = getZipFileHandler parseResult getZipFileParameters

                match result with
                | Ok returnValue -> AnsiConsole.MarkupLine $"[{Colors.Highlighted}]{returnValue.ReturnValue}[/]"
                | Error error -> logToAnsiConsole Colors.Error $"{error}"

                return result |> renderOutput parseResult
            })

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

        directoryVersionCommand.AddAlias("dv")
        directoryVersionCommand.AddAlias("ver")

        let getZipFileCommand =
            new Command("get-zip-file", Description = "Gets the .zip file for a specific directory version.")
            |> addOption Options.sha256Hash
            |> addCommonOptions

        getZipFileCommand.Handler <- GetZipFile
        directoryVersionCommand.AddCommand(getZipFileCommand)

        directoryVersionCommand
