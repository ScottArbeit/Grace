namespace Grace.Server

open Azure
open Azure.Core
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Azure.Storage.Sas
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open Grace.Types.Repository
open Grace.Types.Common
open Grace.Types.Reference
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open System.IO
open System.Net
open System.Text
open Azure.Storage
open System.Diagnostics
open System.Reflection.Metadata
open System.Net.Http.Json

module StorageParameterContracts = Grace.Shared.Parameters.Storage

/// Contains Grace Server storage behavior and supporting helpers.
module Storage =

    let log = ApplicationContext.loggerFactory.CreateLogger("Storage.Server")

    /// Normalizes normalize whole file content reference data for stable server comparisons.
    let private normalizeWholeFileContentReference (fileVersion: FileVersion) =
        if isNull (box fileVersion.ContentReference) then
            FileContentReference.WholeFileContent
        else
            match fileVersion.ContentReference.ReferenceType with
            | FileContentReferenceType.WholeFileContent -> fileVersion.ContentReference
            | FileContentReferenceType.FileManifest ->
                invalidOp "FileManifest content references are not supported by the current storage compatibility endpoints."
            | unsupported -> invalidOp $"Unsupported file content reference type: {unsupported}."

    /// Builds the object key used for whole-file content stored from an upload session.
    let private getWholeFileContentObjectKey (fileVersion: FileVersion) =
        normalizeWholeFileContentReference fileVersion
        |> ignore

        StorageKeys.wholeFileContentObjectKey fileVersion

    /// Builds the object key for readable whole-file content returned by storage materialization.
    let private getReadableWholeFileContentObjectKey (repositoryDto: RepositoryDto) (fileVersion: FileVersion) correlationId =
        task { return getWholeFileContentObjectKey fileVersion }

    /// Builds the object key for a content block within its storage-account shard.
    let private getContentBlockObjectKey (contentBlockAddress: ContentBlockAddress) = StorageKeys.contentBlockObjectKey contentBlockAddress

    /// Implements append shard evidence fragment for the server request pipeline.
    let private appendShardEvidenceFragment (storageAccountName: StorageAccountName) (uri: Uri) =
        let builder = UriBuilder(uri)
        builder.Fragment <- $"graceStorageAccount={Uri.EscapeDataString(storageAccountName)}"
        builder.Uri

    /// Implements invalid content block address error for the server request pipeline.
    let private invalidContentBlockAddressError correlationId =
        GraceError.Create "ContentBlockAddress must be a 64-character hexadecimal BLAKE3 value." correlationId

    /// Implements invalid manifest address error for the server request pipeline.
    let private invalidManifestAddressError correlationId =
        GraceError.Create "ManifestAddress must be a 64-character lowercase hexadecimal BLAKE3 value." correlationId

    /// Validates validate content block address inputs before server processing continues.
    let internal validateContentBlockAddress correlationId (contentBlockAddress: ContentBlockAddress) =
        match ContentAddress.tryNormalizeBlake3Address contentBlockAddress with
        | Some normalizedAddress -> Ok(ContentBlockAddress normalizedAddress)
        | None -> Error(invalidContentBlockAddressError correlationId)

    /// Validates validate supported manifest chunking suite inputs before server processing continues.
    let internal validateSupportedManifestChunkingSuite correlationId (chunkingSuiteId: ChunkingSuiteId) =
        if String.Equals(chunkingSuiteId, RabinChunking.SuiteName, StringComparison.Ordinal) then
            Ok()
        else
            Error(GraceError.Create $"StartManifestUploadSession ChunkingSuiteId must be {RabinChunking.SuiteName}." correlationId)

    /// Validates validate manifest address inputs before server processing continues.
    let private validateManifestAddress correlationId (manifestAddress: ManifestAddress) =
        if ContentAddress.isValidAddress manifestAddress then
            Ok()
        else
            Error(invalidManifestAddressError correlationId)

    /// Implements expected content block storage placement for the server request pipeline.
    let private expectedContentBlockStoragePlacement (route: StoragePoolRouting.StoragePoolRoute) (contentBlockAddress: ContentBlockAddress) eTag =
        StoragePoolRouting.storagePlacementForObjectKey route.Shard (getContentBlockObjectKey contentBlockAddress) eTag

    /// Implements expected content block staging placement for the server request pipeline.
    let private expectedContentBlockStagingPlacement
        (route: StoragePoolRouting.StoragePoolRoute)
        (repositoryId: RepositoryId)
        (uploadSessionId: UploadSessionId)
        (contentBlockAddress: ContentBlockAddress)
        eTag
        =
        getContentBlockStagingPlacement route repositoryId uploadSessionId contentBlockAddress eTag

    /// Validates validate content block staging placement for route inputs before server processing continues.
    let private validateContentBlockStagingPlacementForRoute
        correlationId
        (route: StoragePoolRouting.StoragePoolRoute)
        (repositoryId: RepositoryId)
        (uploadSessionId: UploadSessionId)
        (contentBlockAddress: ContentBlockAddress)
        (placement: ContentBlockStoragePlacement)
        =
        if isNull (box placement) then
            Error(GraceError.Create "StoragePlacement is required." correlationId)
        else
            let expected = expectedContentBlockStagingPlacement route repositoryId uploadSessionId contentBlockAddress placement.ETag

            if placement.StorageAccountName
               <> expected.StorageAccountName then
                Error(
                    GraceError.Create
                        $"StoragePlacement.StorageAccountName must match the selected staging shard. Expected {expected.StorageAccountName}, actual {placement.StorageAccountName}."
                        correlationId
                )
            elif placement.StorageContainerName
                 <> expected.StorageContainerName then
                Error(
                    GraceError.Create
                        $"StoragePlacement.StorageContainerName must match the selected staging shard. Expected {expected.StorageContainerName}, actual {placement.StorageContainerName}."
                        correlationId
                )
            elif placement.ObjectKey <> expected.ObjectKey then
                Error(
                    GraceError.Create
                        $"StoragePlacement.ObjectKey must match the selected staging shard. Expected {expected.ObjectKey}, actual {placement.ObjectKey}."
                        correlationId
                )
            else
                Ok()

    /// Implements content block payload validation error for the server request pipeline.
    let private contentBlockPayloadValidationError contentBlockAddress error correlationId =
        GraceError.Create $"ContentBlock payload for {contentBlockAddress} is invalid: {error}." correlationId

    /// Validates validate content block payload inputs before server processing continues.
    let private validateContentBlockPayload contentBlockAddress (payload: byte array) correlationId =
        if isNull payload then
            Error(GraceError.Create "ContentBlock payload is required." correlationId)
        elif payload.LongLength = 0L then
            Error(GraceError.Create "ContentBlock payload must not be empty." correlationId)
        else
            match ContentBlockFormat.decode payload with
            | Error error -> Error(contentBlockPayloadValidationError contentBlockAddress error correlationId)
            | Ok decodedBlock ->
                match ContentBlockFormat.validateAddress contentBlockAddress decodedBlock with
                | Error error -> Error(contentBlockPayloadValidationError contentBlockAddress error correlationId)
                | Ok () -> Ok()

    /// Implements decode content block payload for the server request pipeline.
    let private decodeContentBlockPayload contentBlockAddress (payload: byte array) correlationId =
        match ContentBlockFormat.decode payload with
        | Error error -> Error(contentBlockPayloadValidationError contentBlockAddress error correlationId)
        | Ok decodedBlock ->
            match ContentBlockFormat.validateAddress contentBlockAddress decodedBlock with
            | Error error -> Error(contentBlockPayloadValidationError contentBlockAddress error correlationId)
            | Ok () -> Ok decodedBlock

    /// Implements content block payloads are equivalent for the server request pipeline.
    let private contentBlockPayloadsAreEquivalent (expected: ContentBlockFormat.DecodedContentBlock) (actual: ContentBlockFormat.DecodedContentBlock) =
        expected.Address = actual.Address
        && expected.Payload.SequenceEqual(actual.Payload)
        && expected.Chunks.Length = actual.Chunks.Length
        && Array.forall2
            (fun (expectedChunk: ContentBlockFormat.ContentBlockChunk) (actualChunk: ContentBlockFormat.ContentBlockChunk) ->
                expectedChunk.LogicalOffset = actualChunk.LogicalOffset
                && expectedChunk.PhysicalOffset = actualChunk.PhysicalOffset
                && expectedChunk.Length = actualChunk.Length
                && expectedChunk.Address = actualChunk.Address
                && expectedChunk.Bytes.SequenceEqual(actualChunk.Bytes))
            expected.Chunks
            actual.Chunks

    /// Coordinates read content block payload from placement processing for Grace Server.
    let private readContentBlockPayloadFromPlacement (placement: ContentBlockStoragePlacement) correlationId =
        task {
            try
                match! getAzureContentBlockClientForPlacement placement correlationId with
                | Error error -> return Error error
                | Ok blobClient ->
                    let! downloadResult = blobClient.DownloadContentAsync()
                    return Ok(downloadResult.Value.Content.ToArray(), Some(downloadResult.Value.Details.ETag.ToString()))
            with
            | :? RequestFailedException as ex ->
                return Error(GraceError.Create $"ContentBlock payload could not be read from object storage: {ex.Message}" correlationId)
        }

    /// Coordinates delete content block payload best effort processing for Grace Server.
    let private deleteContentBlockPayloadBestEffort placement correlationId =
        task {
            match! deleteAzureContentBlockPlacementIfExists placement correlationId with
            | Ok _
            | Error _ -> return ()
        }

    /// Coordinates delete content block staging payload processing for Grace Server.
    let private deleteContentBlockStagingPayload placement correlationId = deleteContentBlockPayloadBestEffort placement correlationId

    /// Represents materialized content block used by Grace Server APIs and background services.
    type private MaterializedContentBlock = { StoragePlacement: ContentBlockStoragePlacement }

    /// Issues a SAS URI for an already-routed content-block placement and rejects missing placement metadata.
    let private createAzureContentBlockSasUriForPlacement (placement: ContentBlockStoragePlacement) permission correlationId =
        task {
            if isNull (box placement) then
                return Error(GraceError.Create "StoragePlacement is required before issuing a ContentBlock SAS URI." correlationId)
            else
                let shard: StoragePoolRouting.StorageShard =
                    { StorageAccountName = placement.StorageAccountName; StorageContainerName = placement.StorageContainerName; ObjectKeyPrefix = String.Empty }

                let route: StoragePoolRouting.StoragePoolRoute =
                    { RepositoryId = RepositoryId.Empty; StoragePoolId = StoragePoolId String.Empty; Shard = shard }

                return! createAzureContentBlockSasUriForObjectKey route placement.ObjectKey permission correlationId
        }

    /// Determines whether existing blob conflict.
    let private isExistingBlobConflict (ex: RequestFailedException) =
        ex.Status = int HttpStatusCode.Conflict
        || ex.Status = int HttpStatusCode.PreconditionFailed
        || String.Equals(ex.ErrorCode, "BlobAlreadyExists", StringComparison.OrdinalIgnoreCase)

    /// Implements materialize validated content block for the server request pipeline.
    let private materializeValidatedContentBlock
        (route: StoragePoolRouting.StoragePoolRoute)
        (contentBlockAddress: ContentBlockAddress)
        (payload: byte array)
        correlationId
        =
        task {
            let finalPlacement = expectedContentBlockStoragePlacement route contentBlockAddress None

            match validateContentBlockPayload contentBlockAddress payload correlationId with
            | Error error -> return Error error
            | Ok () ->
                match! getAzureContentBlockClientForPlacement finalPlacement correlationId with
                | Error error -> return Error error
                | Ok finalBlobClient ->
                    try
                        use payloadStream = new MemoryStream(payload, writable = false)
                        let conditions = BlobRequestConditions()
                        conditions.IfNoneMatch <- ETag.All
                        let uploadOptions = BlobUploadOptions()
                        uploadOptions.Conditions <- conditions
                        let! uploadResult = finalBlobClient.UploadAsync(payloadStream, uploadOptions)

                        return
                            Ok { StoragePlacement = expectedContentBlockStoragePlacement route contentBlockAddress (Some(uploadResult.Value.ETag.ToString())) }
                    with
                    | :? RequestFailedException as ex when isExistingBlobConflict ex ->
                        match! readContentBlockPayloadFromPlacement finalPlacement correlationId with
                        | Error error -> return Error error
                        | Ok (existingPayload, existingETag) ->
                            match decodeContentBlockPayload contentBlockAddress existingPayload correlationId,
                                  decodeContentBlockPayload contentBlockAddress payload correlationId
                                with
                            | Error error, _ ->
                                return
                                    Error(
                                        GraceError.Create
                                            $"Existing final ContentBlock {contentBlockAddress} is invalid and cannot be treated as a successful upload: {error.Error}"
                                            correlationId
                                    )
                            | _, Error error -> return Error error
                            | Ok existingBlock, Ok stagedBlock when not (contentBlockPayloadsAreEquivalent existingBlock stagedBlock) ->
                                return
                                    Error(
                                        GraceError.Create
                                            $"Existing final ContentBlock {contentBlockAddress} does not match the staged validated payload."
                                            correlationId
                                    )
                            | Ok _, Ok _ -> return Ok { StoragePlacement = expectedContentBlockStoragePlacement route contentBlockAddress existingETag }
                    | :? RequestFailedException as ex ->
                        return Error(GraceError.Create $"ContentBlock payload could not be materialized to final CAS storage: {ex.Message}" correlationId)
        }

    /// Resolves resolve storage ids data from request or repository state.
    let private resolveStorageIds (graceIds: GraceIds) (parameters: StorageParameters) =
        let organizationId =
            if graceIds.OrganizationId <> OrganizationId.Empty then
                graceIds.OrganizationId
            else
                OrganizationId.Parse parameters.OrganizationId

        let repositoryId =
            if graceIds.RepositoryId <> RepositoryId.Empty then
                graceIds.RepositoryId
            else
                RepositoryId.Parse parameters.RepositoryId

        organizationId, repositoryId

    /// Resolves resolve repository dedupe storage pool id data from request or repository state.
    let internal resolveRepositoryDedupeStoragePoolId (repositoryDto: RepositoryDto) correlationId =
        try
            Ok(DedupeIndex.storagePoolIdForRepository repositoryDto)
        with
        | ex -> Error(GraceError.Create ex.Message correlationId)

    /// Validates validate repository exists for storage request inputs before server processing continues.
    let internal validateRepositoryExistsForStorageRequest requestedRepositoryId (repositoryDto: RepositoryDto) correlationId =
        if isNull (box repositoryDto)
           || repositoryDto.RepositoryId = RepositoryId.Empty
           || repositoryDto.UpdatedAt.IsNone then
            Error(GraceError.Create (getErrorMessage RepositoryError.RepositoryIdDoesNotExist) correlationId)
        elif repositoryDto.RepositoryId
             <> requestedRepositoryId then
            Error(
                GraceError.Create
                    $"Repository.Get returned repository '{repositoryDto.RepositoryId}' while the storage request targeted '{requestedRepositoryId}'."
                    correlationId
            )
        else
            Ok()

    /// Resolves resolve owner id data from request or repository state.
    let private resolveOwnerId (graceIds: GraceIds) (parameters: StorageParameters) =
        if graceIds.OwnerId <> OwnerId.Empty then
            graceIds.OwnerId
        else
            OwnerId.Parse parameters.OwnerId

    /// Builds request event metadata with the active correlation id and HTTP path for storage actor commands.
    let internal createEventMetadata (context: HttpContext) correlationId =
        let metadata = { createMetadata context with CorrelationId = correlationId }
        metadata.Properties[ "Path" ] <- $"{context.Request.Path}"
        metadata

    /// Represents upload session request context used by Grace Server APIs and background services.
    type private UploadSessionRequestContext =
        {
            UploadSessionActor: IUploadSessionActor
            Metadata: EventMetadata
            RequestRepositoryId: RepositoryId
            SessionForScope: UploadSessionDto
        }

    /// Resolves the request repository and upload-session actor that storage upload-session handlers share.
    let private createUploadSessionRequestContext (context: HttpContext) (parameters: UploadSessionStorageParameters) correlationId =
        task {
            let graceIds = getGraceIds context
            let _, repositoryId = resolveStorageIds graceIds parameters

            let uploadSessionActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy parameters.UploadSessionId repositoryId correlationId

            return
                {
                    UploadSessionActor = uploadSessionActor
                    Metadata = createEventMetadata context correlationId
                    RequestRepositoryId = repositoryId
                    SessionForScope = UploadSessionDto.Default
                }
        }

    /// Coordinates load session for scope processing for Grace Server.
    let private loadSessionForScope (uploadSessionActor: IUploadSessionActor) correlationId =
        task {
            let! session = uploadSessionActor.Get correlationId

            if session.UploadSessionId = UploadSessionId.Empty then
                let! events = uploadSessionActor.GetEvents correlationId

                return
                    events
                    |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default
            else
                return session
        }

    /// Validates validate upload session repository id inputs before server processing continues.
    let internal validateUploadSessionRepositoryId repositoryId (session: UploadSessionDto) correlationId =
        if not (isNull (box session))
           && session.UploadSessionId <> UploadSessionId.Empty
           && session.RepositoryId <> RepositoryId.Empty
           && session.RepositoryId <> repositoryId then
            Error(
                GraceError.Create
                    $"UploadSession RepositoryId must match the request repository. Recorded '{session.RepositoryId}', requested '{repositoryId}'."
                    correlationId
            )
        else
            Ok()

    /// Validates validate start manifest upload session authorized scope inputs before server processing continues.
    let internal validateStartManifestUploadSessionAuthorizedScope correlationId (authorizedScope: RelativePath) =
        let normalizedScope = (normalizeFilePath $"{authorizedScope}").Trim()

        if String.IsNullOrWhiteSpace normalizedScope then
            Error(GraceError.Create "StartManifestUploadSession AuthorizedScope must be an exact file path." correlationId)
        elif normalizedScope = "/" then
            Error(GraceError.Create "StartManifestUploadSession AuthorizedScope must not be the repository root; use the exact file path." correlationId)
        elif normalizedScope.EndsWith("/", StringComparison.Ordinal) then
            Error(GraceError.Create "StartManifestUploadSession AuthorizedScope must not be a directory path; use the exact file path." correlationId)
        else
            Ok()

    /// Validates validate upload session scope inputs before server processing continues.
    let private validateUploadSessionScope requestContext (parameters: UploadSessionStorageParameters) correlationId requireExistingSession =
        task {
            let! sessionForScope = loadSessionForScope requestContext.UploadSessionActor correlationId

            match validateUploadSessionRepositoryId requestContext.RequestRepositoryId sessionForScope correlationId with
            | Error error -> return Error error
            | Ok () when
                requireExistingSession
                && (sessionForScope.UploadSessionId = UploadSessionId.Empty
                    || sessionForScope.LifecycleState = UploadSessionLifecycleState.NotStarted)
                ->
                return Error(GraceError.Create "UploadSession must be started before this operation." correlationId)
            | Ok () when
                sessionForScope.UploadSessionId
                <> UploadSessionId.Empty
                && sessionForScope.AuthorizedScope
                   <> parameters.AuthorizedScope
                ->
                return
                    Error(
                        GraceError.Create
                            $"UploadSession AuthorizedScope must match the scope recorded when the session was started. Expected '{sessionForScope.AuthorizedScope}', actual '{parameters.AuthorizedScope}'."
                            correlationId
                    )
            | Ok () -> return Ok { requestContext with SessionForScope = sessionForScope }
        }

    /// Validates validate active dedupe discovery for claim inputs before server processing continues.
    let private validateActiveDedupeDiscoveryForClaim requestContext (parameters: ClaimReuseRangesParameters) correlationId =
        match requestContext.SessionForScope.DedupeDiscovery with
        | None -> Error(GraceError.Create "Dedupe discovery must be issued before reuse ranges can be claimed." correlationId)
        | Some discovery when
            discovery.OperationId
            <> parameters.DiscoveryOperationId
            ->
            Error(GraceError.Create "ClaimReuseRanges DiscoveryOperationId does not match the active discovery." correlationId)
        | Some discovery when
            requestContext.Metadata.Timestamp
            >= discovery.ExpiresAt
            ->
            Error(GraceError.Create "Dedupe discovery has expired; reuse ranges cannot be claimed." correlationId)
        | Some discovery -> Ok discovery

    /// Implements hint matches issued hint for the server request pipeline.
    let private hintMatchesIssuedHint (hint: ContentBlockReuseRangeHint) (issued: ContentBlockReuseRangeHint) =
        not (isNull (box issued))
        && issued.StoragePoolId = hint.StoragePoolId
        && issued.ContentBlockAddress = hint.ContentBlockAddress
        && issued.OrdinalStart = hint.OrdinalStart
        && issued.OrdinalCount = hint.OrdinalCount
        && issued.MetadataVersion = hint.MetadataVersion

    /// Validates validate issued dedupe discovery hints inputs before server processing continues.
    let private validateIssuedDedupeDiscoveryHints
        correlationId
        storagePoolId
        (keyChunkAddresses: ChunkAddress array)
        (hints: ContentBlockReuseRangeHint array)
        (records: DedupeIndex.DedupeIndexRecord array)
        =
        let boundHints = ResizeArray<ContentBlockReuseRangeHint>()
        let discovery = DedupeIndex.discover storagePoolId keyChunkAddresses (getCurrentInstant ()) records

        let discoveredHints =
            discovery.CandidateContentBlocks
            |> Array.map DedupeIndex.toReuseRangeHint

        let mutable error = None
        let mutable index = 0

        while error.IsNone && index < hints.Length do
            let hint = hints[index]

            if isNull (box hint) then
                error <- Some(GraceError.Create "IssueDedupeDiscovery Hints must not contain null entries." correlationId)
            elif hint.StoragePoolId <> storagePoolId then
                error <- Some(GraceError.Create "IssueDedupeDiscovery Hints must come from server discovery candidates for this repository." correlationId)
            elif keyChunkAddresses.Length = 0 then
                error <-
                    Some(GraceError.Create "IssueDedupeDiscovery Hints require discovery key chunks that reproduce the server discovery result." correlationId)
            else
                match
                    discoveredHints
                    |> Array.tryFind (hintMatchesIssuedHint hint)
                    with
                | Some discoveredHint -> boundHints.Add(discoveredHint)
                | None -> error <- Some(GraceError.Create "IssueDedupeDiscovery Hints must come from server discovery candidates." correlationId)

            index <- index + 1

        match error with
        | Some error -> Error error
        | None -> Ok(boundHints.ToArray())

    /// Validates validate claim reuse hints inputs before server processing continues.
    let private validateClaimReuseHints correlationId storagePoolId (discovery: DedupeDiscoverySnapshot) (hints: ContentBlockReuseRangeHint array) =
        let boundHints = ResizeArray<ContentBlockReuseRangeHint>()
        let issuedHints = if isNull discovery.Hints then Array.empty else discovery.Hints

        let mutable error = None
        let mutable index = 0

        while error.IsNone && index < hints.Length do
            let hint = hints[index]

            if isNull (box hint) then
                error <- Some(GraceError.Create "ClaimReuseRanges Hints must not contain null entries." correlationId)
            elif hint.StoragePoolId <> storagePoolId then
                error <- Some(GraceError.Create "ClaimReuseRanges Hints must belong to the upload session repository storage pool." correlationId)
            else
                match
                    issuedHints
                    |> Array.tryFind (hintMatchesIssuedHint hint)
                    with
                | Some issuedHint -> boundHints.Add(issuedHint)
                | None -> error <- Some(GraceError.Create "ClaimReuseRanges Hints must have been issued by the active dedupe discovery." correlationId)

            index <- index + 1

        match error with
        | Some error -> Error error
        | None -> Ok(boundHints.ToArray())

    /// Coordinates handle upload session command processing for Grace Server.
    let private handleUploadSessionCommand
        (context: HttpContext)
        (parameters: UploadSessionStorageParameters)
        (command: UploadSessionCommand)
        (correlationId: CorrelationId)
        =
        task {
            let! scopeValidation =
                task {
                    let! requestContext = createUploadSessionRequestContext context parameters correlationId

                    match command with
                    | UploadSessionCommand.Start _ -> return Ok requestContext
                    | _ -> return! validateUploadSessionScope requestContext parameters correlationId false
                }

            match scopeValidation with
            | Error error -> return! context |> result400BadRequest error
            | Ok requestContext ->
                let! result = requestContext.UploadSessionActor.Handle command requestContext.Metadata

                match result with
                | Ok returnValue -> return! context |> result200Ok returnValue
                | Error error -> return! context |> result400BadRequest error
        }

    /// Represents hydrated finalize evidence used by Grace Server APIs and background services.
    type private HydratedFinalizeEvidence = { BlockPayloads: FinalizeManifestBlockPayload array; ClaimedMetadata: ContentBlockMetadata array }

    /// Implements manifest blocks for payload hydration for the server request pipeline.
    let private manifestBlocksForPayloadHydration (manifest: FileManifest) =
        let seen = HashSet<ContentBlockAddress>()
        let blocks = ResizeArray<ContentBlock>()

        if
            not (isNull (box manifest))
            && not (isNull manifest.Blocks)
        then
            for block in manifest.Blocks do
                if
                    not (isNull (box block))
                    && seen.Add(block.Address)
                then
                    blocks.Add block

        blocks.ToArray()

    /// Implements manifest block was uploaded for the server request pipeline.
    let private manifestBlockWasUploaded (session: UploadSessionDto) (block: ContentBlock) =
        session.ConfirmedBlockUploads
        |> Array.exists (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = block.Address)
        && session.BlockUploadIntents
           |> Array.exists (fun intent ->
               intent.ContentBlockAddress = block.Address
               && intent.LogicalOffset = block.Offset
               && intent.LogicalLength = block.Size)

    /// Implements manifest blocks requiring claimed metadata for the server request pipeline.
    let private manifestBlocksRequiringClaimedMetadata (session: UploadSessionDto) (manifest: FileManifest) =
        if isNull (box manifest) || isNull manifest.Blocks then
            Array.empty
        else
            manifest.Blocks
            |> Seq.filter (fun block ->
                not (isNull (box block))
                && not (manifestBlockWasUploaded session block))
            |> Seq.toArray

    /// Coordinates read finalize block payload from placement processing for Grace Server.
    let private readFinalizeBlockPayloadFromPlacement contentBlockAddress placement correlationId =
        task {
            match! readContentBlockPayloadFromPlacement placement correlationId with
            | Ok (payload, _) -> return Ok({ Address = contentBlockAddress; Payload = payload })
            | Error error ->
                return
                    Error(
                        GraceError.Create
                            $"Finalized ContentBlock payload {contentBlockAddress} could not be read from authoritative object storage: {error.Error}"
                            correlationId
                    )
        }

    /// Implements complete content block storage placement for the server request pipeline.
    let private completeContentBlockStoragePlacement (placement: ContentBlockStoragePlacement) =
        not (isNull (box placement))
        && not (String.IsNullOrWhiteSpace placement.StorageAccountName)
        && not (String.IsNullOrWhiteSpace placement.StorageContainerName)
        && not (String.IsNullOrWhiteSpace placement.ObjectKey)

    /// Implements has authoritative claimed range for the server request pipeline.
    let private hasAuthoritativeClaimedRange (claimedRange: ClaimedReuseRange) (metadata: ContentBlockMetadata) =
        if isNull (box claimedRange) || isNull (box metadata) then
            false
        else
            let query = { OrdinalStart = claimedRange.OrdinalStart; OrdinalCount = claimedRange.OrdinalCount }

            metadata.StoragePoolId = claimedRange.StoragePoolId
            && metadata.ContentBlockAddress = claimedRange.ContentBlockAddress
            && metadata.BlockFormatVersion > 0s
            && completeContentBlockStoragePlacement metadata.StoragePlacement
            && not (isNull metadata.Ranges)
            && Grace.Types.ContentBlockMetadata.findRanges metadata query
               |> Array.exists (fun range ->
                   range.OrdinalStart = claimedRange.OrdinalStart
                   && range.OrdinalCount = claimedRange.OrdinalCount
                   && range.PhysicalOffset = claimedRange.PhysicalOffset
                   && range.PhysicalLength = claimedRange.PhysicalLength)

    /// Implements has authoritative finalized logical range for the server request pipeline.
    let private hasAuthoritativeFinalizedLogicalRange (claimedRange: ClaimedReuseRange) (metadata: ContentBlockMetadata) =
        if isNull (box claimedRange) || isNull (box metadata) then
            false
        else
            let query = { OrdinalStart = claimedRange.OrdinalStart; OrdinalCount = claimedRange.OrdinalCount }

            metadata.StoragePoolId = claimedRange.StoragePoolId
            && metadata.ContentBlockAddress = claimedRange.ContentBlockAddress
            && metadata.BlockFormatVersion > 0s
            && completeContentBlockStoragePlacement metadata.StoragePlacement
            && not (isNull metadata.Ranges)
            && Grace.Types.ContentBlockMetadata.findRanges metadata query
               |> Array.exists (fun range ->
                   range.OrdinalStart = claimedRange.OrdinalStart
                   && range.OrdinalCount = claimedRange.OrdinalCount
                   && range.PhysicalLength = claimedRange.PhysicalLength
                   && range.ActiveManifestCount > 0)

    /// Implements authoritative claimed range matches for finalize replay for the server request pipeline.
    let internal authoritativeClaimedRangeMatchesForFinalizeReplay (claimedRange: ClaimedReuseRange) (metadata: ContentBlockMetadata) =
        hasAuthoritativeClaimedRange claimedRange metadata
        || hasAuthoritativeFinalizedLogicalRange claimedRange metadata

    /// Implements authoritative claimed range matches for finalize for the server request pipeline.
    let internal authoritativeClaimedRangeMatchesForFinalize (claimedRange: ClaimedReuseRange) (metadata: ContentBlockMetadata) =
        hasAuthoritativeClaimedRange claimedRange metadata
        || hasAuthoritativeFinalizedLogicalRange claimedRange metadata

    /// Implements compare claimed range newest first for the server request pipeline.
    let private compareClaimedRangeNewestFirst (left: ClaimedReuseRange) (right: ClaimedReuseRange) =
        let versionComparison = compare right.MetadataVersion left.MetadataVersion

        if versionComparison <> 0 then
            versionComparison
        else
            compare right.ClaimedAt left.ClaimedAt

    /// Attempts to select claimed range cover and returns an option or result instead of throwing.
    let private trySelectClaimedRangeCover blockLength (claimedRanges: ClaimedReuseRange array) =
        if blockLength <= 0L || isNull claimedRanges then
            None
        else
            let candidates =
                claimedRanges
                |> Array.filter (fun claimedRange ->
                    not (isNull (box claimedRange))
                    && claimedRange.OrdinalStart >= 0
                    && claimedRange.OrdinalCount > 0
                    && claimedRange.PhysicalOffset >= 0L
                    && claimedRange.PhysicalLength > 0L
                    && claimedRange.PhysicalLength <= blockLength)

            /// Implements rec for the server request pipeline.
            let rec selectCover nextOrdinal selectedLength selected =
                if selectedLength = blockLength then
                    selected |> List.rev |> List.toArray |> Some
                elif selectedLength > blockLength then
                    None
                else
                    candidates
                    |> Array.filter (fun claimedRange -> claimedRange.OrdinalStart = nextOrdinal)
                    |> Array.sortWith compareClaimedRangeNewestFirst
                    |> Array.tryPick (fun claimedRange ->
                        selectCover
                            (claimedRange.OrdinalStart
                             + claimedRange.OrdinalCount)
                            (selectedLength + claimedRange.PhysicalLength)
                            (claimedRange :: selected))

            selectCover 0 0L []

    /// Implements claimed reuse ranges cover manifest block for the server request pipeline.
    let internal claimedReuseRangesCoverManifestBlock physicalLength claimedRanges =
        trySelectClaimedRangeCover physicalLength claimedRanges
        |> Option.isSome

    /// Implements claimed ranges for manifest block newest first for the server request pipeline.
    let private claimedRangesForManifestBlockNewestFirst (session: UploadSessionDto) contentBlockAddress =
        if isNull session.ClaimedReuseRanges then
            Array.empty
        else
            session.ClaimedReuseRanges
            |> Array.filter (fun claimedRange ->
                not (isNull (box claimedRange))
                && claimedRange.ContentBlockAddress = contentBlockAddress)
            |> Array.sortWith compareClaimedRangeNewestFirst

    /// Attempts to load authoritative content block metadata for finalize and returns an option or result instead of throwing.
    let private tryLoadAuthoritativeContentBlockMetadataForFinalize (requestContext: UploadSessionRequestContext) contentBlockAddress correlationId =
        task {
            let metadataActor =
                grainFactory.CreateActorProxyWithCorrelationId<IContentBlockMetadataActor>(
                    Grace.Actors.ContentBlockMetadataActorKey.Create requestContext.SessionForScope.StoragePoolId contentBlockAddress,
                    correlationId
                )

            let! authoritativeMetadata = metadataActor.Get correlationId

            match authoritativeMetadata with
            | None -> return Ok None
            | Some metadata when
                metadata.StoragePoolId
                <> requestContext.SessionForScope.StoragePoolId
                ->
                return
                    Error(
                        GraceError.Create
                            $"Authoritative ContentBlockMetadata StoragePoolId does not match upload session StoragePoolId for {contentBlockAddress}."
                            correlationId
                    )
            | Some metadata when
                metadata.ContentBlockAddress
                <> contentBlockAddress
                ->
                return
                    Error(
                        GraceError.Create
                            $"Authoritative ContentBlockMetadata ContentBlockAddress does not match finalized manifest block {contentBlockAddress}."
                            correlationId
                    )
            | Some metadata when metadata.BlockFormatVersion <= 0s ->
                return
                    Error(
                        GraceError.Create
                            $"Authoritative ContentBlockMetadata BlockFormatVersion is required for finalized manifest block {contentBlockAddress}."
                            correlationId
                    )
            | Some metadata when not (completeContentBlockStoragePlacement metadata.StoragePlacement) ->
                return
                    Error(
                        GraceError.Create
                            $"Authoritative ContentBlockMetadata StoragePlacement is incomplete for finalized manifest block {contentBlockAddress}."
                            correlationId
                    )
            | Some metadata -> return Ok(Some metadata)
        }

    /// Coordinates load authoritative content block metadata for finalize processing for Grace Server.
    let private loadAuthoritativeContentBlockMetadataForFinalize requestContext contentBlockAddress correlationId =
        task {
            match! tryLoadAuthoritativeContentBlockMetadataForFinalize requestContext contentBlockAddress correlationId with
            | Ok (Some metadata) -> return Ok metadata
            | Ok None ->
                return
                    Error(GraceError.Create $"Authoritative ContentBlockMetadata is absent for finalized manifest block {contentBlockAddress}." correlationId)
            | Error error -> return Error error
        }

    /// Attempts to read finalize block payload from authoritative metadata and returns an option or result instead of throwing.
    let private tryReadFinalizeBlockPayloadFromAuthoritativeMetadata (requestContext: UploadSessionRequestContext) contentBlockAddress correlationId =
        task {
            match! tryLoadAuthoritativeContentBlockMetadataForFinalize requestContext contentBlockAddress correlationId with
            | Ok (Some metadata) ->
                match! readFinalizeBlockPayloadFromPlacement contentBlockAddress metadata.StoragePlacement correlationId with
                | Ok payload -> return Ok(Some payload)
                | Error error -> return Error error
            | Ok None -> return Ok None
            | Error error -> return Error error
        }

    /// Coordinates load claimed metadata for finalize with processing for Grace Server.
    let private loadClaimedMetadataForFinalizeWith
        (rangeMatches: ClaimedReuseRange -> ContentBlockMetadata -> bool)
        (evidenceMetadata: ClaimedReuseRange -> ContentBlockMetadata -> ContentBlockMetadata)
        staleOrIncompleteMessage
        (requestContext: UploadSessionRequestContext)
        (manifest: FileManifest)
        correlationId
        =
        task {
            let storagePoolId = requestContext.SessionForScope.StoragePoolId

            let manifestBlocks = manifestBlocksRequiringClaimedMetadata requestContext.SessionForScope manifest

            let metadata = ResizeArray<ContentBlockMetadata>()
            let mutable error = None
            let mutable index = 0

            while error.IsNone && index < manifestBlocks.Length do
                let block = manifestBlocks[index]
                let candidates = claimedRangesForManifestBlockNewestFirst requestContext.SessionForScope block.Address
                let validatedCandidates = ResizeArray<ClaimedReuseRange * ContentBlockMetadata>()
                let mutable firstError = None
                let mutable candidateIndex = 0

                while candidateIndex < candidates.Length do
                    let claimedRange = candidates[candidateIndex]

                    if claimedRange.StoragePoolId <> storagePoolId then
                        if firstError.IsNone then
                            firstError <-
                                Some(
                                    GraceError.Create
                                        "Claimed reuse range StoragePoolId must match the upload session StoragePoolId before finalization."
                                        correlationId
                                )
                    else
                        let metadataActor =
                            grainFactory.CreateActorProxyWithCorrelationId<IContentBlockMetadataActor>(
                                Grace.Actors.ContentBlockMetadataActorKey.Create claimedRange.StoragePoolId claimedRange.ContentBlockAddress,
                                correlationId
                            )

                        let! authoritativeMetadata = metadataActor.Get correlationId

                        match authoritativeMetadata with
                        | None ->
                            if firstError.IsNone then
                                firstError <-
                                    Some(
                                        GraceError.Create
                                            $"Authoritative ContentBlockMetadata is absent for {claimedRange.ContentBlockAddress}; claimed reuse cannot be finalized."
                                            correlationId
                                    )
                        | Some authoritativeMetadata when not (rangeMatches claimedRange authoritativeMetadata) ->
                            if firstError.IsNone then
                                firstError <- Some(GraceError.Create $"{staleOrIncompleteMessage} {claimedRange.ContentBlockAddress}." correlationId)
                        | Some authoritativeMetadata -> validatedCandidates.Add(claimedRange, authoritativeMetadata)

                    candidateIndex <- candidateIndex + 1

                let validatedClaims = validatedCandidates |> Seq.map fst |> Seq.toArray

                match trySelectClaimedRangeCover block.Size validatedClaims with
                | Some coveredClaims ->
                    let mutable coveredClaimIndex = 0

                    while coveredClaimIndex < coveredClaims.Length do
                        let coveredClaim = coveredClaims[coveredClaimIndex]

                        match validatedCandidates
                              |> Seq.tryFind (fun (validatedClaim, _) -> validatedClaim = coveredClaim)
                            with
                        | Some (validatedClaim, authoritativeMetadata) -> metadata.Add(evidenceMetadata validatedClaim authoritativeMetadata)
                        | None -> ()

                        coveredClaimIndex <- coveredClaimIndex + 1
                | None ->
                    match firstError with
                    | Some validationError -> error <- Some validationError
                    | None ->
                        error <-
                            Some(
                                GraceError.Create
                                    $"A claimed reuse range set covering manifest block {block.Address} length {block.Size} is required before finalization."
                                    correlationId
                            )

                index <- index + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok(metadata.ToArray())
        }

    /// Coordinates load claimed metadata for finalize processing for Grace Server.
    let private loadClaimedMetadataForFinalize (requestContext: UploadSessionRequestContext) (manifest: FileManifest) correlationId =
        loadClaimedMetadataForFinalizeWith
            authoritativeClaimedRangeMatchesForFinalize
            (fun _ authoritativeMetadata -> authoritativeMetadata)
            "Authoritative ContentBlockMetadata is stale or incomplete for claimed reuse range"
            requestContext
            manifest
            correlationId

    /// Implements hydrate finalize evidence from claimed metadata for the server request pipeline.
    let private hydrateFinalizeEvidenceFromClaimedMetadata
        (requestContext: UploadSessionRequestContext)
        (parameters: FinalizeManifestUploadParameters)
        (manifest: FileManifest)
        (claimedMetadata: ContentBlockMetadata array)
        useRequestBlockPayloads
        (correlationId: CorrelationId)
        =
        task {
            let payloads = ResizeArray<FinalizeManifestBlockPayload>()
            let mutable error = None
            let mutable index = 0
            let requestPayloads = Dictionary<ContentBlockAddress, FinalizeManifestBlockPayload>()

            if useRequestBlockPayloads
               && not (isNull parameters.BlockPayloads)
               && parameters.BlockPayloads.Length > 0 then
                for payload in parameters.BlockPayloads do
                    if not (isNull (box payload)) then requestPayloads[payload.Address] <- payload

            let manifestBlocks = manifestBlocksForPayloadHydration manifest

            let confirmedBlockUploads =
                if isNull requestContext.SessionForScope.ConfirmedBlockUploads then
                    Array.empty
                else
                    requestContext.SessionForScope.ConfirmedBlockUploads

            while index < manifestBlocks.Length
                  && Option.isNone error do
                let block = manifestBlocks[index]
                let address = block.Address

                let claimedMetadataForAddress =
                    claimedMetadata
                    |> Array.tryFind (fun metadata -> metadata.ContentBlockAddress = address)

                match claimedMetadataForAddress with
                | Some metadata ->
                    match! readFinalizeBlockPayloadFromPlacement address metadata.StoragePlacement correlationId with
                    | Ok payload -> payloads.Add payload
                    | Error downloadError -> error <- Some downloadError
                | None ->
                    match requestPayloads.TryGetValue address with
                    | true, payload -> payloads.Add payload
                    | false, _ when manifestBlockWasUploaded requestContext.SessionForScope block ->
                        match! tryReadFinalizeBlockPayloadFromAuthoritativeMetadata requestContext address correlationId with
                        | Error downloadError -> error <- Some downloadError
                        | Ok (Some payload) -> payloads.Add payload
                        | Ok None ->
                            match confirmedBlockUploads
                                  |> Array.tryFind (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = address)
                                with
                            | Some confirmedBlock ->
                                match! readFinalizeBlockPayloadFromPlacement confirmedBlock.ContentBlockAddress confirmedBlock.StoragePlacement correlationId
                                    with
                                | Ok payload -> payloads.Add payload
                                | Error downloadError -> error <- Some downloadError
                            | None -> ()
                    | false, _ -> ()

                index <- index + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok { BlockPayloads = payloads.ToArray(); ClaimedMetadata = claimedMetadata }
        }

    /// Implements hydrate finalize evidence for the server request pipeline.
    let private hydrateFinalizeEvidence
        (requestContext: UploadSessionRequestContext)
        (parameters: FinalizeManifestUploadParameters)
        (manifest: FileManifest)
        useRequestBlockPayloads
        (correlationId: CorrelationId)
        =
        task {
            match! loadClaimedMetadataForFinalize requestContext manifest correlationId with
            | Error error -> return Error error
            | Ok claimedMetadata ->
                return! hydrateFinalizeEvidenceFromClaimedMetadata requestContext parameters manifest claimedMetadata useRequestBlockPayloads correlationId
        }

    /// Implements hydrate finalize replay evidence from current metadata for the server request pipeline.
    let private hydrateFinalizeReplayEvidenceFromCurrentMetadata
        (requestContext: UploadSessionRequestContext)
        (manifest: FileManifest)
        (correlationId: CorrelationId)
        =
        task {
            let payloads = ResizeArray<FinalizeManifestBlockPayload>()
            let metadata = ResizeArray<ContentBlockMetadata>()
            let manifestBlocks = manifestBlocksForPayloadHydration manifest

            let confirmedBlockUploads =
                if isNull requestContext.SessionForScope.ConfirmedBlockUploads then
                    Array.empty
                else
                    requestContext.SessionForScope.ConfirmedBlockUploads

            let mutable error = None
            let mutable index = 0

            while index < manifestBlocks.Length
                  && Option.isNone error do
                let block = manifestBlocks[index]

                match! tryLoadAuthoritativeContentBlockMetadataForFinalize requestContext block.Address correlationId with
                | Error metadataError -> error <- Some metadataError
                | Ok (Some authoritativeMetadata) ->
                    metadata.Add authoritativeMetadata

                    match! readFinalizeBlockPayloadFromPlacement block.Address authoritativeMetadata.StoragePlacement correlationId with
                    | Ok payload -> payloads.Add payload
                    | Error payloadError -> error <- Some payloadError
                | Ok None ->
                    match confirmedBlockUploads
                          |> Array.tryFind (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = block.Address)
                        with
                    | Some confirmedBlock ->
                        match! readFinalizeBlockPayloadFromPlacement confirmedBlock.ContentBlockAddress confirmedBlock.StoragePlacement correlationId with
                        | Ok payload -> payloads.Add payload
                        | Error payloadError -> error <- Some payloadError
                    | None ->
                        error <-
                            Some(
                                GraceError.Create
                                    $"Authoritative ContentBlockMetadata is absent for finalized manifest block {block.Address}, and no confirmed upload placement remains in the upload session."
                                    correlationId
                            )

                index <- index + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok { BlockPayloads = payloads.ToArray(); ClaimedMetadata = metadata.ToArray() }
        }

    /// Implements hydrate finalize replay evidence for the server request pipeline.
    let private hydrateFinalizeReplayEvidence
        (requestContext: UploadSessionRequestContext)
        (_parameters: FinalizeManifestUploadParameters)
        (manifest: FileManifest)
        (correlationId: CorrelationId)
        =
        hydrateFinalizeReplayEvidenceFromCurrentMetadata requestContext manifest correlationId

    /// Looks up whether a finalize operation id already produced a manifest address for replay handling.
    let private getFinalizeReplayState requestContext operationId correlationId =
        task {
            let! events = requestContext.UploadSessionActor.GetEvents correlationId

            /// Implements event operation id for the server request pipeline.
            let eventOperationId (uploadSessionEvent: UploadSessionEvent) =
                match uploadSessionEvent.Event with
                | UploadSessionEventType.Started start -> Some start.OperationId
                | UploadSessionEventType.Abandoned operationId
                | UploadSessionEventType.Expired operationId
                | UploadSessionEventType.PhysicalStateDeleted operationId -> Some operationId
                | UploadSessionEventType.Finalized (operationId, _)
                | UploadSessionEventType.CleanupReminderScheduled (operationId, _)
                | UploadSessionEventType.BlockUploadIntentRegistered (operationId, _)
                | UploadSessionEventType.BlockUploadConfirmed (operationId, _)
                | UploadSessionEventType.DedupeDiscoveryIssued (operationId, _)
                | UploadSessionEventType.ReuseRangesClaimed (operationId, _) -> Some operationId

            let operationAlreadyApplied =
                events
                |> Seq.exists (fun uploadSessionEvent -> eventOperationId uploadSessionEvent = Some operationId)

            let finalizedManifestAddress =
                events
                |> Seq.tryPick (fun uploadSessionEvent ->
                    match uploadSessionEvent.Event with
                    | UploadSessionEventType.Finalized (finalizeOperationId, manifestAddress) when finalizeOperationId = operationId -> Some manifestAddress
                    | _ -> None)

            return operationAlreadyApplied, finalizedManifestAddress
        }

    /// Implements operation already applied to different upload session event error for the server request pipeline.
    let private operationAlreadyAppliedToDifferentUploadSessionEventError operationId correlationId =
        GraceError.Create $"UploadSession OperationId {operationId} was already applied to a non-finalize event and cannot finalize this session." correlationId

    /// Validates validate finalize replay manifest address inputs before server processing continues.
    let private validateFinalizeReplayManifestAddress finalizedManifestAddress (manifest: FileManifest) correlationId =
        if isNull (box manifest) then
            Error(GraceError.Create "FileManifest is required for finalize replay." correlationId)
        elif manifest.ManifestAddress
             <> finalizedManifestAddress then
            Error(
                GraceError.Create
                    $"Finalize replay ManifestAddress must match the durable finalized manifest address. Expected {finalizedManifestAddress}, actual {manifest.ManifestAddress}."
                    correlationId
            )
        else
            Ok()

    /// Validates validate finalize replay evidence inputs before server processing continues.
    let private validateFinalizeReplayEvidence
        (session: UploadSessionDto)
        finalizedManifestAddress
        (manifest: FileManifest)
        (evidence: HydratedFinalizeEvidence)
        correlationId
        =
        validateFinalizeReplayManifestAddress finalizedManifestAddress manifest correlationId
        |> Result.bind (fun () ->
            if manifest.Size <> session.ExpectedSize then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest Size must match UploadSession ExpectedSize. Expected {session.ExpectedSize}, actual {manifest.Size}."
                        correlationId
                )
            elif manifest.FileContentHash
                 <> session.FileContentHash then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest FileContentHash must match UploadSession FileContentHash. Expected {session.FileContentHash}, actual {manifest.FileContentHash}."
                        correlationId
                )
            elif manifest.ChunkingSuiteId
                 <> session.ChunkingSuiteId then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest ChunkingSuiteId must match UploadSession ChunkingSuiteId. Expected {session.ChunkingSuiteId}, actual {manifest.ChunkingSuiteId}."
                        correlationId
                )
            elif String.IsNullOrWhiteSpace manifest.StoragePoolId then
                Error(GraceError.Create "Finalize replay FileManifest StoragePoolId must be recorded before hydration." correlationId)
            elif manifest.StoragePoolId <> session.StoragePoolId then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest StoragePoolId must match UploadSession StoragePoolId. Expected {session.StoragePoolId}, actual {manifest.StoragePoolId}."
                        correlationId
                )
            else
                let blockPayloads =
                    evidence.BlockPayloads
                    |> Array.map (fun payload ->
                        if isNull (box payload) then
                            Unchecked.defaultof<ManifestValidation.ManifestBlockPayload>
                        else
                            ManifestValidation.createBlockPayload payload.Address payload.Payload)

                match ManifestValidation.validate session.ChunkingSuiteId manifest blockPayloads with
                | Ok _ -> Ok()
                | Error validationError ->
                    Error(
                        GraceError.Create
                            $"Finalize replay evidence must reconstruct the durable manifest before side-effect repair: {validationError}."
                            correlationId
                    ))

    /// Validates validate finalize replay manifest before hydration inputs before server processing continues.
    let internal validateFinalizeReplayManifestBeforeHydration (session: UploadSessionDto) finalizedManifestAddress (manifest: FileManifest) correlationId =
        validateFinalizeReplayManifestAddress finalizedManifestAddress manifest correlationId
        |> Result.bind (fun () ->
            if manifest.Size <> session.ExpectedSize then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest Size must match UploadSession ExpectedSize. Expected {session.ExpectedSize}, actual {manifest.Size}."
                        correlationId
                )
            elif manifest.FileContentHash
                 <> session.FileContentHash then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest FileContentHash must match UploadSession FileContentHash. Expected {session.FileContentHash}, actual {manifest.FileContentHash}."
                        correlationId
                )
            elif manifest.ChunkingSuiteId
                 <> session.ChunkingSuiteId then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest ChunkingSuiteId must match UploadSession ChunkingSuiteId. Expected {session.ChunkingSuiteId}, actual {manifest.ChunkingSuiteId}."
                        correlationId
                )
            elif String.IsNullOrWhiteSpace manifest.StoragePoolId then
                Error(GraceError.Create "Finalize replay FileManifest StoragePoolId must be recorded before hydration." correlationId)
            elif manifest.StoragePoolId <> session.StoragePoolId then
                Error(
                    GraceError.Create
                        $"Finalize replay FileManifest StoragePoolId must match UploadSession StoragePoolId. Expected {session.StoragePoolId}, actual {manifest.StoragePoolId}."
                        correlationId
                )
            elif isNull manifest.Blocks
                 || manifest.Blocks.Count = 0 then
                Error(GraceError.Create "Finalize replay FileManifest must include ContentBlocks before hydration." correlationId)
            else
                let mutable expectedOffset = 0L
                let mutable error = None
                let mutable index = 0

                while index < manifest.Blocks.Count
                      && Option.isNone error do
                    let block = manifest.Blocks[index]

                    if isNull (box block) then
                        error <- Some(GraceError.Create $"Finalize replay FileManifest block {index} is required before hydration." correlationId)
                    elif not (ContentAddress.isValidAddress block.Address) then
                        error <-
                            Some(
                                GraceError.Create
                                    $"Finalize replay FileManifest block {index} has an invalid ContentBlockAddress before hydration."
                                    correlationId
                            )
                    elif block.Size <= 0L then
                        error <- Some(GraceError.Create $"Finalize replay FileManifest block {index} must have a positive Size before hydration." correlationId)
                    elif block.Offset <> expectedOffset then
                        error <-
                            Some(
                                GraceError.Create
                                    $"Finalize replay FileManifest block {index} must be contiguous before hydration. Expected offset {expectedOffset}, actual {block.Offset}."
                                    correlationId
                            )
                    else
                        expectedOffset <- expectedOffset + block.Size

                    index <- index + 1

                match error with
                | Some error -> Error error
                | None when expectedOffset <> manifest.Size ->
                    Error(
                        GraceError.Create
                            $"Finalize replay FileManifest blocks must cover Size before hydration. Expected {manifest.Size}, actual {expectedOffset}."
                            correlationId
                    )
                | None ->
                    let expectedManifestAddress = ContentAddress.computeManifestAddressForManifest manifest

                    if manifest.ManifestAddress
                       <> expectedManifestAddress then
                        Error(
                            GraceError.Create
                                $"Finalize replay ManifestAddress must match stable replay manifest address before hydration. Expected {expectedManifestAddress}, actual {manifest.ManifestAddress}."
                                correlationId
                        )
                    else
                        Ok())

    /// Advertises the conservative content-block discovery limits used while positive candidate reuse is disabled.
    let private createDiscoveryPolicy () : StorageParameterContracts.ContentBlockDiscoveryPolicy =
        {
            MaxKeyChunkAddresses = StorageParameterContracts.MaxDiscoveryKeyChunkAddresses
            MaxCandidateWindowsPerKeyChunk = StorageParameterContracts.MaxCandidateWindowsPerKeyChunk
            MaxWindowChunks = StorageParameterContracts.MaxWindowChunks
            MaxResponseProtectedChunks = StorageParameterContracts.MaxResponseProtectedChunks
            ResponseTtlSeconds = StorageParameterContracts.ResponseTtlSeconds
            MinimumAcceptedReuseRunLength = StorageParameterContracts.MinimumAcceptedReuseRunLength
            PositiveCandidatesEnabled = false
            EmptyResponseMeansAbsent = false
            IsAuthoritative = false
        }

    /// Returns a partial, non-authoritative discovery response when the server has no reusable candidates to expose.
    let private createEmptyDiscoveryResult requestedKeyChunkCount : StorageParameterContracts.DiscoverContentBlocksResult =
        {
            RequestedKeyChunkCount = requestedKeyChunkCount
            AcceptedKeyChunkCount = requestedKeyChunkCount
            Policy = createDiscoveryPolicy ()
            CandidateContentBlocks = Array.empty
            IsPartial = true
            Message = "No positive ContentBlock candidates are returned yet. Empty discovery results are non-authoritative and do not prove absence."
        }

    /// Gets the metadata stored in the object storage provider for the specified file.
    let getFileMetadata (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (context: HttpContext) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                let! blobName = getReadableWholeFileContentObjectKey repositoryDto fileVersion (getCorrelationId context)
                let! blobClient = getAzureBlobClient repositoryDto blobName (getCorrelationId context)
                let! azureResponse = blobClient.GetPropertiesAsync()
                let blobProperties = azureResponse.Value
                return Ok(blobProperties.Metadata :?> IReadOnlyDictionary<string, string>)
            | AWSS3 -> return Error(getErrorMessage StorageError.NotImplemented)
            | GoogleCloudStorage -> return Error(getErrorMessage StorageError.NotImplemented)
            | ObjectStorageProvider.Unknown ->
                logToConsole
                    $"Error: Unknown ObjectStorageProvider in getFileMetadata for repository {repositoryDto.RepositoryId} - {repositoryDto.RepositoryName}."

                logToConsole (sprintf "%A" repositoryDto)
                return Error(getErrorMessage StorageError.UnknownObjectStorageProvider)
        }

    /// Determines whether active upload session lifecycle.
    let private isActiveUploadSessionLifecycle lifecycleState =
        match lifecycleState with
        | UploadSessionLifecycleState.Started
        | UploadSessionLifecycleState.Discovering
        | UploadSessionLifecycleState.UploadingBlocks
        | UploadSessionLifecycleState.ClaimingRanges -> true
        | _ -> false

    /// Validates validate upload session for content block upload inputs before server processing continues.
    let private validateUploadSessionForContentBlockUpload (parameters: GetContentBlockUploadUriParameters) repositoryId correlationId =
        task {
            if parameters.UploadSessionId = UploadSessionId.Empty then
                return Error(GraceError.Create "UploadSessionId is required before issuing a ContentBlock upload URI." correlationId)
            else
                let uploadSessionActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy parameters.UploadSessionId repositoryId correlationId

                let! session = loadSessionForScope uploadSessionActor correlationId

                if
                    session.UploadSessionId = UploadSessionId.Empty
                    || not (isActiveUploadSessionLifecycle session.LifecycleState)
                then
                    return
                        Error(
                            GraceError.Create
                                $"UploadSession must be active before issuing a ContentBlock upload URI; current state is {session.LifecycleState}."
                                correlationId
                        )
                else
                    match validateUploadSessionRepositoryId repositoryId session correlationId with
                    | Error error -> return Error error
                    | Ok () ->
                        if session.AuthorizedScope
                           <> parameters.AuthorizedScope then
                            return
                                Error(
                                    GraceError.Create
                                        $"UploadSession AuthorizedScope must match the scope recorded when the session was started. Expected '{session.AuthorizedScope}', actual '{parameters.AuthorizedScope}'."
                                        correlationId
                                )
                        elif
                            isNull session.BlockUploadIntents
                            || not
                                (
                                    session.BlockUploadIntents
                                    |> Array.exists (fun intent ->
                                        not (isNull (box intent))
                                        && intent.ContentBlockAddress = parameters.ContentBlockAddress)
                                )
                        then
                            return
                                Error(
                                    GraceError.Create
                                        $"Block upload intent does not exist for ContentBlockAddress {parameters.ContentBlockAddress}; upload URI cannot be issued."
                                        correlationId
                                )
                        else
                            return Ok session
        }

    /// Implements upload session event operation id for the server request pipeline.
    let private uploadSessionEventOperationId (uploadSessionEvent: UploadSessionEvent) =
        match uploadSessionEvent.Event with
        | UploadSessionEventType.Started start -> Some start.OperationId
        | UploadSessionEventType.Abandoned operationId
        | UploadSessionEventType.Expired operationId
        | UploadSessionEventType.PhysicalStateDeleted operationId -> Some operationId
        | UploadSessionEventType.Finalized (operationId, _)
        | UploadSessionEventType.CleanupReminderScheduled (operationId, _)
        | UploadSessionEventType.BlockUploadIntentRegistered (operationId, _)
        | UploadSessionEventType.BlockUploadConfirmed (operationId, _)
        | UploadSessionEventType.DedupeDiscoveryIssued (operationId, _)
        | UploadSessionEventType.ReuseRangesClaimed (operationId, _) -> Some operationId

    /// Attempts to replay upload session command and returns an option or result instead of throwing.
    let private tryReplayUploadSessionCommand requestContext command operationId correlationId =
        task {
            let! events = requestContext.UploadSessionActor.GetEvents correlationId

            if events
               |> Seq.exists (fun uploadSessionEvent -> uploadSessionEventOperationId uploadSessionEvent = Some operationId) then
                let! replay = requestContext.UploadSessionActor.Handle command requestContext.Metadata
                return Some replay
            else
                return None
        }

    /// Validates validate confirm command before materialization inputs before server processing continues.
    let internal validateConfirmCommandBeforeMaterialization (parameters: ConfirmContentBlockUploadParameters) correlationId =
        if String.IsNullOrWhiteSpace parameters.OperationId then
            Error(GraceError.Create "UploadSession command requires a non-empty operation id." correlationId)
        else
            Ok()

    /// Validates validate active confirm session inputs before server processing continues.
    let private validateActiveConfirmSession requestContext (parameters: ConfirmContentBlockUploadParameters) correlationId =
        let session = requestContext.SessionForScope

        if not (isActiveUploadSessionLifecycle session.LifecycleState) then
            Error(
                GraceError.Create
                    $"UploadSession must be active before confirming a ContentBlock upload; current state is {session.LifecycleState}."
                    correlationId
            )
        elif
            isNull session.BlockUploadIntents
            || not
                (
                    session.BlockUploadIntents
                    |> Array.exists (fun intent ->
                        not (isNull (box intent))
                        && intent.ContentBlockAddress = parameters.ContentBlockAddress)
                )
        then
            Error(GraceError.Create $"Block upload intent does not exist for ContentBlockAddress {parameters.ContentBlockAddress}." correlationId)
        else
            Ok()

    /// Implements decoded content block logical length for the server request pipeline.
    let private decodedContentBlockLogicalLength (decodedBlock: ContentBlockFormat.DecodedContentBlock) =
        decodedBlock.Chunks
        |> Array.sumBy (fun chunk -> int64 chunk.Length)

    /// Validates validate confirm intent against payload inputs before server processing continues.
    let private validateConfirmIntentAgainstPayload requestContext (parameters: ConfirmContentBlockUploadParameters) (stagedPayload: byte array) correlationId =
        let session = requestContext.SessionForScope

        let intents =
            if isNull session.BlockUploadIntents then
                Array.empty
            else
                session.BlockUploadIntents
                |> Array.filter (fun intent ->
                    not (isNull (box intent))
                    && intent.ContentBlockAddress = parameters.ContentBlockAddress)

        match intents with
        | [||] -> Error(GraceError.Create $"Block upload intent does not exist for ContentBlockAddress {parameters.ContentBlockAddress}." correlationId)
        | _ ->
            let matchingPayloadLength =
                intents
                |> Array.filter (fun intent -> intent.ExpectedPayloadLength = stagedPayload.LongLength)

            if matchingPayloadLength.Length = 0 then
                let expectedLengths =
                    intents
                    |> Array.map (fun intent -> intent.ExpectedPayloadLength.ToString())
                    |> String.concat ", "

                Error(
                    GraceError.Create
                        $"ContentBlock payload length mismatch. Expected one of [{expectedLengths}], actual {stagedPayload.LongLength}."
                        correlationId
                )
            else
                match decodeContentBlockPayload parameters.ContentBlockAddress stagedPayload correlationId with
                | Error error -> Error error
                | Ok decodedBlock ->
                    let logicalLength = decodedContentBlockLogicalLength decodedBlock

                    if matchingPayloadLength
                       |> Array.exists (fun intent -> intent.LogicalLength = logicalLength)
                       |> not then
                        let expectedLengths =
                            matchingPayloadLength
                            |> Array.map (fun intent -> intent.LogicalLength.ToString())
                            |> String.concat ", "

                        Error(
                            GraceError.Create
                                $"ContentBlock logical length mismatch. Expected one of [{expectedLengths}], actual {logicalLength}."
                                correlationId
                        )
                    else
                        Ok()

    /// Attempts to find finalized scoped content block metadata and returns an option or result instead of throwing.
    let internal tryFindFinalizedScopedContentBlockMetadata storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress state =
        DedupeIndex.tryFindFinalizedScopedContentBlockMetadata storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress state

    /// Validates validate manifest for content block download inputs before server processing continues.
    let private validateManifestForContentBlockDownload repositoryId (parameters: GetContentBlockDownloadUriParameters) correlationId =
        task {
            if String.IsNullOrWhiteSpace parameters.AuthorizedScope then
                return Error(GraceError.Create "AuthorizedScope is required for ContentBlock manifest download authorization." correlationId)
            else
                match validateManifestAddress correlationId parameters.ManifestAddress with
                | Error error -> return Error error
                | Ok () ->
                    if String.IsNullOrWhiteSpace parameters.StoragePoolId then
                        return Error(GraceError.Create "StoragePoolId is required for ContentBlock manifest download authorization." correlationId)
                    else
                        let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId

                        match!
                            dedupeIndexActor.TryGetFinalizedScopedContentBlockMetadata
                                (
                                    parameters.StoragePoolId,
                                    repositoryId,
                                    parameters.AuthorizedScope,
                                    parameters.ManifestAddress,
                                    parameters.ContentBlockAddress,
                                    correlationId
                                )
                            with
                        | Some metadata -> return Ok metadata.StoragePlacement
                        | None ->
                            return
                                Error(
                                    GraceError.Create
                                        $"ContentBlockAddress {parameters.ContentBlockAddress} is not referenced by finalized metadata reachable from this repository, storage pool, and authorized scope."
                                        correlationId
                                )
        }

    /// Identifies a rejected whole-file download request before reachability lookup.
    type private WholeFileDownloadValidation =
        | Valid
        | Invalid of GraceError

    /// Validates the public whole-file download proof tuple before any storage materialization or SAS issuance.
    let private validateWholeFileDownloadRequest (parameters: GetDownloadUriParameters) correlationId =
        let requestedSha256Hash = string parameters.Sha256Hash
        let requestedBlake3Hash = string parameters.Blake3Hash

        if parameters.ReferenceId = ReferenceId.Empty then
            Invalid(GraceError.Create "ReferenceId is required for whole-file download authorization." correlationId)
        elif String.IsNullOrWhiteSpace(string parameters.RelativePath) then
            Invalid(GraceError.Create "RelativePath is required for whole-file download authorization." correlationId)
        elif String.IsNullOrWhiteSpace requestedSha256Hash
             && String.IsNullOrWhiteSpace requestedBlake3Hash then
            Invalid(GraceError.Create "Sha256Hash or Blake3Hash is required for whole-file download authorization." correlationId)
        elif
            not (String.IsNullOrWhiteSpace requestedSha256Hash)
            && not (Constants.Sha256FullHashRegex.IsMatch requestedSha256Hash)
        then
            Invalid(GraceError.Create "Sha256Hash must be a full 64-character hexadecimal value." correlationId)
        elif
            not (String.IsNullOrWhiteSpace requestedBlake3Hash)
            && not (Constants.Blake3FullHashRegex.IsMatch requestedBlake3Hash)
        then
            Invalid(GraceError.Create "Blake3Hash must be a full 64-character lowercase hexadecimal value." correlationId)
        else
            Valid

    /// Checks whether the reachable FileVersion matches the caller's path and hash proof.
    let private fileVersionMatchesDownloadProof (parameters: GetDownloadUriParameters) (fileVersion: FileVersion) =
        let requestedPath = normalizeFilePath $"{parameters.RelativePath}"
        let requestedSha256Hash = string parameters.Sha256Hash
        let requestedBlake3Hash = string parameters.Blake3Hash

        let pathMatches = String.Equals(normalizeFilePath $"{fileVersion.RelativePath}", requestedPath, StringComparison.Ordinal)

        let shaMatches =
            String.IsNullOrWhiteSpace requestedSha256Hash
            || String.Equals(string fileVersion.Sha256Hash, requestedSha256Hash, StringComparison.OrdinalIgnoreCase)

        let blake3Matches =
            String.IsNullOrWhiteSpace requestedBlake3Hash
            || String.Equals(string fileVersion.Blake3Hash, requestedBlake3Hash, StringComparison.Ordinal)

        pathMatches && shaMatches && blake3Matches

    /// Resolves a FileVersion only when the requested reference is observable and its root tree reaches the file proof.
    let private tryResolveReachableFileVersionForDownload (context: HttpContext) repositoryId (parameters: GetDownloadUriParameters) correlationId =
        task {
            let referenceActorProxy = Reference.CreateActorProxy parameters.ReferenceId repositoryId correlationId
            let! referenceDto = referenceActorProxy.Get correlationId

            if referenceDto.ReferenceId = ReferenceId.Empty
               || referenceDto.DeletedAt.IsSome
               || referenceDto.RepositoryId <> repositoryId then
                return None
            else
                let branchActorProxy = Grace.Actors.Extensions.ActorProxy.Branch.CreateActorProxy referenceDto.BranchId referenceDto.RepositoryId correlationId

                let! branchDto = branchActorProxy.Get correlationId
                let! visibleReferences = Grace.Server.Branch.filterVisibleReferenceWindowForBranch context branchDto -1 [| referenceDto |]

                if visibleReferences.Length = 0 then
                    return None
                else
                    let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId
                    let! recursiveDirectoryVersions = directoryActorProxy.GetRecursiveDirectoryVersions false correlationId

                    return
                        recursiveDirectoryVersions
                        |> Seq.collect (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.Files)
                        |> Seq.tryFind (fileVersionMatchesDownloadProof parameters)
        }

    /// Gets a download URI for a reachable file proof that can be used by a Grace client.
    let GetDownloadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = (getCorrelationId context)
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetDownloadUriParameters>()
                    let organizationId, repositoryId = resolveStorageIds graceIds parameters
                    let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    match validateRepositoryExistsForStorageRequest repositoryId repositoryDto correlationId,
                          validateWholeFileDownloadRequest parameters correlationId
                        with
                    | Error error, _ -> return! context |> result400BadRequest error
                    | _, Invalid error -> return! context |> result400BadRequest error
                    | Ok (), Valid ->
                        let! reachableFileVersion = tryResolveReachableFileVersionForDownload context repositoryId parameters correlationId

                        match reachableFileVersion with
                        | None -> return! context |> result404NotFound
                        | Some fileVersion ->
                            let contentReference =
                                if isNull (box fileVersion.ContentReference) then
                                    FileContentReference.WholeFileContent
                                else
                                    fileVersion.ContentReference

                            let blobName = StorageKeys.wholeFileContentObjectKey fileVersion

                            let! materializationResult =
                                match contentReference.ReferenceType with
                                | FileContentReferenceType.WholeFileContent -> Task.FromResult(Ok())
                                | FileContentReferenceType.FileManifest ->
                                    NormalFileMaterialization.materializeForDownload
                                        repositoryDto
                                        $"{fileVersion.RelativePath}"
                                        fileVersion
                                        blobName
                                        correlationId
                                        context.RequestAborted
                                | unsupported ->
                                    Task.FromResult(
                                        Error(GraceError.Create $"Unsupported file content reference type for download URI: {unsupported}." correlationId)
                                    )

                            match materializationResult with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok () ->
                                let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                                context.SetStatusCode StatusCodes.Status200OK
                                //log.LogTrace("fileVersion: {fileVersion.RelativePath}; downloadUri: {downloadUri}", [| fileVersion.RelativePath, downloadUri |])
                                return! context.WriteStringAsync $"{downloadUri}"
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    return! context.WriteTextAsync $"Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Handles the Grace Server get content block upload uri request.
    let GetContentBlockUploadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetContentBlockUploadUriParameters>()

                    match validateContentBlockAddress correlationId parameters.ContentBlockAddress with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok normalizedContentBlockAddress ->
                        parameters.ContentBlockAddress <- normalizedContentBlockAddress
                        let _, repositoryId = resolveStorageIds graceIds parameters

                        match! validateUploadSessionForContentBlockUpload parameters repositoryId correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok session ->
                            match resolveUploadSessionStoragePoolRoute session.RepositoryId session.StoragePoolId correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok route ->
                                let stagingPlacement =
                                    expectedContentBlockStagingPlacement
                                        route
                                        session.RepositoryId
                                        parameters.UploadSessionId
                                        parameters.ContentBlockAddress
                                        None

                                match! createAzureContentBlockSasUriForObjectKey route stagingPlacement.ObjectKey azureBlobCreatePermissions correlationId with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok uploadUri ->
                                    let uploadUri = appendShardEvidenceFragment route.Shard.StorageAccountName uploadUri
                                    context.SetStatusCode StatusCodes.Status200OK
                                    return! context.WriteStringAsync uploadUri.AbsoluteUri
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetContentBlockUploadUri: {(ExceptionResponse.Create ex)}"

                    return! context.WriteTextAsync $"{getCurrentInstantExtended ()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Handles the Grace Server get content block download uri request.
    let GetContentBlockDownloadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetContentBlockDownloadUriParameters>()

                    match validateContentBlockAddress correlationId parameters.ContentBlockAddress with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok normalizedContentBlockAddress ->
                        parameters.ContentBlockAddress <- normalizedContentBlockAddress
                        let _, repositoryId = resolveStorageIds graceIds parameters

                        match! validateManifestForContentBlockDownload repositoryId parameters correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok storagePlacement ->
                            match! createAzureContentBlockSasUriForPlacement storagePlacement azureBlobReadPermissions correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok downloadUri ->
                                context.SetStatusCode StatusCodes.Status200OK
                                return! context.WriteStringAsync downloadUri.AbsoluteUri
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetContentBlockDownloadUri: {(ExceptionResponse.Create ex)}"

                    return! context.WriteTextAsync $"{getCurrentInstantExtended ()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Discovers reusable ContentBlocks for key chunks without exposing a per-chunk existence oracle.
    let DiscoverContentBlocks: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<DiscoverContentBlocksParameters>()

                    let keyChunkAddresses =
                        if isNull parameters.KeyChunkAddresses then
                            Array.empty
                        else
                            parameters.KeyChunkAddresses

                    Activity.Current.SetTag("keyChunkAddresses.Count", $"{keyChunkAddresses.Length}")
                    |> ignore

                    if keyChunkAddresses.Length > StorageParameterContracts.MaxDiscoveryKeyChunkAddresses then
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create
                                    $"KeyChunkAddresses must contain no more than {StorageParameterContracts.MaxDiscoveryKeyChunkAddresses} items."
                                    correlationId
                            )
                    else
                        let graceIds = getGraceIds context
                        let organizationId, repositoryId = resolveStorageIds graceIds parameters
                        let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                        let! repositoryDto = repositoryActor.Get correlationId

                        match validateRepositoryExistsForStorageRequest repositoryId repositoryDto correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok () ->
                            match resolveRepositoryDedupeStoragePoolId repositoryDto correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok storagePoolId ->
                                let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId
                                let! snapshot = dedupeIndexActor.Snapshot correlationId
                                let result = DedupeIndex.discover storagePoolId keyChunkAddresses (getCurrentInstant ()) snapshot

                                return!
                                    context
                                    |> result200Ok (GraceReturnValue.Create result correlationId)
                with
                | ex ->
                    logToConsole $"Exception in DiscoverContentBlocks: {(ExceptionResponse.Create ex)}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create (getErrorMessage StorageError.ObjectStorageException) correlationId)
            }

    /// Handles the Grace Server start manifest upload session request.
    let StartManifestUploadSession: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<StartManifestUploadSessionParameters>()
                    let ownerId = resolveOwnerId graceIds parameters
                    let organizationId, repositoryId = resolveStorageIds graceIds parameters
                    let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    match validateRepositoryExistsForStorageRequest repositoryId repositoryDto correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok _ ->
                        match resolveRepositoryStoragePoolRoute repositoryDto correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok _ ->
                            match resolveRepositoryDedupeStoragePoolId repositoryDto correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok storagePoolId ->
                                match validateSupportedManifestChunkingSuite correlationId parameters.ChunkingSuiteId with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok () ->
                                    match validateStartManifestUploadSessionAuthorizedScope correlationId parameters.AuthorizedScope with
                                    | Error error -> return! context |> result400BadRequest error
                                    | Ok () ->
                                        let command =
                                            UploadSessionCommand.Start
                                                {
                                                    UploadSessionId = parameters.UploadSessionId
                                                    OwnerId = ownerId
                                                    OrganizationId = organizationId
                                                    RepositoryId = repositoryId
                                                    StoragePoolId = storagePoolId
                                                    AuthorizedScope = parameters.AuthorizedScope
                                                    FileContentHash = parameters.FileContentHash
                                                    ExpectedSize = parameters.ExpectedSize
                                                    ChunkingSuiteId = parameters.ChunkingSuiteId
                                                    SamplingPolicySnapshot = parameters.SamplingPolicySnapshot
                                                    OperationId = parameters.OperationId
                                                }

                                        return! handleUploadSessionCommand context parameters command correlationId
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    logToConsole $"Exception in StartManifestUploadSession: {exceptionResponse}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{exceptionResponse}" correlationId)
            }

    /// Handles the Grace Server issue dedupe discovery request.
    let IssueDedupeDiscovery: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<IssueDedupeDiscoveryParameters>()

                    let hints = if isNull parameters.Hints then Array.empty else parameters.Hints

                    let keyChunkAddresses =
                        if isNull parameters.KeyChunkAddresses then
                            Array.empty
                        else
                            parameters.KeyChunkAddresses

                    if hints.Length > StorageParameterContracts.MaxReuseRangeClaims then
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create
                                    $"IssueDedupeDiscovery Hints must contain no more than {StorageParameterContracts.MaxReuseRangeClaims} items."
                                    correlationId
                            )
                    elif keyChunkAddresses.Length > StorageParameterContracts.MaxDiscoveryKeyChunkAddresses then
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create
                                    $"IssueDedupeDiscovery KeyChunkAddresses must contain no more than {StorageParameterContracts.MaxDiscoveryKeyChunkAddresses} items."
                                    correlationId
                            )
                    else
                        let! requestContext = createUploadSessionRequestContext context parameters correlationId
                        let! scopeValidation = validateUploadSessionScope requestContext parameters correlationId true

                        match scopeValidation with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok requestContext ->
                            let storagePoolId = requestContext.SessionForScope.StoragePoolId
                            let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId
                            let! records = dedupeIndexActor.Snapshot correlationId

                            match validateIssuedDedupeDiscoveryHints correlationId storagePoolId keyChunkAddresses hints records with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok boundHints ->
                                let command =
                                    UploadSessionCommand.IssueDedupeDiscovery
                                        {
                                            OperationId = parameters.OperationId
                                            ExpiresAt = parameters.ExpiresAt
                                            MinimumReuseRunLength = parameters.MinimumReuseRunLength
                                            Hints = boundHints
                                        }

                                let! result = requestContext.UploadSessionActor.Handle command requestContext.Metadata

                                match result with
                                | Ok returnValue -> return! context |> result200Ok returnValue
                                | Error error -> return! context |> result400BadRequest error
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    let exceptionText = $"{exceptionResponse}"
                    logToConsole $"Exception in IssueDedupeDiscovery: {exceptionResponse}"

                    if exceptionText.Contains("expected JSON object, found Null", StringComparison.OrdinalIgnoreCase) then
                        return!
                            context
                            |> result400BadRequest (GraceError.Create "IssueDedupeDiscovery Hints must not contain null entries." correlationId)
                    else
                        return!
                            context
                            |> result500ServerError (GraceError.Create exceptionText correlationId)
            }

    /// Handles the Grace Server claim reuse ranges request.
    let ClaimReuseRanges: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<ClaimReuseRangesParameters>()
                    let hints = if isNull parameters.Hints then Array.empty else parameters.Hints

                    if hints.Length = 0 then
                        return!
                            context
                            |> result400BadRequest (GraceError.Create "At least one reuse range claim is required." correlationId)
                    elif hints.Length > StorageParameterContracts.MaxReuseRangeClaims then
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create
                                    $"ClaimReuseRanges Hints must contain no more than {StorageParameterContracts.MaxReuseRangeClaims} items."
                                    correlationId
                            )
                    else
                        let! requestContext = createUploadSessionRequestContext context parameters correlationId
                        let! scopeValidation = validateUploadSessionScope requestContext parameters correlationId true

                        match scopeValidation with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok requestContext ->
                            match validateActiveDedupeDiscoveryForClaim requestContext parameters correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok discovery ->
                                let storagePoolId = requestContext.SessionForScope.StoragePoolId

                                match validateClaimReuseHints correlationId storagePoolId discovery hints with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok hints ->
                                    let ranges = ResizeArray<ClaimReuseRange>()
                                    let mutable error = None
                                    let mutable index = 0

                                    while error.IsNone && index < hints.Length do
                                        let hint = hints[index]

                                        let metadataActor =
                                            grainFactory.CreateActorProxyWithCorrelationId<IContentBlockMetadataActor>(
                                                Grace.Actors.ContentBlockMetadataActorKey.Create hint.StoragePoolId hint.ContentBlockAddress,
                                                correlationId
                                            )

                                        let! metadata = metadataActor.Get correlationId

                                        match metadata with
                                        | Some metadata -> ranges.Add({ Hint = hint; Metadata = metadata })
                                        | None ->
                                            error <-
                                                Some(
                                                    GraceError.Create
                                                        $"Authoritative ContentBlockMetadata is absent for {hint.ContentBlockAddress}; reuse range cannot be claimed."
                                                        correlationId
                                                )

                                        index <- index + 1

                                    match error with
                                    | Some error -> return! context |> result400BadRequest error
                                    | None ->
                                        let command =
                                            UploadSessionCommand.ClaimReuseRanges
                                                {
                                                    OperationId = parameters.OperationId
                                                    DiscoveryOperationId = parameters.DiscoveryOperationId
                                                    Ranges = ranges.ToArray()
                                                }

                                        let! result = requestContext.UploadSessionActor.Handle command requestContext.Metadata

                                        match result with
                                        | Ok returnValue -> return! context |> result200Ok returnValue
                                        | Error error -> return! context |> result400BadRequest error
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    let exceptionText = $"{exceptionResponse}"
                    logToConsole $"Exception in ClaimReuseRanges: {exceptionResponse}"

                    if exceptionText.Contains("expected JSON object, found Null", StringComparison.OrdinalIgnoreCase) then
                        return!
                            context
                            |> result400BadRequest (GraceError.Create "ClaimReuseRanges Hints must not contain null entries." correlationId)
                    else
                        return!
                            context
                            |> result500ServerError (GraceError.Create exceptionText correlationId)
            }

    /// Handles the Grace Server register content block upload request.
    let RegisterContentBlockUpload: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<RegisterContentBlockUploadParameters>()

                    match validateContentBlockAddress correlationId parameters.ContentBlockAddress with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok normalizedContentBlockAddress ->
                        parameters.ContentBlockAddress <- normalizedContentBlockAddress

                        let command =
                            UploadSessionCommand.RegisterBlockUploadIntent
                                {
                                    OperationId = parameters.OperationId
                                    ContentBlockAddress = parameters.ContentBlockAddress
                                    LogicalOffset = parameters.LogicalOffset
                                    LogicalLength = parameters.LogicalLength
                                    ExpectedPayloadLength = parameters.ExpectedPayloadLength
                                }

                        return! handleUploadSessionCommand context parameters command correlationId
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    logToConsole $"Exception in RegisterContentBlockUpload: {exceptionResponse}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{exceptionResponse}" correlationId)
            }

    /// Handles the Grace Server confirm content block upload request.
    let ConfirmContentBlockUpload: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<ConfirmContentBlockUploadParameters>()

                    match validateContentBlockAddress correlationId parameters.ContentBlockAddress with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok normalizedContentBlockAddress ->
                        parameters.ContentBlockAddress <- normalizedContentBlockAddress
                        let! requestContext = createUploadSessionRequestContext context parameters correlationId
                        let! scopeValidation = validateUploadSessionScope requestContext parameters correlationId true

                        match scopeValidation with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok requestContext ->
                            match
                                resolveUploadSessionStoragePoolRoute
                                    requestContext.SessionForScope.RepositoryId
                                    requestContext.SessionForScope.StoragePoolId
                                    correlationId
                                with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok route ->
                                match
                                    validateContentBlockStagingPlacementForRoute
                                        correlationId
                                        route
                                        requestContext.SessionForScope.RepositoryId
                                        parameters.UploadSessionId
                                        parameters.ContentBlockAddress
                                        parameters.StoragePlacement
                                    with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok () ->
                                    let replayCommand =
                                        UploadSessionCommand.ConfirmBlockUploaded
                                            {
                                                OperationId = parameters.OperationId
                                                ContentBlockAddress = parameters.ContentBlockAddress
                                                Payload = if isNull parameters.Payload then Array.empty else parameters.Payload
                                                StoragePlacement = parameters.StoragePlacement
                                            }

                                    match! tryReplayUploadSessionCommand requestContext replayCommand parameters.OperationId correlationId with
                                    | Some (Ok returnValue) -> return! context |> result200Ok returnValue
                                    | Some (Error error) -> return! context |> result400BadRequest error
                                    | None ->
                                        match validateConfirmCommandBeforeMaterialization parameters correlationId with
                                        | Error error -> return! context |> result400BadRequest error
                                        | Ok () ->
                                            match validateActiveConfirmSession requestContext parameters correlationId with
                                            | Error error ->
                                                do! deleteContentBlockStagingPayload parameters.StoragePlacement correlationId
                                                return! context |> result400BadRequest error
                                            | Ok () ->
                                                match! readContentBlockPayloadFromPlacement parameters.StoragePlacement correlationId with
                                                | Error error -> return! context |> result400BadRequest error
                                                | Ok (stagedPayload, _) ->
                                                    if
                                                        not (isNull parameters.Payload)
                                                        && parameters.Payload.Length > 0
                                                        && not (parameters.Payload.SequenceEqual(stagedPayload))
                                                    then
                                                        do! deleteContentBlockStagingPayload parameters.StoragePlacement correlationId

                                                        return!
                                                            context
                                                            |> result400BadRequest (
                                                                GraceError.Create
                                                                    "ConfirmContentBlockUpload Payload must match the staged uploaded bytes."
                                                                    correlationId
                                                            )
                                                    else
                                                        match validateConfirmIntentAgainstPayload requestContext parameters stagedPayload correlationId with
                                                        | Error error ->
                                                            do! deleteContentBlockStagingPayload parameters.StoragePlacement correlationId
                                                            return! context |> result400BadRequest error
                                                        | Ok () ->
                                                            match!
                                                                materializeValidatedContentBlock
                                                                    route
                                                                    parameters.ContentBlockAddress
                                                                    stagedPayload
                                                                    correlationId
                                                                with
                                                            | Error error ->
                                                                do! deleteContentBlockStagingPayload parameters.StoragePlacement correlationId
                                                                return! context |> result400BadRequest error
                                                            | Ok finalMaterialization ->
                                                                let command =
                                                                    UploadSessionCommand.ConfirmBlockUploaded
                                                                        {
                                                                            OperationId = parameters.OperationId
                                                                            ContentBlockAddress = parameters.ContentBlockAddress
                                                                            Payload = stagedPayload
                                                                            StoragePlacement = finalMaterialization.StoragePlacement
                                                                        }

                                                                let! result = requestContext.UploadSessionActor.Handle command requestContext.Metadata

                                                                match result with
                                                                | Ok returnValue ->
                                                                    do! deleteContentBlockStagingPayload parameters.StoragePlacement correlationId
                                                                    return! context |> result200Ok returnValue
                                                                | Error error ->
                                                                    do! deleteContentBlockStagingPayload parameters.StoragePlacement correlationId
                                                                    return! context |> result400BadRequest error
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    logToConsole $"Exception in ConfirmContentBlockUpload: {exceptionResponse}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{exceptionResponse}" correlationId)
            }

    /// Handles the Grace Server finalize manifest upload request.
    let FinalizeManifestUpload: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<FinalizeManifestUploadParameters>()
                    let! requestContext = createUploadSessionRequestContext context parameters correlationId
                    let! scopeValidation = validateUploadSessionScope requestContext parameters correlationId true

                    match scopeValidation with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok requestContext ->
                        /// Captures manifest finalization evidence in the actor command while preserving replay operation id.
                        let createFinalizeCommand evidence =
                            UploadSessionCommand.FinalizeManifest
                                {
                                    OperationId = parameters.OperationId
                                    Manifest = parameters.Manifest
                                    BlockPayloads = evidence.BlockPayloads
                                    ClaimedMetadata = evidence.ClaimedMetadata
                                }

                        match! getFinalizeReplayState requestContext parameters.OperationId correlationId with
                        | _, Some finalizedManifestAddress ->
                            match validateFinalizeReplayManifestAddress finalizedManifestAddress parameters.Manifest correlationId with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok () ->
                                match
                                    validateFinalizeReplayManifestBeforeHydration
                                        requestContext.SessionForScope
                                        finalizedManifestAddress
                                        parameters.Manifest
                                        correlationId
                                    with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok () ->
                                    let! evidenceResult = hydrateFinalizeReplayEvidence requestContext parameters parameters.Manifest correlationId

                                    match evidenceResult with
                                    | Error error -> return! context |> result400BadRequest error
                                    | Ok evidence ->
                                        match
                                            validateFinalizeReplayEvidence
                                                requestContext.SessionForScope
                                                finalizedManifestAddress
                                                parameters.Manifest
                                                evidence
                                                correlationId
                                            with
                                        | Error error -> return! context |> result400BadRequest error
                                        | Ok () ->
                                            let! result = requestContext.UploadSessionActor.Handle (createFinalizeCommand evidence) requestContext.Metadata

                                            match result with
                                            | Ok returnValue -> return! context |> result200Ok returnValue
                                            | Error error -> return! context |> result400BadRequest error
                        | true, None ->
                            return!
                                context
                                |> result400BadRequest (operationAlreadyAppliedToDifferentUploadSessionEventError parameters.OperationId correlationId)
                        | false, None ->
                            let! evidenceResult = hydrateFinalizeEvidence requestContext parameters parameters.Manifest true correlationId

                            match evidenceResult with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok evidence ->
                                let command = createFinalizeCommand evidence

                                let! result = requestContext.UploadSessionActor.Handle command requestContext.Metadata

                                match result with
                                | Ok returnValue -> return! context |> result200Ok returnValue
                                | Error error -> return! context |> result400BadRequest error
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    logToConsole $"Exception in FinalizeManifestUpload: {exceptionResponse}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{exceptionResponse}" correlationId)
            }

    /// Gets an upload URI for the specified file version that can be used by a Grace client.
    let GetUploadUris: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context
                let uris = Dictionary<string, Uri>()

                try
                    let! parameters = context.BindJsonAsync<GetUploadUriParameters>()
                    let organizationId, repositoryId = resolveStorageIds graceIds parameters
                    let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    for fileVersion in parameters.FileVersions do
                        let blobName = getWholeFileContentObjectKey fileVersion
                        let! uploadUri = getUriWithWriteSharedAccessSignature repositoryDto blobName correlationId
                        uris.Add(fileVersion.RelativePath, uploadUri)

                    if log.IsEnabled(LogLevel.Debug) then
                        let sb = stringBuilderPool.Get()

                        try
                            for kvp in uris do
                                sb.AppendLine($"fileVersion: {kvp.Key}; uploadUri: {kvp.Value}")
                                |> ignore

                            log.LogDebug("In GetUploadUri(): Created {count} uri's for these files: {uploadUris}", uris.Count, sb.ToString())
                        finally
                            stringBuilderPool.Return(sb)

                    context.SetStatusCode StatusCodes.Status200OK
                    return! context.WriteJsonAsync uris
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetUploadUri: {(ExceptionResponse.Create ex)}"

                    return! context.WriteTextAsync $"{getCurrentInstantExtended ()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Checks if a list of files already exists in object storage, and if any do not, return Uri's that the client can use to upload the file.
    let GetUploadMetadataForFiles: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetUploadMetadataForFilesParameters>()

                    Activity.Current.SetTag("fileVersions.Count", $"{parameters.FileVersions.Length}")
                    |> ignore

                    if parameters.FileVersions.Length > 0 then
                        let fileVersionWithMissingBlake3Hash =
                            parameters.FileVersions
                            |> Array.tryFind (fun fileVersion -> String.IsNullOrEmpty $"{fileVersion.Blake3Hash}")

                        let fileVersionWithInvalidBlake3Hash =
                            parameters.FileVersions
                            |> Array.tryFind (fun fileVersion -> not (Constants.Blake3FullHashRegex.IsMatch $"{fileVersion.Blake3Hash}"))

                        if fileVersionWithMissingBlake3Hash.IsSome then
                            return!
                                context
                                |> result400BadRequest (GraceError.Create (getErrorMessage VersionHashError.Blake3HashIsRequired) correlationId)
                        elif fileVersionWithInvalidBlake3Hash.IsSome then
                            return!
                                context
                                |> result400BadRequest (GraceError.Create (getErrorMessage VersionHashError.InvalidBlake3Hash) correlationId)
                        else
                            let organizationId, repositoryId = resolveStorageIds graceIds parameters
                            let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                            let! repositoryDto = repositoryActor.Get correlationId

                            let uploadMetadata = ConcurrentQueue<StorageParameterContracts.UploadMetadata>()

                            do!
                                Parallel.ForEachAsync(
                                    parameters.FileVersions,
                                    Constants.ParallelOptions,
                                    (fun fileVersion ct ->
                                        ValueTask(
                                            task {
                                                //let! fileExists = fileExists repositoryDto fileVersion context

                                                //if not <| fileExists then
                                                let contentReference = normalizeWholeFileContentReference fileVersion
                                                let blobName = getWholeFileContentObjectKey fileVersion

                                                let! blobUriWithSasToken = getUriWithWriteSharedAccessSignature repositoryDto blobName correlationId

                                                let metadata: StorageParameterContracts.UploadMetadata =
                                                    {
                                                        RelativePath = fileVersion.RelativePath
                                                        BlobUriWithSasToken = blobUriWithSasToken
                                                        Sha256Hash = fileVersion.Sha256Hash
                                                        Blake3Hash = fileVersion.Blake3Hash
                                                        ContentReference = contentReference
                                                    }

                                                uploadMetadata.Enqueue(metadata)
                                            }
                                        ))
                                )

                            Activity.Current.SetTag("uploadMetadata.Count", $"{uploadMetadata.Count}")
                            |> ignore

                            context
                                .GetLogger()
                                .LogInformation(
                                    $"{getCurrentInstantExtended ()} Received {parameters.FileVersions.Length} FileVersions; Returning {uploadMetadata.Count} uploadMetadata records."
                                )

                            return!
                                context
                                |> result200Ok (GraceReturnValue.Create (uploadMetadata.ToArray()) correlationId)
                    else
                        return!
                            context
                            |> result400BadRequest (GraceError.Create (getErrorMessage StorageError.FilesMustNotBeEmpty) correlationId)
                with
                | ex ->
                    logToConsole $"Exception in GetUploadMetadataForFiles: {(ExceptionResponse.Create ex)}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create (getErrorMessage StorageError.ObjectStorageException) correlationId)
            }

    /// Deletes all documents from Cosmos DB. After calling, the web connection will time-out, but the method will continue to run until Cosmos DB is empty.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let DeleteAllFromCosmosDB: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
