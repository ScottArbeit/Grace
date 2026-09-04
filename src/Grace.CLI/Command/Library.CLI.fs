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
open System.IO
open System.Security.Cryptography
open System.Text
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

    /// Downloads and applies one accepted remote file under the shared repository-root exclusion.
    let private applyRemoteFileChange
        (parseResult: ParseResult)
        (configuration: GraceConfiguration)
        (localState: LibraryLocalState.RepositoryState)
        (change: LibraryChangeDto)
        =
        task {
            let namespaceValue =
                change.Namespace
                |> Option.defaultWith (fun () -> invalidOp "Remote file change has no namespace state.")

            let content =
                change.Content
                |> Option.defaultWith (fun () -> invalidOp "Remote file change has no content state.")

            let readParameters = PrepareLibraryContentReadParameters()
            applyScope readParameters parseResult |> ignore
            readParameters.ItemId <- change.ItemId
            readParameters.ContentVersionId <- content.ContentVersionId

            let! grantResult = Libraries.PrepareContentRead readParameters

            let grant =
                grantResult
                |> Result.defaultWith (fun error -> invalidOp error.Error)

            let! downloadResult = Libraries.DownloadContent(grant.ReturnValue.GrantId, getCorrelationId parseResult)

            let bytes =
                downloadResult
                |> Result.defaultWith (fun error -> invalidOp error.Error)
                |> fun value -> value.ReturnValue

            let targetPath =
                Path.GetFullPath(Path.Combine(configuration.RootDirectory, namespaceValue.NormalizedPath.Replace('/', Path.DirectorySeparatorChar)))

            let rootPrefix =
                Path
                    .GetFullPath(configuration.RootDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar)
                + string Path.DirectorySeparatorChar

            if not (targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) then
                invalidOp "Accepted Library namespace escaped the configured working root."

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! _lease = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None

            let catalogParameters =
                GetLibraryCatalogParameters()
                |> fun value -> applyScope value parseResult

            let! catalogResult = Libraries.GetCatalog catalogParameters

            let catalog =
                catalogResult
                |> Result.defaultWith (fun error -> invalidOp error.Error)

            if catalog.ReturnValue.Version
               <> localState.LibraryCatalogVersion then
                invalidOp "Library catalog changed before remote filesystem publication."

            let! currentState = LibraryLocalState.readRepositoryState configuration.GraceStatusFile configuration.RepositoryId

            let currentState =
                currentState
                |> Option.defaultWith (fun () -> invalidOp "Library local state disappeared before remote publication.")

            if currentState.LibraryCatalogVersion
               <> change.LibraryCatalogVersion
               || currentState.AppliedCursor
                  <> localState.AppliedCursor then
                invalidOp "Library catalog or cursor predecessor changed before remote publication."

            let! pending = LibraryLocalState.readPendingRemoteFile configuration.GraceStatusFile configuration.RepositoryId change.OperationId

            if pending.IsNone then
                do!
                    LibraryLocalState.recordPending
                        configuration.GraceStatusFile
                        configuration.RepositoryId
                        change.OperationId
                        LibraryLocalState.OperationDirection.Remote
                        change.ChangeKind
                        (change.OperationId.ToString("N"))
                        change.LibraryCatalogVersion
                        (Some change.ItemId)
                        None
                        (Some namespaceValue.NormalizedPath)
                        None
                        None
                        localState.AppliedCursor
                        (Some change.Cursor)
                        currentState.CursorEpoch
                        (Some content.Blake3Hash)
                        (Some content.Sha256Hash)
                        (Some content.Size)

            if not (LibraryFilesystem.matchesContent targetPath content.Blake3Hash content.Sha256Hash content.Size) then
                let! ancestry = LibraryLocalState.readItemAncestry configuration.GraceStatusFile configuration.RepositoryId change.ItemId

                if File.Exists(targetPath) then
                    let target = LibraryFilesystem.stableRead targetPath

                    match ancestry with
                    | Some prior when target.Blake3Hash = prior.Blake3Hash -> ()
                    | _ -> invalidOp "Local Library target changed before remote publication."

                LibraryFilesystem.publishAtomic targetPath content.Blake3Hash content.Sha256Hash content.Size bytes

            let! durable = LibraryLocalState.readPendingRemoteFile configuration.GraceStatusFile configuration.RepositoryId change.OperationId

            match durable with
            | Some operation when operation.OperationState = "pendingFilesystem" ->
                do!
                    LibraryLocalState.markFilesystemPublished
                        configuration.GraceStatusFile
                        configuration.RepositoryId
                        change.OperationId
                        change.LibraryCatalogVersion
            | Some operation when operation.OperationState = "filesystemPublished" -> ()
            | _ -> invalidOp "Remote Library operation lost its durable pending evidence."

            do! LibraryLocalState.completeAcceptedFile configuration.GraceStatusFile configuration.RepositoryId change

            let! advanced =
                LibraryLocalState.tryAdvanceCursor
                    configuration.GraceStatusFile
                    configuration.RepositoryId
                    change.OperationId
                    change.LibraryCatalogVersion
                    (currentState.CursorEpoch
                     |> Option.defaultValue String.Empty)
                    (localState.AppliedCursor
                     |> Option.defaultValue String.Empty)
                    change.Cursor

            if not advanced then
                invalidOp "Terminal Library state could not CAS-advance its exact accepted cursor."
        }

    /// Derives a retry-stable operation identity from working-copy, path, and complete-byte identity.
    let private localOperationId workingCopyId normalizedPath blake3 =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Grace.Library.local.v1:{workingCopyId:D}:{normalizedPath}:{blake3}"))
        Guid(bytes[0..15])

    /// Commits one accepted local receipt to terminal SQLite state and then advances its exact cursor predecessor.
    let private completeLocalReceipt
        (configuration: GraceConfiguration)
        (state: LibraryLocalState.RepositoryState)
        (operationState: string)
        (change: LibraryChangeDto)
        =
        task {
            if operationState <> "terminal" then
                do!
                    LibraryLocalState.markServerAccepted
                        configuration.GraceStatusFile
                        configuration.RepositoryId
                        change.OperationId
                        state.LibraryCatalogVersion
                        change.Cursor

                do! LibraryLocalState.completeAcceptedFile configuration.GraceStatusFile configuration.RepositoryId change

            let! advanced =
                LibraryLocalState.tryAdvanceCursor
                    configuration.GraceStatusFile
                    configuration.RepositoryId
                    change.OperationId
                    state.LibraryCatalogVersion
                    (state.CursorEpoch
                     |> Option.defaultValue String.Empty)
                    (state.AppliedCursor
                     |> Option.defaultValue String.Empty)
                    change.Cursor

            if not advanced then
                invalidOp "Accepted local publication could not CAS-advance its exact cursor."
        }

    /// Replays an accepted operation receipt before temporary upload state or changed namespace state is needed.
    let private tryRecoverLocalPublication
        (parseResult: ParseResult)
        (configuration: GraceConfiguration)
        (state: LibraryLocalState.RepositoryState)
        operationId
        =
        task {
            let! recovery = LibraryLocalState.readRecoverableLocalOperation configuration.GraceStatusFile configuration.RepositoryId operationId

            match recovery with
            | None -> return false
            | Some operation when operation.OperationState = "terminal" ->
                let cursor =
                    operation.ServerCursor
                    |> Option.defaultWith (fun () -> invalidOp "Terminal local publication has no accepted cursor evidence.")

                let! advanced =
                    LibraryLocalState.tryAdvanceCursor
                        configuration.GraceStatusFile
                        configuration.RepositoryId
                        operationId
                        state.LibraryCatalogVersion
                        (state.CursorEpoch
                         |> Option.defaultValue String.Empty)
                        (state.AppliedCursor
                         |> Option.defaultValue String.Empty)
                        cursor

                if not advanced then
                    invalidOp "Terminal local publication could not resume its exact cursor CAS."

                return true
            | Some operation ->
                let parameters = GetLibraryOperationParameters()
                applyScope parameters parseResult |> ignore
                parameters.OperationId <- operationId

                match! Libraries.GetOperation parameters with
                | Error _ when operation.OperationState = "pendingServer" -> return false
                | Error error -> return invalidOp error.Error
                | Ok receipt ->
                    let change =
                        receipt.ReturnValue.Change
                        |> Option.defaultWith (fun () -> invalidOp "Accepted local operation receipt returned no change.")

                    do! completeLocalReceipt configuration state operation.OperationState change
                    return true
        }

    /// Publishes the first stable local file delta through the unchanged prepared-content and change routes.
    let private publishFirstLocalChange (parseResult: ParseResult) (configuration: GraceConfiguration) (state: LibraryLocalState.RepositoryState) =
        task {
            let catalogParameters =
                GetLibraryCatalogParameters()
                |> fun value -> applyScope value parseResult

            let! catalogResult = Libraries.GetCatalog catalogParameters

            let catalog =
                catalogResult
                |> Result.defaultWith (fun error -> invalidOp error.Error)

            if catalog.ReturnValue.Version
               <> state.LibraryCatalogVersion then
                invalidOp "Library catalog changed before local publication."

            let! ancestry = LibraryLocalState.readLiveFileAncestry configuration.GraceStatusFile configuration.RepositoryId

            let ancestryByPath =
                ancestry
                |> Array.map (fun item -> item.NormalizedPath, item)
                |> Map.ofArray

            let candidate =
                catalog.ReturnValue.Libraries
                |> Array.collect (fun library ->
                    let directory = Path.Combine(configuration.RootDirectory, library.Replace('/', Path.DirectorySeparatorChar))

                    if Directory.Exists(directory) then
                        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    else
                        Array.empty)
                |> Array.sort
                |> Array.tryPick (fun path ->
                    let normalized =
                        Path
                            .GetRelativePath(configuration.RootDirectory, path)
                            .Replace(Path.DirectorySeparatorChar, '/')

                    let content = LibraryFilesystem.stableRead path

                    match ancestryByPath |> Map.tryFind normalized with
                    | Some prior when prior.Blake3Hash = content.Blake3Hash -> None
                    | prior -> Some(path, normalized, content, prior))

            match candidate with
            | None -> return false
            | Some (path, normalizedPath, content, prior) ->
                let operationId = localOperationId state.WorkingCopyId normalizedPath content.Blake3Hash
                let! recovered = tryRecoverLocalPublication parseResult configuration state operationId

                if recovered then
                    return true
                else

                    let changeKind = if prior.IsSome then ChangeKind.UpdateContent else ChangeKind.CreateFile
                    let fileName = Path.GetFileName(path)

                    let libraryPath =
                        catalog.ReturnValue.Libraries
                        |> Array.find (fun library -> normalizedPath.StartsWith(library + "/", StringComparison.OrdinalIgnoreCase))

                    let parent = { Kind = "root"; LibraryPath = Some libraryPath; ItemId = None }
                    let slotParameters = GetLibraryNamespaceSlotParameters()
                    applyScope slotParameters parseResult |> ignore
                    slotParameters.Parent <- Some parent
                    slotParameters.Name <- fileName
                    let! slotResult = Libraries.GetNamespaceSlot slotParameters

                    let slot =
                        slotResult
                        |> Result.defaultWith (fun error -> invalidOp error.Error)

                    do!
                        LibraryLocalState.recordPending
                            configuration.GraceStatusFile
                            configuration.RepositoryId
                            operationId
                            LibraryLocalState.OperationDirection.Local
                            changeKind
                            (operationId.ToString("N"))
                            state.LibraryCatalogVersion
                            (prior |> Option.map (fun item -> item.ItemId))
                            (Some normalizedPath)
                            (Some normalizedPath)
                            None
                            None
                            state.AppliedCursor
                            None
                            state.CursorEpoch
                            (Some content.Blake3Hash)
                            (Some content.Sha256Hash)
                            (Some content.Size)

                    let prepare = PrepareLibraryContentParameters()
                    applyScope prepare parseResult |> ignore
                    prepare.OperationId <- operationId
                    prepare.Blake3Hash <- content.Blake3Hash
                    prepare.Sha256Hash <- content.Sha256Hash
                    prepare.Size <- content.Size
                    let! preparedResult = Libraries.PrepareContent prepare

                    let prepared =
                        preparedResult
                        |> Result.defaultWith (fun error -> invalidOp error.Error)

                    let stagingPath = Path.Combine(configuration.GraceDirectory, $"library-upload-{operationId:N}.tmp")

                    try
                        File.WriteAllBytes(stagingPath, content.Bytes)

                        let! upload =
                            LibraryManifestUpload.uploadPrepared
                                configuration
                                operationId
                                prepared.ReturnValue
                                normalizedPath
                                stagingPath
                                (getCorrelationId parseResult)

                        upload
                        |> Result.defaultWith (fun error -> invalidOp error.Error)
                        |> ignore

                        let submit = SubmitLibraryChangeParameters()
                        applyScope submit parseResult |> ignore
                        submit.OperationId <- operationId
                        submit.LibraryCatalogVersion <- state.LibraryCatalogVersion
                        submit.ChangeKind <- changeKind
                        submit.ItemKind <- ItemKind.File
                        submit.PreparedContentId <- Nullable prepared.ReturnValue.PreparedContentId

                        match prior with
                        | None ->
                            submit.CreationSlotExpectation <-
                                Some
                                    {
                                        Parent = parent
                                        Name = fileName
                                        ExpectedSlotVersion = slot.ReturnValue.SlotVersion
                                        ExpectedState = slot.ReturnValue.State
                                    }
                        | Some item ->
                            submit.ItemId <- Nullable item.ItemId
                            submit.ContentPrecondition <- Some { ItemId = item.ItemId; ExpectedContentVersionId = item.ContentVersionId }

                        let! receiptResult = Libraries.SubmitChange submit

                        let receipt =
                            receiptResult
                            |> Result.defaultWith (fun error -> invalidOp error.Error)

                        let change =
                            receipt.ReturnValue.Change
                            |> Option.defaultWith (fun () -> invalidOp "Accepted local publication returned no change.")

                        do! completeLocalReceipt configuration state "pendingServer" change
                        return true
                    finally
                        if File.Exists(stagingPath) then File.Delete(stagingPath)
        }

    /// Pulls after the durable cursor and applies accepted ordinary-file changes in repository order.
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
                        | Ok result ->
                            for change in result.ReturnValue.Changes do
                                if change.ItemKind <> ItemKind.File
                                   || (change.ChangeKind <> ChangeKind.CreateFile
                                       && change.ChangeKind <> ChangeKind.UpdateContent) then
                                    invalidOp "The Windows two-copy tracer accepts ordinary file create and content-update changes only."

                                do! applyRemoteFileChange parseResult configuration localState change

                            let! refreshed = LibraryLocalState.readRepositoryState configuration.GraceStatusFile configuration.RepositoryId
                            let! _ = publishFirstLocalChange parseResult configuration (refreshed |> Option.defaultValue localState)
                            ()

                            return! synchronizationStatusHandler parseResult
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
