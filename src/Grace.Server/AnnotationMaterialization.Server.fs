namespace Grace.Server

open Azure
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Repository
open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module internal AnnotationMaterialization =

    [<Literal>]
    let MaxMaterializedTextBytes = 10L * 1024L * 1024L

    [<Literal>]
    let private MaxContentBlockPayloadBytes = MaxMaterializedTextBytes + 1024L * 1024L

    type MaterializedTextContent = { FileVersion: FileVersion; Bytes: byte array; Text: string }

    type ObjectPayloadReader = string -> CorrelationId -> CancellationToken -> Task<Result<byte array, GraceError>>

    let private error correlationId message = GraceError.Create message correlationId

    let private copyBytes (bytes: byte array) =
        let copy = Array.zeroCreate<byte> bytes.Length
        Array.Copy(bytes, copy, bytes.Length)
        copy

    let private sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private blake3Hex (bytes: byte array) = ContentAddress.computeBlake3Hex bytes

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

    let private looksLikeGzip (bytes: byte array) =
        not (isNull bytes)
        && bytes.Length >= 2
        && bytes[0] = 0x1fuy
        && bytes[1] = 0x8buy

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

    let private describeManifestValidationError validationError = $"Invalid manifest reconstruction: {validationError}."

    let private manifestPayloads (manifest: FileManifest) (readObjectPayload: ObjectPayloadReader) correlationId (cancellationToken: CancellationToken) =
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
                let objectKey = StorageKeys.contentBlockObjectKey address

                match! readObjectPayload objectKey correlationId cancellationToken with
                | Ok payload -> payloads.Add(ManifestValidation.createBlockPayload address payload)
                | Error readError -> error <- Some readError

                index <- index + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok(payloads.ToArray())
        }

    let private materializeManifestBytes
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        (readObjectPayload: ObjectPayloadReader)
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
                match! manifestPayloads manifest readObjectPayload correlationId cancellationToken with
                | Error readError -> return Error readError
                | Ok payloads ->
                    match ManifestValidation.validate RabinChunking.SuiteName manifest payloads with
                    | Ok bytes -> return validateExactFileBytes fileVersion bytes correlationId
                    | Error validationError ->
                        return Error(error correlationId $"Annotation target '{fileVersion.RelativePath}' {describeManifestValidationError validationError}")
        }

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

    let materializeTextWithObjectReader
        (readObjectPayload: ObjectPayloadReader)
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
                    | FileContentReferenceType.WholeFileContent -> materializeWholeFileBytes fileVersion readObjectPayload correlationId cancellationToken
                    | FileContentReferenceType.FileManifest ->
                        match fileVersion.ContentReference.Manifest with
                        | Some manifest -> materializeManifestBytes fileVersion manifest readObjectPayload correlationId cancellationToken
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

    let materializeTargetText (repositoryDto: RepositoryDto) (fileVersion: FileVersion) correlationId cancellationToken =
        let reader (objectKey: string) correlationId cancellationToken =
            azureObjectPayloadReader repositoryDto MaxContentBlockPayloadBytes objectKey correlationId cancellationToken

        materializeTextWithObjectReader reader fileVersion correlationId cancellationToken
