namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Library
open Grace.Shared.Utilities
open Grace.Shared.Validation.Library
open Grace.Types.Common
open Grace.Types.Library
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

/// Defines Library catalog management and local synchronization commands.
module LibraryCommand =

    /// Defines options shared by the Library handlers.
    module private Options =
        let libraryPath = Argument<string>("path", Description = "Repository-relative Library path.")

        /// Selects one materialized file without exposing internal item or namespace identities.
        let renamePath = Argument<string>("path", Description = "Repository-relative synchronized nonempty file path.")

        /// Supplies a different filename within the file's existing parent.
        let newName = Argument<string>("new-name", Description = "New filename in the same parent; case-only changes are excluded.")

        let expectedVersion =
            Option<Guid>("--expected-version", Required = false, Description = "Exact Library catalog version <Guid>; reads the current version when omitted.")

        let operationId =
            Option<Guid>("--operation-id", Required = false, Description = "Library operation identity <Guid>; creates a new identity when omitted.")

        let ownerId =
            Option<OwnerId>(OptionName.OwnerId, Required = false, Description = "Repository owner ID <Guid>.", DefaultValueFactory = (fun _ -> OwnerId.Empty))

        let ownerName = Option<string>(OptionName.OwnerName, Required = false, Description = "Repository owner name.")

        let organizationId =
            Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "Repository organization ID <Guid>.",
                DefaultValueFactory = (fun _ -> OrganizationId.Empty)
            )

        let organizationName = Option<string>(OptionName.OrganizationName, Required = false, Description = "Repository organization name.")

        let repositoryId =
            Option<RepositoryId>(
                OptionName.RepositoryId,
                Required = false,
                Description = "Repository ID <Guid>.",
                DefaultValueFactory = (fun _ -> RepositoryId.Empty)
            )

        let repositoryName = Option<string>(OptionName.RepositoryName, Required = false, Description = "Repository name.")

    /// Applies the resolved repository identity and correlation ID to one Library request.
    let private applyScope (parameters: #LibraryParameters) (parseResult: ParseResult) =
        let graceIds = getNormalizedIdsAndNames parseResult
        parameters.OwnerId <- graceIds.OwnerIdString
        parameters.OwnerName <- graceIds.OwnerName
        parameters.OrganizationId <- graceIds.OrganizationIdString
        parameters.OrganizationName <- graceIds.OrganizationName
        parameters.RepositoryId <- graceIds.RepositoryIdString
        parameters.RepositoryName <- graceIds.RepositoryName
        parameters.CorrelationId <- graceIds.CorrelationId
        parameters

    /// Reads the persisted Library configuration through the remote SDK.
    let internal getLibraryHandler parseResult =
        task {
            try
                let parameters =
                    GetLibraryCatalogParameters()
                    |> fun value -> applyScope value parseResult

                match! Libraries.GetCatalog parameters with
                | Error error -> return Error error
                | Ok result ->
                    let requestedPath = parseResult.GetValue Options.libraryPath

                    match
                        result.ReturnValue.Libraries
                        |> Array.tryFind (pathsEqual requestedPath)
                        with
                    | Some libraryPath -> return Ok { result with ReturnValue = { result.ReturnValue with Libraries = [| libraryPath |] } }
                    | None -> return Error(GraceError.Create $"Library '{requestedPath}' was not found." (getCorrelationId parseResult))
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Lists the current deterministic Library paths through the remote SDK.
    let internal listLibrariesHandler parseResult =
        task {
            try
                let parameters =
                    ListLibrariesParameters()
                    |> fun value -> applyScope value parseResult

                return! Libraries.ListLibraries parameters
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Sends one catalog mutation using explicit inputs or one lookup and one invocation-local operation identity.
    let internal changeLibraryHandlerWith
        (newOperationId: unit -> Guid)
        (getCatalog: GetLibraryCatalogParameters -> Task<GraceResult<LibraryCatalogDto>>)
        (add: AddLibraryParameters -> Task<GraceResult<LibraryCatalogChangeResultDto>>)
        (remove: RemoveLibraryParameters -> Task<GraceResult<LibraryCatalogChangeResultDto>>)
        addLibrary
        (parseResult: ParseResult)
        : Task<GraceResult<LibraryCatalogChangeResultDto>>
        =
        task {
            try
                let libraryPath = parseResult.GetValue Options.libraryPath
                let operationResult = parseResult.GetResult Options.operationId

                let operationId =
                    if isNull operationResult || operationResult.Implicit then
                        newOperationId ()
                    else
                        parseResult.GetValue Options.operationId

                let! version =
                    task {
                        let versionResult = parseResult.GetResult Options.expectedVersion

                        if isNull versionResult || versionResult.Implicit then
                            let! catalog = getCatalog (applyScope (GetLibraryCatalogParameters()) parseResult)

                            return
                                catalog
                                |> Result.map (fun result -> result.ReturnValue.Version)
                        else
                            return Ok(parseResult.GetValue Options.expectedVersion)
                    }

                match version with
                | Error error -> return Error error
                | Ok expectedVersion when addLibrary ->
                    let parameters =
                        AddLibraryParameters()
                        |> fun value -> applyScope value parseResult

                    parameters.ExpectedVersion <- expectedVersion
                    parameters.LibraryPath <- libraryPath
                    parameters.OperationId <- operationId
                    return! add parameters
                | Ok expectedVersion ->
                    let parameters =
                        RemoveLibraryParameters()
                        |> fun value -> applyScope value parseResult

                    parameters.ExpectedVersion <- expectedVersion
                    parameters.LibraryPath <- libraryPath
                    parameters.OperationId <- operationId
                    return! remove parameters
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Uses the ordinary SDK once for lookup when needed and once for the requested catalog mutation, without retries.
    let private changeLibraryHandler addLibrary parseResult =
        changeLibraryHandlerWith Guid.NewGuid Libraries.GetCatalog Libraries.AddLibrary Libraries.RemoveLibrary addLibrary parseResult

    /// Dispatches `grace library get <path>` and renders the standard Grace result envelope.
    type GetLibrary() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-configuration read action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = getLibraryHandler parseResult
                return renderOutput parseResult result
            }

    /// Dispatches `grace library list` and renders the standard Grace result envelope.
    type ListLibraries() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-list read action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = listLibrariesHandler parseResult
                return renderOutput parseResult result
            }

    /// Dispatches `grace library add <path>` and renders its typed accepted, stale, unchanged, or rejected result.
    type AddLibrary() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-add action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = changeLibraryHandler true parseResult
                return renderOutput parseResult result
            }

    /// Dispatches `grace library remove <path>` and renders its typed accepted, stale, unchanged, or rejected result.
    type RemoveLibrary() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-remove action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = changeLibraryHandler false parseResult
                return renderOutput parseResult result
            }

    /// Runs synchronization against the repository configured for this working copy.
    let internal synchronizationHandler verb (parseResult: ParseResult) cancellationToken =
        task {
            try
                let configuration = Current()
                let locator = applyScope (GetLibraryCatalogParameters()) parseResult

                if locator.RepositoryId
                   <> configuration.RepositoryId.ToString("D")
                   || locator.OwnerId
                      <> configuration.OwnerId.ToString("D")
                   || locator.OrganizationId
                      <> configuration.OrganizationId.ToString("D") then
                    invalidOp "Library synchronization must target the configured working-copy repository."

                let! status =
                    match verb with
                    | "enable" -> LibrarySynchronization.enable configuration locator.CorrelationId cancellationToken
                    | "run" -> LibrarySynchronization.run configuration locator.CorrelationId cancellationToken
                    | "status" -> LibrarySynchronization.status configuration
                    | _ -> invalidArg (nameof verb) "Unsupported Library synchronization command."

                return Ok(GraceReturnValue.Create status locator.CorrelationId)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Explains the retained rename state without describing an unresolved filename change as success.
    let internal renameMessage (result: LibrarySynchronization.RenameResult) =
        let message =
            match result.Outcome with
            | "completed" -> $"Library rename completed: '{result.SourcePath}' to '{result.TargetPath}'."
            | "rejected" -> "Library rename rejected. This request changed no local filenames."
            | "acceptedButObstructed" ->
                "Library rename accepted by the server; local application is incomplete. Resolve the obstruction and rerun the same command."
            | _ -> "Library rename outcome is ambiguous. The selected request is retained; rerun the same command to resume."

        result.Reason
        |> Option.map (fun reason -> message + " " + reason)
        |> Option.defaultValue message

    /// Builds Library catalog and synchronization commands.
    let Build =
        let addScopeOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId

        let addChangeOptions command =
            command
            |> addOption Options.expectedVersion
            |> addOption Options.operationId
            |> addScopeOptions

        let libraryCommand = Command("library", "Manage repository Libraries.")

        let getCommand =
            Command("get", "Get one configured Library.")
            |> addScopeOptions

        getCommand.Arguments.Add Options.libraryPath
        getCommand.Action <- GetLibrary()
        libraryCommand.Subcommands.Add getCommand

        let listCommand =
            Command("list", "List Library paths.")
            |> addScopeOptions

        listCommand.Action <- ListLibraries()
        libraryCommand.Subcommands.Add listCommand

        let addCommand =
            Command("add", "Add one empty repository-relative Library.")
            |> addChangeOptions

        addCommand.Arguments.Add Options.libraryPath
        addCommand.Action <- AddLibrary()
        libraryCommand.Subcommands.Add addCommand

        let removeCommand =
            Command("remove", "Remove one empty Library.")
            |> addChangeOptions

        removeCommand.Arguments.Add Options.libraryPath
        removeCommand.Action <- RemoveLibrary()
        libraryCommand.Subcommands.Add removeCommand

        let renameCommand = Command("rename", "Rename one clean synchronized nonempty file in its existing parent.")
        renameCommand.Arguments.Add Options.renamePath
        renameCommand.Arguments.Add Options.newName

        renameCommand.Action <-
            { new AsynchronousCommandLineAction() with
                /// Runs the configured-copy rename and reports receipt and local completion separately.
                override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) =
                    task {
                        let! result =
                            task {
                                try
                                    let correlationId = getCorrelationId parseResult

                                    let! result =
                                        LibrarySynchronization.rename
                                            (Current())
                                            correlationId
                                            (parseResult.GetValue Options.renamePath)
                                            (parseResult.GetValue Options.newName)
                                            cancellationToken

                                    return Ok(GraceReturnValue.Create result correlationId)
                                with
                                | ex -> return Error(GraceError.Create ex.Message (getCorrelationId parseResult))
                            }

                        match result with
                        | Ok value when not (hasSelect parseResult) ->
                            let output =
                                discriminatedUnionFromString<OutputFormat>(
                                    parseResult.GetValue Grace.CLI.Common.Options.output
                                )
                                    .Value

                            match output with
                            | Normal
                            | Verbose
                            | Minimal -> Console.Out.WriteLine(renameMessage value.ReturnValue)
                            | _ -> ()
                        | _ -> ()

                        let rendered = renderOutput parseResult result

                        return
                            match result with
                            | Ok result when result.ReturnValue.Outcome <> "completed" -> 1
                            | _ -> rendered
                    }
            }

        libraryCommand.Subcommands.Add renameCommand

        let syncCommand = Command("sync", "Synchronize Library files in this working copy.")

        for verb in [ "enable"; "run"; "status" ] do
            let command =
                Command(verb, $"Library synchronization {verb}.")
                |> addScopeOptions

            command.Action <-
                { new AsynchronousCommandLineAction() with
                    override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) =
                        task {
                            let! result = synchronizationHandler verb parseResult cancellationToken
                            return renderOutput parseResult result
                        }
                }

            syncCommand.Subcommands.Add command

        libraryCommand.Subcommands.Add syncCommand
        libraryCommand
