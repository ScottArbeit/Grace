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

    let private getLegacyWholeFileContentObjectKey (fileVersion: FileVersion) =
        normalizeWholeFileContentReference fileVersion
        |> ignore

        StorageKeys.legacyWholeFileContentObjectKey fileVersion

    let private getReadableWholeFileContentObjectKey (repositoryDto: RepositoryDto) (fileVersion: FileVersion) correlationId =
        task {
            let currentBlobName = getWholeFileContentObjectKey fileVersion

            if StorageKeys.hasBlake3SpecificWholeFileContentObjectKey fileVersion then
                let! currentBlobClient = getAzureBlobClient repositoryDto currentBlobName correlationId
                let! currentExists = currentBlobClient.ExistsAsync()

                if currentExists.Value then
                    return currentBlobName
                else
                    let legacyBlobName = getLegacyWholeFileContentObjectKey fileVersion
                    let! legacyBlobClient = getAzureBlobClient repositoryDto legacyBlobName correlationId
                    let! legacyExists = legacyBlobClient.ExistsAsync()

                    if legacyExists.Value then return legacyBlobName else return currentBlobName
            else
                return currentBlobName
        }

    let private getContentBlockObjectKey (contentBlockAddress: ContentBlockAddress) = StorageKeys.contentBlockObjectKey contentBlockAddress

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

    let private downloadContentBlockPayload (repositoryDto: RepositoryDto) (confirmedBlock: ConfirmedBlockUpload) correlationId =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                try
                    let! blobClient = getAzureBlobClient repositoryDto confirmedBlock.StoragePlacement.ObjectKey correlationId
                    let! downloadResult = blobClient.DownloadContentAsync()

                    return Ok({ Address = confirmedBlock.ContentBlockAddress; Payload = downloadResult.Value.Content.ToArray() })
                with
                | :? Azure.RequestFailedException as ex ->
                    return
                        Error(
                            GraceError.Create
                                $"Confirmed ContentBlock payload {confirmedBlock.ContentBlockAddress} could not be read from object storage: {ex.Message}"
                                correlationId
                        )
            | AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            | GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.UnknownObjectStorageProvider) correlationId)
        }

    let private hydrateFinalizeBlockPayloads
        (context: HttpContext)
        (parameters: FinalizeManifestUploadParameters)
        (manifest: FileManifest)
        (correlationId: CorrelationId)
        =
        task {
            if isNull parameters.BlockPayloads |> not
               && parameters.BlockPayloads.Length > 0 then
                return Ok parameters.BlockPayloads
            else
                let graceIds = getGraceIds context
                let organizationId, repositoryId = resolveStorageIds graceIds parameters
                let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                let! repositoryDto = repositoryActor.Get correlationId
                let uploadSessionActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy parameters.UploadSessionId repositoryId correlationId
                let! session = uploadSessionActor.Get correlationId

                let! confirmedBlockUploads =
                    task {
                        if
                            isNull (box session)
                            || isNull session.ConfirmedBlockUploads
                        then
                            let! events = uploadSessionActor.GetEvents correlationId

                            return
                                events
                                |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default
                                |> fun dto ->
                                    if isNull dto.ConfirmedBlockUploads then
                                        Array.empty
                                    else
                                        dto.ConfirmedBlockUploads
                        else
                            return session.ConfirmedBlockUploads
                    }

                let payloads = ResizeArray<FinalizeManifestBlockPayload>()
                let mutable error = None
                let mutable index = 0

                let blockAddresses =
                    if isNull (box manifest) || isNull manifest.Blocks then
                        Array.empty
                    else
                        manifest.Blocks
                        |> Seq.map (fun block -> block.Address)
                        |> Seq.distinct
                        |> Seq.toArray

                while index < blockAddresses.Length
                      && Option.isNone error do
                    let address = blockAddresses[index]

                    match confirmedBlockUploads
                          |> Array.tryFind (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = address)
                        with
                    | None -> ()
                    | Some confirmedBlock ->
                        match! downloadContentBlockPayload repositoryDto confirmedBlock correlationId with
                        | Ok payload -> payloads.Add payload
                        | Error downloadError -> error <- Some downloadError

                    index <- index + 1

                match error with
                | Some error -> return Error error
                | None -> return Ok(payloads.ToArray())
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

    /// Gets an upload URI for the specified ContentBlock payload without probing whether the blob already exists.
    let GetContentBlockUploadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetContentBlockUploadUriParameters>()
                    let organizationId, repositoryId = resolveStorageIds graceIds parameters
                    let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    let blobName = getContentBlockObjectKey parameters.ContentBlockAddress
                    let! uploadUri = getUriWithCreateSharedAccessSignature repositoryDto blobName correlationId
                    context.SetStatusCode StatusCodes.Status200OK
                    return! context.WriteStringAsync uploadUri.AbsoluteUri
                with
                | ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetContentBlockUploadUri: {(ExceptionResponse.Create ex)}"

                    return! context.WriteTextAsync $"{getCurrentInstantExtended ()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Gets a download URI for the specified ContentBlock payload without probing whether the blob already exists.
    let GetContentBlockDownloadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetContentBlockDownloadUriParameters>()
                    let organizationId, repositoryId = resolveStorageIds graceIds parameters
                    let repositoryActor = Repository.CreateActorProxy organizationId repositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    let blobName = getContentBlockObjectKey parameters.ContentBlockAddress
                    let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
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
                        let storagePoolId = DedupeIndex.storagePoolIdForRepository repositoryDto
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

    let StartManifestUploadSession: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<StartManifestUploadSessionParameters>()
                    let ownerId = resolveOwnerId graceIds parameters
                    let organizationId, repositoryId = resolveStorageIds graceIds parameters

                    let command =
                        UploadSessionCommand.Start
                            {
                                UploadSessionId = parameters.UploadSessionId
                                OwnerId = ownerId
                                OrganizationId = organizationId
                                RepositoryId = repositoryId
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
                            let repositoryActor =
                                Repository.CreateActorProxy
                                    requestContext.SessionForScope.OrganizationId
                                    requestContext.SessionForScope.RepositoryId
                                    correlationId

                            let! repositoryDto = repositoryActor.Get correlationId
                            let storagePoolId = DedupeIndex.storagePoolIdForRepository repositoryDto
                            let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId
                            let! records = dedupeIndexActor.Snapshot correlationId

                            match validateIssuedDedupeDiscoveryHints correlationId storagePoolId hints records with
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
                                let repositoryActor =
                                    Repository.CreateActorProxy
                                        requestContext.SessionForScope.OrganizationId
                                        requestContext.SessionForScope.RepositoryId
                                        correlationId

                                let! repositoryDto = repositoryActor.Get correlationId
                                let storagePoolId = DedupeIndex.storagePoolIdForRepository repositoryDto

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
                    | Ok blockPayloads ->
                        let command =
                            UploadSessionCommand.FinalizeManifest
                                { OperationId = parameters.OperationId; Manifest = parameters.Manifest; BlockPayloads = blockPayloads }

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
