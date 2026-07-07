namespace Grace.Server.Tests

open Azure
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text

/// Groups shared helpers for directory version server test helpers.
module DirectoryVersionServerTestHelpers =
    /// Captures directory version model values used by the test suite.
    type DirectoryVersionModel = Grace.Types.Common.DirectoryVersion

    /// Computes the canonical SHA-256 hash for bytes used by test FileVersions.
    let computeSha256Hash (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Computes the canonical BLAKE3 hash for bytes used by test FileVersions.
    let computeBlake3Hash (bytes: byte array) = ContentAddress.computeBlake3Hex bytes

    /// Builds deterministic byte payloads for directory projection storage tests.
    let deterministicBytes (label: string) (length: int) =
        let seed = Encoding.UTF8.GetBytes(label)

        [|
            for index in 0 .. length - 1 do
                byte ((int seed[index % seed.Length] + index * 17) % 251)
        |]

    /// Normalizes d directory size for hash for stable assertions.
    let normalizedDirectorySizeForHash (directoryVersion: DirectoryVersionModel) =
        if directoryVersion.Size = Constants.InitialDirectorySize then
            0L
        else
            directoryVersion.Size

    /// Normalizes file paths for stable directory preimage assertions.
    let normalizedFilePath (relativePath: RelativePath) = RelativePath(normalizeFilePath $"{relativePath}")

    /// Builds a binary FileVersion with whole-file content reference.
    let createWholeFileVersion (relativePath: RelativePath) (payload: byte array) =
        FileVersion.CreateWithHashes
            (normalizedFilePath relativePath)
            (Sha256Hash(computeSha256Hash payload))
            (Blake3Hash(computeBlake3Hash payload))
            String.Empty
            true
            (int64 payload.Length)

    let createDirectoryVersion
        (directoryVersionId: DirectoryVersionId)
        (repositoryId: string)
        (relativePath: RelativePath)
        (childDirectoryVersions: DirectoryVersionModel seq)
        : DirectoryVersionModel
        =
        let childDirectoryVersions = childDirectoryVersions |> Seq.toArray

        let childDirectoryIds =
            childDirectoryVersions
            |> Seq.map (fun directoryVersion -> directoryVersion.DirectoryVersionId)

        let entries =
            childDirectoryVersions
            |> Seq.map (fun directoryVersion ->
                DirectoryVersionPreimageEntry.Directory
                    directoryVersion.RelativePath
                    (normalizedDirectorySizeForHash directoryVersion)
                    directoryVersion.Blake3Hash
                    directoryVersion.Sha256Hash)
            |> Seq.toArray

        let sha256Hash = computeSha256ForDirectoryEntries relativePath entries
        let blake3Hash = computeBlake3ForDirectory relativePath entries

        DirectoryVersionModel.CreateWithHashes
            directoryVersionId
            (Guid.Parse ownerId)
            (Guid.Parse organizationId)
            (Guid.Parse repositoryId)
            relativePath
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>(childDirectoryIds))
            (List<FileVersion>())
            Constants.InitialDirectorySize

    let createDirectoryVersionWithFiles
        (directoryVersionId: DirectoryVersionId)
        (repositoryId: string)
        (relativePath: RelativePath)
        (files: FileVersion seq)
        : DirectoryVersionModel
        =
        let fileVersions = files |> Seq.toArray

        let entries =
            fileVersions
            |> Seq.map (fun fileVersion ->
                DirectoryVersionPreimageEntry.File fileVersion.RelativePath fileVersion.Size fileVersion.Blake3Hash fileVersion.Sha256Hash)
            |> Seq.toArray

        let sha256Hash = computeSha256ForDirectoryEntries relativePath entries
        let blake3Hash = computeBlake3ForDirectory relativePath entries

        DirectoryVersionModel.CreateWithHashes
            directoryVersionId
            (Guid.Parse ownerId)
            (Guid.Parse organizationId)
            (Guid.Parse repositoryId)
            relativePath
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>())
            (List<FileVersion>(fileVersions))
            (fileVersions
             |> Seq.sumBy (fun fileVersion -> fileVersion.Size))

    /// Builds create parameters for route calls.
    let createParameters (directoryVersion: DirectoryVersionModel) =
        let parameters = Parameters.DirectoryVersion.CreateParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{directoryVersion.RepositoryId}"
        parameters.DirectoryVersionId <- $"{directoryVersion.DirectoryVersionId}"
        parameters.DirectoryVersion <- directoryVersion
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds get parameters for route calls.
    let getParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        let parameters = Parameters.DirectoryVersion.GetParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds get by SHA256 hash parameters for route calls.
    let getBySha256HashParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) (sha256Hash: Sha256Hash) =
        let parameters = Parameters.DirectoryVersion.GetBySha256HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.Sha256Hash <- sha256Hash
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds get by BLAKE3 hash parameters for route calls.
    let getByBlake3HashParameters (repositoryId: string) (blake3Hash: Blake3Hash) =
        let parameters = Parameters.DirectoryVersion.GetByBlake3HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Blake3Hash <- blake3Hash
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds save parameters for route calls.
    let saveParameters (repositoryId: string) (directoryVersions: DirectoryVersionModel seq) =
        let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()

        for directoryVersion in directoryVersions do
            parameters.DirectoryVersions.Add(directoryVersion)

        parameters

    /// Builds storage parameters for route calls used by manifest-backed DirectoryVersion tests.
    let setStorageParameters (parameters: Parameters.Storage.StorageParameters) (repositoryId: string) correlationId =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- correlationId

    /// Asserts OK for helper route calls declared before the shared module assertOk helper.
    let assertProjectionHelperOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
        }

    /// Uploads a binary whole-file object and returns the server-normalized FileVersion.
    let uploadWholeFileAsync (repositoryId: string) (fileVersion: FileVersion) (payload: byte array) =
        task {
            let parameters = Parameters.Storage.GetUploadMetadataForFilesParameters()
            setStorageParameters parameters repositoryId (generateCorrelationId ())
            parameters.FileVersions <- [| fileVersion |]

            let! uploadResponse = Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent parameters)
            do! assertProjectionHelperOk uploadResponse
            let! uploadMetadata = deserializeContent<GraceReturnValue<List<Parameters.Storage.UploadMetadata>>> uploadResponse
            let metadata = uploadMetadata.ReturnValue |> Seq.exactlyOne

            use payloadStream = new MemoryStream(payload, writable = false)
            let blockBlobClient = BlockBlobClient(metadata.BlobUriWithSasToken)
            let! response = blockBlobClient.UploadAsync(payloadStream, BlobUploadOptions())
            Assert.That(response.GetRawResponse().Status, Is.EqualTo(int HttpStatusCode.Created))

            fileVersion.ContentReference <- metadata.ContentReference
            return fileVersion
        }

    /// Encodes bytes as one content block for manifest upload tests.
    let encodeBlock (bytes: byte array) =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test ContentBlock to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Builds a finalized FileManifest for a storage pool returned by the upload session.
    let manifestForStoragePool (storagePoolId: StoragePoolId) (bytes: byte array) (blocks: ContentBlockFormat.EncodedContentBlock array) =
        let mutable offset = 0L

        let contentBlocks =
            blocks
            |> Array.map (fun block ->
                let decodedLength =
                    match ContentBlockFormat.decode block.Payload with
                    | Ok decoded -> decoded.Payload.Length
                    | Error error ->
                        Assert.Fail($"Expected test ContentBlock to decode, got {error}.")
                        0

                let contentBlock = ContentBlock.Create(block.Address, offset, int64 decodedLength)
                offset <- offset + int64 decodedLength
                contentBlock)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId RabinChunking.SuiteName,
                FileContentHash(computeBlake3Hash bytes),
                int64 bytes.Length,
                storagePoolId,
                contentBlocks |> Array.toList
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Parses placement details from a content block SAS URI and upload ETag.
    let contentBlockPlacementFromUri (blobUriWithSasToken: Uri) eTag =
        let pathSegments =
            blobUriWithSasToken
                .AbsolutePath
                .Trim('/')
                .Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map Uri.UnescapeDataString

        let isPathStyleAzurite =
            blobUriWithSasToken.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(blobUriWithSasToken.Host)
               |> fst

        let accountName =
            if isPathStyleAzurite && pathSegments.Length >= 3 then
                pathSegments[0]
            else
                let host = blobUriWithSasToken.Host
                let firstDot = host.IndexOf('.')
                if firstDot > 0 then host.Substring(0, firstDot) else host

        let containerIndex = if isPathStyleAzurite then 1 else 0

        {
            StorageAccountName = accountName
            StorageContainerName = StorageContainerName pathSegments[containerIndex]
            ObjectKey = String.Join("/", pathSegments |> Array.skip (containerIndex + 1))
            ETag = eTag
        }

    /// Uploads content block bytes through a SAS URI.
    let uploadContentBlockWithSasAsync (payload: byte array) (uploadUri: Uri) =
        task {
            let blockBlobClient = BlockBlobClient(uploadUri)
            use payloadStream = new MemoryStream(payload, writable = false)
            let options = BlobUploadOptions()
            options.Conditions <- BlobRequestConditions(IfNoneMatch = ETag.All)
            let! response = blockBlobClient.UploadAsync(payloadStream, options)
            return response.Value.ETag.ToString()
        }

    /// Posts an upload session command and returns the decision payload.
    let postUploadSessionDecisionAsync (route: string) parameters =
        task {
            let! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return deserialize<GraceReturnValue<UploadSessionDecision>> body
        }

    /// Creates and finalizes one manifest-backed FileVersion through the storage routes from caller-selected chunks.
    let createManifestBackedFileVersionFromChunksAsync (repositoryId: string) (relativePath: RelativePath) (chunks: byte array array) =
        task {
            let correlationId = generateCorrelationId ()
            let uploadSessionId = Guid.NewGuid()
            let payload = chunks |> Array.concat
            let blocks = chunks |> Array.map encodeBlock

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- uploadSessionId
            start.AuthorizedScope <- normalizedFilePath relativePath
            start.FileContentHash <- FileContentHash(computeBlake3Hash payload)
            start.ExpectedSize <- int64 payload.Length
            start.ChunkingSuiteId <- RabinChunking.SuiteName
            start.SamplingPolicySnapshot <- "directory-version-zip-test"
            start.OperationId <- "start"

            let! startResult = postUploadSessionDecisionAsync "/storage/startManifestUploadSession" start
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId payload blocks

            let mutable logicalOffset = 0L
            let mutable blockIndex = 0

            while blockIndex < blocks.Length do
                let block = blocks[blockIndex]

                let decodedLength =
                    match ContentBlockFormat.decode block.Payload with
                    | Ok decoded -> decoded.Payload.Length
                    | Error error ->
                        Assert.Fail($"Expected test ContentBlock to decode, got {error}.")
                        0

                let register = Parameters.Storage.RegisterContentBlockUploadParameters()
                setStorageParameters register repositoryId correlationId
                register.UploadSessionId <- uploadSessionId
                register.AuthorizedScope <- normalizedFilePath relativePath
                register.OperationId <- $"register-{blockIndex}"
                register.ContentBlockAddress <- block.Address
                register.LogicalOffset <- logicalOffset
                register.LogicalLength <- int64 decodedLength
                register.ExpectedPayloadLength <- int64 block.Payload.Length

                let! _ = postUploadSessionDecisionAsync "/storage/registerContentBlockUpload" register

                let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
                setStorageParameters uploadUriParameters repositoryId correlationId
                uploadUriParameters.UploadSessionId <- uploadSessionId
                uploadUriParameters.AuthorizedScope <- normalizedFilePath relativePath
                uploadUriParameters.ContentBlockAddress <- block.Address

                let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
                let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
                Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)
                let uploadUri = Uri uploadUriBody
                let! uploadETag = uploadContentBlockWithSasAsync block.Payload uploadUri

                let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
                setStorageParameters confirm repositoryId correlationId
                confirm.UploadSessionId <- uploadSessionId
                confirm.AuthorizedScope <- normalizedFilePath relativePath
                confirm.OperationId <- $"confirm-{blockIndex}"
                confirm.ContentBlockAddress <- block.Address
                confirm.Payload <- block.Payload
                confirm.StoragePlacement <- contentBlockPlacementFromUri uploadUri (Some uploadETag)

                let! _ = postUploadSessionDecisionAsync "/storage/confirmContentBlockUpload" confirm

                logicalOffset <- logicalOffset + int64 decodedLength
                blockIndex <- blockIndex + 1

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- uploadSessionId
            finalize.AuthorizedScope <- normalizedFilePath relativePath
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest

            finalize.BlockPayloads <-
                blocks
                |> Array.map (fun block -> { Address = block.Address; Payload = block.Payload })

            let! finalizeResult = postUploadSessionDecisionAsync "/storage/finalizeManifestUpload" finalize
            Assert.That(finalizeResult.ReturnValue.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))

            let fileVersion = createWholeFileVersion relativePath payload
            fileVersion.ContentReference <- FileContentReference.FileManifest manifest
            return fileVersion
        }

    /// Creates and finalizes one manifest-backed FileVersion through the storage routes.
    let createManifestBackedFileVersionAsync (repositoryId: string) (relativePath: RelativePath) (payload: byte array) =
        createManifestBackedFileVersionFromChunksAsync repositoryId relativePath [| payload |]

    /// Gets the retained zip route artifact bytes.
    let getZipBytesAsync (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        task {
            let parameters = Parameters.DirectoryVersion.GetZipFileParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.DirectoryVersionId <- $"{directoryVersionId}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/directory/getZipFile", createJsonContent parameters)
            do! assertProjectionHelperOk response
            let! returnValue = deserializeContent<GraceReturnValue<UriWithSharedAccessSignature>> response
            let blobClient = BlobClient(returnValue.ReturnValue)
            let! download = blobClient.DownloadContentAsync()
            return download.Value.Content.ToArray()
        }

    /// Reads a zip entry into a byte array.
    let readZipEntryBytes (zipBytes: byte array) (entryName: string) =
        use stream = new MemoryStream(zipBytes, writable = false)
        use archive = new ZipArchive(stream, ZipArchiveMode.Read)
        let entry = archive.GetEntry(entryName)
        Assert.That(entry, Is.Not.Null, $"Expected zip entry '{entryName}'.")
        use entryStream = entry.Open()
        use output = new MemoryStream()
        entryStream.CopyTo(output)
        output.ToArray()

    /// Builds a deterministic same SHA256 prefix directory pair for integration setup fixture for the server integration directory Version assertions.
    let createSameSha256PrefixDirectoryPair repositoryId pathPrefix =
        let candidates =
            [|
                for index in 0..512 -> createDirectoryVersion (Guid.NewGuid()) repositoryId (RelativePath $"/{pathPrefix}/{index}/") []
            |]

        candidates
        |> Array.groupBy (fun directoryVersion ->
            (string directoryVersion.Sha256Hash)
                .Substring(0, 2))
        |> Array.tryPick (fun (sharedPrefix, matches) -> if matches.Length >= 2 then Some(matches[0], matches[1], sharedPrefix) else None)
        |> function
            | Some pair -> pair
            | None -> failwith "Could not generate same-prefix SHA-256 directory versions for directory route tests."

    /// Asserts ok for integration responses.
    let assertOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))
        }

    /// Asserts bad request grace error for integration responses.
    let assertBadRequestGraceError (expectedError: string) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            let error = deserialize<GraceError> body
            Assert.That(error.Error, Is.EqualTo(expectedError))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Builds a deterministic directory version for integration setup fixture for the server integration directory Version assertions.
    let createDirectoryVersionAsync (directoryVersion: DirectoryVersionModel) =
        task {
            let! response = Client.PostAsync("/directory/create", createJsonContent (createParameters directoryVersion))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.ReturnValue, Is.EqualTo("Directory version command succeeded."))
            Assert.That(returnValue.Properties.ContainsKey(nameof DirectoryVersionId), Is.True)
        }

    /// Gets directory version from the running test server.
    let getDirectoryVersionAsync (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        task {
            let! response = Client.PostAsync("/directory/get", createJsonContent (getParameters repositoryId directoryVersionId))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<DirectoryVersionDto>> response
            return returnValue.ReturnValue
        }

    /// Asserts directory version DTO for integration responses.
    let assertDirectoryVersionDto (expected: DirectoryVersionModel) (actual: DirectoryVersionDto) =
        Assert.That(actual.DirectoryVersion.DirectoryVersionId, Is.EqualTo(expected.DirectoryVersionId))
        Assert.That(actual.DirectoryVersion.OwnerId, Is.EqualTo(expected.OwnerId))
        Assert.That(actual.DirectoryVersion.OrganizationId, Is.EqualTo(expected.OrganizationId))
        Assert.That(actual.DirectoryVersion.RepositoryId, Is.EqualTo(expected.RepositoryId))
        Assert.That(actual.DirectoryVersion.RelativePath, Is.EqualTo(expected.RelativePath))
        Assert.That(actual.DirectoryVersion.Sha256Hash, Is.EqualTo(expected.Sha256Hash))
        Assert.That(actual.DirectoryVersion.Blake3Hash, Is.EqualTo(expected.Blake3Hash))
        Assert.That(actual.DirectoryVersion.HashesValidated, Is.True)

    /// Asserts directory version for integration responses.
    let assertDirectoryVersion (expected: DirectoryVersionModel) (actual: DirectoryVersionModel) =
        Assert.That(actual.DirectoryVersionId, Is.EqualTo(expected.DirectoryVersionId))
        Assert.That(actual.OwnerId, Is.EqualTo(expected.OwnerId))
        Assert.That(actual.OrganizationId, Is.EqualTo(expected.OrganizationId))
        Assert.That(actual.RepositoryId, Is.EqualTo(expected.RepositoryId))
        Assert.That(actual.RelativePath, Is.EqualTo(expected.RelativePath))
        Assert.That(actual.Sha256Hash, Is.EqualTo(expected.Sha256Hash))
        Assert.That(actual.Blake3Hash, Is.EqualTo(expected.Blake3Hash))
        Assert.That(actual.HashesValidated, Is.True)

/// Covers directory version server scenarios.
[<NonParallelizable>]
type DirectoryVersionServer() =

    /// Verifies the create get get by SHA and recursive routes preserve directory DTO shape and identity scenario.
    [<Test>]
    member _.CreateGetGetByShaAndRecursiveRoutesPreserveDirectoryDtoShapeAndIdentity() =
        task {
            let repositoryId = repositoryIds[0]
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "/src/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "/" [ child ]

            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync child
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync root

            let! fetched = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetched

            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId rootId root.Sha256Hash

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByShaResponse
            let! getBySha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByShaResponse

            DirectoryVersionServerTestHelpers.assertDirectoryVersion root getBySha.ReturnValue

            let getByBlake3Parameters = DirectoryVersionServerTestHelpers.getByBlake3HashParameters repositoryId root.Blake3Hash

            let! getByBlake3Response = Client.PostAsync("/directory/getByBlake3Hash", createJsonContent getByBlake3Parameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByBlake3Response
            let! getByBlake3 = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByBlake3Response

            DirectoryVersionServerTestHelpers.assertDirectoryVersion root getByBlake3.ReturnValue

            let! recursiveResponse =
                Client.PostAsync(
                    "/directory/getDirectoryVersionsRecursive",
                    createJsonContent (DirectoryVersionServerTestHelpers.getParameters repositoryId rootId)
                )

            do! DirectoryVersionServerTestHelpers.assertOk recursiveResponse
            let! recursive = deserializeContent<GraceReturnValue<DirectoryVersionDto array>> recursiveResponse

            let recursiveDirectories =
                recursive.ReturnValue
                |> Seq.map (fun dto -> dto.DirectoryVersion)
                |> Seq.toArray

            Assert.That(recursiveDirectories, Has.Length.EqualTo(2))

            Assert.That(
                recursiveDirectories
                |> Array.exists (fun directoryVersion -> directoryVersion.DirectoryVersionId = rootId),
                Is.True
            )

            Assert.That(
                recursiveDirectories
                |> Array.exists (fun directoryVersion -> directoryVersion.DirectoryVersionId = childId),
                Is.True
            )
        }

    /// Verifies the get by SHA returns default sentinel when no directory version matches scenario.
    [<Test>]
    member _.GetByShaReturnsDefaultSentinelWhenNoDirectoryVersionMatches() =
        task {
            let repositoryId = repositoryIds[0]
            let missingDirectoryId = Guid.NewGuid()
            let missingSha256Hash = Sha256Hash "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId missingDirectoryId missingSha256Hash

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByShaResponse
            let! getBySha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByShaResponse

            Assert.That(getBySha.ReturnValue.DirectoryVersionId, Is.EqualTo(DirectoryVersion.Default.DirectoryVersionId))
            Assert.That(getBySha.ReturnValue.Sha256Hash, Is.EqualTo(DirectoryVersion.Default.Sha256Hash))
            Assert.That(getBySha.ReturnValue.Blake3Hash, Is.EqualTo(DirectoryVersion.Default.Blake3Hash))
        }

    /// Verifies the get by SHA rejects malformed prefixes before lookup scenario.
    [<TestCase("not-a-sha")>]
    [<TestCase("f")>]
    member _.GetByShaRejectsMalformedPrefixesBeforeLookup(sha256Hash: string) =
        task {
            let repositoryId = repositoryIds[0]
            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId (Guid.NewGuid()) (Sha256Hash sha256Hash)

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            let! getByShaBody = getByShaResponse.Content.ReadAsStringAsync()

            Assert.That(getByShaResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), getByShaBody)
            Assert.That((deserialize<GraceError> getByShaBody).Error, Is.EqualTo(DirectoryVersionError.getErrorMessage DirectoryVersionError.InvalidSha256Hash))
        }

    /// Verifies the get by SHA rejects ambiguous prefix instead of returning default sentinel scenario.
    [<Test>]
    member _.GetByShaRejectsAmbiguousPrefixInsteadOfReturningDefaultSentinel() =
        task {
            let repositoryId = repositoryIds[0]

            /// Defines first behavior for the surrounding tests used by the server integration directory Version scenario.
            let first, second, sharedPrefix =
                DirectoryVersionServerTestHelpers.createSameSha256PrefixDirectoryPair repositoryId $"ambiguous-directory-sha/{Guid.NewGuid():N}"

            let! saveResponse =
                Client.PostAsync(
                    "/directory/saveDirectoryVersions",
                    createJsonContent (DirectoryVersionServerTestHelpers.saveParameters repositoryId [ first; second ])
                )

            do! DirectoryVersionServerTestHelpers.assertOk saveResponse

            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId first.DirectoryVersionId (Sha256Hash sharedPrefix)

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            let! getByShaBody = getByShaResponse.Content.ReadAsStringAsync()

            Assert.That(getByShaResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), getByShaBody)
            Assert.That((deserialize<GraceError> getByShaBody).Error, Does.Contain("ambiguous"))
        }

    /// Verifies the save directory versions creates missing directories and keeps missing get as grace error scenario.
    [<Test>]
    member _.SaveDirectoryVersionsCreatesMissingDirectoriesAndKeepsMissingGetAsGraceError() =
        task {
            let repositoryId = repositoryIds[1]
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "/docs/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "/" [ child ]

            let! saveResponse =
                Client.PostAsync(
                    "/directory/saveDirectoryVersions",
                    createJsonContent (DirectoryVersionServerTestHelpers.saveParameters repositoryId [ child; root ])
                )

            do! DirectoryVersionServerTestHelpers.assertOk saveResponse
            let! saveReturnValue = deserializeContent<GraceReturnValue<string>> saveResponse
            Assert.That(saveReturnValue.ReturnValue, Is.EqualTo("Uploaded new directory versions."))

            let! fetchedRoot = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetchedRoot
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Has.Count.EqualTo(1))
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Does.Contain(childId))

            let! fetchedChild = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId childId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto child fetchedChild

            let missingId = Guid.NewGuid()

            let! missingResponse =
                Client.PostAsync("/directory/get", createJsonContent (DirectoryVersionServerTestHelpers.getParameters repositoryId missingId))

            do!
                DirectoryVersionServerTestHelpers.assertBadRequestGraceError
                    (DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryDoesNotExist)
                    missingResponse
        }

    /// Verifies the save directory versions orders dot root after direct children when input is root first scenario.
    [<Test>]
    member _.SaveDirectoryVersionsOrdersDotRootAfterDirectChildrenWhenInputIsRootFirst() =
        task {
            let repositoryId = repositoryIds[2]
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "src" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "." [ child ]

            let! saveResponse =
                Client.PostAsync(
                    "/directory/saveDirectoryVersions",
                    createJsonContent (DirectoryVersionServerTestHelpers.saveParameters repositoryId [ root; child ])
                )

            do! DirectoryVersionServerTestHelpers.assertOk saveResponse
            let! saveReturnValue = deserializeContent<GraceReturnValue<string>> saveResponse
            Assert.That(saveReturnValue.ReturnValue, Is.EqualTo("Uploaded new directory versions."))

            let! fetchedRoot = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetchedRoot
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Has.Count.EqualTo(1))
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Does.Contain(childId))

            let! fetchedChild = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId childId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto child fetchedChild
        }

    /// Verifies the retained zip route preserves bytes for mixed whole-file and FileManifest-backed files.
    [<Test>]
    member _.GetZipFilePreservesManifestBackedFileBytes() =
        task {
            let repositoryId = repositoryIds[0]
            let rootId = Guid.NewGuid()
            let wholeFilePath = RelativePath $"/zip-proof/{Guid.NewGuid():N}/whole.bin"
            let manifestFilePath = RelativePath $"/zip-proof/{Guid.NewGuid():N}/manifest.bin"
            let wholeFilePayload = DirectoryVersionServerTestHelpers.deterministicBytes "whole-file-zip-proof" 8192

            let manifestPayloadChunks =
                [|
                    DirectoryVersionServerTestHelpers.deterministicBytes "manifest-backed-zip-proof-a" 64000
                    DirectoryVersionServerTestHelpers.deterministicBytes "manifest-backed-zip-proof-b" 96000
                    DirectoryVersionServerTestHelpers.deterministicBytes "manifest-backed-zip-proof-c" 60000
                |]

            let manifestPayload = manifestPayloadChunks |> Array.concat

            let wholeFileVersion = DirectoryVersionServerTestHelpers.createWholeFileVersion wholeFilePath wholeFilePayload

            let! uploadedWholeFileVersion = DirectoryVersionServerTestHelpers.uploadWholeFileAsync repositoryId wholeFileVersion wholeFilePayload

            let! manifestBackedFileVersion =
                DirectoryVersionServerTestHelpers.createManifestBackedFileVersionFromChunksAsync repositoryId manifestFilePath manifestPayloadChunks

            let root =
                DirectoryVersionServerTestHelpers.createDirectoryVersionWithFiles
                    rootId
                    repositoryId
                    "/"
                    [
                        uploadedWholeFileVersion
                        manifestBackedFileVersion
                    ]

            let! saveResponse =
                Client.PostAsync("/directory/saveDirectoryVersions", createJsonContent (DirectoryVersionServerTestHelpers.saveParameters repositoryId [ root ]))

            do! DirectoryVersionServerTestHelpers.assertOk saveResponse

            let! zipBytes = DirectoryVersionServerTestHelpers.getZipBytesAsync repositoryId rootId

            let wholeFileEntry = DirectoryVersionServerTestHelpers.readZipEntryBytes zipBytes uploadedWholeFileVersion.RelativePath

            let manifestEntry = DirectoryVersionServerTestHelpers.readZipEntryBytes zipBytes manifestBackedFileVersion.RelativePath

            Assert.That(Convert.ToBase64String(wholeFileEntry), Is.EqualTo(Convert.ToBase64String(wholeFilePayload)))
            Assert.That(Convert.ToBase64String(manifestEntry), Is.EqualTo(Convert.ToBase64String(manifestPayload)))
        }
