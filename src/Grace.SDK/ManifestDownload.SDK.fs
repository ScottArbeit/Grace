namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open System
open System.Collections.Generic
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
            CorrelationId: CorrelationId
            ExpectedChunkingSuiteId: ChunkingSuiteId
        }

    type ManifestDownloadResult =
        {
            FileVersion: FileVersion
            Manifest: FileManifest option
            Bytes: byte array
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
        { FileVersion = fileVersion; Manifest = None; Bytes = Array.empty; DownloadedBlockCount = 0; UsedManifestDownload = false }

    let private buildDownloadUriParameters request contentBlockAddress =
        let parameters = GetContentBlockDownloadUriParameters()
        setStorageParameters request parameters |> ignore
        parameters.ContentBlockAddress <- contentBlockAddress
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

    let private uniqueManifestBlocks (manifest: FileManifest) =
        let addresses = ResizeArray<ContentBlockAddress>()
        let seen = HashSet<ContentBlockAddress>()
        let mutable index = 0

        while index < manifest.Blocks.Count do
            let block = manifest.Blocks[index]

            if
                not (isNull (box block))
                && seen.Add(block.Address)
            then
                addresses.Add(block.Address)

            index <- index + 1

        addresses.ToArray()

    let downloadFileWithClient (client: ManifestDownloadClient) (request: ManifestDownloadRequest) : Task<GraceResult<ManifestDownloadResult>> =
        task {
            if isNull (box request.FileVersion) then
                return error request.CorrelationId "FileVersion is required for manifest download."
            elif
                isNull (box request.FileVersion.ContentReference)
                || request.FileVersion.ContentReference.ReferenceType = FileContentReferenceType.WholeFileContent
            then
                return Ok(GraceReturnValue.Create (emptyResult request.FileVersion) request.CorrelationId)
            else
                match request.FileVersion.ContentReference.Manifest with
                | None -> return error request.CorrelationId "FileVersion ContentReference did not include a FileManifest."
                | Some manifest ->
                    let blockPayloads = ResizeArray<ManifestValidation.ManifestBlockPayload>()
                    let blockAddresses = uniqueManifestBlocks manifest
                    let mutable errorResult = None
                    let mutable index = 0

                    while index < blockAddresses.Length
                          && Option.isNone errorResult do
                        let contentBlockAddress = blockAddresses[index]
                        let parameters = buildDownloadUriParameters request contentBlockAddress

                        match! client.GetContentBlockDownloadUri parameters with
                        | Error error -> errorResult <- Some error
                        | Ok uriResult ->
                            match Uri.TryCreate(uriResult.ReturnValue, UriKind.Absolute) with
                            | false, _ ->
                                errorResult <-
                                    Some(GraceError.Create $"Failed to get a valid ContentBlock download URI for {contentBlockAddress}." request.CorrelationId)
                            | true, downloadUri ->
                                match! client.DownloadContentBlock contentBlockAddress downloadUri with
                                | Error error -> errorResult <- Some error
                                | Ok payloadResult -> blockPayloads.Add(ManifestValidation.createBlockPayload contentBlockAddress payloadResult.ReturnValue)

                        index <- index + 1

                    match errorResult with
                    | Some error -> return Error error
                    | None ->
                        match ManifestValidation.validate request.ExpectedChunkingSuiteId manifest blockPayloads with
                        | Error validationError ->
                            return error request.CorrelationId $"Manifest download reconstruction failed: {describeValidationError validationError}"
                        | Ok reconstructedBytes ->
                            return
                                Ok(
                                    GraceReturnValue.Create
                                        {
                                            FileVersion = request.FileVersion
                                            Manifest = Some manifest
                                            Bytes = reconstructedBytes
                                            DownloadedBlockCount = blockPayloads.Count
                                            UsedManifestDownload = true
                                        }
                                        request.CorrelationId
                                )
        }

    let serverClient correlationId =
        {
            GetContentBlockDownloadUri = Storage.GetContentBlockDownloadUri
            DownloadContentBlock = fun contentBlockAddress uri -> Storage.DownloadContentBlockFromObjectStorage contentBlockAddress uri correlationId
        }

    let downloadFile request = downloadFileWithClient (serverClient request.CorrelationId) request
