namespace Grace.CLI.Tests

open Grace.CLI
open Grace.CLI.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks

/// Exercises manifest download sdk behavior.
[<NonParallelizable>]
type ManifestDownloadSdkTests() =
    static member private PseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        /// Tracks state changes so this scenario can assert the resulting side effect explicitly.
        let mutable state = 0x7843ac21u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    static member private BinaryPolicy thresholdBytes = { ManifestEligibilityPolicy.Default with ThresholdBytes = thresholdBytes; BinaryScanBytes = 16 }

    static member private ComputeSha256Hash(bytes: byte array) = Sha256Hash(byteArrayToString (SHA256.HashData(bytes).AsSpan()))

    static member private ManifestFor(plan: LocalPlanner.LocalFilePlan) =
        match plan.ManifestAddress with
        | None -> failwith "Expected manifest-backed plan."
        | Some manifestAddress ->
            let blocks =
                plan.Blocks
                |> Array.map (fun block -> ContentBlock.Create(block.Address, block.Offset, block.Size))
                |> List.ofArray

            FileManifest.Create(manifestAddress, plan.ChunkingSuiteId, plan.FileContentHash, plan.ExpectedSize, blocks)

    static member private EncodeBlocks(plan: LocalPlanner.LocalFilePlan) =
        let blocks = Dictionary<ContentBlockAddress, byte array>()

        for uploadPlan in plan.ContentBlockUploads do
            match ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L uploadPlan.Bytes ] with
            | Error error -> failwith $"Failed to encode test ContentBlock: {error}."
            | Ok encodedBlock -> blocks[encodedBlock.Address] <- encodedBlock.Payload

        blocks

    static member private CreateManifestFileVersion relativePath payload manifest =
        let fileVersion = FileVersion.Create relativePath (ManifestDownloadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion

    static member private CreateRequest fileVersion correlationId : ManifestDownload.ManifestDownloadRequest =
        {
            OwnerId = "22222222-2222-2222-2222-222222222222"
            OwnerName = "owner"
            OrganizationId = "33333333-3333-3333-3333-333333333333"
            OrganizationName = "org"
            RepositoryId = "11111111-1111-1111-1111-111111111111"
            RepositoryName = "repo"
            FileVersion = fileVersion
            OutputStream = None
            CorrelationId = correlationId
            ExpectedChunkingSuiteId = ChunkingSuiteId RabinChunking.SuiteName
        }

    static member private ConfigureForRoot root =
        let graceDirectory = System.IO.Path.Combine(root, Constants.GraceConfigDirectory)

        System.IO.Directory.CreateDirectory(graceDirectory)
        |> ignore

        let graceConfigPath = System.IO.Path.Combine(graceDirectory, Constants.GraceConfigFileName)

        if not (System.IO.File.Exists(graceConfigPath)) then
            System.IO.File.WriteAllText(graceConfigPath, "{}")

        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.Parse("22222222-2222-2222-2222-222222222222")
        configuration.OwnerName <- "owner"
        configuration.OrganizationId <- Guid.Parse("33333333-3333-3333-3333-333333333333")
        configuration.OrganizationName <- "org"
        configuration.RepositoryId <- Guid.Parse("11111111-1111-1111-1111-111111111111")
        configuration.RepositoryName <- "repo"
        configuration.RootDirectory <- root
        configuration.StandardizedRootDirectory <- normalizeFilePath root
        configuration.GraceDirectory <- graceDirectory
        configuration.ObjectDirectory <- System.IO.Path.Combine(configuration.GraceDirectory, Constants.GraceObjectsDirectory)
        configuration.ConfigurationDirectory <- configuration.GraceDirectory
        configuration.ObjectStorageProvider <- ObjectStorageProvider.AzureBlobStorage
        configuration.IsPopulated <- true
        updateConfiguration configuration
        configuration

    static member private WithTempConfiguration(action: string -> Task<'T>) =
        task {
            let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"grace-manifest-download-{Guid.NewGuid():N}")
            let previousDirectory = Environment.CurrentDirectory
            let previousConfiguration = if configurationFileExists () then Some(Current()) else None

            try
                System.IO.Directory.CreateDirectory(root)
                |> ignore

                Environment.CurrentDirectory <- root

                ManifestDownloadSdkTests.ConfigureForRoot root
                |> ignore

                return! action root
            finally
                Environment.CurrentDirectory <- previousDirectory

                match previousConfiguration with
                | Some configuration -> updateConfiguration configuration
                | None -> resetConfiguration ()

                if System.IO.Directory.Exists(root) then
                    try
                        System.IO.Directory.Delete(root, true)
                    with
                    | _ -> ()
        }

    /// Verifies that manifest download resolves blocks and reconstructs original bytes.
    [<Test>]
    member _.ManifestDownloadResolvesBlocksAndReconstructsOriginalBytes() =
        task {
            let payload = ManifestDownloadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 4uy

            let plan = LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

            let manifest = ManifestDownloadSdkTests.ManifestFor plan
            let blockPayloads = ManifestDownloadSdkTests.EncodeBlocks plan
            let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "large.bin" payload manifest
            let requestedBlocks = ResizeArray<ContentBlockAddress>()
            let downloadedBlocks = ResizeArray<ContentBlockAddress>()
            let correlationId = "corr-sdk-manifest-download"
            use outputStream = new MemoryStream()

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters ->
                            requestedBlocks.Add(parameters.ContentBlockAddress)
                            Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                            Assert.That(parameters.ManifestAddress, Is.EqualTo(manifest.ManifestAddress))
                            Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress uri ->
                            Assert.That(uri.AbsoluteUri, Does.Contain(contentBlockAddress))
                            downloadedBlocks.Add(contentBlockAddress)
                            Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let request = { ManifestDownloadSdkTests.CreateRequest fileVersion correlationId with OutputStream = Some outputStream }

            let! result = ManifestDownload.downloadFileWithClient client request

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok returnValue ->
                Assert.That(returnValue.ReturnValue.UsedManifestDownload, Is.True)
                Assert.That(returnValue.ReturnValue.BytesWritten, Is.EqualTo(payload.LongLength))
                let downloadedBytes = outputStream.ToArray()
                Assert.That(downloadedBytes.Length, Is.EqualTo(payload.Length))
                Assert.That(Array.forall2 (=) downloadedBytes payload, Is.True)

                let expectedBlocks =
                    manifest.Blocks
                    |> Seq.map (fun block -> block.Address)
                    |> Seq.toArray

                Assert.That(requestedBlocks.ToArray() = expectedBlocks, Is.True)
                Assert.That(downloadedBlocks.ToArray() = expectedBlocks, Is.True)
        }

    /// Verifies that manifest download requests only manifest identity for each block uri.
    [<Test>]
    member _.ManifestDownloadRequestsOnlyManifestIdentityForEachBlockUri() =
        task {
            let payload = ManifestDownloadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 9uy

            let plan = LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

            let manifest = ManifestDownloadSdkTests.ManifestFor plan
            let blockPayloads = ManifestDownloadSdkTests.EncodeBlocks plan
            let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "identity-only-large.bin" payload manifest
            /// Tracks request Count changes so this scenario can assert the resulting side effect explicitly.
            let mutable requestCount = 0
            let correlationId = "corr-sdk-manifest-download-identity-only"
            use outputStream = new MemoryStream()

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters ->
                            requestCount <- requestCount + 1
                            Assert.That(parameters.ManifestAddress, Is.EqualTo(manifest.ManifestAddress))
                            Assert.That(parameters.StoragePoolId, Is.EqualTo(manifest.StoragePoolId))
                            Assert.That(parameters.ContentBlockAddress, Is.Not.EqualTo(String.Empty))
                            Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                            let manifestProperty = parameters.GetType().GetProperty("Manifest")
                            Assert.That(manifestProperty, Is.Null)
                            let blocksProperty = parameters.GetType().GetProperty("Blocks")
                            Assert.That(blocksProperty, Is.Null)
                            Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress _ -> Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let request = { ManifestDownloadSdkTests.CreateRequest fileVersion correlationId with OutputStream = Some outputStream }

            let! result = ManifestDownload.downloadFileWithClient client request

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok returnValue ->
                Assert.That(returnValue.ReturnValue.DownloadedBlockCount, Is.EqualTo(manifest.Blocks.Count))
                Assert.That(requestCount, Is.EqualTo(manifest.Blocks.Count))
        }

    /// Verifies that whole file content download uses compatibility fallback without resolving blocks.
    [<Test>]
    member _.WholeFileContentDownloadUsesCompatibilityFallbackWithoutResolvingBlocks() =
        task {
            let payload = ManifestDownloadSdkTests.PseudoRandomBytes 128
            let fileVersion = FileVersion.Create "small.txt" (ManifestDownloadSdkTests.ComputeSha256Hash payload) String.Empty false (int64 payload.Length)
            let correlationId = "corr-sdk-manifest-download-whole-file"

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun _ ->
                            Assert.Fail("WholeFileContent downloads must not resolve ContentBlock download URIs.")
                            Task.FromResult(Error(GraceError.Create "unexpected" correlationId))
                    DownloadContentBlock =
                        fun _ _ ->
                            Assert.Fail("WholeFileContent downloads must not download ContentBlock payloads.")
                            Task.FromResult(Error(GraceError.Create "unexpected" correlationId))
                }

            let! result = ManifestDownload.downloadFileWithClient client (ManifestDownloadSdkTests.CreateRequest fileVersion correlationId)

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok returnValue ->
                Assert.That(returnValue.ReturnValue.UsedManifestDownload, Is.False)
                Assert.That(returnValue.ReturnValue.BytesWritten, Is.EqualTo(0L))
                Assert.That(returnValue.ReturnValue.DownloadedBlockCount, Is.EqualTo(0))
        }

    /// Verifies that cli object download preserves manifest reference before choosing download path.
    [<Test>]
    member _.CliObjectDownloadPreservesManifestReferenceBeforeChoosingDownloadPath() =
        ManifestDownloadSdkTests.WithTempConfiguration (fun _ ->
            task {
                let payload = ManifestDownloadSdkTests.PseudoRandomBytes 220000
                payload[0] <- 6uy

                let plan =
                    LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

                let manifest = ManifestDownloadSdkTests.ManifestFor plan
                let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "cli-large.bin" payload manifest
                let localFileVersion = fileVersion.ToLocalFileVersion DateTime.UtcNow
                let downloadFile = Services.objectStorageDownloadFileFromFileVersion fileVersion

                Assert.That(localFileVersion.ToFileVersion.ContentReference.ReferenceType, Is.EqualTo(FileContentReferenceType.WholeFileContent))

                Assert.That(
                    (Services.fileVersionForObjectStorageDownload downloadFile)
                        .ContentReference
                        .ReferenceType,
                    Is.EqualTo(FileContentReferenceType.FileManifest)
                )

                let getDownloadUriParameters =
                    GetDownloadUriParameters(
                        OwnerId = String.Empty,
                        OwnerName = Current().OwnerName,
                        OrganizationId = String.Empty,
                        OrganizationName = Current().OrganizationName,
                        RepositoryId = String.Empty,
                        RepositoryName = Current().RepositoryName,
                        CorrelationId = "corr-cli-manifest-download"
                    )

                /// Tracks manifest Path Taken changes so this scenario can assert the resulting side effect explicitly.
                let mutable manifestPathTaken = false

                /// Builds manifest download test data used to exercise CLI manifest Download behavior.
                let manifestDownload (request: ManifestDownload.ManifestDownloadRequest) =
                    manifestPathTaken <- true
                    Assert.That(request.OwnerId, Is.EqualTo(String.Empty))
                    Assert.That(request.OrganizationId, Is.EqualTo(String.Empty))
                    Assert.That(request.RepositoryId, Is.EqualTo(String.Empty))
                    Assert.That(request.FileVersion.ContentReference.ReferenceType, Is.EqualTo(FileContentReferenceType.FileManifest))
                    Assert.That(request.FileVersion.ContentReference.Manifest, Is.EqualTo(Some manifest))

                    match request.OutputStream with
                    | None -> Assert.Fail("Manifest-backed CLI downloads must provide an output stream.")
                    | Some outputStream -> outputStream.Write(payload, 0, payload.Length)

                    let result: ManifestDownload.ManifestDownloadResult =
                        {
                            FileVersion = request.FileVersion
                            Manifest = Some manifest
                            BytesWritten = payload.LongLength
                            DownloadedBlockCount = manifest.Blocks.Count
                            UsedManifestDownload = true
                        }

                    Task.FromResult(Ok(GraceReturnValue.Create result request.CorrelationId))

                /// Builds whole file download test data used to exercise CLI manifest Download behavior.
                let wholeFileDownload _ _ _ =
                    Assert.Fail("Manifest-backed CLI downloads must not use the WholeFileContent object-storage path.")
                    Task.FromResult(Error(GraceError.Create "unexpected whole-file download" getDownloadUriParameters.CorrelationId))

                let! result =
                    Services.downloadFilesFromObjectStorageWithClients
                        manifestDownload
                        wholeFileDownload
                        getDownloadUriParameters
                        [| downloadFile |]
                        getDownloadUriParameters.CorrelationId

                match result with
                | Error error -> Assert.Fail(error)
                | Ok () ->
                    Assert.That(manifestPathTaken, Is.True)
                    Assert.That(System.IO.File.Exists(localFileVersion.FullObjectPath), Is.True)
                    let downloadedBytes = System.IO.File.ReadAllBytes(localFileVersion.FullObjectPath)
                    Assert.That(downloadedBytes.Length, Is.EqualTo(payload.Length))
                    Assert.That(Array.forall2 (=) downloadedBytes payload, Is.True)
            })

    /// Verifies that cli manifest download reports object cache write failures as grace errors.
    [<Test>]
    member _.CliManifestDownloadReportsObjectCacheWriteFailuresAsGraceErrors() =
        ManifestDownloadSdkTests.WithTempConfiguration (fun root ->
            task {
                let payload = ManifestDownloadSdkTests.PseudoRandomBytes 220000
                payload[0] <- 7uy

                let plan =
                    LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

                let manifest = ManifestDownloadSdkTests.ManifestFor plan
                let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "cli-cache-write.bin" payload manifest
                let downloadFile = Services.objectStorageDownloadFileFromFileVersion fileVersion

                let objectCacheFile = Path.Combine(root, "object-cache-file")
                File.WriteAllText(objectCacheFile, "not a directory")

                let configuration = Current()
                configuration.ObjectDirectory <- objectCacheFile
                updateConfiguration configuration

                let getDownloadUriParameters =
                    GetDownloadUriParameters(
                        OwnerId = String.Empty,
                        OwnerName = Current().OwnerName,
                        OrganizationId = String.Empty,
                        OrganizationName = Current().OrganizationName,
                        RepositoryId = String.Empty,
                        RepositoryName = Current().RepositoryName,
                        CorrelationId = "corr-cli-manifest-cache-write"
                    )

                /// Builds manifest download test data used to exercise CLI manifest Download behavior.
                let manifestDownload (request: ManifestDownload.ManifestDownloadRequest) =
                    let result: ManifestDownload.ManifestDownloadResult =
                        {
                            FileVersion = request.FileVersion
                            Manifest = Some manifest
                            BytesWritten = payload.LongLength
                            DownloadedBlockCount = manifest.Blocks.Count
                            UsedManifestDownload = true
                        }

                    Task.FromResult(Ok(GraceReturnValue.Create result request.CorrelationId))

                /// Builds whole file download test data used to exercise CLI manifest Download behavior.
                let wholeFileDownload _ _ _ =
                    Assert.Fail("Manifest cache write failures must not fall back to WholeFileContent downloads.")
                    Task.FromResult(Error(GraceError.Create "unexpected whole-file download" getDownloadUriParameters.CorrelationId))

                let! result =
                    Services.downloadFilesFromObjectStorageWithClients
                        manifestDownload
                        wholeFileDownload
                        getDownloadUriParameters
                        [| downloadFile |]
                        getDownloadUriParameters.CorrelationId

                match result with
                | Ok () -> Assert.Fail("Expected object-cache write failure to be returned as GraceError.")
                | Error error ->
                    Assert.That(error, Does.Contain("Some files could not be downloaded from object storage."))
                    Assert.That(error, Does.Contain("Failed writing manifest-backed file to object cache"))
            })

    /// Verifies that cli manifest download deletes partial object cache file on failure.
    [<Test>]
    member _.CliManifestDownloadDeletesPartialObjectCacheFileOnFailure() =
        ManifestDownloadSdkTests.WithTempConfiguration (fun _ ->
            task {
                let payload = ManifestDownloadSdkTests.PseudoRandomBytes 220000
                payload[0] <- 8uy

                let plan =
                    LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

                let manifest = ManifestDownloadSdkTests.ManifestFor plan
                let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "cli-partial-cache.bin" payload manifest
                let downloadFile = Services.objectStorageDownloadFileFromFileVersion fileVersion
                let localFileVersion = downloadFile.LocalFileVersion

                let getDownloadUriParameters =
                    GetDownloadUriParameters(
                        OwnerId = String.Empty,
                        OwnerName = Current().OwnerName,
                        OrganizationId = String.Empty,
                        OrganizationName = Current().OrganizationName,
                        RepositoryId = String.Empty,
                        RepositoryName = Current().RepositoryName,
                        CorrelationId = "corr-cli-manifest-partial-cache"
                    )

                /// Builds manifest download test data used to exercise CLI manifest Download behavior.
                let manifestDownload (request: ManifestDownload.ManifestDownloadRequest) =
                    match request.OutputStream with
                    | None -> Assert.Fail("Manifest-backed CLI downloads must provide an output stream.")
                    | Some outputStream -> outputStream.Write(payload, 0, 4096)

                    Task.FromResult(Error(GraceError.Create "simulated manifest reconstruction failure" request.CorrelationId))

                /// Builds whole file download test data used to exercise CLI manifest Download behavior.
                let wholeFileDownload _ _ _ =
                    Assert.Fail("Manifest reconstruction failures must not fall back to WholeFileContent downloads.")
                    Task.FromResult(Error(GraceError.Create "unexpected whole-file download" getDownloadUriParameters.CorrelationId))

                let! result =
                    Services.downloadFilesFromObjectStorageWithClients
                        manifestDownload
                        wholeFileDownload
                        getDownloadUriParameters
                        [| downloadFile |]
                        getDownloadUriParameters.CorrelationId

                match result with
                | Ok () -> Assert.Fail("Expected manifest reconstruction failure to be returned as GraceError.")
                | Error error ->
                    Assert.That(error, Does.Contain("simulated manifest reconstruction failure"))
                    Assert.That(File.Exists localFileVersion.FullObjectPath, Is.False)
            })

    /// Verifies that manifest download rejects out of order manifest ranges.
    [<Test>]
    member _.ManifestDownloadRejectsOutOfOrderManifestRanges() =
        task {
            let payload = Array.zeroCreate<byte> (RabinChunking.MaximumChunkSize * 2)

            let plan = LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

            let validManifest = ManifestDownloadSdkTests.ManifestFor plan
            let blockPayloads = ManifestDownloadSdkTests.EncodeBlocks plan

            let invalidBlocks =
                validManifest.Blocks
                |> Seq.mapi (fun index block -> if index = 1 then ContentBlock.Create(block.Address, 0L, block.Size) else block)
                |> List.ofSeq

            let invalidManifestAddress =
                ContentAddress.computeManifestAddress validManifest.ChunkingSuiteId validManifest.FileContentHash validManifest.Size invalidBlocks

            let invalidManifest =
                FileManifest.Create(invalidManifestAddress, validManifest.ChunkingSuiteId, validManifest.FileContentHash, validManifest.Size, invalidBlocks)

            let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "out-of-order-large.bin" payload invalidManifest
            let correlationId = "corr-sdk-manifest-download-ranges"
            use outputStream = new MemoryStream()

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters -> Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress _ -> Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let request = { ManifestDownloadSdkTests.CreateRequest fileVersion correlationId with OutputStream = Some outputStream }

            let! result = ManifestDownload.downloadFileWithClient client request

            match result with
            | Error error ->
                Assert.That(error.Error, Does.Contain("Manifest download reconstruction failed"))
                Assert.That(error.Error, Does.Contain("BlockRangeOutOfOrder"))
            | Ok _ -> Assert.Fail("Expected out-of-order manifest ranges to be rejected.")
        }

    /// Verifies that manifest download rejects corrupt content block payload.
    [<Test>]
    member _.ManifestDownloadRejectsCorruptContentBlockPayload() =
        task {
            let payload = ManifestDownloadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 5uy

            let plan = LocalPlanner.analyzeBytes { LocalPlanner.Options.Default with EligibilityPolicy = ManifestDownloadSdkTests.BinaryPolicy 1024L } payload

            let manifest = ManifestDownloadSdkTests.ManifestFor plan
            let blockPayloads = ManifestDownloadSdkTests.EncodeBlocks plan
            let firstAddress = plan.ContentBlockUploads[0].BlockAddress
            let corruptPayload = Array.copy blockPayloads[firstAddress]
            corruptPayload[0] <- corruptPayload[0] ^^^ 0xffuy
            blockPayloads[firstAddress] <- corruptPayload

            let fileVersion = ManifestDownloadSdkTests.CreateManifestFileVersion "corrupt-large.bin" payload manifest
            let correlationId = "corr-sdk-manifest-download-corrupt"
            use outputStream = new MemoryStream()

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters -> Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress _ -> Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let request = { ManifestDownloadSdkTests.CreateRequest fileVersion correlationId with OutputStream = Some outputStream }

            let! result = ManifestDownload.downloadFileWithClient client request

            match result with
            | Error error ->
                Assert.That(error.Error, Does.Contain("Manifest download reconstruction failed"))
                Assert.That(error.Error, Does.Contain("ContentBlock payload is invalid"))
            | Ok _ -> Assert.Fail("Expected corrupt ContentBlock payload to be rejected.")
        }
