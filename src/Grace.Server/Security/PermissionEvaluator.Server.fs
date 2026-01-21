namespace Grace.Server.Security

open Grace.Actors
open Grace.Shared
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open System
open System.Threading.Tasks

type IGracePermissionEvaluator =
    abstract member CheckAsync:
        principalSet: Principal list * effectiveClaims: Set<string> * operation: Operation * resource: Resource -> Task<PermissionCheckResult>

module PermissionEvaluatorDefaults =

    let getAssignmentsForScope (scope: Scope, correlationId: CorrelationId) =
        task {
            let scopeKey = AccessControl.getScopeKey scope
            let actorProxy = Extensions.ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId
            return! actorProxy.GetAssignments None correlationId
        }

    let getPathPermissionsForRepository (repositoryId: RepositoryId, correlationId: CorrelationId) =
        task {
            let actorProxy = Extensions.ActorProxy.RepositoryPermission.CreateActorProxy repositoryId correlationId
            return! actorProxy.GetPathPermissions None correlationId
        }

type GracePermissionEvaluator
    (
        getAssignmentsForScope: Scope * CorrelationId -> Task<RoleAssignment list>,
        getPathPermissionsForRepository: RepositoryId * CorrelationId -> Task<PathPermission list>
    ) =

    new () =
        GracePermissionEvaluator(
            PermissionEvaluatorDefaults.getAssignmentsForScope,
            PermissionEvaluatorDefaults.getPathPermissionsForRepository
        )

    interface IGracePermissionEvaluator with
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
                        | Resource.Path (_, _, repositoryId, _) ->
                            getPathPermissionsForRepository (repositoryId, correlationId)
                        | _ -> Task.FromResult List.empty

                    return checkPermission roleCatalog allAssignments pathPermissions principalSet effectiveClaims operation resource
                with
                | ex -> return Denied $"Authorization evaluation failed: {ex.Message}"
            }
