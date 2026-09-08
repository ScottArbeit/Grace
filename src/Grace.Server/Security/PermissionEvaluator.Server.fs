namespace Grace.Server.Security

open Grace.Actors
open Grace.Shared
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open System
open System.Threading.Tasks

/// Defines the contract for igrace permission evaluator.
type IGracePermissionEvaluator =
    /// Defines the check async operation for implementers.
    abstract member CheckAsync:
        principalSet: Principal list * effectiveClaims: Set<string> * operation: Operation * resource: Resource -> Task<PermissionCheckResult>

/// Contains Grace Server permission evaluator defaults behavior and supporting helpers.
module PermissionEvaluatorDefaults =

    /// Loads RBAC assignments for the exact scope being evaluated.
    let getAssignmentsForScope (scope: Scope, correlationId: CorrelationId) =
        task {
            let scopeKey = AccessControl.getScopeKey scope
            let actorProxy = Extensions.ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId
            return! actorProxy.GetAssignments None correlationId
        }

    /// Loads repository path permissions when the authorization resource targets a repository path.
    let getPathPermissionsForRepository (repositoryId: RepositoryId, correlationId: CorrelationId) =
        task {
            let actorProxy = Extensions.ActorProxy.RepositoryPermission.CreateActorProxy repositoryId correlationId
            return! actorProxy.GetPathPermissions None correlationId
        }

/// Represents grace permission evaluator used by Grace Server APIs and background services.
type GracePermissionEvaluator
    (
        getAssignmentsForScope: Scope * CorrelationId -> Task<RoleAssignment list>,
        getPathPermissionsForRepository: RepositoryId * CorrelationId -> Task<PathPermission list>
    ) =

    new() = GracePermissionEvaluator(PermissionEvaluatorDefaults.getAssignmentsForScope, PermissionEvaluatorDefaults.getPathPermissionsForRepository)

    interface IGracePermissionEvaluator with
        /// Evaluates principals, scoped role assignments, and path permissions for one authorization decision.
        member _.CheckAsync(principalSet, effectiveClaims, operation, resource) =
            task {
                try
                    let correlationId = generateCorrelationId ()
                    let roleCatalog = RoleCatalog.getAll ()
                    let scopes = scopesForResource resource

                    let! assignments =
                        scopes
                        |> List.map (fun scope -> getAssignmentsForScope (scope, correlationId))
                        |> Task.WhenAll

                    let allAssignments = assignments |> Seq.collect id |> Seq.toList

                    let! pathPermissions =
                        match resource with
                        | Resource.Path (_, _, repositoryId, _) -> getPathPermissionsForRepository (repositoryId, correlationId)
                        | _ -> Task.FromResult List.empty

                    return checkPermission roleCatalog allAssignments pathPermissions principalSet effectiveClaims operation resource
                with
                | ex -> return Denied $"Authorization evaluation failed: {ex.Message}"
            }
