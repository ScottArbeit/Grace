namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Text.Json
open System.Threading.Tasks

/// Contains administrator-enrolled Grace Cache HTTP handlers.
module CacheRegistration =

    /// Builds a non-secret Cache route error with the request correlation id.
    let private cacheError correlationId message = GraceError.Create message correlationId

    /// Binds a Cache route body while treating malformed JSON as a client failure.
    let private bindJson<'T> (context: HttpContext) correlationId =
        task {
            try
                let! value = context.BindJsonAsync<'T>()
                return Ok value
            with
            | :? JsonException
            | :? BadHttpRequestException
            | :? NullReferenceException -> return Error(cacheError correlationId "Cache request body is invalid.")
        }

    /// Evaluates one current Grace administrative permission without relying on enrollment-time roles.
    let private hasPermission (context: HttpContext) operation resource =
        task {
            match PrincipalMapper.tryGetUserId context.User with
            | None -> return false
            | Some _ ->
                let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

                let! decision =
                    evaluator.CheckAsync(PrincipalMapper.getPrincipals context.User, PrincipalMapper.getEffectiveClaims context.User, operation, resource)

                return
                    match decision with
                    | Allowed _ -> true
                    | Denied _ -> false
        }

    /// Validates every explicit repository scope against authoritative repository records and current administrative permission.
    let private authorizeBoundaryAndRepositories context boundaryKind ownerId organizationId repositoryScopes correlationId =
        task {
            let boundaryResource =
                match boundaryKind, organizationId with
                | CacheBoundaryKind.Owner, None -> Some(Resource.Owner ownerId)
                | CacheBoundaryKind.Organization, Some organization -> Some(Resource.Organization(ownerId, organization))
                | _ -> None

            match boundaryResource with
            | None -> return Error(cacheError correlationId "Cache boundary is malformed.")
            | Some resource ->
                let! boundaryAllowed =
                    match resource with
                    | Resource.Owner _ -> hasPermission context Operation.OwnerAdmin resource
                    | Resource.Organization _ -> hasPermission context Operation.OrganizationAdmin resource
                    | _ -> Task.FromResult false

                if not boundaryAllowed then
                    return Error(cacheError correlationId "Current Grace administrator permission is required for the Cache boundary.")
                else
                    let scopes =
                        if isNull repositoryScopes then
                            Seq.empty
                        else
                            repositoryScopes :> seq<CacheRepositoryScope>

                    let! checks =
                        scopes
                        |> Seq.map (fun scope ->
                            task {
                                if isNull (box scope) then
                                    return false
                                else
                                    let repository = ActorProxy.Repository.CreateActorProxy scope.OrganizationId scope.RepositoryId correlationId
                                    let! stored = repository.Get correlationId

                                    let boundaryMatches =
                                        stored.RepositoryId = scope.RepositoryId
                                        && stored.OwnerId = ownerId
                                        && stored.OrganizationId = scope.OrganizationId
                                        && stored.DeletedAt.IsNone
                                        && (match organizationId with
                                            | None -> true
                                            | Some organization -> stored.OrganizationId = organization)

                                    let! repositoryAllowed =
                                        hasPermission
                                            context
                                            Operation.RepositoryAdmin
                                            (Resource.Repository(stored.OwnerId, stored.OrganizationId, stored.RepositoryId))

                                    return boundaryMatches && repositoryAllowed
                            })
                        |> Task.WhenAll

                    if checks.Length = 0 || checks |> Array.exists not then
                        return
                            Error(
                                cacheError
                                    correlationId
                                    "Every selected Cache repository must exist inside the boundary and be currently administered by the caller."
                            )
                    else
                        return Ok()
        }

    /// Revalidates the stored boundary and all stored repositories before administrator mutation.
    let private authorizeStoredRegistration context (registration: CacheRegistration) correlationId =
        authorizeBoundaryAndRepositories
            context
            registration.BoundaryKind
            registration.OwnerId
            registration.OrganizationId
            (Collections.Generic.List<CacheRepositoryScope>(registration.RepositoryScopes))
            correlationId

    /// Resolves the current durable registration for an administrator operation without treating missing state as success.
    let private getStoredRegistration context cacheId correlationId =
        task {
            let actor = ActorProxy.CacheRegistration.CreateActorProxy correlationId
            let! registration = actor.Get(cacheId, correlationId)

            return
                match registration with
                | Some value -> Ok(actor, value)
                | None -> Error(cacheError correlationId "Cache registration was not found.")
        }

    /// Handles POST /cache/enroll with current administrator authorization for an exact boundary and repository set.
    let Enroll: HttpHandler =
        fun _next context ->
            task {
                let correlationId = getCorrelationId context

                match! bindJson<CacheEnrollmentRequest> context correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok request ->
                    match Lifecycle.validateEnrollmentRequest request with
                    | Error errors ->
                        return!
                            context
                            |> result400BadRequest (cacheError correlationId (String.concat " " errors))
                    | Ok () when not (CacheRegistrationProof.isValidPublicKey request.PublicKey) ->
                        return!
                            context
                            |> result400BadRequest (cacheError correlationId "PublicKey must be a canonical P-256 public key.")
                    | Ok () ->
                        match!
                            authorizeBoundaryAndRepositories
                                context
                                request.BoundaryKind
                                request.OwnerId
                                request.OrganizationId
                                request.RepositoryScopes
                                correlationId
                            with
                        | Error error ->
                            return!
                                context
                                |> returnResult StatusCodes.Status403Forbidden error
                        | Ok () ->
                            match PrincipalMapper.tryGetUserId context.User with
                            | None ->
                                return!
                                    context
                                    |> returnResult StatusCodes.Status401Unauthorized (cacheError correlationId "Authentication is required.")
                            | Some enrolledBy ->
                                let actor = ActorProxy.CacheRegistration.CreateActorProxy correlationId

                                match! actor.Enroll(Guid.NewGuid(), request, enrolledBy, getCurrentInstant (), correlationId) with
                                | Ok result -> return! context |> result200Ok result
                                | Error error -> return! context |> result400BadRequest error
            }

    /// Handles POST /cache/refresh using only the registered Cache identity proof and permitted operational facts.
    let Refresh: HttpHandler =
        fun _next context ->
            task {
                let correlationId = getCorrelationId context

                match! bindJson<CacheRegistrationRefreshRequest> context correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok request ->
                    match Lifecycle.validateRefreshRequest request with
                    | Error errors ->
                        return!
                            context
                            |> result400BadRequest (cacheError correlationId (String.concat " " errors))
                    | Ok () ->
                        let actor = ActorProxy.CacheRegistration.CreateActorProxy correlationId

                        match! actor.Refresh(request, getCurrentInstant (), correlationId) with
                        | Ok result -> return! context |> result200Ok result
                        | Error error -> return! context |> result400BadRequest error
            }

    /// Handles POST /cache/assign-repositories after revalidating the stored boundary and every replacement repository atomically.
    let AssignRepositories: HttpHandler =
        fun _next context ->
            task {
                let correlationId = getCorrelationId context

                match! bindJson<CacheRepositoryAssignmentRequest> context correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok request when
                    isNull (box request)
                    || request.Class
                       <> nameof CacheRepositoryAssignmentRequest
                    || request.CacheId = Guid.Empty
                    || isNull request.RepositoryScopes
                    || request.RepositoryScopes.Count = 0
                    ->
                    return!
                        context
                        |> result400BadRequest (cacheError correlationId "Cache repository assignment request is invalid.")
                | Ok request ->
                    match! getStoredRegistration context request.CacheId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok (actor, registration) ->
                        match! authorizeStoredRegistration context registration correlationId with
                        | Error error ->
                            return!
                                context
                                |> returnResult StatusCodes.Status403Forbidden error
                        | Ok () ->
                            match!
                                authorizeBoundaryAndRepositories
                                    context
                                    registration.BoundaryKind
                                    registration.OwnerId
                                    registration.OrganizationId
                                    request.RepositoryScopes
                                    correlationId
                                with
                            | Error error ->
                                return!
                                    context
                                    |> returnResult StatusCodes.Status403Forbidden error
                            | Ok () ->
                                match! actor.UpdateAssignments(request, correlationId) with
                                | Ok result -> return! context |> result200Ok result
                                | Error error -> return! context |> result400BadRequest error
            }

    /// Handles POST /cache/revoke after current administrator authorization against the stored Cache boundary.
    let Revoke: HttpHandler =
        fun _next context ->
            task {
                let correlationId = getCorrelationId context

                match! bindJson<CacheRevocationRequest> context correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok request when
                    isNull (box request)
                    || request.Class <> nameof CacheRevocationRequest
                    || request.CacheId = Guid.Empty
                    ->
                    return!
                        context
                        |> result400BadRequest (cacheError correlationId "Cache revocation request is invalid.")
                | Ok request ->
                    match! getStoredRegistration context request.CacheId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok (actor, registration) ->
                        match! authorizeStoredRegistration context registration correlationId with
                        | Error error ->
                            return!
                                context
                                |> returnResult StatusCodes.Status403Forbidden error
                        | Ok () ->
                            match! actor.Revoke(request, getCurrentInstant (), correlationId) with
                            | Ok result -> return! context |> result200Ok result
                            | Error error -> return! context |> result400BadRequest error
            }

    /// Handles POST /cache/rotate-key using proof of the old key before the actor durably accepts the new key.
    let RotateKey: HttpHandler =
        fun _next context ->
            task {
                let correlationId = getCorrelationId context

                match! bindJson<CacheKeyRotationRequest> context correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok request when
                    isNull (box request)
                    || request.Class <> nameof CacheKeyRotationRequest
                    || request.CacheId = Guid.Empty
                    || isNull (box request.Proof)
                    ->
                    return!
                        context
                        |> result400BadRequest (cacheError correlationId "Cache key rotation request is invalid.")
                | Ok request ->
                    let actor = ActorProxy.CacheRegistration.CreateActorProxy correlationId

                    match! actor.RotateKey(request, getCurrentInstant (), correlationId) with
                    | Ok result -> return! context |> result200Ok result
                    | Error error -> return! context |> result400BadRequest error
            }

    /// Handles GET /cache/validation-keys without exposing server private signing material.
    let GetValidationKeys: HttpHandler =
        fun _next context ->
            task {
                let correlationId = getCorrelationId context

                try
                    let keyRing = context.RequestServices.GetRequiredService<ArtifactGrantKeys.ArtifactGrantKeyRing>()
                    let! keySet = keyRing.PublishValidationKeys(getCurrentInstant ())
                    return! context |> result200Ok keySet
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex "Artifact grant validation-key publication failed." correlationId)
            }
