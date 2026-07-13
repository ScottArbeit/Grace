namespace Grace.Server

open Giraffe
open Grace.Actors.DirectoryVersion
open Grace.Actors.Extensions
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Materialization
open Grace.Types.Branch
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.MaterializationPlan
open Grace.Types.Reference
open Grace.Types.Repository
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks

/// Contains Grace Server Materialization Plan route behavior and supporting helpers.
module Materialization =

    let private rootArtifactKinds =
        set [ MaterializationArtifactKind.DirectoryVersionZip
              MaterializationArtifactKind.RecursiveDirectoryMetadata ]

    /// Carries the stable identity and immutable root that Grace Server has fully resolved and authorized for one plan request.
    type internal ResolvedMaterializationTarget =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetRootDirectoryVersionId: DirectoryVersionId
        }

    /// Builds a GraceError for Materialization Plan route validation failures.
    let private planError correlationId message = GraceError.Create message correlationId

    /// Rejects artifact kinds that require path or content-address projection before a root-plan request can begin side effects.
    let private validateRootArtifactKinds (request: MaterializationPlanRequest) correlationId =
        let requestedKinds = set request.RequestedArtifactKinds
        let pathScopedKinds = requestedKinds - rootArtifactKinds

        if not pathScopedKinds.IsEmpty then
            Error(planError correlationId "Requested path-scoped artifact kinds are not supported by /materialization/plan yet.")
        elif not (Set.isSubset rootArtifactKinds requestedKinds) then
            Error(planError correlationId "Materialization Plan requests must include DirectoryVersionZip and RecursiveDirectoryMetadata artifact kinds.")
        else
            Ok()

    /// Public-safe selector error used when a DirectoryVersionId is missing or outside the authorized repository.
    let directoryVersionSelectorNotFoundMessage = "DirectoryVersionId selector did not match an authorized directory version."

    /// Public-safe selector error used when a repository authority is missing, deleted, or outside the authorized scope.
    let repositorySelectorNotFoundMessage = "Repository selector did not match an authorized repository."

    /// Public-safe selector error used when a ReferenceId is missing, deleted, or outside the authorized repository.
    let referenceSelectorNotFoundMessage = "ReferenceId selector did not match an authorized reference."

    /// Public-safe selector error used when a BranchName is missing, deleted, or outside the authorized repository.
    let branchNameSelectorNotFoundMessage = "BranchName selector did not match an authorized branch."

    /// Public-safe selector error used when a branch resolves but has no immutable target root.
    let branchTipSelectorNotFoundMessage = "BranchName selector did not match an authorized branch tip."

    /// Public-safe selector error used when a ReferenceType selector cannot resolve to an authorized branch reference.
    let referenceTypeSelectorNotFoundMessage = "ReferenceType selector did not match an authorized branch reference."

    /// Classifies Direct/Bypass plan creation failures after the route has parsed repository authority and target selector intent.
    type internal DirectPlanFailure =
        /// The request shape is invalid for the Direct root-only tracer contract and must remain a client error.
        | ClientPlanValidation of GraceError
        /// Server-side projection, actor, storage, artifact ensuring, or generated artifact descriptors failed after request validation.
        | ServerProjectionFailure of GraceError

    /// Converts Materialization Plan JSON binding outcomes into route validation results instead of server faults.
    let bindPlanParametersWith (bindParameters: unit -> Task<PlanParameters>) correlationId =
        task {
            try
                let! parameters = bindParameters ()

                if isNull (box parameters) then
                    return Error(planError correlationId "Materialization Plan parameters are required.")
                else
                    return Ok parameters
            with
            | :? JsonException -> return Error(planError correlationId "Materialization Plan request body is invalid.")
            | :? BadHttpRequestException -> return Error(planError correlationId "Materialization Plan request body is invalid.")
            | :? NullReferenceException -> return Error(planError correlationId "Materialization Plan parameters are required.")
        }

    /// Binds POST /materialization/plan parameters while keeping malformed client bodies in the 400 path.
    let bindPlanParameters (context: HttpContext) correlationId = bindPlanParametersWith (fun () -> context |> parse<PlanParameters>) correlationId

    /// Resolves the repository authority supplied by route middleware or request parameters.
    let resolveRepositoryAuthority (graceIds: GraceIds) (parameters: PlanParameters) =
        let mutable parsedRepositoryId = RepositoryId.Empty

        if graceIds.RepositoryId <> RepositoryId.Empty then
            graceIds.RepositoryId
        elif not (String.IsNullOrWhiteSpace parameters.RepositoryId)
             && Guid.TryParse(parameters.RepositoryId, &parsedRepositoryId)
             && parsedRepositoryId <> RepositoryId.Empty then
            parsedRepositoryId
        else
            RepositoryId.Empty

    /// Validates route parameters before any selector projection or materialization work starts.
    let validatePlanParameters (repositoryId: RepositoryId) (parameters: PlanParameters) correlationId : Result<MaterializationPlanRequest, GraceError> =
        if repositoryId = RepositoryId.Empty then
            Error(planError correlationId "RepositoryId is required for Materialization Plan requests.")
        elif isNull (box parameters) then
            Error(planError correlationId "Materialization Plan parameters are required.")
        elif isNull (box parameters.Request) then
            Error(planError correlationId "Materialization Plan Request is required.")
        else
            match Validation.validateRequest parameters.Request with
            | Error errors -> Error(planError correlationId (String.concat " " errors))
            | Ok () ->
                match validateRootArtifactKinds parameters.Request correlationId with
                | Ok () -> Ok parameters.Request
                | Error error -> Error error

    /// Validates that this tracer slice only plans Direct root artifacts and rejects cache/path expansion.
    let private validateDirectRootRequest (request: MaterializationPlanRequest) correlationId =
        match Validation.validateRequest request with
        | Error errors -> Error(planError correlationId (String.concat " " errors))
        | Ok () ->
            if request.ExecutionMode
               <> MaterializationExecutionMode.Direct
               || request.CacheSelection.SelectionKind
                  <> MaterializationCacheSelectionKind.BypassCache then
                Error(planError correlationId "Cache materialization plan selection is not implemented for /materialization/plan.")
            else
                validateRootArtifactKinds request correlationId

    /// Builds a Direct Materialization Plan with failure classification for the route status-code taxonomy.
    let internal createDirectPlanForResolvedRootWithFailureKind
        (request: MaterializationPlanRequest)
        (targetRootDirectoryVersionId: DirectoryVersionId)
        (ensureArtifacts: CorrelationId -> Task<Result<MaterializationArtifactDescriptor array, GraceError>>)
        correlationId
        : Task<Result<Grace.Types.MaterializationPlan.MaterializationPlan, DirectPlanFailure>>
        =
        task {
            match validateDirectRootRequest request correlationId with
            | Error error -> return Error(ClientPlanValidation error)
            | Ok () ->
                let! artifactsResult =
                    task {
                        try
                            return! ensureArtifacts correlationId
                        with
                        | ex -> return Error(GraceError.CreateWithException ex "Materialization Plan projection artifacts could not be ensured." correlationId)
                    }

                match artifactsResult with
                | Error error -> return Error(ServerProjectionFailure error)
                | Ok artifacts ->
                    let plan =
                        Grace.Types.MaterializationPlan.MaterializationPlan.Create(
                            targetRootDirectoryVersionId,
                            request.ExecutionMode,
                            request.CacheSelection,
                            artifacts
                        )

                    match Validation.validatePlan plan with
                    | Ok () -> return Ok plan
                    | Error errors -> return Error(ServerProjectionFailure(planError correlationId (String.concat " " errors)))
        }

    /// Builds a Direct Materialization Plan for a target root that has already been resolved and authorized.
    let createDirectPlanForResolvedRoot
        (request: MaterializationPlanRequest)
        (targetRootDirectoryVersionId: DirectoryVersionId)
        (ensureArtifacts: CorrelationId -> Task<Result<MaterializationArtifactDescriptor array, GraceError>>)
        correlationId
        : Task<Result<Grace.Types.MaterializationPlan.MaterializationPlan, GraceError>>
        =
        task {
            match! createDirectPlanForResolvedRootWithFailureKind request targetRootDirectoryVersionId ensureArtifacts correlationId with
            | Ok plan -> return Ok plan
            | Error (ClientPlanValidation error)
            | Error (ServerProjectionFailure error) -> return Error error
        }

    /// Builds a cache-capable plan from exact projection artifacts and one server-selected current registration.
    let internal createCachePlanForResolvedRoot
        (request: MaterializationPlanRequest)
        (target: ResolvedMaterializationTarget)
        (ensureArtifacts: CorrelationId -> Task<Result<MaterializationArtifactDescriptor array, GraceError>>)
        (getCurrentInstant: unit -> Instant)
        (selectEligible: CacheRegistrationSelectionQuery -> Instant -> Task<CacheRegistration array>)
        (issueGrant: Instant -> CacheRegistration -> string array -> Task<Result<Grace.Types.ArtifactGrant.SignedArtifactGrant, GraceError>>)
        (recordCacheFailure: GraceError -> unit)
        correlationId
        =
        task {
            match Validation.validateRequest request with
            | Error errors -> return Error(planError correlationId (String.concat " " errors))
            | Ok () when request.ExecutionMode = MaterializationExecutionMode.Direct ->
                return Error(planError correlationId "A cache-capable execution mode is required.")
            | Ok () ->
                match validateRootArtifactKinds request correlationId with
                | Error error -> return Error error
                | Ok () ->
                    match request.HolderPublicKey with
                    | None -> return Error(planError correlationId "HolderPublicKey is required for cache-capable Materialization Plans.")
                    | Some holder when not (ArtifactGrant.isValidHolderPublicKey holder) ->
                        return Error(planError correlationId "HolderPublicKey must be a canonical P-256 public key.")
                    | Some _ ->
                        let! artifactsResult =
                            task {
                                try
                                    return! ensureArtifacts correlationId
                                with
                                | ex ->
                                    return
                                        Error(GraceError.CreateWithException ex "Materialization Plan projection artifacts could not be ensured." correlationId)
                            }

                        match artifactsResult with
                        | Error error -> return Error error
                        | Ok artifacts ->
                            let directPlanResult =
                                if request.ExecutionMode = MaterializationExecutionMode.CachePreferred then
                                    let directPlan =
                                        Grace.Types.MaterializationPlan.MaterializationPlan.Create(
                                            target.TargetRootDirectoryVersionId,
                                            request.ExecutionMode,
                                            request.CacheSelection,
                                            artifacts
                                        )

                                    match Validation.validatePlan directPlan with
                                    | Ok () -> Ok(Some directPlan)
                                    | Error errors -> Error(planError correlationId (String.concat " " errors))
                                else
                                    Ok None

                            match directPlanResult with
                            | Error error -> return Error error
                            | Ok directPlan ->
                                let now = getCurrentInstant ()

                                let query = CacheRegistrationSelectionQuery.Create(Some target.RepositoryId, false)

                                let! cachePlanResult =
                                    task {
                                        try
                                            let! registrations = selectEligible query now

                                            match registrations |> Array.tryHead with
                                            | None -> return Error(planError correlationId "No eligible Cache registration is currently available.")
                                            | Some registration ->
                                                let identities =
                                                    artifacts
                                                    |> Array.choose (fun artifact -> artifact.CanonicalArtifactIdentity)

                                                match! issueGrant now registration identities with
                                                | Error error -> return Error error
                                                | Ok grant ->
                                                    let cacheArtifacts =
                                                        artifacts
                                                        |> Array.map (fun artifact ->
                                                            let fallback =
                                                                if request.ExecutionMode = MaterializationExecutionMode.CachePreferred then
                                                                    artifact.Source
                                                                    |> Option.bind (fun source -> source.DirectUri)
                                                                else
                                                                    None

                                                            { artifact with
                                                                Source =
                                                                    artifact.CanonicalArtifactIdentity
                                                                    |> Option.map (fun identity ->
                                                                        MaterializationArtifactSource.Cache(
                                                                            identity,
                                                                            registration.Endpoint,
                                                                            registration.CacheId.ToString("D"),
                                                                            fallback
                                                                        ))
                                                            })

                                                    let cachePlan =
                                                        Grace
                                                            .Types
                                                            .MaterializationPlan
                                                            .MaterializationPlan
                                                            .Create(
                                                                target.TargetRootDirectoryVersionId,
                                                                request.ExecutionMode,
                                                                request.CacheSelection,
                                                                cacheArtifacts
                                                            )
                                                            .WithArtifactGrant(grant)

                                                    match Validation.validatePlan cachePlan with
                                                    | Ok () -> return Ok cachePlan
                                                    | Error errors -> return Error(planError correlationId (String.concat " " errors))
                                        with
                                        | ex -> return Error(GraceError.CreateWithException ex "Cache Materialization Plan issuance failed." correlationId)
                                    }

                                match cachePlanResult with
                                | Ok plan -> return Ok plan
                                | Error error when
                                    request.ExecutionMode = MaterializationExecutionMode.CachePreferred
                                    && directPlan.IsSome
                                    ->
                                    recordCacheFailure error
                                    return Ok directPlan.Value
                                | Error error -> return Error error
        }

    /// Validates that a resolved DirectoryVersion belongs to the authorized repository without leaking cross-scope state.
    let validateDirectoryVersionSelectorScope repositoryId (directoryVersion: DirectoryVersion) correlationId =
        if directoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
            Error(planError correlationId directoryVersionSelectorNotFoundMessage)
        elif directoryVersion.RepositoryId <> repositoryId then
            Error(planError correlationId directoryVersionSelectorNotFoundMessage)
        else
            Ok()

    /// Resolves a DirectoryVersion actor result to the immutable root selector accepted by the Materialization Plan route.
    let internal resolveDirectoryVersionSelectorWith
        repositoryId
        (directoryVersionId: DirectoryVersionId)
        (getDirectoryVersion: unit -> Task<DirectoryVersionDto>)
        correlationId
        =
        task {
            if directoryVersionId = DirectoryVersionId.Empty then
                return Error(planError correlationId "DirectoryVersionId selector is required.")
            else
                try
                    let! directoryVersionDto = getDirectoryVersion ()
                    let directoryVersion = directoryVersionDto.DirectoryVersion

                    match validateDirectoryVersionSelectorScope repositoryId directoryVersion correlationId with
                    | Error error -> return Error error
                    | Ok () -> return Ok directoryVersion.DirectoryVersionId
                with
                | :? KeyNotFoundException -> return Error(planError correlationId directoryVersionSelectorNotFoundMessage)
        }

    /// Resolves a DirectoryVersionId selector to an immutable repository root.
    let private resolveDirectoryVersionSelector repositoryId (directoryVersionId: DirectoryVersionId) correlationId =
        task {
            let actorProxy = ActorProxy.DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId

            match! resolveDirectoryVersionSelectorWith repositoryId directoryVersionId (fun () -> actorProxy.Get correlationId) correlationId with
            | Error error -> return Error error
            | Ok resolvedDirectoryVersionId -> return Ok resolvedDirectoryVersionId
        }

    /// Validates that repository authority exists before moving selectors can resolve to repository state.
    let validateRepositorySelectorScope ownerId organizationId repositoryId (repositoryDto: RepositoryDto) correlationId =
        if repositoryDto.RepositoryId = RepositoryId.Empty then
            Error(planError correlationId repositorySelectorNotFoundMessage)
        elif repositoryDto.RepositoryId <> repositoryId then
            Error(planError correlationId repositorySelectorNotFoundMessage)
        elif repositoryDto.OwnerId <> ownerId then
            Error(planError correlationId repositorySelectorNotFoundMessage)
        elif repositoryDto.OrganizationId <> organizationId then
            Error(planError correlationId repositorySelectorNotFoundMessage)
        elif repositoryDto.DeletedAt.IsSome then
            Error(planError correlationId repositorySelectorNotFoundMessage)
        else
            Ok()

    /// Resolves repository authority to current repository state for selector lookups.
    let internal resolveRepositorySelectorWith
        ownerId
        organizationId
        repositoryId
        (getRepository: unit -> Task<RepositoryDto>)
        correlationId
        : Task<Result<RepositoryDto, GraceError>>
        =
        task {
            try
                let! repositoryDto = getRepository ()

                match validateRepositorySelectorScope ownerId organizationId repositoryId repositoryDto correlationId with
                | Error error -> return Error error
                | Ok () -> return Ok repositoryDto
            with
            | :? KeyNotFoundException -> return Error(planError correlationId repositorySelectorNotFoundMessage)
        }

    /// Resolves repository authority from actors before selector materialization starts.
    let private resolveRepositorySelector ownerId organizationId repositoryId correlationId =
        task {
            let repositoryActorProxy = ActorProxy.Repository.CreateActorProxy organizationId repositoryId correlationId

            return! resolveRepositorySelectorWith ownerId organizationId repositoryId (fun () -> repositoryActorProxy.Get correlationId) correlationId
        }

    /// Validates that a reference belongs to the authorized repository and carries one immutable root directory.
    let validateReferenceSelectorScope repositoryId (referenceDto: ReferenceDto) correlationId =
        if referenceDto.ReferenceId = ReferenceId.Empty then
            Error(planError correlationId referenceSelectorNotFoundMessage)
        elif referenceDto.RepositoryId <> repositoryId then
            Error(planError correlationId referenceSelectorNotFoundMessage)
        elif referenceDto.DeletedAt.IsSome then
            Error(planError correlationId referenceSelectorNotFoundMessage)
        elif referenceDto.DirectoryId = DirectoryVersionId.Empty then
            Error(planError correlationId referenceSelectorNotFoundMessage)
        else
            Ok()

    /// Resolves a ReferenceId selector to the immutable root represented by the current stored reference.
    let internal resolveReferenceSelectorWith
        repositoryId
        (referenceId: ReferenceId)
        (getReference: unit -> Task<ReferenceDto>)
        (getDirectoryVersion: DirectoryVersionId -> Task<DirectoryVersionDto>)
        correlationId
        : Task<Result<DirectoryVersionId, GraceError>>
        =
        task {
            if referenceId = ReferenceId.Empty then
                return Error(planError correlationId "ReferenceId selector is required.")
            else
                try
                    let! referenceDto = getReference ()

                    match validateReferenceSelectorScope repositoryId referenceDto correlationId with
                    | Error error -> return Error error
                    | Ok () ->
                        return!
                            resolveDirectoryVersionSelectorWith
                                repositoryId
                                referenceDto.DirectoryId
                                (fun () -> getDirectoryVersion referenceDto.DirectoryId)
                                correlationId
                with
                | :? KeyNotFoundException -> return Error(planError correlationId referenceSelectorNotFoundMessage)
        }

    /// Resolves a ReferenceId selector to one target root from actor state.
    let private resolveReferenceSelector repositoryId referenceId correlationId =
        task {
            let referenceActorProxy = ActorProxy.Reference.CreateActorProxy referenceId repositoryId correlationId

            return!
                resolveReferenceSelectorWith
                    repositoryId
                    referenceId
                    (fun () -> referenceActorProxy.Get correlationId)
                    (fun directoryVersionId ->
                        let directoryActorProxy = ActorProxy.DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
                        directoryActorProxy.Get correlationId)
                    correlationId
        }

    /// Validates that a branch-name lookup resolved to the requested live branch in the authorized repository.
    let validateBranchNameSelectorScope repositoryId branchId branchName (branchDto: BranchDto) correlationId =
        if branchDto.BranchId = BranchId.Empty then
            Error(planError correlationId branchNameSelectorNotFoundMessage)
        elif branchDto.BranchId <> branchId then
            Error(planError correlationId branchNameSelectorNotFoundMessage)
        elif branchDto.RepositoryId <> repositoryId then
            Error(planError correlationId branchNameSelectorNotFoundMessage)
        elif branchDto.DeletedAt.IsSome then
            Error(planError correlationId branchNameSelectorNotFoundMessage)
        elif not (String.Equals(string branchDto.BranchName, string branchName, StringComparison.OrdinalIgnoreCase)) then
            Error(planError correlationId branchNameSelectorNotFoundMessage)
        else
            Ok()

    /// Validates that a branch snapshot exposes one immutable latest reference for Direct planning.
    let validateBranchTipReference repositoryId branchId referenceId (referenceDto: ReferenceDto) correlationId =
        if referenceDto.ReferenceId = ReferenceId.Empty then
            Error(planError correlationId branchTipSelectorNotFoundMessage)
        elif referenceDto.ReferenceId <> referenceId then
            Error(planError correlationId branchTipSelectorNotFoundMessage)
        elif referenceDto.RepositoryId <> repositoryId then
            Error(planError correlationId branchTipSelectorNotFoundMessage)
        elif referenceDto.BranchId <> branchId then
            Error(planError correlationId branchTipSelectorNotFoundMessage)
        elif referenceDto.DeletedAt.IsSome then
            Error(planError correlationId branchTipSelectorNotFoundMessage)
        elif referenceDto.DirectoryId = DirectoryVersionId.Empty then
            Error(planError correlationId branchTipSelectorNotFoundMessage)
        else
            Ok()

    /// Resolves a BranchName selector to the branch's current immutable root at the branch Get revalidation point.
    let internal resolveBranchNameSelectorWith
        repositoryId
        (branchName: BranchName)
        (resolveBranchIdByName: unit -> Task<BranchId option>)
        (getBranch: BranchId -> Task<BranchDto>)
        (getReference: ReferenceId -> Task<ReferenceDto>)
        (getDirectoryVersion: DirectoryVersionId -> Task<DirectoryVersionDto>)
        correlationId
        : Task<Result<DirectoryVersionId, GraceError>>
        =
        task {
            if String.IsNullOrWhiteSpace branchName then
                return Error(planError correlationId "BranchName selector is required.")
            else
                try
                    let! branchId = resolveBranchIdByName ()

                    match branchId with
                    | None -> return Error(planError correlationId branchNameSelectorNotFoundMessage)
                    | Some branchId when branchId = BranchId.Empty -> return Error(planError correlationId branchNameSelectorNotFoundMessage)
                    | Some branchId ->
                        let! branchDto = getBranch branchId

                        match validateBranchNameSelectorScope repositoryId branchId branchName branchDto correlationId with
                        | Error error -> return Error error
                        | Ok () ->
                            let snapshotReferenceId = branchDto.LatestReference.ReferenceId

                            if snapshotReferenceId = ReferenceId.Empty then
                                return Error(planError correlationId branchTipSelectorNotFoundMessage)
                            else
                                let! latestReferenceResult =
                                    task {
                                        try
                                            let! latestReference = getReference snapshotReferenceId
                                            return Ok latestReference
                                        with
                                        | :? KeyNotFoundException -> return Error(planError correlationId branchTipSelectorNotFoundMessage)
                                    }

                                match latestReferenceResult with
                                | Error error -> return Error error
                                | Ok latestReference ->
                                    match validateBranchTipReference repositoryId branchDto.BranchId snapshotReferenceId latestReference correlationId with
                                    | Error error -> return Error error
                                    | Ok () ->
                                        return!
                                            resolveDirectoryVersionSelectorWith
                                                repositoryId
                                                latestReference.DirectoryId
                                                (fun () -> getDirectoryVersion latestReference.DirectoryId)
                                                correlationId
                with
                | :? KeyNotFoundException -> return Error(planError correlationId branchNameSelectorNotFoundMessage)
        }

    /// Resolves a BranchName selector to one target root from actor state.
    let private resolveBranchNameSelector ownerId organizationId repositoryId branchName correlationId =
        task {
            return!
                resolveBranchNameSelectorWith
                    repositoryId
                    branchName
                    (fun () -> resolveBranchId ownerId organizationId repositoryId String.Empty branchName correlationId)
                    (fun branchId ->
                        let branchActorProxy = ActorProxy.Branch.CreateActorProxy branchId repositoryId correlationId
                        branchActorProxy.Get correlationId)
                    (fun referenceId ->
                        let referenceActorProxy = ActorProxy.Reference.CreateActorProxy referenceId repositoryId correlationId
                        referenceActorProxy.Get correlationId)
                    (fun directoryVersionId ->
                        let directoryActorProxy = ActorProxy.DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
                        directoryActorProxy.Get correlationId)
                    correlationId
        }

    /// Resolves a ReferenceType selector to the latest matching reference for the selected branch at plan time.
    let internal resolveReferenceTypeSelectorWith
        repositoryId
        (branchId: BranchId option)
        (branchName: BranchName option)
        (referenceType: ReferenceType)
        (resolveBranchIdByName: BranchName -> Task<BranchId option>)
        (getBranch: BranchId -> Task<BranchDto>)
        (getLatestReference: BranchId -> Task<ReferenceDto option>)
        (getDirectoryVersion: DirectoryVersionId -> Task<DirectoryVersionDto>)
        correlationId
        : Task<Result<DirectoryVersionId, GraceError>>
        =
        task {
            let hasBranchId =
                match branchId with
                | Some branchId -> branchId <> BranchId.Empty
                | None -> false

            let hasBranchName =
                match branchName with
                | Some branchName -> not (String.IsNullOrWhiteSpace branchName)
                | None -> false

            if hasBranchId = hasBranchName then
                return Error(planError correlationId "ReferenceType selector requires exactly one branch identity.")
            else
                try
                    let! resolvedBranchId =
                        match branchId, branchName with
                        | Some branchId, None -> Task.FromResult(Some branchId)
                        | None, Some branchName -> resolveBranchIdByName branchName
                        | _ -> Task.FromResult(None)

                    match resolvedBranchId with
                    | None -> return Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                    | Some resolvedBranchId when resolvedBranchId = BranchId.Empty -> return Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                    | Some resolvedBranchId ->
                        let! branchDto = getBranch resolvedBranchId

                        let branchScopeResult =
                            match branchName with
                            | Some branchName -> validateBranchNameSelectorScope repositoryId resolvedBranchId branchName branchDto correlationId
                            | None ->
                                if branchDto.BranchId = BranchId.Empty then
                                    Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                                elif branchDto.BranchId <> resolvedBranchId then
                                    Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                                elif branchDto.RepositoryId <> repositoryId then
                                    Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                                elif branchDto.DeletedAt.IsSome then
                                    Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                                else
                                    Ok()

                        match branchScopeResult with
                        | Error _ -> return Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                        | Ok () ->
                            let! latestReference = getLatestReference resolvedBranchId

                            match latestReference with
                            | None -> return Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                            | Some referenceDto ->
                                match validateBranchTipReference repositoryId branchDto.BranchId referenceDto.ReferenceId referenceDto correlationId with
                                | Error _ -> return Error(planError correlationId referenceTypeSelectorNotFoundMessage)
                                | Ok () ->
                                    return!
                                        resolveDirectoryVersionSelectorWith
                                            repositoryId
                                            referenceDto.DirectoryId
                                            (fun () -> getDirectoryVersion referenceDto.DirectoryId)
                                            correlationId
                with
                | :? KeyNotFoundException -> return Error(planError correlationId referenceTypeSelectorNotFoundMessage)
        }

    /// Resolves a ReferenceType selector to one target root from current branch reference state.
    let private resolveReferenceTypeSelector ownerId organizationId repositoryId branchId branchName referenceType correlationId =
        task {
            return!
                resolveReferenceTypeSelectorWith
                    repositoryId
                    branchId
                    branchName
                    referenceType
                    (fun branchName -> resolveBranchId ownerId organizationId repositoryId String.Empty branchName correlationId)
                    (fun branchId ->
                        let branchActorProxy = ActorProxy.Branch.CreateActorProxy branchId repositoryId correlationId
                        branchActorProxy.Get correlationId)
                    (fun branchId -> getLatestReferenceByType referenceType repositoryId branchId)
                    (fun directoryVersionId ->
                        let directoryActorProxy = ActorProxy.DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
                        directoryActorProxy.Get correlationId)
                    correlationId
        }

    /// Resolves supported target selectors before artifact planning starts.
    let private resolveTargetRoot ownerId organizationId repositoryId (selector: MaterializationTargetSelector) correlationId =
        task {
            match! resolveRepositorySelector ownerId organizationId repositoryId correlationId with
            | Error error -> return Error error
            | Ok repositoryDto ->
                let! rootResult =
                    match selector.SelectorKind with
                    | MaterializationTargetSelectorKind.DirectoryVersionId ->
                        match selector.DirectoryVersionId with
                        | Some directoryVersionId -> resolveDirectoryVersionSelector repositoryId directoryVersionId correlationId
                        | None -> Task.FromResult(Error(planError correlationId "DirectoryVersionId selector is required."))
                    | MaterializationTargetSelectorKind.ReferenceId ->
                        match selector.ReferenceId with
                        | Some referenceId -> resolveReferenceSelector repositoryId referenceId correlationId
                        | None -> Task.FromResult(Error(planError correlationId "ReferenceId selector is required."))
                    | MaterializationTargetSelectorKind.BranchName ->
                        match selector.BranchName with
                        | Some branchName ->
                            let resolvedBranchName =
                                if branchName = repositoryDto.DefaultBranchName then
                                    repositoryDto.DefaultBranchName
                                else
                                    branchName

                            resolveBranchNameSelector ownerId organizationId repositoryId resolvedBranchName correlationId
                        | None -> Task.FromResult(Error(planError correlationId "BranchName selector is required."))
                    | MaterializationTargetSelectorKind.ReferenceType ->
                        match selector.BranchId, selector.BranchName, selector.ReferenceType with
                        | branchId, branchName, Some referenceType when
                            ((branchId.IsSome && branchName.IsNone)
                             || (branchId.IsNone && branchName.IsSome))
                            ->
                            resolveReferenceTypeSelector ownerId organizationId repositoryId branchId branchName referenceType correlationId
                        | _, _, None -> Task.FromResult(Error(planError correlationId "ReferenceType selector is required."))
                        | _, _, _ -> Task.FromResult(Error(planError correlationId "ReferenceType selector requires exactly one branch identity."))
                    | _ -> Task.FromResult(Error(planError correlationId $"Target selector kind '{int selector.SelectorKind}' is not supported."))

                match rootResult with
                | Error error -> return Error error
                | Ok targetRootDirectoryVersionId ->
                    return
                        Ok
                            {
                                OwnerId = repositoryDto.OwnerId
                                OrganizationId = repositoryDto.OrganizationId
                                RepositoryId = repositoryDto.RepositoryId
                                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                            }
        }

    /// Handles POST /materialization/plan.
    let Plan: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    match! bindPlanParameters context correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok parameters ->
                        let graceIds = getGraceIds context
                        let repositoryId = resolveRepositoryAuthority graceIds parameters

                        match validatePlanParameters repositoryId parameters correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok request ->
                            match! resolveTargetRoot graceIds.OwnerId graceIds.OrganizationId repositoryId request.TargetSelector correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok target ->
                                let targetRootDirectoryVersionId = target.TargetRootDirectoryVersionId
                                let actorProxy = ActorProxy.DirectoryVersion.CreateActorProxy targetRootDirectoryVersionId target.RepositoryId correlationId

                                let ensureArtifacts artifactCorrelationId =
                                    task {
                                        match! Grace.Server.DirectoryVersion.ensureTargetRootProjectionArtifacts actorProxy artifactCorrelationId with
                                        | Ok graceReturnValue -> return Ok graceReturnValue.ReturnValue
                                        | Error error -> return Error error
                                    }

                                let! planResult =
                                    if request.ExecutionMode = MaterializationExecutionMode.Direct then
                                        task {
                                            match!
                                                createDirectPlanForResolvedRootWithFailureKind
                                                    request
                                                    targetRootDirectoryVersionId
                                                    ensureArtifacts
                                                    correlationId
                                                with
                                            | Ok plan -> return Ok plan
                                            | Error (ClientPlanValidation error) -> return Error(error, true)
                                            | Error (ServerProjectionFailure error) -> return Error(error, false)
                                        }
                                    else
                                        task {
                                            match PrincipalMapper.tryGetUserId context.User, request.HolderPublicKey with
                                            | None, _ -> return Error(planError correlationId "Authenticated grace_user_id is required.", true)
                                            | _, None ->
                                                return
                                                    Error(planError correlationId "HolderPublicKey is required for cache-capable Materialization Plans.", true)
                                            | Some userId, Some holderPublicKey when not (ArtifactGrant.isValidHolderPublicKey holderPublicKey) ->
                                                return Error(planError correlationId "HolderPublicKey must be a canonical P-256 public key.", true)
                                            | Some userId, Some holderPublicKey ->
                                                let cacheActor = ActorProxy.CacheRegistration.CreateActorProxy correlationId

                                                let logger =
                                                    context
                                                        .RequestServices
                                                        .GetRequiredService<ILoggerFactory>()
                                                        .CreateLogger("Grace.Server.Materialization")

                                                let keyRing =
                                                    ArtifactGrantKeys.ArtifactGrantKeyRing(context.RequestServices.GetRequiredService<IGrainFactory>())

                                                match!
                                                    createCachePlanForResolvedRoot
                                                        request
                                                        target
                                                        ensureArtifacts
                                                        (fun () -> SystemClock.Instance.GetCurrentInstant())
                                                        (fun query now -> cacheActor.SelectEligible(query, now, correlationId))
                                                        (fun now registration identities ->
                                                            task {
                                                                match!
                                                                    keyRing.IssueGrant
                                                                        (
                                                                            now,
                                                                            {
                                                                                AuthenticatedUserId = userId
                                                                                HolderPublicKey = holderPublicKey
                                                                                CacheId = registration.CacheId.ToString("D")
                                                                                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                                                                                ExecutionMode = request.ExecutionMode
                                                                                ArtifactIdentities = identities
                                                                                RequestedTtl = None
                                                                            }
                                                                        )
                                                                    with
                                                                | Ok grant -> return Ok grant
                                                                | Error issueError ->
                                                                    return
                                                                        Error(
                                                                            planError
                                                                                correlationId
                                                                                (Grace.Types.ArtifactGrant.ArtifactGrantIssueError.toMessage issueError)
                                                                        )
                                                            })
                                                        (fun error ->
                                                            logger.LogWarning(
                                                                "CachePreferred materialization plan fell back to Direct after a cache attempt failed. CorrelationId: {CorrelationId}; CacheError: {CacheError}",
                                                                correlationId,
                                                                error.Error
                                                            ))
                                                        correlationId
                                                    with
                                                | Ok plan -> return Ok plan
                                                | Error error -> return Error(error, false)
                                        }

                                match planResult with
                                | Error (error, true) -> return! context |> result400BadRequest error
                                | Error (error, false) -> return! context |> result500ServerError error
                                | Ok plan ->
                                    let graceReturnValue = GraceReturnValue.Create plan correlationId

                                    if graceIds.OwnerId <> OwnerId.Empty then
                                        graceReturnValue.Properties[ nameof OwnerId ] <- graceIds.OwnerId

                                    if graceIds.OrganizationId <> OrganizationId.Empty then
                                        graceReturnValue.Properties[ nameof OrganizationId ] <- graceIds.OrganizationId

                                    graceReturnValue.Properties[ nameof RepositoryId ] <- repositoryId

                                    return! context |> result200Ok graceReturnValue
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex "Materialization Plan request failed." correlationId)
            }
