namespace Grace.Server

open Giraffe
open Grace.Actors.DirectoryVersion
open Grace.Actors.Extensions
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Materialization
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.MaterializationPlan
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Contains Grace Server Materialization Plan route behavior and supporting helpers.
module Materialization =

    let private rootArtifactKinds =
        set [ MaterializationArtifactKind.DirectoryVersionZip
              MaterializationArtifactKind.RecursiveDirectoryMetadata ]

    /// Builds a GraceError for Materialization Plan route validation failures.
    let private planError correlationId message = GraceError.Create message correlationId

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
            | Ok () -> Ok parameters.Request

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
                let requestedKinds = set request.RequestedArtifactKinds
                let pathScopedKinds = requestedKinds - rootArtifactKinds

                if not pathScopedKinds.IsEmpty then
                    Error(planError correlationId "Requested path-scoped artifact kinds are not supported by /materialization/plan yet.")
                elif not (Set.isSubset rootArtifactKinds requestedKinds) then
                    Error(
                        planError
                            correlationId
                            "Direct Materialization Plan requests must include DirectoryVersionZip and RecursiveDirectoryMetadata artifact kinds."
                    )
                else
                    Ok()

    /// Builds a Direct Materialization Plan for a target root that has already been resolved and authorized.
    let createDirectPlanForResolvedRoot
        (request: MaterializationPlanRequest)
        (targetRootDirectoryVersionId: DirectoryVersionId)
        (ensureArtifacts: CorrelationId -> Task<Result<MaterializationArtifactDescriptor array, GraceError>>)
        correlationId
        : Task<Result<Grace.Types.MaterializationPlan.MaterializationPlan, GraceError>>
        =
        task {
            match validateDirectRootRequest request correlationId with
            | Error error -> return Error error
            | Ok () ->
                match! ensureArtifacts correlationId with
                | Error error -> return Error error
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
                    | Error errors -> return Error(planError correlationId (String.concat " " errors))
        }

    /// Resolves a DirectoryVersionId selector to an immutable repository root.
    let private resolveDirectoryVersionSelector repositoryId (directoryVersionId: DirectoryVersionId) correlationId =
        task {
            if directoryVersionId = DirectoryVersionId.Empty then
                return Error(planError correlationId "DirectoryVersionId selector is required.")
            else
                let actorProxy = ActorProxy.DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
                let! directoryVersionDto = actorProxy.Get correlationId
                let directoryVersion = directoryVersionDto.DirectoryVersion

                if directoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
                    return Error(planError correlationId "DirectoryVersionId selector did not match a directory version.")
                elif directoryVersion.RepositoryId <> repositoryId then
                    return Error(planError correlationId "DirectoryVersionId selector did not resolve inside the authorized repository.")
                elif directoryVersion.RelativePath
                     <> Constants.RootDirectoryPath
                     && directoryVersion.RelativePath <> "/" then
                    return Error(planError correlationId "Path-scoped Materialization Plan selectors are not supported by /materialization/plan yet.")
                else
                    return Ok(directoryVersion.DirectoryVersionId, actorProxy)
        }

    /// Resolves supported target selectors before artifact planning starts.
    let private resolveTargetRoot repositoryId (selector: MaterializationTargetSelector) correlationId =
        task {
            match selector.SelectorKind with
            | MaterializationTargetSelectorKind.DirectoryVersionId ->
                match selector.DirectoryVersionId with
                | Some directoryVersionId -> return! resolveDirectoryVersionSelector repositoryId directoryVersionId correlationId
                | None -> return Error(planError correlationId "DirectoryVersionId selector is required.")
            | MaterializationTargetSelectorKind.ReferenceId ->
                return Error(planError correlationId "ReferenceId Materialization Plan selectors are not implemented by /materialization/plan yet.")
            | MaterializationTargetSelectorKind.BranchName ->
                return Error(planError correlationId "BranchName Materialization Plan selectors are not implemented by /materialization/plan yet.")
            | _ -> return Error(planError correlationId $"Target selector kind '{int selector.SelectorKind}' is not supported.")
        }

    /// Handles POST /materialization/plan.
    let Plan: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context |> parse<PlanParameters>
                    let graceIds = getGraceIds context
                    let repositoryId = resolveRepositoryAuthority graceIds parameters

                    match validatePlanParameters repositoryId parameters correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok request ->
                        match! resolveTargetRoot repositoryId request.TargetSelector correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok (targetRootDirectoryVersionId, actorProxy) ->
                            let! planResult =
                                createDirectPlanForResolvedRoot
                                    request
                                    targetRootDirectoryVersionId
                                    (fun artifactCorrelationId ->
                                        task {
                                            match! Grace.Server.DirectoryVersion.ensureTargetRootProjectionArtifacts actorProxy artifactCorrelationId with
                                            | Ok graceReturnValue -> return Ok graceReturnValue.ReturnValue
                                            | Error error -> return Error error
                                        })
                                    correlationId

                            match planResult with
                            | Error error -> return! context |> result400BadRequest error
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
