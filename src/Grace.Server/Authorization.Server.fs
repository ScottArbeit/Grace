namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared.Authorization
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Access
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Security.Claims
open System.Threading.Tasks

module Authorization =

    type IGracePermissionEvaluator =
        abstract member CheckAsync: Principal * Operation * Resource -> Task<PermissionCheckResult>

    type GracePermissionEvaluator() =
        let getRoleAssignmentsForOwner ownerId correlationId =
            task {
                let actor = Owner.CreateActorProxy ownerId correlationId
                let! assignments = actor.GetRoleAssignments correlationId
                return assignments |> Seq.toList
            }

        let getRoleAssignmentsForOrganization ownerId organizationId correlationId =
            task {
                let actor = Organization.CreateActorProxy organizationId correlationId
                let! assignments = actor.GetRoleAssignments correlationId
                let! ownerAssignments = getRoleAssignmentsForOwner ownerId correlationId
                return ownerAssignments @ (assignments |> Seq.toList)
            }

        let getRoleAssignmentsForRepository ownerId organizationId repositoryId correlationId =
            task {
                let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                let! repoAssignments = actor.GetRoleAssignments correlationId
                let! orgAssignments = getRoleAssignmentsForOrganization ownerId organizationId correlationId
                return orgAssignments @ (repoAssignments |> Seq.toList)
            }

        interface IGracePermissionEvaluator with
            member _.CheckAsync(principal, operation, resource) =
                task {
                    let correlationId = generateCorrelationId ()
                    let mutable assignments: RoleAssignment list = []
                    let mutable pathAcls: PathAce list = []

                    match resource with
                    | System -> ()
                    | Owner ownerId ->
                        let! ownerAssignments = getRoleAssignmentsForOwner ownerId correlationId
                        assignments <- ownerAssignments
                    | Org(ownerId, organizationId) ->
                        let! orgAssignments = getRoleAssignmentsForOrganization ownerId organizationId correlationId
                        assignments <- orgAssignments
                    | Repo(ownerId, organizationId, repositoryId) ->
                        let! repoAssignments = getRoleAssignmentsForRepository ownerId organizationId repositoryId correlationId
                        assignments <- repoAssignments
                    | Branch(ownerId, organizationId, repositoryId, _) ->
                        let! repoAssignments = getRoleAssignmentsForRepository ownerId organizationId repositoryId correlationId
                        assignments <- repoAssignments
                    | Path(ownerId, organizationId, repositoryId, _) ->
                        let! repoAssignments = getRoleAssignmentsForRepository ownerId organizationId repositoryId correlationId
                        assignments <- repoAssignments
                        let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                        let! acls = actor.GetPathAcls correlationId
                        pathAcls <- acls |> Seq.toList

                    let authState = { Roles = roleCatalog; Assignments = assignments; PathAcls = pathAcls }

                    return checkPermission authState principal operation resource
                }

    module PrincipalMapper =
        let tryGetPrincipal (claimsPrincipal: ClaimsPrincipal) =
            if
                isNull claimsPrincipal
                || isNull claimsPrincipal.Identity
                || not claimsPrincipal.Identity.IsAuthenticated
            then
                None
            else
                let claim = claimsPrincipal.FindFirst(Constants.Authentication.GraceUserIdClaim: string)

                if isNull claim || String.IsNullOrWhiteSpace(claim.Value) then
                    None
                else
                    Some(Principal.User claim.Value)

    let private result403Forbidden (context: HttpContext) (reason: string) =
        let graceError = GraceError.Create reason (getCorrelationId context)
        returnResult StatusCodes.Status403Forbidden graceError context

    let requiresPermission (operation: Operation) (resourceBuilder: HttpContext -> Task<Resource>) : HttpHandler =
        fun next context ->
            task {
                if
                    isNull context.User
                    || isNull context.User.Identity
                    || not context.User.Identity.IsAuthenticated
                then
                    return! RequestErrors.UNAUTHORIZED "Bearer" "Grace" "You must be logged in." next context
                else
                    match PrincipalMapper.tryGetPrincipal context.User with
                    | None -> return! result403Forbidden context "Missing grace_user_id claim."
                    | Some principal ->
                        let evaluator = context.GetService<IGracePermissionEvaluator>()
                        let! resource = resourceBuilder context
                        let! result = evaluator.CheckAsync(principal, operation, resource)

                        match result with
                        | Allowed _ -> return! next context
                        | Denied reason -> return! result403Forbidden context reason
            }
