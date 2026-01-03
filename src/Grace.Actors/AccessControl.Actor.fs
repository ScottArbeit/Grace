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
        | Scope.Organization(ownerId, organizationId) -> $"org:{ownerId}:{organizationId}"
        | Scope.Repository(ownerId, organizationId, repositoryId) -> $"repo:{ownerId}:{organizationId}:{repositoryId}"
        | Scope.Branch(ownerId, organizationId, repositoryId, branchId) -> $"branch:{ownerId}:{organizationId}:{repositoryId}:{branchId}"

    type AccessControlActor([<PersistentState(StateName.AccessControl, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<AccessControlState>)
        =
        inherit Grain()

        let log = loggerFactory.CreateLogger("AccessControl.Actor")

        let mutable accessControlState = AccessControlState.Empty
        let mutable correlationId: CorrelationId = String.Empty

        override this.OnActivateAsync(ct) =
            accessControlState <- if state.RecordExists then state.State else AccessControlState.Empty

            Task.CompletedTask

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
                | AccessControlCommand.RevokeRole(principal, roleId) -> this.RevokeRole principal roleId metadata
                | AccessControlCommand.ListAssignments principal -> this.ListAssignments principal metadata

            member this.GetAssignments principal correlationId =
                let filtered =
                    match principal with
                    | None -> accessControlState.Assignments
                    | Some value ->
                        accessControlState.Assignments
                        |> List.filter (fun assignment -> assignment.Principal = value)

                filtered |> returnTask
