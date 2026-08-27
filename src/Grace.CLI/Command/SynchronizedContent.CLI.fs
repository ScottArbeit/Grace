namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters.SynchronizedContent
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.SynchronizedContent
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

/// Defines the remote-only synchronized-root command tree without activating local synchronization participation.
module SynchronizedContentCommand =

    /// Defines options shared by the synchronized-root handlers.
    module private Options =
        let rootPath = Option<string>("--root", Required = true, Description = "Repository-relative synchronized root path.")
        let expectedVersion = Option<Guid>("--expected-version", Required = true, Description = "Exact current root-configuration version <Guid>.")
        let operationId = Option<Guid>("--operation-id", Required = true, Description = "Idempotent root-operation identity <Guid>.")

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

    /// Applies the resolved repository identity and correlation ID to one synchronized request.
    let private applyScope (parameters: #SynchronizedContentParameters) (parseResult: ParseResult) =
        let graceIds = getNormalizedIdsAndNames parseResult
        parameters.OwnerId <- graceIds.OwnerIdString
        parameters.OwnerName <- graceIds.OwnerName
        parameters.OrganizationId <- graceIds.OrganizationIdString
        parameters.OrganizationName <- graceIds.OrganizationName
        parameters.RepositoryId <- graceIds.RepositoryIdString
        parameters.RepositoryName <- graceIds.RepositoryName
        parameters.CorrelationId <- graceIds.CorrelationId
        parameters

    /// Reads the persisted synchronized-root configuration through the remote SDK.
    let internal getRootsHandler parseResult =
        task {
            try
                let parameters =
                    GetSynchronizedRootConfigurationParameters()
                    |> fun value -> applyScope value parseResult

                return! SynchronizedContent.GetRoots parameters
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Lists the current deterministic synchronized-root paths through the remote SDK.
    let internal listRootsHandler parseResult =
        task {
            try
                let parameters =
                    ListSynchronizedRootsParameters()
                    |> fun value -> applyScope value parseResult

                return! SynchronizedContent.ListRoots parameters
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Sends one exact-version root add or remove operation through the remote SDK.
    let private changeRootHandler addRoot (parseResult: ParseResult) : Task<GraceResult<SynchronizedRootMutationResultDto>> =
        task {
            try
                let expectedVersion = parseResult.GetValue Options.expectedVersion
                let rootPath = parseResult.GetValue Options.rootPath
                let operationId = parseResult.GetValue Options.operationId

                if addRoot then
                    let parameters =
                        AddSynchronizedRootParameters()
                        |> fun value -> applyScope value parseResult

                    parameters.ExpectedVersion <- expectedVersion
                    parameters.RootPath <- rootPath
                    parameters.OperationId <- operationId
                    return! SynchronizedContent.AddRoot parameters
                else
                    let parameters =
                        RemoveSynchronizedRootParameters()
                        |> fun value -> applyScope value parseResult

                    parameters.ExpectedVersion <- expectedVersion
                    parameters.RootPath <- rootPath
                    parameters.OperationId <- operationId
                    return! SynchronizedContent.RemoveRoot parameters
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Dispatches `sync roots get` and renders the standard Grace result envelope.
    type GetRoots() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-configuration read action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = getRootsHandler parseResult
                return renderOutput parseResult result
            }

    /// Dispatches `sync roots list` and renders the standard Grace result envelope.
    type ListRoots() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-list read action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = listRootsHandler parseResult
                return renderOutput parseResult result
            }

    /// Dispatches `sync roots add` and renders its typed accepted, stale, unchanged, or rejected result.
    type AddRoot() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-add action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = changeRootHandler true parseResult
                return renderOutput parseResult result
            }

    /// Dispatches `sync roots remove` and renders its typed accepted, stale, unchanged, or rejected result.
    type RemoveRoot() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous root-remove action.
        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) =
            task {
                let! result = changeRootHandler false parseResult
                return renderOutput parseResult result
            }

    /// Builds the remote-only `sync roots` command tree accepted by Issue #1038.
    let Build =
        let addScopeOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId

        let addMutationOptions command =
            command
            |> addOption Options.expectedVersion
            |> addOption Options.rootPath
            |> addOption Options.operationId
            |> addScopeOptions

        let syncCommand = Command("sync", "Manage remote synchronized-content configuration.")
        syncCommand.Aliases.Add("synchronize")
        let rootsCommand = Command("roots", "Manage repository synchronized roots.")

        let getCommand =
            Command("get", "Get the persisted synchronized-root configuration.")
            |> addScopeOptions

        getCommand.Action <- GetRoots()
        rootsCommand.Subcommands.Add getCommand

        let listCommand =
            Command("list", "List synchronized-root paths.")
            |> addScopeOptions

        listCommand.Action <- ListRoots()
        rootsCommand.Subcommands.Add listCommand

        let addCommand =
            Command("add", "Add one empty repository-relative synchronized root.")
            |> addMutationOptions

        addCommand.Action <- AddRoot()
        rootsCommand.Subcommands.Add addCommand

        let removeCommand =
            Command("remove", "Remove one empty synchronized root.")
            |> addMutationOptions

        removeCommand.Action <- RemoveRoot()
        rootsCommand.Subcommands.Add removeCommand

        syncCommand.Subcommands.Add rootsCommand
        syncCommand
