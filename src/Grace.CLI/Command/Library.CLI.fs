namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
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

/// Defines the remote-only Library command tree without activating local synchronization participation.
module LibraryCommand =

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

    /// Builds the remote-only `grace library` command tree accepted by Issue #1038.
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

        libraryCommand
