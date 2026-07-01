namespace Grace.Server.Security

open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Threading.Tasks

/// Contains Grace Server creator scope admin grant behavior and supporting helpers.
module CreatorScopeAdminGrant =

    /// Represents creator admin grant result used by Grace Server APIs and background services.
    type CreatorAdminGrantResult =
        | InheritedAdminAlreadyApplies
        | DirectGrantPersisted

    /// Represents admin check used by Grace Server APIs and background services.
    type AdminCheck = Principal list -> Set<string> -> Operation -> Resource -> Task<PermissionCheckResult>

    /// Represents grant creator admin used by Grace Server APIs and background services.
    type GrantCreatorAdmin = RoleAssignment -> Task<GraceResult<RoleAssignment list>>

    /// Computes resource for scope data used by Grace Server.
    let internal resourceForScope scope =
        match scope with
        | Scope.System -> Resource.System
        | Scope.Owner ownerId -> Resource.Owner ownerId
        | Scope.Organization (ownerId, organizationId) -> Resource.Organization(ownerId, organizationId)
        | Scope.Repository (ownerId, organizationId, repositoryId) -> Resource.Repository(ownerId, organizationId, repositoryId)
        | Scope.Branch (ownerId, organizationId, repositoryId, branchId) -> Resource.Branch(ownerId, organizationId, repositoryId, branchId)

    /// Selects the administrative RBAC operation needed for the created scope.
    let internal adminOperationForScope scope =
        match scope with
        | Scope.System -> Operation.SystemAdmin
        | Scope.Owner _ -> Operation.OwnerAdmin
        | Scope.Organization _ -> Operation.OrganizationAdmin
        | Scope.Repository _ -> Operation.RepositoryAdmin
        | Scope.Branch _ -> Operation.BranchAdmin

    /// Selects the administrative RBAC role granted for the created scope.
    let internal adminRoleForScope scope =
        match scope with
        | Scope.System -> "SystemAdmin"
        | Scope.Owner _ -> "OwnerAdmin"
        | Scope.Organization _ -> "OrganizationAdmin"
        | Scope.Repository _ -> "RepositoryAdmin"
        | Scope.Branch _ -> "BranchAdmin"

    /// Ensures creator admin core before the handler returns success.
    let internal ensureCreatorAdminCore
        (correlationId: CorrelationId)
        (creatorUserId: string option)
        (principals: Principal list)
        (effectiveClaims: Set<string>)
        (scope: Scope)
        (checkAdmin: AdminCheck)
        (grantCreatorAdmin: GrantCreatorAdmin)
        =
        task {
            match creatorUserId with
            | None ->
                return Error(GraceError.Create "Cannot grant creator admin because the authenticated principal has no mapped Grace user id." correlationId)
            | Some userId when String.IsNullOrWhiteSpace userId ->
                return Error(GraceError.Create "Cannot grant creator admin because the authenticated principal has no mapped Grace user id." correlationId)
            | Some userId ->
                let operation = adminOperationForScope scope
                let resource = resourceForScope scope

                match! checkAdmin principals effectiveClaims operation resource with
                | Allowed _ -> return Ok InheritedAdminAlreadyApplies
                | Denied _ ->
                    let creatorPrincipal = { PrincipalType = PrincipalType.User; PrincipalId = userId }

                    let assignment =
                        {
                            Principal = creatorPrincipal
                            Scope = scope
                            RoleId = adminRoleForScope scope
                            Source = "scope-create"
                            SourceDetail = Some "creator-admin"
                            CreatedAt = getCurrentInstant ()
                        }

                    match! grantCreatorAdmin assignment with
                    | Error error -> return Error error
                    | Ok _ ->
                        match! checkAdmin [ creatorPrincipal ] Set.empty operation resource with
                        | Allowed _ -> return Ok DirectGrantPersisted
                        | Denied reason ->
                            return
                                Error(
                                    GraceError.Create
                                        $"Creator admin grant for role '{assignment.RoleId}' on the new scope could not be proven: {reason}"
                                        correlationId
                                )
        }

    /// Ensures creator admin for created scope before the handler returns success.
    let ensureCreatorAdminForCreatedScope (context: HttpContext) (scope: Scope) =
        let correlationId = getCorrelationId context
        let creatorUserId = PrincipalMapper.tryGetUserId context.User
        let principals = PrincipalMapper.getPrincipals context.User
        let effectiveClaims = PrincipalMapper.getEffectiveClaims context.User

        /// Checks admin through the authorization actor.
        let checkAdmin principals effectiveClaims operation resource =
            task {
                let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                return! evaluator.CheckAsync(principals, effectiveClaims, operation, resource)
            }

        /// Grants creator admin after verifying the created scope.
        let grantCreatorAdmin assignment =
            task {
                let scopeKey = AccessControl.getScopeKey assignment.Scope
                let actorProxy = Grace.Actors.Extensions.ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId
                return! actorProxy.Handle (AccessControlCommand.GrantRole assignment) (createMetadata context)
            }

        ensureCreatorAdminCore correlationId creatorUserId principals effectiveClaims scope checkAdmin grantCreatorAdmin
