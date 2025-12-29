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

type GracePermissionEvaluator() =

    interface IGracePermissionEvaluator with
        member _.CheckAsync(principalSet, effectiveClaims, operation, resource) =
            task {
                try
                    let correlationId = generateCorrelationId ()
                    let roleCatalog = RoleCatalog.getAll ()
                    let scopes = scopesForResource resource

                    let! assignments =
                        scopes
                        |> List.map (fun scope ->
                            task {
                                let scopeKey = AccessControl.getScopeKey scope
                                let actorProxy = Extensions.ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId
                                return! actorProxy.GetAssignments None correlationId
                            })
                        |> Task.WhenAll

                    let allAssignments = assignments |> Seq.collect id |> Seq.toList

                    let! pathPermissions =
                        match resource with
                        | Resource.Path(_, _, repositoryId, _) ->
                            task {
                                let actorProxy = Extensions.ActorProxy.RepositoryPermission.CreateActorProxy repositoryId correlationId
                                return! actorProxy.GetPathPermissions None correlationId
                            }
                        | _ -> Task.FromResult List.empty

                    return
                        checkPermission
                            roleCatalog
                            allAssignments
                            pathPermissions
                            principalSet
                            effectiveClaims
                            operation
                            resource
                with ex ->
                    return Denied $"Authorization evaluation failed: {ex.Message}"
            }
