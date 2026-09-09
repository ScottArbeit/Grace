namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Artifact
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Artifact
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open System.Threading
open System.IO
open System.Text.Json
open Grace.Types.Usage

/// Contains Grace Server artifact behavior and supporting helpers.
module Artifact =
    let activitySource = new ActivitySource("Artifact")

    /// Places uploaded artifact content under a time-partitioned blob key based on its creation timestamp.
    let internal buildBlobPath (createdAt: Instant) (artifactId: ArtifactId) =
        let utc = createdAt.ToDateTimeUtc()
        $"grace-artifacts/{utc:yyyy}/{utc:MM}/{utc:dd}/{utc:HH}/{artifactId}"

    /// Uses the artifact id as the stable blob key for deterministic or replayable artifact writes.
    let internal buildDeterministicBlobPath (artifactId: ArtifactId) = $"grace-artifacts/by-id/{artifactId}"

    /// Hashes a normalized seed into a deterministic artifact identifier for idempotent artifact creation.
    let internal createDeterministicArtifactId (seed: string) =
        let normalizedSeed =
            if String.IsNullOrWhiteSpace(seed) then
                String.Empty
            else
                seed.Trim().ToLowerInvariant()

        let seedBytes = Encoding.UTF8.GetBytes(normalizedSeed)

        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(seedBytes)
        let guidBytes = hash[0..15]
        guidBytes[7] <- (guidBytes[7] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(guidBytes)

    /// Converts the artifact type parameter into the server union while preserving unknown values as `Other`.
    let internal parseArtifactType (rawArtifactType: string) =
        if String.Equals(rawArtifactType, "AgentSummary", StringComparison.OrdinalIgnoreCase) then
            ArtifactType.AgentSummary
        elif String.Equals(rawArtifactType, "ConflictReport", StringComparison.OrdinalIgnoreCase) then
            ArtifactType.ConflictReport
        elif String.Equals(rawArtifactType, "Prompt", StringComparison.OrdinalIgnoreCase) then
            ArtifactType.Prompt
        elif String.Equals(rawArtifactType, "ValidationOutput", StringComparison.OrdinalIgnoreCase) then
            ArtifactType.ValidationOutput
        elif String.Equals(rawArtifactType, "ReviewNotes", StringComparison.OrdinalIgnoreCase) then
            ArtifactType.ReviewNotes
        elif String.Equals(rawArtifactType, "Other", StringComparison.OrdinalIgnoreCase) then
            ArtifactType.Other "Other"
        else
            ArtifactType.Other rawArtifactType

    /// Reads the authenticated principal name, falling back to the Grace system user for unauthenticated server work.
    let private getPrincipal (context: HttpContext) =
        if
            isNull context.User
            || isNull context.User.Identity
            || String.IsNullOrWhiteSpace(context.User.Identity.Name)
        then
            Grace.Shared.Constants.GraceSystemUser
        else
            context.User.Identity.Name

    /// Reads a non-empty GUID query parameter and maps invalid or missing values to the supplied artifact error.
    let private parseGuidQueryParameter (context: HttpContext) (queryParameterName: string) (error: ArtifactError) =
        match context.TryGetQueryStringValue queryParameterName with
        | Some rawValue when not (String.IsNullOrWhiteSpace rawValue) ->
            let mutable parsed = Guid.Empty

            if
                Guid.TryParse(rawValue, &parsed)
                && parsed <> Guid.Empty
            then
                Ok parsed
            else
                Error error
        | _ -> Error error

    /// Creates artifact metadata and returns upload uri details.
    let Create: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("Create", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<CreateArtifactParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations =
                    [|
                        (if String.IsNullOrWhiteSpace(parameters.ArtifactId) then
                             Ok() |> returnValueTask
                         else
                             Guid.isValidAndNotEmptyGuid parameters.ArtifactId ArtifactError.InvalidArtifactId)
                        String.isNotEmpty parameters.ArtifactType ArtifactError.InvalidArtifactType
                        (if String.IsNullOrWhiteSpace(parameters.WorkItemId) then
                             Ok() |> returnValueTask
                         else
                             Guid.isValidAndNotEmptyGuid parameters.WorkItemId ArtifactError.InvalidArtifactId)
                        String.isNotEmpty parameters.MimeType ArtifactError.InvalidMimeType
                        if parameters.Size >= 0L then Ok() else Error ArtifactError.InvalidSize
                        |> returnValueTask
                    |]

                let! validationsPassed = validations |> allPass

                if validationsPassed then
                    let artifactId =
                        if String.IsNullOrWhiteSpace(parameters.ArtifactId) then
                            Guid.NewGuid()
                        else
                            Guid.Parse(parameters.ArtifactId)

                    let createdAt = getCurrentInstant ()
                    let blobPath = buildBlobPath createdAt artifactId
                    let artifactType = parseArtifactType parameters.ArtifactType
                    let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                    let! repositoryDto = repositoryActorProxy.Get correlationId
                    let! uploadUri = getUriWithWriteSharedAccessSignature repositoryDto blobPath correlationId

                    let artifactDto: ArtifactMetadata =
                        { ArtifactMetadata.Default with
                            ArtifactId = artifactId
                            OwnerId = graceIds.OwnerId
                            OrganizationId = graceIds.OrganizationId
                            RepositoryId = graceIds.RepositoryId
                            ArtifactType = artifactType
                            MimeType = parameters.MimeType
                            Size = parameters.Size
                            Sha256 =
                                if String.IsNullOrWhiteSpace(parameters.Sha256) then
                                    None
                                else
                                    Some(Sha256Hash parameters.Sha256)
                            BlobPath = blobPath
                            CreatedAt = createdAt
                            CreatedBy = UserId(getPrincipal context)
                            WorkItemId =
                                if String.IsNullOrWhiteSpace parameters.WorkItemId then
                                    None
                                else
                                    Some(Guid.Parse parameters.WorkItemId)
                        }

                    let metadata = createMetadata context
                    let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId

                    match! artifactActorProxy.Handle (ArtifactCommand.Create(ArtifactCreated.FromMetadata artifactDto)) metadata with
                    | Error graceError -> return! context |> result400BadRequest graceError
                    | Ok _ ->
                        let response: ArtifactCreateResult = { ArtifactId = artifactId; UploadUri = uploadUri; BlobPath = blobPath }

                        let graceReturnValue =
                            (GraceReturnValue.Create response correlationId)
                                .enhance(getParametersAsDictionary parameters)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof ArtifactId, artifactId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                else
                    let! validationError = validations |> getFirstError
                    let errorMessage = ArtifactError.getErrorMessage (validationError: ArtifactError option)

                    return!
                        context
                        |> result400BadRequest (GraceError.Create errorMessage correlationId)
            }

    /// Gets a read uri for an artifact.
    let GetDownloadUri (artifactId: Guid) : HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("GetDownloadUri", ActivityKind.Server)
                let correlationId = getCorrelationId context

                match parseGuidQueryParameter context "ownerId" ArtifactError.InvalidArtifactId with
                | Error error ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create (ArtifactError.getErrorMessage error) correlationId)
                | Ok ownerId ->
                    match parseGuidQueryParameter context "organizationId" ArtifactError.InvalidArtifactId with
                    | Error error ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create (ArtifactError.getErrorMessage error) correlationId)
                    | Ok organizationId ->
                        match parseGuidQueryParameter context "repositoryId" ArtifactError.InvalidArtifactId with
                        | Error error ->
                            return!
                                context
                                |> result400BadRequest (GraceError.Create (ArtifactError.getErrorMessage error) correlationId)
                        | Ok repositoryId ->
                            let artifactActorProxy = Artifact.CreateActorProxy artifactId repositoryId correlationId

                            match! artifactActorProxy.Get correlationId with
                            | Some artifact when
                                artifact.OwnerId = ownerId
                                && artifact.OrganizationId = organizationId
                                && artifact.RepositoryId = repositoryId
                                && not artifact.IsDeleted
                                ->
                                let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId
                                let! repositoryDto = repositoryActorProxy.Get correlationId
                                let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto artifact.BlobPath correlationId

                                let response: ArtifactDownloadUriResult = { ArtifactId = artifactId; DownloadUri = downloadUri }

                                let graceReturnValue = GraceReturnValue.Create response correlationId
                                return! context |> result200Ok graceReturnValue
                            | None
                            | Some _ ->
                                return!
                                    context
                                    |> result400BadRequest (GraceError.Create (ArtifactError.getErrorMessage ArtifactError.ArtifactDoesNotExist) correlationId)
            }

    /// Keeps declared bytes and distinct identities attached to verified scope and the non-atomic read window.
    type ArtifactSizeDiagnostic =
        {
            Scope: UsageFactScope
            DeclaredArtifactBytes: int64
            DistinctArtifactCount: int64
            EnumerationStartedAt: Instant
            EnumerationFinishedAt: Instant
        }

    /// Rejects name selectors and incomplete identifiers before reading repository state.
    let internal validateArtifactSizeDiagnosticParameters (parameters: Grace.Shared.Parameters.Repository.GetRepositoryParameters) =
        /// Accepts only explicit, non-empty scope identifiers.
        let parseId value =
            match Guid.TryParse(value: string) with
            | true, id when id <> Guid.Empty -> Some id
            | _ -> None

        if isNull (box parameters) then
            Error "A repository scope is required."
        elif
            [
                parameters.OwnerName
                parameters.OrganizationName
                parameters.RepositoryName
            ]
            |> List.exists (String.IsNullOrEmpty >> not)
        then
            Error "Use explicit owner, organization and repository IDs; name selectors are not supported."
        else
            match parseId parameters.OwnerId, parseId parameters.OrganizationId, parseId parameters.RepositoryId with
            | Some owner, Some organization, Some repository -> Ok { OwnerId = owner; OrganizationId = organization; RepositoryId = repository }
            | _ -> Error "OwnerId, OrganizationId and RepositoryId must be non-empty GUIDs."

    /// Rechecks the live repository around enumeration and discards a completed quantity if scope or cancellation changed.
    let internal diagnoseArtifactSizeWith
        (checkArtifactSizeRepository: CancellationToken -> Task<unit>)
        (collect: CancellationToken -> Task<ArtifactSizeDiagnostic>)
        (token: CancellationToken)
        =
        task {
            token.ThrowIfCancellationRequested()
            do! checkArtifactSizeRepository token
            let! result = collect token
            do! checkArtifactSizeRepository token
            token.ThrowIfCancellationRequested()
            return result
        }

    /// Requires a present, nondeleted repository bound to the three requested identifiers.
    let private checkArtifactSizeRepository (scope: UsageFactScope) correlationId (token: CancellationToken) =
        task {
            let repositoryActor = Repository.CreateActorProxy scope.OrganizationId scope.RepositoryId correlationId

            let! repository =
                (repositoryActor.Get correlationId)
                    .WaitAsync(token)

            if repository.RepositoryId <> scope.RepositoryId
               || repository.OwnerId <> scope.OwnerId
               || repository.OrganizationId <> scope.OrganizationId
               || repository.UpdatedAt.IsNone
               || repository.DeletedAt.IsSome then
                raise (InvalidDataException "Repository is missing, deleted or outside the requested scope.")
        }

    /// Serves a fresh declaration scan after route-level SystemAdmin authorization, without publishing partial quantities.
    let DiagnoseSize: HttpHandler =
        fun next context ->
            task {
                let correlationId = Grace.Server.Services.getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<Grace.Shared.Parameters.Repository.GetRepositoryParameters>()

                    match validateArtifactSizeDiagnosticParameters parameters with
                    | Error message -> return! RequestErrors.BAD_REQUEST (GraceError.Create message correlationId) next context
                    | Ok scope ->
                        let! result =
                            diagnoseArtifactSizeWith
                                (checkArtifactSizeRepository scope correlationId)
                                (fun token ->
                                    task {
                                        let! total, count, started, finished = readArtifactSize scope token

                                        return
                                            {
                                                Scope = scope
                                                DeclaredArtifactBytes = total
                                                DistinctArtifactCount = count
                                                EnumerationStartedAt = started
                                                EnumerationFinishedAt = finished
                                            }
                                    })
                                context.RequestAborted

                        context.RequestAborted.ThrowIfCancellationRequested()
                        return! json (GraceReturnValue.Create result correlationId) next context
                with
                | :? JsonException ->
                    return! RequestErrors.BAD_REQUEST (GraceError.Create "The request body must be valid repository-scope JSON." correlationId) next context
                | :? InvalidDataException ->
                    return!
                        RequestErrors.BAD_REQUEST
                            (GraceError.Create "Repository scope or retained Artifact source is invalid; no quantity was produced." correlationId)
                            next
                            context
                | :? OperationCanceledException ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "Artifact enumeration was cancelled; no quantity was produced." correlationId)
                            next
                            context
                | _ ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE (GraceError.Create "Artifact enumeration failed; no quantity was produced." correlationId) next context
            }
