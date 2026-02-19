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
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module Artifact =
    let activitySource = new ActivitySource("Artifact")

    let internal buildBlobPath (createdAt: Instant) (artifactId: ArtifactId) =
        let utc = createdAt.ToDateTimeUtc()
        $"grace-artifacts/{utc:yyyy}/{utc:MM}/{utc:dd}/{utc:HH}/{artifactId}"

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

    let private getPrincipal (context: HttpContext) =
        if
            isNull context.User
            || isNull context.User.Identity
            || String.IsNullOrWhiteSpace(context.User.Identity.Name)
        then
            Grace.Shared.Constants.GraceSystemUser
        else
            context.User.Identity.Name

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
                        }

                    let metadata = createMetadata context
                    let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId

                    match! artifactActorProxy.Handle (ArtifactCommand.Create artifactDto) metadata with
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
                        | None ->
                            return!
                                context
                                |> result400BadRequest (GraceError.Create (ArtifactError.getErrorMessage ArtifactError.ArtifactDoesNotExist) correlationId)
                        | Some artifact ->
                            let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId
                            let! repositoryDto = repositoryActorProxy.Get correlationId
                            let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto artifact.BlobPath correlationId

                            let response: ArtifactDownloadUriResult = { ArtifactId = artifactId; DownloadUri = downloadUri }

                            let graceReturnValue = GraceReturnValue.Create response correlationId
                            return! context |> result200Ok graceReturnValue
            }
