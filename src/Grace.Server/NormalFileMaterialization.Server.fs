namespace Grace.Server

open Azure
open Azure.Storage.Blobs.Models
open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Repository
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Linq
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

/// Contains Grace Server normal file materialization behavior and supporting helpers.
module internal NormalFileMaterialization =

    /// Represents object payload reader used by Grace Server APIs and background services.
    type ObjectPayloadReader = string -> CorrelationId -> CancellationToken -> Task<Result<byte array, GraceError>>

    /// Represents object payload writer used by Grace Server APIs and background services.
    type ObjectPayloadWriter = string -> byte array -> CorrelationId -> CancellationToken -> Task<Result<unit, GraceError>>

    /// Represents content block metadata batch resolver used by Grace Server APIs and background services.
    type ContentBlockMetadataBatchResolver =
        FileManifest -> ContentBlockAddress array -> CorrelationId -> CancellationToken -> Task<Result<ContentBlockMetadata array, GraceError>>

    /// Represents content block placement payload reader used by Grace Server APIs and background services.
    type ContentBlockPlacementPayloadReader =
        ContentBlockStoragePlacement -> ContentBlockAddress -> CorrelationId -> CancellationToken -> Task<Result<byte array, GraceError>>

    /// Computes error data used by Grace Server.
    let private error correlationId message = GraceError.Create message correlationId

    /// Computes copy bytes data used by Grace Server.
    let private copyBytes (bytes: byte array) =
        let copy = Array.zeroCreate<byte> bytes.Length
        Array.Copy(bytes, copy, bytes.Length)
        copy

    /// Computes sha256 hex data used by Grace Server.
    let private sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Computes blake3 hex data used by Grace Server.
    let private blake3Hex (bytes: byte array) = ContentAddress.computeBlake3Hex bytes

    /// Implements gzip bytes for the server request pipeline.
    let private gzipBytes (bytes: byte array) =
        use compressed = new MemoryStream()

        do
            use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)
            gzipStream.Write(bytes, 0, bytes.Length)

        compressed.ToArray()

    /// Implements complete content block storage placement for the server request pipeline.
    let private completeContentBlockStoragePlacement (placement: ContentBlockStoragePlacement) =
        not (isNull (box placement))
        && not (String.IsNullOrWhiteSpace placement.StorageAccountName)
        && not (String.IsNullOrWhiteSpace placement.StorageContainerName)
        && not (String.IsNullOrWhiteSpace placement.ObjectKey)

    /// Validates validate file version shape inputs before server processing continues.
    let private validateFileVersionShape (fileVersion: FileVersion) correlationId =
        if isNull (box fileVersion) then
            Error(error correlationId "Normal file download FileVersion is required.")
        elif fileVersion.Size < 0L then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' has an invalid negative size.")
        elif isNull (box fileVersion.ContentReference) then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' has no ContentReference.")
        else
            Ok()

    /// Validates validate exact file bytes inputs before server processing continues.
    let private validateExactFileBytes (fileVersion: FileVersion) (bytes: byte array) correlationId =
        if isNull bytes then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' materialized null bytes.")
        elif int64 bytes.Length <> fileVersion.Size then
            Error(
                error
                    correlationId
                    $"Normal file download target '{fileVersion.RelativePath}' materialized {bytes.Length} bytes, but FileVersion.Size is {fileVersion.Size} bytes."
            )
        elif String.IsNullOrWhiteSpace fileVersion.Sha256Hash then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' must include FileVersion.Sha256Hash before materialization.")
        elif not (String.Equals(sha256Hex bytes, fileVersion.Sha256Hash, StringComparison.OrdinalIgnoreCase)) then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' materialized bytes do not match FileVersion.Sha256Hash.")
        elif String.IsNullOrWhiteSpace fileVersion.Blake3Hash then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' must include FileVersion.Blake3Hash before materialization.")
        elif not (String.Equals(blake3Hex bytes, fileVersion.Blake3Hash, StringComparison.OrdinalIgnoreCase)) then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' materialized bytes do not match FileVersion.Blake3Hash.")
        else
            Ok(copyBytes bytes)

    /// Implements describe manifest validation error for the server request pipeline.
    let private describeManifestValidationError validationError = $"Invalid manifest reconstruction: {validationError}."

    /// Validates validate manifest storage pool inputs before server processing continues.
    let private validateManifestStoragePool (fileVersion: FileVersion) (manifest: FileManifest) correlationId =
        if String.IsNullOrWhiteSpace manifest.StoragePoolId then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' FileManifest StoragePoolId is required.")
        else
            Ok()

    /// Implements manifest block addresses for the server request pipeline.
    let private manifestBlockAddresses (manifest: FileManifest) =
        let addresses = ResizeArray<ContentBlockAddress>()
        let seen = HashSet<ContentBlockAddress>()

        if
            not (isNull (box manifest))
            && not (isNull manifest.Blocks)
        then
            let mutable index = 0

            while index < manifest.Blocks.Count do
                let block = manifest.Blocks[index]

                if not (isNull (box block)) && seen.Add block.Address then
                    addresses.Add block.Address

                index <- index + 1

        addresses.ToArray()

    /// Implements manifest blocks by address for the server request pipeline.
    let private manifestBlocksByAddress (manifest: FileManifest) =
        let blocks = Dictionary<ContentBlockAddress, ResizeArray<ContentBlock>>()

        if
            not (isNull (box manifest))
            && not (isNull manifest.Blocks)
        then
            let mutable index = 0

            while index < manifest.Blocks.Count do
                let block = manifest.Blocks[index]

                if not (isNull (box block)) then
                    match blocks.TryGetValue block.Address with
                    | true, existing -> existing.Add block
                    | false, _ ->
                        let values = ResizeArray<ContentBlock>()
                        values.Add block
                        blocks[block.Address] <- values

                index <- index + 1

        blocks

    /// Implements has active physical range cover for the server request pipeline.
    let private hasActivePhysicalRangeCover expectedLength (metadata: ContentBlockMetadata) =
        if expectedLength <= 0L || isNull metadata.Ranges then
            false
        else
            let ranges =
                metadata.Ranges
                |> Array.filter (fun range ->
                    not (isNull (box range))
                    && range.ActiveManifestCount > 0
                    && range.OrdinalStart >= 0
                    && range.OrdinalCount > 0
                    && range.PhysicalOffset >= 0L
                    && range.PhysicalLength > 0L)
                |> Array.sortBy (fun range -> range.OrdinalStart, range.PhysicalOffset)

            let mutable coveredLength = 0L
            let mutable expectedOrdinal = 0
            let mutable error = false
            let mutable index = 0

            while index < ranges.Length
                  && not error
                  && coveredLength < expectedLength do
                let range = ranges[index]

                if range.OrdinalStart <> expectedOrdinal then
                    error <- true
                else
                    coveredLength <- coveredLength + range.PhysicalLength
                    expectedOrdinal <- expectedOrdinal + range.OrdinalCount

                    if coveredLength > expectedLength then error <- true

                index <- index + 1

            not error && coveredLength = expectedLength

    /// Validates validate manifest block metadata inputs before server processing continues.
    let private validateManifestBlockMetadata
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (blocksForAddress: IDictionary<ContentBlockAddress, ResizeArray<ContentBlock>>)
        (metadata: ContentBlockMetadata)
        correlationId
        =
        if isNull (box metadata) then
            Error(error correlationId $"Normal file download target '{fileVersion.RelativePath}' ContentBlockMetadata is absent.")
        elif metadata.StoragePoolId <> manifest.StoragePoolId then
            Error(
                error
                    correlationId
                    $"Normal file download target '{fileVersion.RelativePath}' ContentBlockMetadata StoragePoolId does not match FileManifest.StoragePoolId for {metadata.ContentBlockAddress}."
            )
        elif not (blocksForAddress.ContainsKey metadata.ContentBlockAddress) then
            Error(
                error
                    correlationId
                    $"Normal file download target '{fileVersion.RelativePath}' ContentBlockMetadata ContentBlockAddress is not referenced by FileManifest."
            )
        elif metadata.BlockFormatVersion <= 0s then
            Error(
                error
                    correlationId
                    $"Normal file download target '{fileVersion.RelativePath}' ContentBlockMetadata BlockFormatVersion is required for manifest block {metadata.ContentBlockAddress}."
            )
        elif not (completeContentBlockStoragePlacement metadata.StoragePlacement) then
            Error(
                error
                    correlationId
                    $"Normal file download target '{fileVersion.RelativePath}' ContentBlockMetadata StoragePlacement is incomplete for manifest block {metadata.ContentBlockAddress}."
            )
        else
            let blocks = blocksForAddress[metadata.ContentBlockAddress]
            let mutable blockIndex = 0
            let mutable missingRangeCover = false

            while blockIndex < blocks.Count && not missingRangeCover do
                missingRangeCover <- not (hasActivePhysicalRangeCover blocks[blockIndex].Size metadata)
                blockIndex <- blockIndex + 1

            if missingRangeCover then
                Error(
                    error
                        correlationId
                        $"Normal file download target '{fileVersion.RelativePath}' ContentBlockMetadata active ranges do not cover manifest block {metadata.ContentBlockAddress}."
                )
            else
                Ok metadata.StoragePlacement

    /// Implements materialize whole file bytes for the server request pipeline.
    let private materializeWholeFileBytes
        (fileVersion: FileVersion)
        (objectKey: string)
        (readObjectPayload: ObjectPayloadReader)
        correlationId
        cancellationToken
        =
        task {
            match! readObjectPayload objectKey correlationId cancellationToken with
            | Ok bytes -> return validateExactFileBytes fileVersion bytes correlationId
            | Error readError -> return Error readError
        }

    /// Implements manifest payloads for the server request pipeline.
    let private manifestPayloads
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (resolveContentBlockMetadata: ContentBlockMetadataBatchResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            let blockAddresses = manifestBlockAddresses manifest
            let blocksByAddress = manifestBlocksByAddress manifest

            match! resolveContentBlockMetadata manifest blockAddresses correlationId cancellationToken with
            | Error metadataError -> return Error metadataError
            | Ok metadataRecords ->
                let metadataByAddress = Dictionary<ContentBlockAddress, ContentBlockMetadata>()
                let mutable metadataError = None
                let mutable metadataIndex = 0

                while metadataIndex < metadataRecords.Length
                      && Option.isNone metadataError do
                    let metadata = metadataRecords[metadataIndex]

                    if isNull (box metadata) then
                        metadataError <-
                            Some(error correlationId $"Normal file download target '{fileVersion.RelativePath}' resolved null ContentBlockMetadata.")
                    else
                        match validateManifestBlockMetadata fileVersion manifest blocksByAddress metadata correlationId with
                        | Error validationError -> metadataError <- Some validationError
                        | Ok _ -> metadataByAddress[metadata.ContentBlockAddress] <- metadata

                    metadataIndex <- metadataIndex + 1

                match metadataError with
                | Some validationError -> return Error validationError
                | None ->
                    let payloads = ResizeArray<ManifestValidation.ManifestBlockPayload>()
                    let mutable readError = None
                    let mutable blockIndex = 0

                    while blockIndex < blockAddresses.Length
                          && Option.isNone readError do
                        let address = blockAddresses[blockIndex]

                        match metadataByAddress.TryGetValue address with
                        | false, _ ->
                            readError <-
                                Some(
                                    error
                                        correlationId
                                        $"Normal file download target '{fileVersion.RelativePath}' has no finalized StoragePool placement evidence for FileManifest block {address}."
                                )
                        | true, metadata ->
                            match validateManifestBlockMetadata fileVersion manifest blocksByAddress metadata correlationId with
                            | Error validationError -> readError <- Some validationError
                            | Ok placement ->
                                match! readContentBlockPayload placement address correlationId cancellationToken with
                                | Ok payload -> payloads.Add(ManifestValidation.createBlockPayload address payload)
                                | Error payloadError -> readError <- Some payloadError

                        blockIndex <- blockIndex + 1

                    match readError with
                    | Some error -> return Error error
                    | None -> return Ok(payloads.ToArray())
        }

    /// Implements materialize manifest bytes for the server request pipeline.
    let private materializeManifestBytes
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (resolveContentBlockMetadata: ContentBlockMetadataBatchResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        correlationId
        cancellationToken
        =
        task {
            if isNull (box manifest) then
                return
                    Error(
                        error correlationId $"Normal file download target '{fileVersion.RelativePath}' FileManifest ContentReference is missing its manifest."
                    )
            elif manifest.Size <> fileVersion.Size then
                return
                    Error(
                        error
                            correlationId
                            $"Normal file download target '{fileVersion.RelativePath}' FileManifest.Size {manifest.Size} does not match FileVersion.Size {fileVersion.Size}."
                    )
            else
                match validateManifestStoragePool fileVersion manifest correlationId with
                | Error validationError -> return Error validationError
                | Ok () ->
                    match! manifestPayloads fileVersion manifest resolveContentBlockMetadata readContentBlockPayload correlationId cancellationToken with
                    | Error readError -> return Error readError
                    | Ok payloads ->
                        match ManifestValidation.validate RabinChunking.SuiteName manifest payloads with
                        | Ok bytes -> return validateExactFileBytes fileVersion bytes correlationId
                        | Error validationError ->
                            return
                                Error(
                                    error
                                        correlationId
                                        $"Normal file download target '{fileVersion.RelativePath}' {describeManifestValidationError validationError}"
                                )
        }

    /// Implements materialize bytes with readers for the server request pipeline.
    let materializeBytesWithReaders
        (readWholeFileObjectPayload: ObjectPayloadReader)
        (resolveContentBlockMetadata: ContentBlockMetadataBatchResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        (fileVersion: FileVersion)
        objectKey
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            match validateFileVersionShape fileVersion correlationId with
            | Error validationError -> return Error validationError
            | Ok () ->
                match fileVersion.ContentReference.ReferenceType with
                | FileContentReferenceType.WholeFileContent ->
                    return! materializeWholeFileBytes fileVersion objectKey readWholeFileObjectPayload correlationId cancellationToken
                | FileContentReferenceType.FileManifest ->
                    match fileVersion.ContentReference.Manifest with
                    | Some manifest ->
                        return!
                            materializeManifestBytes fileVersion manifest resolveContentBlockMetadata readContentBlockPayload correlationId cancellationToken
                    | None ->
                        return
                            Error(
                                error
                                    correlationId
                                    $"Normal file download target '{fileVersion.RelativePath}' FileManifest ContentReference is missing its manifest."
                            )
                | unsupported ->
                    return
                        Error(
                            error
                                correlationId
                                $"Normal file download target '{fileVersion.RelativePath}' has unsupported ContentReference type '{unsupported}'."
                        )
        }

    /// Implements materialize for download with readers for the server request pipeline.
    let materializeForDownloadWithReaders
        (readWholeFileObjectPayload: ObjectPayloadReader)
        (writeWholeFileObjectPayload: ObjectPayloadWriter)
        (resolveContentBlockMetadata: ContentBlockMetadataBatchResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        (fileVersion: FileVersion)
        objectKey
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            match!
                materializeBytesWithReaders
                    readWholeFileObjectPayload
                    resolveContentBlockMetadata
                    readContentBlockPayload
                    fileVersion
                    objectKey
                    correlationId
                    cancellationToken
                with
            | Error materializationError -> return Error materializationError
            | Ok bytes ->
                match fileVersion.ContentReference.ReferenceType with
                | FileContentReferenceType.WholeFileContent -> return Ok()
                | FileContentReferenceType.FileManifest ->
                    let storagePayload = if fileVersion.IsBinary then bytes else gzipBytes bytes
                    return! writeWholeFileObjectPayload objectKey storagePayload correlationId cancellationToken
                | unsupported ->
                    return
                        Error(
                            error
                                correlationId
                                $"Normal file download target '{fileVersion.RelativePath}' has unsupported ContentReference type '{unsupported}'."
                        )
        }

    /// Implements azure object payload reader for the server request pipeline.
    let private azureObjectPayloadReader (repositoryDto: RepositoryDto) objectKey correlationId (cancellationToken: CancellationToken) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                try
                    let! blobClient = getAzureBlobClient repositoryDto objectKey correlationId
                    let! download = blobClient.DownloadContentAsync(cancellationToken)
                    return Ok(download.Value.Content.ToArray())
                with
                | :? RequestFailedException as ex ->
                    return Error(error correlationId $"Normal file content object '{objectKey}' could not be read from object storage: {ex.Message}")
            | AWSS3 -> return Error(error correlationId "Normal file content materialization is not implemented for AWS S3 object storage.")
            | GoogleCloudStorage -> return Error(error correlationId "Normal file content materialization is not implemented for Google Cloud Storage.")
            | ObjectStorageProvider.Unknown ->
                return Error(error correlationId "Normal file content materialization cannot use an unknown object storage provider.")
        }

    /// Implements azure object payload writer for the server request pipeline.
    let private azureObjectPayloadWriter (repositoryDto: RepositoryDto) objectKey (bytes: byte array) correlationId (cancellationToken: CancellationToken) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                try
                    let! blobClient = getAzureBlobClient repositoryDto objectKey correlationId
                    use payloadStream = new MemoryStream(bytes, writable = false)
                    let uploadOptions = BlobUploadOptions()
                    let! _ = blobClient.UploadAsync(payloadStream, uploadOptions, cancellationToken)
                    return Ok()
                with
                | :? RequestFailedException as ex ->
                    return Error(error correlationId $"Normal file content object '{objectKey}' could not be written to object storage: {ex.Message}")
            | AWSS3 -> return Error(error correlationId "Normal file content materialization is not implemented for AWS S3 object storage.")
            | GoogleCloudStorage -> return Error(error correlationId "Normal file content materialization is not implemented for Google Cloud Storage.")
            | ObjectStorageProvider.Unknown ->
                return Error(error correlationId "Normal file content materialization cannot use an unknown object storage provider.")
        }

    /// Implements azure content block placement payload reader for the server request pipeline.
    let private azureContentBlockPlacementPayloadReader
        (placement: ContentBlockStoragePlacement)
        (contentBlockAddress: ContentBlockAddress)
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            try
                match! getAzureContentBlockClientForPlacement placement correlationId with
                | Error error -> return Error error
                | Ok blobClient ->
                    let! download = blobClient.DownloadContentAsync(cancellationToken)
                    return Ok(download.Value.Content.ToArray())
            with
            | :? RequestFailedException as ex ->
                return
                    Error(
                        error
                            correlationId
                            $"Normal file ContentBlock payload '{contentBlockAddress}' could not be read from stored CAS placement: {ex.Message}"
                    )
        }

    /// Implements finalized manifest metadata resolver for the server request pipeline.
    let private finalizedManifestMetadataResolver
        (repositoryDto: RepositoryDto)
        (authorizedScope: string)
        (manifest: FileManifest)
        (contentBlockAddresses: ContentBlockAddress array)
        correlationId
        _
        =
        task {
            let dedupeIndexActor = DedupeIndexActor.CreateActorProxy correlationId
            let metadata = ResizeArray<ContentBlockMetadata>()
            let mutable firstError = None
            let mutable index = 0

            while index < contentBlockAddresses.Length
                  && Option.isNone firstError do
                let contentBlockAddress = contentBlockAddresses[index]

                match!
                    dedupeIndexActor.TryGetFinalizedScopedContentBlockMetadata
                        (
                            manifest.StoragePoolId,
                            repositoryDto.RepositoryId,
                            authorizedScope,
                            manifest.ManifestAddress,
                            contentBlockAddress,
                            correlationId
                        )
                    with
                | Some record -> metadata.Add record
                | None ->
                    firstError <-
                        Some(
                            error
                                correlationId
                                $"Normal file download has no finalized StoragePool placement evidence for FileManifest block {contentBlockAddress}."
                        )

                index <- index + 1

            match firstError with
            | Some error -> return Error error
            | None -> return Ok(metadata.ToArray())
        }

    /// Implements materialize for download for the server request pipeline.
    let materializeForDownload (repositoryDto: RepositoryDto) (authorizedScope: string) (fileVersion: FileVersion) objectKey correlationId cancellationToken =
        /// Computes metadata resolver data used by Grace Server.
        let metadataResolver manifest contentBlockAddresses correlationId cancellationToken =
            finalizedManifestMetadataResolver repositoryDto authorizedScope manifest contentBlockAddresses correlationId cancellationToken

        materializeForDownloadWithReaders
            (azureObjectPayloadReader repositoryDto)
            (azureObjectPayloadWriter repositoryDto)
            metadataResolver
            azureContentBlockPlacementPayloadReader
            fileVersion
            objectKey
            correlationId
            cancellationToken
