namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Security
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.AspNetCore.Http
open NodaTime
open System
open System.Threading
open System.Threading.Tasks

/// Answers `GET /usage/storage`: the retained logical bytes Grace keeps for one repository, by content class.
///
/// Authorization runs on the explicit OwnerId before anything is read, so a caller without OwnerAdmin gets the same 403
/// whether the repository exists or not. An OwnerAdmin gets 404 for a repository that is missing or belongs to another
/// owner or organization. Permission is checked again after the reads, and a failed or timed-out read is a 503.
module StorageUsage =

    /// The answer the read produced, before it is written to the response.
    type internal StorageUsageOutcome =
        | Usage of RepositoryStorageUsage
        | Forbidden
        | NotFound
        | Unavailable

    /// Parses the three explicit GUIDs and rejects name selectors, without reading any entity.
    let internal parseScope (query: IQueryCollection) =
        let guid name =
            match Guid.TryParse(string query[name]) with
            | true, value when value <> Guid.Empty -> Some value
            | _ -> None

        let named =
            [
                "OwnerName"
                "OrganizationName"
                "RepositoryName"
            ]
            |> List.exists (fun name -> query.ContainsKey name)

        match named, guid "OwnerId", guid "OrganizationId", guid "RepositoryId" with
        | true, _, _, _ -> Error "Usage reads take explicit OwnerId, OrganizationId, and RepositoryId GUIDs, not names."
        | false, Some ownerId, Some organizationId, Some repositoryId -> Ok { OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId }
        | _ -> Error "OwnerId, OrganizationId, and RepositoryId must each be a non-empty GUID."

    /// Resolves the permission from the explicit OwnerId alone, before any read.
    let private resolve (context: HttpContext) =
        parseScope context.Request.Query
        |> Result.map (fun scope -> Operation.OwnerAdmin, Resource.Owner scope.OwnerId)
        |> Result.mapError (fun message -> GraceError.Create message (Grace.Server.Services.getCorrelationId context))
        |> Task.FromResult

    /// Reads the repository and its meter, re-checking permission after the reads so a revoked caller sees nothing.
    let internal readWith
        (check: unit -> Task<PermissionCheckResult>)
        (lookupRepository: CancellationToken -> Task<(OwnerId * OrganizationId) option>)
        (readManifestBytes: CancellationToken -> Task<int64>)
        (now: unit -> Instant)
        (scope: UsageScope)
        (token: CancellationToken)
        =
        task {
            /// Treats an evaluator failure as a denial.
            let allowed () =
                task {
                    try
                        match! check () with
                        | Allowed _ -> return true
                        | Denied _ -> return false
                    with
                    | _ -> return false
                }

            try
                token.ThrowIfCancellationRequested()
                let! repository = lookupRepository token

                match repository with
                | Some (ownerId, organizationId) when
                    ownerId = scope.OwnerId
                    && organizationId = scope.OrganizationId
                    ->
                    let! bytes = readManifestBytes token
                    let! stillAllowed = allowed ()
                    token.ThrowIfCancellationRequested()

                    if stillAllowed then
                        return
                            Usage(
                                RepositoryStorageUsage.Create
                                    scope
                                    (now ())
                                    [
                                        StorageContentClass.ManifestBackedFiles, bytes
                                    ]
                            )
                    else
                        return Forbidden
                | _ ->
                    let! stillAllowed = allowed ()
                    return if stillAllowed then NotFound else Forbidden
            with
            | _ -> return Unavailable
        }

    /// Runs the route with injectable reads so tests can exercise hidden, missing, revoked, and failed reads.
    let internal handleWith
        (lookupRepository: HttpContext -> UsageScope -> CancellationToken -> Task<(OwnerId * OrganizationId) option>)
        (readManifestBytes: HttpContext -> UsageScope -> CancellationToken -> Task<int64>)
        : HttpHandler
        =
        AuthorizationMiddleware.requiresPermissionResolved resolve
        >=> fun next context ->
                task {
                    let correlationId = Grace.Server.Services.getCorrelationId context

                    match parseScope context.Request.Query with
                    | Error message -> return! RequestErrors.BAD_REQUEST (GraceError.Create message correlationId) next context
                    | Ok scope ->
                        use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
                        deadline.CancelAfter(TimeSpan.FromSeconds 30.)
                        let evaluator = context.GetService<IGracePermissionEvaluator>()

                        /// Reloads assignments for the same caller and owner.
                        let check () =
                            evaluator.CheckAsync(
                                PrincipalMapper.getPrincipals context.User,
                                PrincipalMapper.getEffectiveClaims context.User,
                                Operation.OwnerAdmin,
                                Resource.Owner scope.OwnerId
                            )

                        let! outcome = readWith check (lookupRepository context scope) (readManifestBytes context scope) getCurrentInstant scope deadline.Token

                        match outcome with
                        | Usage usage -> return! json (GraceReturnValue.Create usage correlationId) next context
                        | Forbidden -> return! RequestErrors.FORBIDDEN (GraceError.Create "Forbidden." correlationId) next context
                        | NotFound -> return! RequestErrors.NOT_FOUND (GraceError.Create "Repository was not found." correlationId) next context
                        | Unavailable ->
                            return!
                                ServerErrors.SERVICE_UNAVAILABLE
                                    (GraceError.Create "Storage usage could not be read. Retry the read." correlationId)
                                    next
                                    context
                }

    /// Reads the repository's recorded owner and organization; a deleted repository still answers until it is reclaimed.
    let private lookupRepository (context: HttpContext) (scope: UsageScope) (_: CancellationToken) =
        task {
            let correlationId = Grace.Server.Services.getCorrelationId context
            let actor = Repository.CreateActorProxy scope.OrganizationId scope.RepositoryId correlationId

            match! actor.Exists correlationId with
            | false -> return None
            | true ->
                let! repository = actor.Get correlationId
                return Some(repository.OwnerId, repository.OrganizationId)
        }

    /// Reads the repository's storage meter, which applies every committed change it can reach before answering.
    let private readManifestBytes (context: HttpContext) (scope: UsageScope) (token: CancellationToken) =
        let correlationId = Grace.Server.Services.getCorrelationId context

        let meter = ApplicationContext.grainFactory.GetGrain<IRepositoryStorageMeterActor>(RepositoryStorageMeter.primaryKey scope.RepositoryId)

        (meter.ReadRetainedManifestBytes correlationId)
            .WaitAsync(token)

    /// `GET /usage/storage?OwnerId=&OrganizationId=&RepositoryId=`.
    let GetStorage: HttpHandler = handleWith lookupRepository readManifestBytes