#if DEBUG
                let correlationId = getCorrelationId context
                let log = context.GetLogger()

                log.LogWarning("{CurrentInstant} Deleting all rows from Cosmos DB.", getCurrentInstantExtended ())

                let! failed = deleteAllFromCosmosDb ()

                if failed |> Seq.isEmpty then
                    log.LogWarning("{CurrentInstant} Succeeded deleting all rows from CosmosDB.", getCurrentInstantExtended ())

                    return!
                        context
                        |> result200Ok (GraceReturnValue.Create "Succeeded deleting all rows from Cosmos DB." (getCorrelationId context))
                else
                    let sb = stringBuilderPool.Get()

                    try
                        for fail in failed do
                            sb.AppendLine(fail) |> ignore

                        log.LogWarning(
                            "{CurrentInstant} Failed to delete all rows from Cosmos DB. Failures: {failedCount}.",
                            getCurrentInstantExtended (),
                            failed.Count
                        )

                        log.LogWarning(sb.ToString())

                        return!
                            context
                            |> result500ServerError (
                                GraceError.Create
                                    $"Failed to delete all rows from Cosmos DB. Failures: {failed.Count}.{Environment.NewLine}{sb.ToString()}"
                                    (getCorrelationId context)
                            )
                    finally
                        stringBuilderPool.Return(sb)
