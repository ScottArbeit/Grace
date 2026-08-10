namespace Grace.Server

open Azure
open Azure.Storage.Blobs.Models
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.TextContent
open Microsoft.Extensions.Configuration
open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks

/// Provides immutable, verified repository-scoped text storage for work-item descriptions.
module TextContentStorage =
    let private strictUtf8 = UTF8Encoding(false, true)

    /// Parses the configured Unicode scalar limit, using the Product V1 default when no value is supplied.
    let parseMaximumCharacters configured =
        if String.IsNullOrWhiteSpace(configured) then
            Ok 65_536
        else
            match Int32.TryParse(configured.Trim()) with
            | true, value when value > 0 -> Ok value
            | _ -> Error $"Configuration '{EnvironmentVariables.GraceTextContentMaxCharacters}' must be a positive integer."

    /// Resolves the configured Unicode scalar limit and rejects malformed configuration before content processing.
    let getMaximumCharacters () =
        let configuration = ApplicationContext.Configuration()

        let configured =
            if isNull configuration then
                null
            else
                configuration[getConfigKey EnvironmentVariables.GraceTextContentMaxCharacters]

        parseMaximumCharacters configured

    /// Counts Unicode scalar values so supplementary-plane characters consume one configured character.
    let countUnicodeScalars (text: string) = text.EnumerateRunes() |> Seq.length

    /// Validates text against a resolved Unicode scalar limit before any storage operation begins.
    let validateTextForMaximum maximum (text: string) =
        if String.IsNullOrWhiteSpace(text) then
            Error "Description text is required."
        elif countUnicodeScalars text > maximum then
            Error $"Description text exceeds the configured limit of {maximum} Unicode scalar values."
        else
            Ok()

    /// Rejects empty or oversized text before immutable object storage is touched.
    let validateText (text: string) =
        match getMaximumCharacters () with
        | Error error -> Error error
        | Ok maximum -> validateTextForMaximum maximum text

    /// Derives a stable opaque identifier from the exact actor replay identity and purpose without using content hashes.
    let private deterministicId (repositoryId: RepositoryId) (workItemId: WorkItemId) (correlationId: CorrelationId) (purpose: string) =
        let seed = $"{repositoryId:N}|{workItemId:N}|{correlationId}|{purpose}"
        let bytes = Encoding.UTF8.GetBytes(seed)
        let hash = System.Security.Cryptography.SHA256.HashData(bytes)
        Guid(hash[0..15])

    /// Creates stable description and text identities for retries of one supported operation.
    let createIds repositoryId workItemId correlationId =
        deterministicId repositoryId workItemId correlationId "description", deterministicId repositoryId workItemId correlationId "text-content"

    /// Builds the immutable description reference that one create or set operation must persist before it can be replayed.
    let createDescription repositoryId workItemId correlationId (text: string) =
        let descriptionId, textContentId = createIds repositoryId workItemId correlationId
        let bytes = strictUtf8.GetBytes(text)

        {
            DescriptionId = descriptionId
            TextContent = Some { TextContentId = textContentId; Blake3Hash = ContentAddress.computeBlake3Hex bytes; Utf8ByteLength = int64 bytes.LongLength }
        }

    /// Compresses unchanged UTF-8 bytes into the immutable object representation.
    let private gzip (bytes: byte array) =
        use output = new MemoryStream()
        use gzipStream = new GZipStream(output, CompressionLevel.SmallestSize, true)
        gzipStream.Write(bytes, 0, bytes.Length)
        gzipStream.Flush()
        gzipStream.Dispose()
        output.ToArray()

    /// Compresses unchanged strict UTF-8 text bytes for the immutable repository object representation.
    let compressText (text: string) = text |> strictUtf8.GetBytes |> gzip

    /// Decompresses and verifies immutable text bytes before exposing their strict UTF-8 text.
    let verifyCompressedText maximum (reference: TextContent) (compressed: Stream) =
        try
            let maximumBytes = int64 maximum * 4L

            if reference.Utf8ByteLength > maximumBytes then
                Error "Text content length verification failed."
            else
                use gzipStream = new GZipStream(compressed, CompressionMode.Decompress, true)
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 8192
                let mutable total = 0L
                let mutable readCount = gzipStream.Read(buffer, 0, buffer.Length)

                while readCount > 0 && total <= reference.Utf8ByteLength do
                    total <- total + int64 readCount

                    if total <= reference.Utf8ByteLength then output.Write(buffer, 0, readCount)

                    readCount <- gzipStream.Read(buffer, 0, buffer.Length)

                if total <> reference.Utf8ByteLength then
                    Error "Text content length verification failed."
                else
                    let bytes = output.ToArray()
                    let actualHash = ContentAddress.computeBlake3Hex bytes

                    if not (String.Equals(actualHash, reference.Blake3Hash, StringComparison.Ordinal)) then
                        Error "Text content hash verification failed."
                    else
                        let text = strictUtf8.GetString(bytes)

                        if countUnicodeScalars text > maximum then
                            Error "Text content exceeds the configured character limit."
                        else
                            Ok text
        with
        | :? InvalidDataException as ex -> Error $"Text content GZip validation failed: {ex.Message}"
        | :? DecoderFallbackException as ex -> Error $"Text content UTF-8 validation failed: {ex.Message}"
        | ex -> Error $"Text content verification failed: {ex.Message}"

    /// Downloads, bounds, decompresses, decodes, and verifies one immutable text object before it is returned.
    let read (repositoryDto: RepositoryDto) (reference: TextContent) (correlationId: CorrelationId) =
        task {
            try
                match repositoryDto.ObjectStorageProvider, getMaximumCharacters () with
                | ObjectStorageProvider.AzureBlobStorage, Error error -> return Error(GraceError.Create error correlationId)
                | ObjectStorageProvider.AzureBlobStorage, Ok maximum ->
                    let! containerClient = getContainerClient repositoryDto correlationId
                    let blobClient = containerClient.GetBlobClient(StorageKeys.textContentObjectKey reference.TextContentId)
                    let! download = blobClient.DownloadStreamingAsync()
                    use compressed = download.Value.Content

                    match verifyCompressedText maximum reference compressed with
                    | Ok text -> return Ok text
                    | Error error -> return Error(GraceError.Create error correlationId)
                | _ -> return Error(GraceError.Create "Text content storage is only implemented for Azure Blob Storage." correlationId)
            with
            | :? RequestFailedException as ex -> return Error(GraceError.Create $"Text content could not be read: {ex.Message}" correlationId)
            | :? InvalidDataException as ex -> return Error(GraceError.Create $"Text content GZip validation failed: {ex.Message}" correlationId)
            | :? DecoderFallbackException as ex -> return Error(GraceError.Create $"Text content UTF-8 validation failed: {ex.Message}" correlationId)
            | ex -> return Error(GraceError.Create $"Text content verification failed: {ex.Message}" correlationId)
        }

    /// Writes one immutable compressed text object or proves that the retry object already contains the exact same bytes.
    let write repositoryDto repositoryId workItemId correlationId text =
        task {
            match validateText text with
            | Error error -> return Error(GraceError.Create error correlationId)
            | Ok () when
                repositoryDto.ObjectStorageProvider
                <> ObjectStorageProvider.AzureBlobStorage
                ->
                return Error(GraceError.Create "Text content storage is only implemented for Azure Blob Storage." correlationId)
            | Ok () ->
                try
                    let description = createDescription repositoryId workItemId correlationId text
                    let reference = description.TextContent.Value

                    let! containerClient = getContainerClient repositoryDto correlationId
                    let blobClient = containerClient.GetBlobClient(StorageKeys.textContentObjectKey reference.TextContentId)
                    let compressed = compressText text
                    use content = new MemoryStream(compressed)
                    let conditions = BlobRequestConditions(IfNoneMatch = Azure.ETag.All)
                    let options = BlobUploadOptions(Conditions = conditions)

                    try
                        let! _ = blobClient.UploadAsync(content, options)
                        return Ok(description, true)
                    with
                    | :? RequestFailedException as ex when ex.Status = 409 || ex.Status = 412 ->
                        match! read repositoryDto reference correlationId with
                        | Ok existing when String.Equals(existing, text, StringComparison.Ordinal) -> return Ok(description, false)
                        | Ok _ -> return Error(GraceError.Create "Text-content retry identity already contains different content." correlationId)
                        | Error error -> return Error error
                with
                | ex -> return Error(GraceError.Create $"Text content could not be written: {ex.Message}" correlationId)
        }

    /// Removes a newly-created object only after a known downstream rejection; uncertain calls retain retry evidence.
    let deleteIfNewlyCreated repositoryDto (reference: TextContent) correlationId =
        task {
            try
                let! containerClient = getContainerClient repositoryDto correlationId
                let blobClient = containerClient.GetBlobClient(StorageKeys.textContentObjectKey reference.TextContentId)
                let! _ = blobClient.DeleteIfExistsAsync()
                return Ok()
            with
            | ex -> return Error(GraceError.Create $"Text content cleanup failed: {ex.Message}" correlationId)
        }
