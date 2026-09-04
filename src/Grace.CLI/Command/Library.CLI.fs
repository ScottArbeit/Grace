namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI
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

/// Defines the Library catalog commands and the explicit Windows synchronization tracer.
module LibraryCommand =

    /// Reports the durable participation and cursor state of one Windows working copy.
    [<CLIMutable>]
    type LibrarySynchronizationStatus =
        {
            Enabled: bool
            State: string
            LibraryCatalogVersion: Guid option
            CursorEpoch: string option
            AppliedCursor: string option
        }

    /// Defines options shared by the Library handlers.
    module private Options =
        let libraryPath = Argument<string>("path", Description = "Repository-relative Library path.")
        let expectedVersion = Option<Guid>("--expected-version", Required = true, Description = "Exact current Library catalog version <Guid>.")
        let operationId = Option<Guid>("--operation-id", Required = true, Description = "Idempotent Library operation identity <Guid>.")

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

    /// Sends one exact-version root add or remove operation through the remote SDK.
    let private changeLibraryHandler addLibrary (parseResult: ParseResult) : Task<GraceResult<LibraryCatalogChangeResultDto>> =
        task {
            try
                let expectedVersion = parseResult.GetValue Options.expectedVersion
                let libraryPath = parseResult.GetValue Options.libraryPath
                let operationId = parseResult.GetValue Options.operationId

                if addLibrary then
                    let parameters =
                        AddLibraryParameters()
                        |> fun value -> applyScope value parseResult

                    parameters.ExpectedVersion <- expectedVersion
                    parameters.LibraryPath <- libraryPath
                    parameters.OperationId <- operationId
                    return! Libraries.AddLibrary parameters
                else
                    let parameters =
                        RemoveLibraryParameters()
                        |> fun value -> applyScope value parseResult

                    parameters.ExpectedVersion <- expectedVersion
                    parameters.LibraryPath <- libraryPath
                    parameters.OperationId <- operationId
                    return! Libraries.RemoveLibrary parameters
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Starts the immutable empty-Library baseline and durably enables this Windows working copy.
    let internal enableSynchronizationHandler (parseResult: ParseResult) : Task<GraceResult<LibrarySynchronizationStatus>> =
        task {
            try
                let parameters =
                    StartLibraryBootstrapParameters()
                    |> fun value -> applyScope value parseResult

                match! Libraries.StartBootstrap parameters with
                | Error error -> return Error error
                | Ok result ->
                    let page = result.ReturnValue

                    if page.Items.Length <> 0
                       || page.NextPageToken.IsSome then
                        return
                            Error(GraceError.Create "The Windows two-copy tracer requires an initially empty Library baseline." (getCorrelationId parseResult))
                    else
                        let configuration = Current()
                        let! priorState = LibraryLocalState.readRepositoryState configuration.GraceStatusFile page.LibraryCatalog.RepositoryId

                        let workingCopyId =
                            priorState
                            |> Option.map (fun state -> state.WorkingCopyId)
                            |> Option.defaultWith Guid.NewGuid

                        do!
                            LibraryLocalState.enable
                                configuration.GraceStatusFile
                                page.LibraryCatalog.RepositoryId
                                workingCopyId
                                page.LibraryCatalog.Version
                                page.LibraryCatalog.Libraries
                                page.CursorEpoch
                                page.BoundaryCursor

                        configuration.LibrarySynchronizationEnabled <- true
                        updateConfiguration configuration

                        return
                            Ok(
                                GraceReturnValue.Create
                                    {
                                        Enabled = true
                                        State = "current"
                                        LibraryCatalogVersion = Some page.LibraryCatalog.Version
                                        CursorEpoch = Some page.CursorEpoch
                                        AppliedCursor = Some page.BoundaryCursor
                                    }
                                    (getCorrelationId parseResult)
                            )
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Reads synchronization status from configuration and the existing local SQLite database.
    let internal synchronizationStatusHandler (parseResult: ParseResult) : Task<GraceResult<LibrarySynchronizationStatus>> =
        task {
            try
                let configuration = Current()
                let! state = LibraryLocalState.readRepositoryState configuration.GraceStatusFile configuration.RepositoryId

                return
                    Ok(
                        GraceReturnValue.Create
                            {
                                Enabled = configuration.LibrarySynchronizationEnabled
                                State =
                                    state
                                    |> Option.map (fun value -> value.LifecycleState)
                                    |> Option.defaultValue "disabled"
                                LibraryCatalogVersion =
                                    state
                                    |> Option.map (fun value -> value.LibraryCatalogVersion)
                                CursorEpoch =
                                    state
                                    |> Option.bind (fun value -> value.CursorEpoch)
                                AppliedCursor =
                                    state
                                    |> Option.bind (fun value -> value.AppliedCursor)
                            }
                            (getCorrelationId parseResult)
                    )
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Pulls after the durable cursor and reports current only when the server has no accepted work to apply.
    let internal runSynchronizationHandler (parseResult: ParseResult) : Task<GraceResult<LibrarySynchronizationStatus>> =
        task {
            try
                let configuration = Current()

                if not configuration.LibrarySynchronizationEnabled then
                    return Error(GraceError.Create "Library synchronization is not enabled for this working copy." (getCorrelationId parseResult))
                else
                    let! state = LibraryLocalState.readRepositoryState configuration.GraceStatusFile configuration.RepositoryId

                    match state with
                    | None -> return Error(GraceError.Create "Library synchronization has no durable local baseline." (getCorrelationId parseResult))
                    | Some localState ->
                        let parameters =
                            GetLibraryChangesParameters()
                            |> fun value -> applyScope value parseResult

                        parameters.AfterCursor <-
                            localState.AppliedCursor
                            |> Option.defaultValue String.Empty

                        match! Libraries.GetChanges parameters with
                        | Error error -> return Error error
                        | Ok result when result.ReturnValue.Changes.Length = 0 -> return! synchronizationStatusHandler parseResult
                        | Ok _ ->
                            return Error(GraceError.Create "The accepted change page requires the filesystem publication stage." (getCorrelationId parseResult))
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

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

    /// Dispatches one typed synchronization handler through the standard Grace output envelope.
    type SynchronizationAction<'T>(handler: ParseResult -> Task<GraceResult<'T>>) =
        inherit AsynchronousCommandLineAction()

        /// Runs the selected synchronization action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = handler parseResult
                return renderOutput parseResult result
            }

    /// Builds the `grace library` catalog and nested synchronization command tree.
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

        let syncCommand = Command("sync", "Synchronize configured Libraries on this Windows working copy.")

        let enableCommand =
            Command("enable", "Enable Library synchronization from an immutable empty baseline.")
            |> addScopeOptions

        enableCommand.Action <- SynchronizationAction<LibrarySynchronizationStatus>(enableSynchronizationHandler)
        syncCommand.Subcommands.Add enableCommand

        let runCommand =
            Command("run", "Run one durable Library synchronization pass.")
            |> addScopeOptions

        runCommand.Action <- SynchronizationAction<LibrarySynchronizationStatus>(runSynchronizationHandler)
        syncCommand.Subcommands.Add runCommand

        let statusCommand = Command("status", "Report durable Library synchronization status.")
        statusCommand.Action <- SynchronizationAction<LibrarySynchronizationStatus>(synchronizationStatusHandler)
        syncCommand.Subcommands.Add statusCommand
        libraryCommand.Subcommands.Add syncCommand

        libraryCommand
