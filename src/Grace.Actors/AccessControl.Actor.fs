namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module AccessControl =

    let ActorName = ActorName.AccessControl

    [<GenerateSerializer>]
    type AccessControlState = { Assignments: RoleAssignment list }

    module AccessControlState =
        let Empty = { Assignments = [] }

    let getScopeKey (scope: Scope) =
        match scope with
        | Scope.System -> "system"
        | Scope.Owner ownerId -> $"owner:{ownerId}"
        | Scope.Organization (ownerId, organizationId) -> $"org:{ownerId}:{organizationId}"
        | Scope.Repository (ownerId, organizationId, repositoryId) -> $"repo:{ownerId}:{organizationId}:{repositoryId}"
        | Scope.Branch (ownerId, organizationId, repositoryId, branchId) -> $"branch:{ownerId}:{organizationId}:{repositoryId}:{branchId}"

    type AccessControlActor([<PersistentState(StateName.AccessControl, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<AccessControlState>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("AccessControl.Actor")

        let mutable accessControlState = AccessControlState.Empty
        let mutable correlationId: CorrelationId = String.Empty

        override this.OnActivateAsync(ct) =
            task {
                accessControlState <- if state.RecordExists then state.State else AccessControlState.Empty

                let isSystemScope =
                    this.GetPrimaryKeyString()
                        .Equals(getScopeKey Scope.System, StringComparison.OrdinalIgnoreCase)

                if isSystemScope && accessControlState.Assignments.IsEmpty then
                    let parseList (value: string) =
                        if String.IsNullOrWhiteSpace value then
                            []
                        else
                            value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
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

                    if (not bootstrapUsers.IsEmpty) || (not bootstrapGroups.IsEmpty) then
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

        member private this.ValidateScope (scope: Scope) (correlationId: CorrelationId) =
            let expectedKey = getScopeKey scope
            let actualKey = this.GetPrimaryKeyString()

            if expectedKey = actualKey then
                Ok()
            else
                Error(GraceError.Create $"AccessControl scope mismatch. Expected '{expectedKey}', got '{actualKey}'." correlationId)

        member private this.SaveState() =
            task {
                state.State <- accessControlState

                if accessControlState.Assignments |> List.isEmpty then
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.ClearStateAsync())
                else
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.WriteStateAsync())
            }

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
            member this.Handle command metadata =
                match command with
                | AccessControlCommand.GrantRole assignment -> this.GrantRole assignment metadata
                | AccessControlCommand.RevokeRole (principal, roleId) -> this.RevokeRole principal roleId metadata
                | AccessControlCommand.ListAssignments principal -> this.ListAssignments principal metadata

            member this.GetAssignments principal correlationId =
                let filtered =
                    match principal with
                    | None -> accessControlState.Assignments
                    | Some value ->
                        accessControlState.Assignments
                        |> List.filter (fun assignment -> assignment.Principal = value)

                filtered |> returnTask
