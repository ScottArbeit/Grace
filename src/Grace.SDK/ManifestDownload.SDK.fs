namespace Grace.SDK

open Blake3
open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open System
open System.IO
open System.Threading.Tasks

module ManifestDownload =
    type ManifestDownloadRequest =
        {
            OwnerId: string
            OwnerName: OwnerName
            OrganizationId: string
            OrganizationName: OrganizationName
            RepositoryId: string
            RepositoryName: RepositoryName
            FileVersion: FileVersion
            OutputStream: Stream option
            CorrelationId: CorrelationId
            ExpectedChunkingSuiteId: ChunkingSuiteId
        }

    type ManifestDownloadResult =
        {
            FileVersion: FileVersion
            Manifest: FileManifest option
            BytesWritten: int64
            DownloadedBlockCount: int
            UsedManifestDownload: bool
        }

    type ManifestDownloadClient =
        {
            GetContentBlockDownloadUri: GetContentBlockDownloadUriParameters -> Task<GraceResult<string>>
            DownloadContentBlock: ContentBlockAddress -> Uri -> Task<GraceResult<byte array>>
        }

    let private setStorageParameters (request: ManifestDownloadRequest) (parameters: StorageParameters) =
        parameters.OwnerId <- request.OwnerId
        parameters.OwnerName <- request.OwnerName
        parameters.OrganizationId <- request.OrganizationId
        parameters.OrganizationName <- request.OrganizationName
        parameters.RepositoryId <- request.RepositoryId
        parameters.RepositoryName <- request.RepositoryName
        parameters.CorrelationId <- request.CorrelationId
        parameters

    let private error correlationId message = Error(GraceError.Create message correlationId)

    let private emptyResult fileVersion =
        { FileVersion = fileVersion; Manifest = None; BytesWritten = 0L; DownloadedBlockCount = 0; UsedManifestDownload = false }

    let private buildDownloadUriParameters request storagePoolId contentBlockAddress =
        let parameters = GetContentBlockDownloadUriParameters()
        setStorageParameters request parameters |> ignore
        parameters.ContentBlockAddress <- contentBlockAddress
        parameters.StoragePoolId <- storagePoolId
        parameters.Manifest <- request.FileVersion.ContentReference.Manifest.Value
        parameters

    let private describeValidationError validationError =
        match validationError with
        | ManifestValidation.ContentBlockPayloadInvalid (_, contentBlockError) -> $"ContentBlock payload is invalid: {contentBlockError}"
        | ManifestValidation.MissingContentBlockPayload (index, address) -> $"Manifest block {index} is missing ContentBlock payload {address}."
        | ManifestValidation.ContentBlockPayloadSizeMismatch (index, expected, actual) ->
            $"Manifest block {index} size mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.FileContentHashMismatch (expected, actual) -> $"File content hash mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.ManifestSizeMismatch (expected, actual) -> $"Manifest size mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.ChunkingSuiteMismatch (expected, actual) -> $"Chunking suite mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.ManifestAddressMismatch (expected, actual) -> $"Manifest address mismatch. Expected {expected}, actual {actual}."
        | other -> $"{other}"

    let private validateManifestShape expectedChunkingSuiteId (manifest: FileManifest) =
        if isNull (box manifest) then
            Error ManifestValidation.NullManifest
        elif String.IsNullOrWhiteSpace(expectedChunkingSuiteId) then
            Error(ManifestValidation.InvalidChunkingSuiteId expectedChunkingSuiteId)
        elif manifest.Size <= 0L then
            Error ManifestValidation.InvalidManifestSize
        elif isNull manifest.Blocks
             || manifest.Blocks.Count = 0 then
            Error ManifestValidation.EmptyManifest
        elif String.IsNullOrWhiteSpace(manifest.ChunkingSuiteId) then
            Error(ManifestValidation.InvalidChunkingSuiteId manifest.ChunkingSuiteId)
        elif not (ContentAddress.isValidAddress manifest.FileContentHash) then
            Error(ManifestValidation.InvalidFileContentHash manifest.FileContentHash)
        elif not (ContentAddress.isValidAddress manifest.ManifestAddress) then
            Error(ManifestValidation.InvalidManifestAddress manifest.ManifestAddress)
        elif manifest.ChunkingSuiteId
             <> expectedChunkingSuiteId then
            Error(ManifestValidation.ChunkingSuiteMismatch(expectedChunkingSuiteId, manifest.ChunkingSuiteId))
        else
            Ok()

    let private validateManifestRanges (manifest: FileManifest) =
        let mutable expectedOffset = 0L
        let mutable validationError = None
        let mutable index = 0

        while index < manifest.Blocks.Count
              && Option.isNone validationError do
            let block = manifest.Blocks[index]

            if isNull (box block) then
                validationError <- Some(ManifestValidation.NullContentBlock index)
            elif not (ContentAddress.isValidAddress block.Address) then
                validationError <- Some(ManifestValidation.InvalidContentBlockAddress(index, block.Address))
            elif block.Size <= 0L then
                validationError <- Some(ManifestValidation.BlockRangeNotPositive index)
            elif block.Offset <> expectedOffset then
                validationError <- Some(ManifestValidation.BlockRangeOutOfOrder index)
            else
                expectedOffset <- expectedOffset + block.Size

            index <- index + 1

        match validationError with
        | Some validationError -> Error validationError
        | None when expectedOffset <> manifest.Size -> Error(ManifestValidation.ManifestSizeMismatch(manifest.Size, expectedOffset))
        | None -> Ok()

    let private validateManifestAddress (manifest: FileManifest) =
        let expectedAddress = ContentAddress.computeManifestAddressForManifest manifest

        if manifest.ManifestAddress <> expectedAddress then
            Error(ManifestValidation.ManifestAddressMismatch(expectedAddress, manifest.ManifestAddress))
        else
            Ok()

    let private validateManifest expectedChunkingSuiteId manifest =
        validateManifestShape expectedChunkingSuiteId manifest
        |> Result.bind (fun () -> validateManifestRanges manifest)
        |> Result.bind (fun () -> validateManifestAddress manifest)

    let private writeManifestToStream (client: ManifestDownloadClient) (request: ManifestDownloadRequest) (manifest: FileManifest) (outputStream: Stream) =
        task {
            use hasher = Hasher.New()
            let mutable errorResult = None
            let mutable index = 0
            let mutable bytesWritten = 0L

            while index < manifest.Blocks.Count
                  && Option.isNone errorResult do
                let block = manifest.Blocks[index]
                let parameters = buildDownloadUriParameters request manifest.StoragePoolId block.Address

                match! client.GetContentBlockDownloadUri parameters with
                | Error error -> errorResult <- Some error
                | Ok uriResult ->
                    match Uri.TryCreate(uriResult.ReturnValue, UriKind.Absolute) with
                    | false, _ ->
                        errorResult <- Some(GraceError.Create $"Failed to get a valid ContentBlock download URI for {block.Address}." request.CorrelationId)
                    | true, downloadUri ->
                        match! client.DownloadContentBlock block.Address downloadUri with
                        | Error error -> errorResult <- Some error
                        | Ok payloadResult ->
                            match ContentBlockFormat.decode payloadResult.ReturnValue with
                            | Error decodeError ->
                                errorResult <-
                                    Some(
                                        GraceError.Create
                                            $"Manifest download reconstruction failed: {describeValidationError (ManifestValidation.ContentBlockPayloadInvalid(block.Address, decodeError))}"
                                            request.CorrelationId
                                    )
                            | Ok decodedBlock ->
                                match ContentBlockFormat.validateAddress block.Address decodedBlock with
                                | Error addressError ->
                                    errorResult <-
                                        Some(
                                            GraceError.Create
                                                $"Manifest download reconstruction failed: {describeValidationError (ManifestValidation.ContentBlockPayloadInvalid(block.Address, addressError))}"
                                                request.CorrelationId
                                        )
                                | Ok () ->
                                    let actualSize = int64 decodedBlock.Payload.Length

                                    if actualSize <> block.Size then
                                        errorResult <-
                                            Some(
                                                GraceError.Create
                                                    $"Manifest download reconstruction failed: {describeValidationError (ManifestValidation.ContentBlockPayloadSizeMismatch(index, block.Size, actualSize))}"
                                                    request.CorrelationId
                                            )
                                    else
                                        try
                                            do! outputStream.WriteAsync(decodedBlock.Payload, 0, decodedBlock.Payload.Length)
                                            hasher.Update(decodedBlock.Payload)
                                            bytesWritten <- bytesWritten + actualSize
                                        with
                                        | ex ->
                                            errorResult <-
                                                Some(
                                                    GraceError.Create
                                                        $"Failed writing manifest-backed file to output stream: {ExceptionResponse.Create ex}"
                                                        request.CorrelationId
                                                )

                index <- index + 1

            match errorResult with
            | Some error -> return Error error
            | None when bytesWritten <> manifest.Size ->
                return
                    error
                        request.CorrelationId
                        $"Manifest download reconstruction failed: {describeValidationError (ManifestValidation.ManifestSizeMismatch(manifest.Size, bytesWritten))}"
            | None ->
                let actualHash = FileContentHash(hasher.Finalize().ToString().ToLowerInvariant())

                if manifest.FileContentHash <> actualHash then
                    return
                        error
                            request.CorrelationId
                            $"Manifest download reconstruction failed: {describeValidationError (ManifestValidation.FileContentHashMismatch(manifest.FileContentHash, actualHash))}"
                else
                    return
                        Ok(
                            GraceReturnValue.Create
                                {
                                    FileVersion = request.FileVersion
                                    Manifest = Some manifest
                                    BytesWritten = bytesWritten
                                    DownloadedBlockCount = manifest.Blocks.Count
                                    UsedManifestDownload = true
                                }
                                request.CorrelationId
                        )
        }

    let downloadFileWithClient (client: ManifestDownloadClient) (request: ManifestDownloadRequest) : Task<GraceResult<ManifestDownloadResult>> =
        if isNull (box request.FileVersion) then
            Task.FromResult(error request.CorrelationId "FileVersion is required for manifest download.")
        elif
            isNull (box request.FileVersion.ContentReference)
            || request.FileVersion.ContentReference.ReferenceType = FileContentReferenceType.WholeFileContent
        then
            Task.FromResult(Ok(GraceReturnValue.Create (emptyResult request.FileVersion) request.CorrelationId))
        else
            match request.FileVersion.ContentReference.Manifest with
            | None -> Task.FromResult(error request.CorrelationId "FileVersion ContentReference did not include a FileManifest.")
            | Some manifest ->
                match request.OutputStream with
                | None -> Task.FromResult(error request.CorrelationId "OutputStream is required for manifest download.")
                | Some outputStream ->
                    match validateManifest request.ExpectedChunkingSuiteId manifest with
                    | Error validationError ->
                        Task.FromResult(error request.CorrelationId $"Manifest download reconstruction failed: {describeValidationError validationError}")
                    | Ok () -> writeManifestToStream client request manifest outputStream

    let serverClient correlationId =
        {
            GetContentBlockDownloadUri = Storage.GetContentBlockDownloadUri
            DownloadContentBlock = fun contentBlockAddress uri -> Storage.DownloadContentBlockFromObjectStorage contentBlockAddress uri correlationId
        }

    let downloadFile request = downloadFileWithClient (serverClient request.CorrelationId) request
