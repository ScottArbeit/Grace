namespace Grace.Server

open Azure.Core
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Sas
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.RepositoryContentCounter
open Grace.Types.UploadSession
open Grace.Types.Repository
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open System.IO
open System.Text
open Azure.Storage
open System.Diagnostics
open System.Reflection.Metadata
open System.Net.Http.Json

module StorageParameterContracts = Grace.Shared.Parameters.Storage

module Storage =

    let log = ApplicationContext.loggerFactory.CreateLogger("Storage.Server")

    type internal FinalizeBlockPayloadSource =
        | ConfirmedUpload of ConfirmedBlockUpload
        | ClaimedReuseRange of ClaimedReuseRange

    let private normalizeWholeFileContentReference (fileVersion: FileVersion) =
        if isNull (box fileVersion.ContentReference) then
            FileContentReference.WholeFileContent
        else
            match fileVersion.ContentReference.ReferenceType with
            | FileContentReferenceType.WholeFileContent -> fileVersion.ContentReference
            | FileContentReferenceType.FileManifest ->
                invalidOp "FileManifest content references are not supported by the current storage compatibility endpoints."
            | unsupported -> invalidOp $"Unsupported file content reference type: {unsupported}."

    let private getWholeFileContentObjectKey (fileVersion: FileVersion) =
        normalizeWholeFileContentReference fileVersion
        |> ignore

        StorageKeys.wholeFileContentObjectKey fileVersion

    let private getReadableWholeFileContentObjectKey (repositoryDto: RepositoryDto) (fileVersion: FileVersion) correlationId =
        task { return getWholeFileContentObjectKey fileVersion }

    let private getContentBlockObjectKey (contentBlockAddress: ContentBlockAddress) = StorageKeys.contentBlockObjectKey contentBlockAddress

    let private invalidContentBlockAddressError correlationId =
        GraceError.Create "ContentBlockAddress must be a 64-character hexadecimal BLAKE3 value." correlationId

    let private validateContentBlockAddress correlationId (contentBlockAddress: ContentBlockAddress) =
        match ContentAddress.tryNormalizeBlake3Address contentBlockAddress with
        | Some _ -> Ok()
        | None -> Error(invalidContentBlockAddressError correlationId)

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

    let private resolveOwnerId (graceIds: GraceIds) (parameters: StorageParameters) =
        if graceIds.OwnerId <> OwnerId.Empty then
            graceIds.OwnerId
        else
            OwnerId.Parse parameters.OwnerId

    let private resolveRepositoryStorageRoute correlationId repositoryDto = DedupeIndex.resolveRepositoryStorageRouteWithDefaults correlationId repositoryDto

    let private resolveRepositoryStorageRouteForPool correlationId storagePoolId repositoryDto =
        DedupeIndex.resolveRepositoryStorageRouteForPool correlationId storagePoolId repositoryDto

    let private repositoryForCasStorage route repositoryDto = DedupeIndex.repositoryForStorageRoute route repositoryDto

    let private stampManifestStoragePool storagePoolId (manifest: FileManifest) =
        if isNull (box manifest) then
            manifest
        else
            { manifest with StoragePoolId = storagePoolId }

    let internal createEventMetadata (context: HttpContext) correlationId =
        let metadata = { createMetadata context with CorrelationId = correlationId }
        metadata.Properties[ "Path" ] <- $"{context.Request.Path}"
        metadata

    type private UploadSessionRequestContext = { UploadSessionActor: IUploadSessionActor; Metadata: EventMetadata; SessionForScope: UploadSessionDto }

    let private createUploadSessionRequestContext (context: HttpContext) (parameters: UploadSessionStorageParameters) correlationId =
        task {
            let graceIds = getGraceIds context
            let _, repositoryId = resolveStorageIds graceIds parameters

            let uploadSessionActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy parameters.UploadSessionId repositoryId correlationId

            return { UploadSessionActor = uploadSessionActor; Metadata = createEventMetadata context correlationId; SessionForScope = UploadSessionDto.Default }
        }

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

    let private validateUploadSessionScope requestContext (parameters: UploadSessionStorageParameters) correlationId requireExistingSession =
        task {
            let! sessionForScope = loadSessionForScope requestContext.UploadSessionActor correlationId

            if requireExistingSession
               && (sessionForScope.UploadSessionId = UploadSessionId.Empty
                   || sessionForScope.LifecycleState = UploadSessionLifecycleState.NotStarted) then
                return Error(GraceError.Create "UploadSession must be started before this operation." correlationId)
            elif sessionForScope.UploadSessionId
                 <> UploadSessionId.Empty
                 && sessionForScope.AuthorizedScope
                    <> parameters.AuthorizedScope then
                return
                    Error(
                        GraceError.Create
                            $"UploadSession AuthorizedScope must match the scope recorded when the session was started. Expected '{sessionForScope.AuthorizedScope}', actual '{parameters.AuthorizedScope}'."
                            correlationId
                    )
            else
                return Ok { requestContext with SessionForScope = sessionForScope }
        }

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

    let private hintMatchesDedupeIndexRecord (hint: ContentBlockReuseRangeHint) (record: DedupeIndex.DedupeIndexRecord) =
        record.StoragePoolId = hint.StoragePoolId
        && record.ContentBlockAddress = hint.ContentBlockAddress
        && record.OrdinalStart = hint.OrdinalStart
        && record.OrdinalCount = hint.OrdinalCount
        && record.MetadataVersion = hint.MetadataVersion

    let private reuseRangeHintFromDedupeIndexRecord (record: DedupeIndex.DedupeIndexRecord) : ContentBlockReuseRangeHint =
        {
            StoragePoolId = record.StoragePoolId
            ContentBlockAddress = record.ContentBlockAddress
            OrdinalStart = record.OrdinalStart
            OrdinalCount = record.OrdinalCount
            MetadataVersion = record.MetadataVersion
        }

    let private validateIssuedDedupeDiscoveryHints
        correlationId
        storagePoolId
        (hints: ContentBlockReuseRangeHint array)
        (records: DedupeIndex.DedupeIndexRecord array)
        =
        let boundHints = ResizeArray<ContentBlockReuseRangeHint>()
        let mutable error = None
        let mutable index = 0

        while error.IsNone && index < hints.Length do
            let hint = hints[index]

            if isNull (box hint) then
                error <- Some(GraceError.Create "IssueDedupeDiscovery Hints must not contain null entries." correlationId)
            elif hint.StoragePoolId <> storagePoolId then
                error <- Some(GraceError.Create "IssueDedupeDiscovery Hints must come from server discovery candidates for this repository." correlationId)
            else
                match
                    records
                    |> Array.tryFind (hintMatchesDedupeIndexRecord hint)
                    with
                | Some record -> boundHints.Add(reuseRangeHintFromDedupeIndexRecord record)
                | None -> error <- Some(GraceError.Create "IssueDedupeDiscovery Hints must come from server discovery candidates." correlationId)

            index <- index + 1

        match error with
        | Some error -> Error error
        | None -> Ok(boundHints.ToArray())

    let internal requireUploadSessionStoragePool correlationId (session: UploadSessionDto) =
        if String.IsNullOrWhiteSpace session.StoragePoolId then
            Error(GraceError.Create "UploadSession StoragePoolId must be recorded before reuse operations." correlationId)
        else
            Ok session.StoragePoolId

    let internal validateIssuedDedupeDiscoveryHintsForSession correlationId (session: UploadSessionDto) hints records =
        match requireUploadSessionStoragePool correlationId session with
        | Error error -> Error error
        | Ok storagePoolId -> validateIssuedDedupeDiscoveryHints correlationId storagePoolId hints records

    let private hintMatchesIssuedHint (hint: ContentBlockReuseRangeHint) (issued: ContentBlockReuseRangeHint) =
        not (isNull (box issued))
        && issued.StoragePoolId = hint.StoragePoolId
        && issued.ContentBlockAddress = hint.ContentBlockAddress
        && issued.OrdinalStart = hint.OrdinalStart
        && issued.OrdinalCount = hint.OrdinalCount
        && issued.MetadataVersion = hint.MetadataVersion

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

    let internal validateClaimReuseHintsForSession correlationId (session: UploadSessionDto) discovery hints =
        match requireUploadSessionStoragePool correlationId session with
        | Error error -> Error error
        | Ok storagePoolId -> validateClaimReuseHints correlationId storagePoolId discovery hints

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

    let private downloadContentBlockPayloadFromPlacement
        (repositoryDto: RepositoryDto)
        (contentBlockAddress: ContentBlockAddress)
        (storagePlacement: ContentBlockStoragePlacement)
        correlationId
        =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                try
                    let! blobClient = getAzureBlobClient repositoryDto storagePlacement.ObjectKey correlationId
                    let! downloadResult = blobClient.DownloadContentAsync()

                    return Ok({ Address = contentBlockAddress; Payload = downloadResult.Value.Content.ToArray() })
                with
                | :? Azure.RequestFailedException as ex ->
                    return
                        Error(GraceError.Create $"ContentBlock payload {contentBlockAddress} could not be read from object storage: {ex.Message}" correlationId)
            | AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            | GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.UnknownObjectStorageProvider) correlationId)
        }

    let private downloadConfirmedContentBlockPayload (repositoryDto: RepositoryDto) (confirmedBlock: ConfirmedBlockUpload) correlationId =
        downloadContentBlockPayloadFromPlacement repositoryDto confirmedBlock.ContentBlockAddress confirmedBlock.StoragePlacement correlationId

    let private downloadClaimedContentBlockPayload (repositoryDto: RepositoryDto) (claimedRange: ClaimedReuseRange) correlationId =
        task {
            let metadataActor =
                grainFactory.CreateActorProxyWithCorrelationId<IContentBlockMetadataActor>(
                    Grace.Actors.ContentBlockMetadataActorKey.Create claimedRange.StoragePoolId claimedRange.ContentBlockAddress,
                    correlationId
                )

            let! metadata = metadataActor.Get correlationId

            match metadata with
            | None ->
                return
                    Error(
                        GraceError.Create
                            $"Authoritative ContentBlockMetadata is absent for claimed ContentBlock {claimedRange.ContentBlockAddress}; payload cannot be hydrated."
                            correlationId
                    )
            | Some metadata when
                metadata.StoragePoolId
                <> claimedRange.StoragePoolId
                || metadata.ContentBlockAddress
                   <> claimedRange.ContentBlockAddress
                ->
                return
                    Error(
                        GraceError.Create
                            $"Authoritative ContentBlockMetadata does not match claimed ContentBlock {claimedRange.ContentBlockAddress}."
                            correlationId
                    )
            | Some metadata ->
                return! downloadContentBlockPayloadFromPlacement repositoryDto claimedRange.ContentBlockAddress metadata.StoragePlacement correlationId
        }

    let internal selectFinalizeBlockPayloadSources
        (manifest: FileManifest)
        (confirmedBlockUploads: ConfirmedBlockUpload array)
        (claimedReuseRanges: ClaimedReuseRange array)
        =
        let sources = ResizeArray<ContentBlockAddress * FinalizeBlockPayloadSource>()

        let blockAddresses =
            if isNull (box manifest) || isNull manifest.Blocks then
                Array.empty
            else
                manifest.Blocks
                |> Seq.map (fun block -> block.Address)
                |> Seq.distinct
                |> Seq.toArray

        let confirmedBlockUploads = if isNull confirmedBlockUploads then Array.empty else confirmedBlockUploads

        let claimedReuseRanges = if isNull claimedReuseRanges then Array.empty else claimedReuseRanges

        for address in blockAddresses do
            match confirmedBlockUploads
                  |> Array.tryFind (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = address)
                with
            | Some confirmedBlock -> sources.Add(address, ConfirmedUpload confirmedBlock)
            | None ->
                match claimedReuseRanges
                      |> Array.tryFind (fun claimedRange -> claimedRange.ContentBlockAddress = address)
                    with
                | Some claimedRange -> sources.Add(address, ClaimedReuseRange claimedRange)
                | None -> ()

        sources.ToArray()

    let private hydrateFinalizeBlockPayloads
        (context: HttpContext)
        (parameters: FinalizeManifestUploadParameters)
        (manifest: FileManifest)
        (correlationId: CorrelationId)
        =
        task {
            let graceIds = getGraceIds context
            let organizationId, repositoryId = resolveStorageIds graceIds parameters
            let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
            let! repositoryDto = repositoryActor.Get correlationId
            let uploadSessionActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy parameters.UploadSessionId repositoryId correlationId
            let! session = loadSessionForScope uploadSessionActor correlationId

            match requireUploadSessionStoragePool correlationId session with
            | Error error -> return Error error
            | Ok storagePoolId ->
                match resolveRepositoryStorageRouteForPool correlationId storagePoolId repositoryDto with
                | Error routeError -> return Error routeError
                | Ok route ->
                    if isNull parameters.BlockPayloads |> not
                       && parameters.BlockPayloads.Length > 0 then
                        return Ok(parameters.BlockPayloads, storagePoolId)
                    else
                        let confirmedBlockUploads =
                            if isNull session.ConfirmedBlockUploads then
                                Array.empty
                            else
                                session.ConfirmedBlockUploads

                        let claimedReuseRanges =
                            if isNull session.ClaimedReuseRanges then
                                Array.empty
                            else
                                session.ClaimedReuseRanges

                        let payloads = ResizeArray<FinalizeManifestBlockPayload>()
                        let mutable error = None
                        let mutable index = 0

                        let casRepositoryDto = repositoryForCasStorage route repositoryDto
                        let payloadSources = selectFinalizeBlockPayloadSources manifest confirmedBlockUploads claimedReuseRanges

                        while index < payloadSources.Length
                              && Option.isNone error do
                            let _, payloadSource = payloadSources[index]

                            match payloadSource with
                            | ConfirmedUpload confirmedBlock ->
                                match! downloadConfirmedContentBlockPayload casRepositoryDto confirmedBlock correlationId with
                                | Ok payload -> payloads.Add payload
                                | Error downloadError -> error <- Some downloadError
                            | ClaimedReuseRange claimedRange ->
                                match! downloadClaimedContentBlockPayload casRepositoryDto claimedRange correlationId with
                                | Ok payload -> payloads.Add payload
                                | Error downloadError -> error <- Some downloadError

                            index <- index + 1

                        match error with
                        | Some error -> return Error error
                        | None -> return Ok(payloads.ToArray(), storagePoolId)
        }

    let private manifestContainsContentBlock (contentBlockAddress: ContentBlockAddress) (manifest: FileManifest) =
        not (isNull (box manifest))
        && not (isNull manifest.Blocks)
        && manifest.Blocks
           |> Seq.exists (fun block ->
               not (isNull (box block))
               && block.Address = contentBlockAddress)

    let private validateContentBlockDownloadManifest
        correlationId
        (contentBlockAddress: ContentBlockAddress)
        (storagePoolId: StoragePoolId)
        (manifest: FileManifest)
        =
        if isNull (box manifest) then
            Error(GraceError.Create "ContentBlock download requires a FileManifest." correlationId)
        elif String.IsNullOrWhiteSpace manifest.ManifestAddress then
            Error(GraceError.Create "ContentBlock download requires a FileManifest ManifestAddress." correlationId)
        elif not (ContentAddress.isValidAddress manifest.ManifestAddress) then
            Error(GraceError.Create "ContentBlock download FileManifest ManifestAddress must be a 64-character hexadecimal BLAKE3 value." correlationId)
        elif String.IsNullOrWhiteSpace manifest.StoragePoolId then
            Error(GraceError.Create "ContentBlock download FileManifest StoragePoolId is required." correlationId)
        elif manifest.StoragePoolId <> storagePoolId then
            Error(GraceError.Create "ContentBlock download FileManifest StoragePoolId must match the resolved repository storage route." correlationId)
        elif not (manifestContainsContentBlock contentBlockAddress manifest) then
            Error(GraceError.Create "ContentBlock download requires the requested ContentBlockAddress to belong to the supplied FileManifest." correlationId)
        else
            let expectedManifestAddress = ContentAddress.computeManifestAddressForManifest manifest

            if expectedManifestAddress
               <> manifest.ManifestAddress then
                Error(GraceError.Create "ContentBlock download FileManifest ManifestAddress does not match its reconstruction contract." correlationId)
            else
                Ok()

    let private loadRepositoryContentCounter repositoryId manifestAddress correlationId =
        task {
            let counterActor =
                grainFactory.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                    Grace.Actors.RepositoryContentCounter.primaryKey repositoryId manifestAddress,
                    correlationId
                )

            return! counterActor.Get correlationId
        }

    let private repositoryOwnsManifest repositoryId manifestAddress correlationId =
        task {
            let! counter = loadRepositoryContentCounter repositoryId manifestAddress correlationId

            return
                counter.RepositoryId = repositoryId
                && counter.ManifestAddress = manifestAddress
                && counter.ReferenceCount > 0L
                && counter.LifecycleState = RepositoryContentCounterLifecycleState.Referenced
        }

    let private uploadSessionOwnsManifest repositoryId (parameters: GetContentBlockDownloadUriParameters) (manifest: FileManifest) correlationId =
        task {
            if parameters.UploadSessionId = UploadSessionId.Empty then
                return false
            else
                let uploadSessionActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy parameters.UploadSessionId repositoryId correlationId

                let! session = loadSessionForScope uploadSessionActor correlationId

                return
                    session.UploadSessionId = parameters.UploadSessionId
                    && session.RepositoryId = repositoryId
                    && session.FinalizedManifestAddress = Some manifest.ManifestAddress
                    && session.StoragePoolId = manifest.StoragePoolId
                    && (String.IsNullOrWhiteSpace parameters.AuthorizedScope
                        || session.AuthorizedScope = parameters.AuthorizedScope)
        }

    let private authorizeContentBlockDownloadForManifest repositoryId (parameters: GetContentBlockDownloadUriParameters) correlationId =
        task {
            let! sessionOwnsManifest = uploadSessionOwnsManifest repositoryId parameters parameters.Manifest correlationId

            if sessionOwnsManifest then
                return Ok()
            else
                let! hasRepositoryManifestReference = repositoryOwnsManifest repositoryId parameters.Manifest.ManifestAddress correlationId

                if hasRepositoryManifestReference then
                    return Ok()
                else
                    return
                        Error(
                            GraceError.Create
                                "ContentBlock download requires a repository-owned manifest or finalized upload session before issuing a read SAS."
                                correlationId
                        )
        }

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

    /// Gets a download URI for the specified file version that can be used by a Grace client.
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

                    let! blobName = getReadableWholeFileContentObjectKey repositoryDto parameters.FileVersion correlationId
                    let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                    context.SetStatusCode StatusCodes.Status200OK
                    //log.LogTrace("fileVersion: {fileVersion.RelativePath}; downloadUri: {downloadUri}", [| parameters.FileVersion.RelativePath, downloadUri |])
                    return! context.WriteStringAsync $"{downloadUri}"
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    return! context.WriteTextAsync $"Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    let GetContentBlockUploadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetContentBlockUploadUriParameters>()

                    match validateContentBlockAddress correlationId parameters.ContentBlockAddress with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok () ->
                        let organizationId, repositoryId = resolveStorageIds graceIds parameters
                        let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                        let! repositoryDto = repositoryActor.Get correlationId

                        let! routeResult =
                            task {
                                if parameters.UploadSessionId = UploadSessionId.Empty then
                                    return resolveRepositoryStorageRoute correlationId repositoryDto
                                else
                                    let! requestContext = createUploadSessionRequestContext context parameters correlationId
                                    let! scopeValidation = validateUploadSessionScope requestContext parameters correlationId true

                                    match scopeValidation with
                                    | Error error -> return Error error
                                    | Ok requestContext ->
                                        match requireUploadSessionStoragePool correlationId requestContext.SessionForScope with
                                        | Error error -> return Error error
                                        | Ok storagePoolId -> return resolveRepositoryStorageRouteForPool correlationId storagePoolId repositoryDto
                            }

                        match routeResult with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok route ->
                            let blobName = getContentBlockObjectKey parameters.ContentBlockAddress
                            let casRepositoryDto = repositoryForCasStorage route repositoryDto
                            let! uploadUri = getUriWithCreateSharedAccessSignature casRepositoryDto blobName correlationId
                            context.SetStatusCode StatusCodes.Status200OK
                            return! context.WriteStringAsync uploadUri.AbsoluteUri
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetContentBlockUploadUri: {(ExceptionResponse.Create ex)}"

                    return! context.WriteTextAsync $"{getCurrentInstantExtended ()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    let GetContentBlockDownloadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetContentBlockDownloadUriParameters>()

                    match validateContentBlockAddress correlationId parameters.ContentBlockAddress with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok () ->
                        let organizationId, repositoryId = resolveStorageIds graceIds parameters
                        let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                        let! repositoryDto = repositoryActor.Get correlationId

                        let routeResult =
                            if String.IsNullOrWhiteSpace parameters.StoragePoolId then
                                resolveRepositoryStorageRoute correlationId repositoryDto
                            else
                                resolveRepositoryStorageRouteForPool correlationId parameters.StoragePoolId repositoryDto

                        match routeResult with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok route ->
                            match validateContentBlockDownloadManifest correlationId parameters.ContentBlockAddress route.StoragePoolId parameters.Manifest with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok () ->
                                match! authorizeContentBlockDownloadForManifest repositoryId parameters correlationId with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok () ->
                                    let blobName = getContentBlockObjectKey parameters.ContentBlockAddress
                                    let casRepositoryDto = repositoryForCasStorage route repositoryDto
                                    let! downloadUri = getUriWithReadSharedAccessSignature casRepositoryDto blobName correlationId
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

                        match resolveRepositoryStorageRoute correlationId repositoryDto with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok route ->
                            let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId
                            let! snapshot = dedupeIndexActor.Snapshot correlationId
                            let result = DedupeIndex.discover route.StoragePoolId keyChunkAddresses (getCurrentInstant ()) snapshot

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

                    match resolveRepositoryStorageRoute correlationId repositoryDto with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok route ->
                        let command =
                            UploadSessionCommand.Start
                                {
                                    UploadSessionId = parameters.UploadSessionId
                                    OwnerId = ownerId
                                    OrganizationId = organizationId
                                    RepositoryId = repositoryId
                                    StoragePoolId = route.StoragePoolId
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

    let IssueDedupeDiscovery: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<IssueDedupeDiscoveryParameters>()

                    let hints = if isNull parameters.Hints then Array.empty else parameters.Hints

                    if hints.Length > StorageParameterContracts.MaxReuseRangeClaims then
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create
                                    $"IssueDedupeDiscovery Hints must contain no more than {StorageParameterContracts.MaxReuseRangeClaims} items."
                                    correlationId
                            )
                    else
                        let! requestContext = createUploadSessionRequestContext context parameters correlationId
                        let! scopeValidation = validateUploadSessionScope requestContext parameters correlationId true

                        match scopeValidation with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok requestContext ->
                            let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId
                            let! records = dedupeIndexActor.Snapshot correlationId

                            match validateIssuedDedupeDiscoveryHintsForSession correlationId requestContext.SessionForScope hints records with
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
                                match validateClaimReuseHintsForSession correlationId requestContext.SessionForScope discovery hints with
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

    let RegisterContentBlockUpload: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<RegisterContentBlockUploadParameters>()

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

    let ConfirmContentBlockUpload: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<ConfirmContentBlockUploadParameters>()

                    let command =
                        UploadSessionCommand.ConfirmBlockUploaded
                            {
                                OperationId = parameters.OperationId
                                ContentBlockAddress = parameters.ContentBlockAddress
                                Payload = parameters.Payload
                                StoragePlacement = parameters.StoragePlacement
                            }

                    return! handleUploadSessionCommand context parameters command correlationId
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    logToConsole $"Exception in ConfirmContentBlockUpload: {exceptionResponse}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{exceptionResponse}" correlationId)
            }

    let FinalizeManifestUpload: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<FinalizeManifestUploadParameters>()
                    let! blockPayloadsResult = hydrateFinalizeBlockPayloads context parameters parameters.Manifest correlationId

                    match blockPayloadsResult with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok (blockPayloads, storagePoolId) ->
                        let manifest = stampManifestStoragePool storagePoolId parameters.Manifest

                        let command =
                            UploadSessionCommand.FinalizeManifest { OperationId = parameters.OperationId; Manifest = manifest; BlockPayloads = blockPayloads }

                        return! handleUploadSessionCommand context parameters command correlationId
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