#else
                return! context |> result404NotFound
#endif
            }

    /// Deletes all reminders from Cosmos DB. After calling, the web connection will time-out, but the method will continue to run until Cosmos DB is empty.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let DeleteAllRemindersFromCosmosDB: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
#if DEBUG
                let correlationId = getCorrelationId context
                let log = context.GetLogger()

                log.LogWarning("{CurrentInstant} Deleting all reminders from Cosmos DB.", getCurrentInstantExtended ())

                let! failed = deleteAllRemindersFromCosmosDb ()

                if failed |> Seq.isEmpty then
                    log.LogWarning("{CurrentInstant} Succeeded deleting all reminders from CosmosDB.", getCurrentInstantExtended ())

                    return!
                        context
                        |> result200Ok (GraceReturnValue.Create "Succeeded deleting all reminders from Cosmos DB." (getCorrelationId context))
                else
                    let sb = stringBuilderPool.Get()

                    try
                        for fail in failed do
                            sb.AppendLine(fail) |> ignore

                        log.LogWarning(
                            "{CurrentInstant} Failed to delete all reminders from Cosmos DB. Failures: {failedCount}.",
                            getCurrentInstantExtended (),
                            failed.Count
                        )

                        log.LogWarning(sb.ToString())

                        return!
                            context
                            |> result500ServerError (
                                GraceError.Create
                                    $"Failed to delete all reminders from Cosmos DB. Failures: {failed.Count}.{Environment.NewLine}{sb.ToString()}"
                                    (getCorrelationId context)
                            )
                    finally
                        stringBuilderPool.Return(sb)
#else
                return! context |> result404NotFound
#endif
            }
