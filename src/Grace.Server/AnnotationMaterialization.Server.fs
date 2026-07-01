namespace Grace.Server

open Azure
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.Repository
open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Contains Grace Server annotation materialization behavior and supporting helpers.
module internal AnnotationMaterialization =

    [<Literal>]
    let MaxMaterializedTextBytes = 10L * 1024L * 1024L

    [<Literal>]
    let private MaxContentBlockPayloadBytes = MaxMaterializedTextBytes + 1024L * 1024L

    /// Represents materialized text content used by Grace Server APIs and background services.
    type MaterializedTextContent = { FileVersion: FileVersion; Bytes: byte array; Text: string }

    /// Represents object payload reader used by Grace Server APIs and background services.
    type ObjectPayloadReader = string -> CorrelationId -> CancellationToken -> Task<Result<byte array, GraceError>>

    /// Represents content block metadata resolver used by Grace Server APIs and background services.
    type ContentBlockMetadataResolver =
        FileManifest -> ContentBlockAddress -> CorrelationId -> CancellationToken -> Task<Result<ContentBlockMetadata, GraceError>>

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

    /// Validates validate file version shape inputs before server processing continues.
    let private validateFileVersionShape (fileVersion: FileVersion) correlationId =
        if isNull (box fileVersion) then
            Error(error correlationId "Annotation target FileVersion is required.")
        elif fileVersion.IsBinary then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' is binary and cannot be materialized as text.")
        elif fileVersion.Size < 0L then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' has an invalid negative size.")
        elif fileVersion.Size > MaxMaterializedTextBytes then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' is too large to materialize as text. Size {fileVersion.Size} bytes exceeds the {MaxMaterializedTextBytes} byte limit."
            )
        elif isNull (box fileVersion.ContentReference) then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' has no ContentReference.")
        else
            Ok()

    /// Validates validate exact file bytes inputs before server processing continues.
    let private validateExactFileBytes (fileVersion: FileVersion) (bytes: byte array) correlationId =
        if isNull bytes then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' content reader returned null bytes.")
        elif int64 bytes.Length <> fileVersion.Size then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' materialized {bytes.Length} bytes, but FileVersion.Size is {fileVersion.Size} bytes."
            )
        elif
            not (String.IsNullOrWhiteSpace fileVersion.Sha256Hash)
            && not (String.Equals(sha256Hex bytes, fileVersion.Sha256Hash, StringComparison.OrdinalIgnoreCase))
        then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' materialized bytes do not match FileVersion.Sha256Hash.")
        elif
            not (String.IsNullOrWhiteSpace fileVersion.Blake3Hash)
            && not (String.Equals(blake3Hex bytes, fileVersion.Blake3Hash, StringComparison.OrdinalIgnoreCase))
        then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' materialized bytes do not match FileVersion.Blake3Hash.")
        else
            Ok(copyBytes bytes)

    /// Implements looks like gzip for the server request pipeline.
    let private looksLikeGzip (bytes: byte array) =
        not (isNull bytes)
        && bytes.Length >= 2
        && bytes[0] = 0x1fuy
        && bytes[1] = 0x8buy

    /// Implements decompress gzip whole file bytes for the server request pipeline.
    let private decompressGzipWholeFileBytes (fileVersion: FileVersion) (bytes: byte array) correlationId =
        if looksLikeGzip bytes then
            try
                use source = new MemoryStream(bytes, writable = false)
                use gzipStream = new GZipStream(source, CompressionMode.Decompress, leaveOpen = false)
                use decompressed = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 81920
                let mutable bytesRead = gzipStream.Read(buffer, 0, buffer.Length)

                while bytesRead > 0
                      && decompressed.Length <= fileVersion.Size do
                    decompressed.Write(buffer, 0, bytesRead)
                    bytesRead <- gzipStream.Read(buffer, 0, buffer.Length)

                Ok(decompressed.ToArray())
            with
            | :? InvalidDataException as ex ->
                Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' gzip content could not be decompressed: {ex.Message}")
        else
            Ok bytes

    /// Implements decode utf8 text for the server request pipeline.
    let private decodeUtf8Text (fileVersion: FileVersion) (bytes: byte array) correlationId =
        if int64 bytes.Length > MaxMaterializedTextBytes then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' decoded text payload is too large. Materialized {bytes.Length} bytes exceeds the {MaxMaterializedTextBytes} byte limit."
            )
        else
            try
                let strictUtf8 = UTF8Encoding(encoderShouldEmitUTF8Identifier = false, throwOnInvalidBytes = true)
                Ok(strictUtf8.GetString bytes)
            with
            | :? DecoderFallbackException as ex ->
                Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' is not valid UTF-8 text: {ex.Message}")

    /// Implements describe manifest validation error for the server request pipeline.
    let private describeManifestValidationError validationError = $"Invalid manifest reconstruction: {validationError}."

    /// Implements complete content block storage placement for the server request pipeline.
    let private completeContentBlockStoragePlacement (placement: ContentBlockStoragePlacement) =
        not (isNull (box placement))
        && not (String.IsNullOrWhiteSpace placement.StorageAccountName)
        && not (String.IsNullOrWhiteSpace placement.StorageContainerName)
        && not (String.IsNullOrWhiteSpace placement.ObjectKey)

    /// Validates validate manifest storage pool inputs before server processing continues.
    let private validateManifestStoragePool (fileVersion: FileVersion) (manifest: FileManifest) correlationId =
        if String.IsNullOrWhiteSpace manifest.StoragePoolId then
            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' FileManifest StoragePoolId is required.")
        else
            Ok()

    /// Validates validate manifest block metadata inputs before server processing continues.
    let private validateManifestBlockMetadata
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (contentBlockAddress: ContentBlockAddress)
        (metadata: ContentBlockMetadata)
        correlationId
        =
        if isNull (box metadata) then
            Error(
                error correlationId $"Annotation target '{fileVersion.RelativePath}' ContentBlockMetadata is absent for manifest block {contentBlockAddress}."
            )
        elif metadata.StoragePoolId <> manifest.StoragePoolId then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' ContentBlockMetadata StoragePoolId does not match FileManifest.StoragePoolId for {contentBlockAddress}."
            )
        elif metadata.ContentBlockAddress
             <> contentBlockAddress then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' ContentBlockMetadata ContentBlockAddress does not match FileManifest block {contentBlockAddress}."
            )
        elif metadata.BlockFormatVersion <= 0s then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' ContentBlockMetadata BlockFormatVersion is required for manifest block {contentBlockAddress}."
            )
        elif not (completeContentBlockStoragePlacement metadata.StoragePlacement) then
            Error(
                error
                    correlationId
                    $"Annotation target '{fileVersion.RelativePath}' ContentBlockMetadata StoragePlacement is incomplete for manifest block {contentBlockAddress}."
            )
        else
            Ok metadata.StoragePlacement

    /// Implements manifest payloads for the server request pipeline.
    let private manifestPayloads
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (resolveContentBlockMetadata: ContentBlockMetadataResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            let payloads = ResizeArray<ManifestValidation.ManifestBlockPayload>()

            let blockAddresses =
                if isNull (box manifest) || isNull manifest.Blocks then
                    Array.empty
                else
                    manifest.Blocks
                    |> Seq.map (fun block -> if isNull (box block) then String.Empty else block.Address)
                    |> Seq.distinct
                    |> Seq.toArray

            let mutable error = None
            let mutable index = 0

            while index < blockAddresses.Length
                  && Option.isNone error do
                let address = blockAddresses[index]

                match! resolveContentBlockMetadata manifest address correlationId cancellationToken with
                | Error metadataError -> error <- Some metadataError
                | Ok metadata ->
                    match validateManifestBlockMetadata fileVersion manifest address metadata correlationId with
                    | Error validationError -> error <- Some validationError
                    | Ok placement ->
                        match! readContentBlockPayload placement address correlationId cancellationToken with
                        | Ok payload -> payloads.Add(ManifestValidation.createBlockPayload address payload)
                        | Error readError -> error <- Some readError

                index <- index + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok(payloads.ToArray())
        }

    /// Implements materialize manifest bytes for the server request pipeline.
    let private materializeManifestBytes
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (resolveContentBlockMetadata: ContentBlockMetadataResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        correlationId
        cancellationToken
        =
        task {
            if isNull (box manifest) then
                return Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' FileManifest ContentReference is missing its manifest.")
            elif manifest.Size <> fileVersion.Size then
                return
                    Error(
                        error
                            correlationId
                            $"Annotation target '{fileVersion.RelativePath}' FileManifest.Size {manifest.Size} does not match FileVersion.Size {fileVersion.Size}."
                    )
            elif manifest.Size > MaxMaterializedTextBytes then
                return
                    Error(
                        error
                            correlationId
                            $"Annotation target '{fileVersion.RelativePath}' is too large to materialize as text. FileManifest.Size {manifest.Size} bytes exceeds the {MaxMaterializedTextBytes} byte limit."
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
                                Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' {describeManifestValidationError validationError}")
        }

    /// Implements unsupported manifest metadata resolver for the server request pipeline.
    let private unsupportedManifestMetadataResolver (manifest: FileManifest) contentBlockAddress correlationId _ =
        Task.FromResult(
            Error(
                error
                    correlationId
                    $"Annotation target FileManifest block {contentBlockAddress} cannot be materialized without finalized StoragePool placement evidence."
            )
        )

    /// Implements unsupported content block payload reader for the server request pipeline.
    let private unsupportedContentBlockPayloadReader placement contentBlockAddress correlationId _ =
        Task.FromResult(
            Error(error correlationId $"Annotation target FileManifest block {contentBlockAddress} cannot be read without a ContentBlock placement reader.")
        )

    /// Implements materialize whole file bytes for the server request pipeline.
    let private materializeWholeFileBytes (fileVersion: FileVersion) (readObjectPayload: ObjectPayloadReader) correlationId cancellationToken =
        task {
            let objectKey = StorageKeys.wholeFileContentObjectKey fileVersion

            match! readObjectPayload objectKey correlationId cancellationToken with
            | Ok bytes ->
                match decompressGzipWholeFileBytes fileVersion bytes correlationId with
                | Ok decompressedBytes -> return validateExactFileBytes fileVersion decompressedBytes correlationId
                | Error decompressionError -> return Error decompressionError
            | Error readError -> return Error readError
        }

    /// Implements materialize text with readers for the server request pipeline.
    let materializeTextWithReaders
        (readWholeFileObjectPayload: ObjectPayloadReader)
        (resolveContentBlockMetadata: ContentBlockMetadataResolver)
        (readContentBlockPayload: ContentBlockPlacementPayloadReader)
        (fileVersion: FileVersion)
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            match validateFileVersionShape fileVersion correlationId with
            | Error validationError -> return Error validationError
            | Ok () ->
                let! bytesResult =
                    match fileVersion.ContentReference.ReferenceType with
                    | FileContentReferenceType.WholeFileContent ->
                        materializeWholeFileBytes fileVersion readWholeFileObjectPayload correlationId cancellationToken
                    | FileContentReferenceType.FileManifest ->
                        match fileVersion.ContentReference.Manifest with
                        | Some manifest ->
                            materializeManifestBytes fileVersion manifest resolveContentBlockMetadata readContentBlockPayload correlationId cancellationToken
                        | None ->
                            Task.FromResult(
                                Error(
                                    error correlationId $"Annotation target '{fileVersion.RelativePath}' FileManifest ContentReference is missing its manifest."
                                )
                            )
                    | unsupported ->
                        Task.FromResult(
                            Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' has unsupported ContentReference type '{unsupported}'.")
                        )

                match bytesResult with
                | Error materializationError -> return Error materializationError
                | Ok bytes ->
                    match decodeUtf8Text fileVersion bytes correlationId with
                    | Error decodeError -> return Error decodeError
                    | Ok text -> return Ok { FileVersion = fileVersion; Bytes = bytes; Text = text }
        }

    /// Implements materialize text with object reader for the server request pipeline.
    let materializeTextWithObjectReader
        (readObjectPayload: ObjectPayloadReader)
        (fileVersion: FileVersion)
        correlationId
        (cancellationToken: CancellationToken)
        =
        materializeTextWithReaders
            readObjectPayload
            unsupportedManifestMetadataResolver
            unsupportedContentBlockPayloadReader
            fileVersion
            correlationId
            cancellationToken

    /// Implements azure object payload reader for the server request pipeline.
    let private azureObjectPayloadReader
        (repositoryDto: RepositoryDto)
        (maxPayloadBytes: int64)
        objectKey
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                try
                    let! blobClient = getAzureBlobClient repositoryDto objectKey correlationId
                    let! properties = blobClient.GetPropertiesAsync(cancellationToken = cancellationToken)

                    if properties.Value.ContentLength > maxPayloadBytes then
                        return
                            Error(
                                error
                                    correlationId
                                    $"Annotation content object '{objectKey}' is too large to read. Content length {properties.Value.ContentLength} bytes exceeds the {maxPayloadBytes} byte read limit."
                            )
                    else
                        let! download = blobClient.DownloadContentAsync(cancellationToken)
                        return Ok(download.Value.Content.ToArray())
                with
                | :? RequestFailedException as ex ->
                    return Error(error correlationId $"Annotation content object '{objectKey}' could not be read from object storage: {ex.Message}")
            | AWSS3 -> return Error(error correlationId "Annotation content materialization is not implemented for AWS S3 object storage.")
            | GoogleCloudStorage -> return Error(error correlationId "Annotation content materialization is not implemented for Google Cloud Storage.")
            | ObjectStorageProvider.Unknown ->
                return Error(error correlationId "Annotation content materialization cannot use an unknown object storage provider.")
        }

    /// Implements azure content block placement payload reader for the server request pipeline.
    let private azureContentBlockPlacementPayloadReader
        maxPayloadBytes
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
                    let! properties = blobClient.GetPropertiesAsync(cancellationToken = cancellationToken)

                    if properties.Value.ContentLength > maxPayloadBytes then
                        return
                            Error(
                                error
                                    correlationId
                                    $"Annotation ContentBlock payload '{contentBlockAddress}' is too large to read. Content length {properties.Value.ContentLength} bytes exceeds the {maxPayloadBytes} byte read limit."
                            )
                    else
                        let! download = blobClient.DownloadContentAsync(cancellationToken)
                        return Ok(download.Value.Content.ToArray())
            with
            | :? RequestFailedException as ex ->
                return
                    Error(
                        error correlationId $"Annotation ContentBlock payload '{contentBlockAddress}' could not be read from stored CAS placement: {ex.Message}"
                    )
        }

    /// Implements finalized manifest metadata resolver for the server request pipeline.
    let private finalizedManifestMetadataResolver
        (repositoryDto: RepositoryDto)
        (fileVersion: FileVersion)
        (authorizedScope: string)
        (manifest: FileManifest)
        contentBlockAddress
        correlationId
        _
        =
        task {
            let dedupeIndexActor = Grace.Actors.DedupeIndexActor.CreateActorProxy correlationId

            let! metadata =
                dedupeIndexActor.TryGetFinalizedScopedContentBlockMetadata(
                    manifest.StoragePoolId,
                    repositoryDto.RepositoryId,
                    authorizedScope,
                    manifest.ManifestAddress,
                    contentBlockAddress,
                    correlationId
                )

            match metadata with
            | Some metadata -> return Ok metadata
            | None ->
                return
                    Error(
                        error
                            correlationId
                            $"Annotation target '{fileVersion.RelativePath}' has no finalized StoragePool placement evidence for FileManifest block {contentBlockAddress}."
                    )
        }

    /// Implements materialize target text for the server request pipeline.
    let materializeTargetText (repositoryDto: RepositoryDto) authorizedScope (fileVersion: FileVersion) correlationId cancellationToken =
        /// Implements whole file reader for the server request pipeline.
        let wholeFileReader (objectKey: string) correlationId cancellationToken =
            azureObjectPayloadReader repositoryDto MaxContentBlockPayloadBytes objectKey correlationId cancellationToken

        /// Computes metadata resolver data used by Grace Server.
        let metadataResolver manifest contentBlockAddress correlationId cancellationToken =
            finalizedManifestMetadataResolver repositoryDto fileVersion authorizedScope manifest contentBlockAddress correlationId cancellationToken

        /// Implements content block reader for the server request pipeline.
        let contentBlockReader placement contentBlockAddress correlationId cancellationToken =
            azureContentBlockPlacementPayloadReader MaxContentBlockPayloadBytes placement contentBlockAddress correlationId cancellationToken

        materializeTextWithReaders wholeFileReader metadataResolver contentBlockReader fileVersion correlationId cancellationToken
