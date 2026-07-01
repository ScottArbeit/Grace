namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Groups Orleans actor helpers for repository permission keys, proxies, state, or workflow transitions.
module RepositoryPermission =

    let ActorName = ActorName.RepositoryPermission

    /// Stores durable state for repository permission state.
    [<GenerateSerializer>]
    type RepositoryPermissionState = { PathPermissions: PathPermission list }

    /// Groups Orleans actor helpers for repository permission state keys, proxies, state, or workflow transitions.
    module RepositoryPermissionState =
        let Empty = { PathPermissions = [] }

    /// Implements the Orleans grain for repository permission actor.
    type RepositoryPermissionActor
        (
            [<PersistentState(StateName.RepositoryPermission, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<RepositoryPermissionState>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("RepositoryPermission.Actor")

        let mutable permissionState = RepositoryPermissionState.Empty

        override this.OnActivateAsync(ct) =
            permissionState <- if state.RecordExists then state.State else RepositoryPermissionState.Empty

            Task.CompletedTask

        /// Persists the updated RepositoryPermission actor state through the Orleans storage provider.
        member private this.SaveState() =
            task {
                state.State <- permissionState

                if permissionState.PathPermissions |> List.isEmpty then
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.ClearStateAsync())
                else
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.WriteStateAsync())
            }

        /// Stores upsert data in the RepositoryPermission actor state.
        member private this.Upsert (pathPermission: PathPermission) (metadata: EventMetadata) =
            task {
                let normalizedPath = normalizeFilePath pathPermission.Path
                let normalizedPermission = { pathPermission with Path = normalizedPath }

                let updated =
                    permissionState.PathPermissions
                    |> List.filter (fun existing -> normalizeFilePath existing.Path <> normalizedPath)

                permissionState <- { permissionState with PathPermissions = normalizedPermission :: updated }
                do! this.SaveState()

                let returnValue = GraceReturnValue.Create permissionState.PathPermissions metadata.CorrelationId
                return Ok returnValue
            }

        /// Removes an appearance entry from this RepositoryPermission actor's sorted set.
        member private this.Remove (path: RelativePath) (metadata: EventMetadata) =
            task {
                let normalizedPath = normalizeFilePath path

                let updated =
                    permissionState.PathPermissions
                    |> List.filter (fun existing -> normalizeFilePath existing.Path <> normalizedPath)

                permissionState <- { permissionState with PathPermissions = updated }
                do! this.SaveState()

                let returnValue = GraceReturnValue.Create permissionState.PathPermissions metadata.CorrelationId
                return Ok returnValue
            }

        /// Returns the RepositoryPermission records tracked by this actor.
        member private this.List (pathFilter: RelativePath option) (metadata: EventMetadata) =
            task {
                let filtered =
                    match pathFilter with
                    | None -> permissionState.PathPermissions
                    | Some value ->
                        let normalizedPath = normalizeFilePath value

                        permissionState.PathPermissions
                        |> List.filter (fun existing -> normalizeFilePath existing.Path = normalizedPath)

                let returnValue = GraceReturnValue.Create filtered metadata.CorrelationId
                return Ok returnValue
            }

        interface IRepositoryPermissionActor with
            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                match command with
                | RepositoryPermissionCommand.UpsertPathPermission pathPermission -> this.Upsert pathPermission metadata
                | RepositoryPermissionCommand.RemovePathPermission path -> this.Remove path metadata
                | RepositoryPermissionCommand.ListPathPermissions path -> this.List path metadata

            /// Returns path permissions data from the RepositoryPermission actor state or related storage.
            member this.GetPathPermissions pathFilter correlationId =
                let filtered =
                    match pathFilter with
                    | None -> permissionState.PathPermissions
                    | Some value ->
                        let normalizedPath = normalizeFilePath value

                        permissionState.PathPermissions
                        |> List.filter (fun existing -> normalizeFilePath existing.Path = normalizedPath)

                filtered |> returnTask
