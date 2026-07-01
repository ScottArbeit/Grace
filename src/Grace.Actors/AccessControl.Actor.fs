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
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for access control keys, proxies, state, or workflow transitions.
module AccessControl =

    let ActorName = ActorName.AccessControl

    /// Stores durable state for access control state.
    [<GenerateSerializer>]
    type AccessControlState = { Assignments: RoleAssignment list }

    /// Groups Orleans actor helpers for access control state keys, proxies, state, or workflow transitions.
    module AccessControlState =
        let Empty = { Assignments = [] }

    /// Converts an authorization scope into the stable key used for access-control lookups.
    let getScopeKey (scope: Scope) =
        match scope with
        | Scope.System -> "system"
        | Scope.Owner ownerId -> $"owner:{ownerId}"
        | Scope.Organization (ownerId, organizationId) -> $"org:{ownerId}:{organizationId}"
        | Scope.Repository (ownerId, organizationId, repositoryId) -> $"repo:{ownerId}:{organizationId}:{repositoryId}"
        | Scope.Branch (ownerId, organizationId, repositoryId, branchId) -> $"branch:{ownerId}:{organizationId}:{repositoryId}:{branchId}"

    /// Implements the Orleans grain for access control actor.
    type AccessControlActor([<PersistentState(StateName.AccessControl, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<AccessControlState>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("AccessControl.Actor")

        let mutable accessControlState = AccessControlState.Empty
        let mutable correlationId: CorrelationId = String.Empty

        override this.OnActivateAsync(ct) =
            task {
                accessControlState <- if state.RecordExists then state.State else AccessControlState.Empty

                let isSystemScope =
                    this
                        .GetPrimaryKeyString()
                        .Equals(getScopeKey Scope.System, StringComparison.OrdinalIgnoreCase)

                if isSystemScope
                   && accessControlState.Assignments.IsEmpty then
                    /// Splits a comma-delimited metadata value into normalized role or scope entries.
                    let parseList (value: string) =
                        if String.IsNullOrWhiteSpace value then
                            []
                        else
                            value.Split(
                                ';',
                                StringSplitOptions.RemoveEmptyEntries
                                ||| StringSplitOptions.TrimEntries
                            )
                            |> Seq.map (fun item -> item.Trim())
                            |> Seq.filter (fun item -> not (String.IsNullOrWhiteSpace item))
                            |> Seq.distinct
                            |> Seq.toList

                    let bootstrapUsers =
                        Environment.GetEnvironmentVariable(EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers)
                        |> parseList

                    let bootstrapGroups =
                        Environment.GetEnvironmentVariable(EnvironmentVariables.GraceAuthzBootstrapSystemAdminGroups)
                        |> parseList

                    if (not bootstrapUsers.IsEmpty)
                       || (not bootstrapGroups.IsEmpty) then
                        let now = getCurrentInstant ()
                        let sourceDetailParts = List<string>()

                        if not bootstrapUsers.IsEmpty then
                            sourceDetailParts.Add($"users={String.Join(';', bootstrapUsers)}")

                        if not bootstrapGroups.IsEmpty then
                            sourceDetailParts.Add($"groups={String.Join(';', bootstrapGroups)}")

                        let sourceDetail =
                            if sourceDetailParts.Count = 0 then
                                None
                            else
                                Some(String.Join("; ", sourceDetailParts))

                        let userAssignments =
                            bootstrapUsers
                            |> List.map (fun userId ->
                                {
                                    Principal = { PrincipalType = PrincipalType.User; PrincipalId = userId }
                                    Scope = Scope.System
                                    RoleId = "SystemAdmin"
                                    Source = "bootstrap"
                                    SourceDetail = sourceDetail
                                    CreatedAt = now
                                })

                        let groupAssignments =
                            bootstrapGroups
                            |> List.map (fun groupId ->
                                {
                                    Principal = { PrincipalType = PrincipalType.Group; PrincipalId = groupId }
                                    Scope = Scope.System
                                    RoleId = "SystemAdmin"
                                    Source = "bootstrap"
                                    SourceDetail = sourceDetail
                                    CreatedAt = now
                                })

                        accessControlState <- { accessControlState with Assignments = userAssignments @ groupAssignments }
                        do! this.SaveState()

                        log.LogWarning(
                            "{CurrentInstant}: Bootstrapped {UserCount} system admin user(s) and {GroupCount} group(s) for system scope.",
                            getCurrentInstantExtended (),
                            userAssignments.Length,
                            groupAssignments.Length
                        )

                return ()
            }
            :> Task

        /// Validates scope before the operation continues.
        member private this.ValidateScope (scope: Scope) (correlationId: CorrelationId) =
            let expectedKey = getScopeKey scope
            let actualKey = this.GetPrimaryKeyString()

            if expectedKey = actualKey then
                Ok()
            else
                Error(GraceError.Create $"AccessControl scope mismatch. Expected '{expectedKey}', got '{actualKey}'." correlationId)

        /// Persists the updated AccessControl actor state through the Orleans storage provider.
        member private this.SaveState() =
            task {
                state.State <- accessControlState

                if accessControlState.Assignments |> List.isEmpty then
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.ClearStateAsync())
                else
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.WriteStateAsync())
            }

        /// Adds a role assignment event when the principal does not already hold the role.
        member private this.GrantRole (assignment: RoleAssignment) (metadata: EventMetadata) =
            task {
                correlationId <- metadata.CorrelationId

                match this.ValidateScope assignment.Scope metadata.CorrelationId with
                | Error error -> return Error error
                | Ok _ ->
                    let assignments =
                        accessControlState.Assignments
                        |> List.filter (fun existing ->
                            not (
                                existing.Principal = assignment.Principal
                                && existing.RoleId.Equals(assignment.RoleId, StringComparison.OrdinalIgnoreCase)
                            ))

                    accessControlState <- { accessControlState with Assignments = assignment :: assignments }
                    do! this.SaveState()

                    let returnValue = GraceReturnValue.Create accessControlState.Assignments metadata.CorrelationId
                    return Ok returnValue
            }

        /// Records a role revocation event when the principal currently holds the role.
        member private this.RevokeRole (principal: Principal) (roleId: RoleId) (metadata: EventMetadata) =
            task {
                correlationId <- metadata.CorrelationId

                let assignments =
                    accessControlState.Assignments
                    |> List.filter (fun existing ->
                        not (
                            existing.Principal = principal
                            && existing.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase)
                        ))

                accessControlState <- { accessControlState with Assignments = assignments }
                do! this.SaveState()

                let returnValue = GraceReturnValue.Create accessControlState.Assignments metadata.CorrelationId
                return Ok returnValue
            }

        /// Filters role assignments by principal and projects the access-control response.
        member private this.ListAssignments (principal: Principal option) (metadata: EventMetadata) =
            task {
                correlationId <- metadata.CorrelationId

                let filtered =
                    match principal with
                    | None -> accessControlState.Assignments
                    | Some value ->
                        accessControlState.Assignments
                        |> List.filter (fun assignment -> assignment.Principal = value)

                let returnValue = GraceReturnValue.Create filtered metadata.CorrelationId
                return Ok returnValue
            }

        interface IAccessControlActor with
            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                match command with
                | AccessControlCommand.GrantRole assignment -> this.GrantRole assignment metadata
                | AccessControlCommand.RevokeRole (principal, roleId) -> this.RevokeRole principal roleId metadata
                | AccessControlCommand.ListAssignments principal -> this.ListAssignments principal metadata

            /// Returns access-control assignments visible to the requested principal filter.
            member this.GetAssignments principal correlationId =
                let filtered =
                    match principal with
                    | None -> accessControlState.Assignments
                    | Some value ->
                        accessControlState.Assignments
                        |> List.filter (fun assignment -> assignment.Principal = value)

                filtered |> returnTask
