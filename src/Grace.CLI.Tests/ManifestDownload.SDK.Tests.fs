namespace Grace.CLI.Tests

open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Threading.Tasks

[<NonParallelizable>]
type ManifestDownloadSdkTests() =
    static member private PseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
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
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            OwnerName = "owner"
            OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            OrganizationName = "org"
            RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            RepositoryName = "repo"
            FileVersion = fileVersion
            CorrelationId = correlationId
            ExpectedChunkingSuiteId = ChunkingSuiteId RabinChunking.SuiteName
        }

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

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters ->
                            requestedBlocks.Add(parameters.ContentBlockAddress)
                            Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress uri ->
                            Assert.That(uri.AbsoluteUri, Does.Contain(contentBlockAddress))
                            downloadedBlocks.Add(contentBlockAddress)
                            Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let! result = ManifestDownload.downloadFileWithClient client (ManifestDownloadSdkTests.CreateRequest fileVersion correlationId)

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok returnValue ->
                Assert.That(returnValue.ReturnValue.UsedManifestDownload, Is.True)
                Assert.That(returnValue.ReturnValue.Bytes.Length, Is.EqualTo(payload.Length))
                Assert.That(Array.forall2 (=) returnValue.ReturnValue.Bytes payload, Is.True)

                Assert.That(
                    requestedBlocks,
                    Is.EquivalentTo(
                        manifest.Blocks
                        |> Seq.map (fun block -> block.Address)
                        |> Seq.distinct
                    )
                )

                Assert.That(downloadedBlocks, Is.EquivalentTo(requestedBlocks))
        }

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
                Assert.That(returnValue.ReturnValue.Bytes, Is.Empty)
                Assert.That(returnValue.ReturnValue.DownloadedBlockCount, Is.EqualTo(0))
        }

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

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters -> Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress _ -> Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let! result = ManifestDownload.downloadFileWithClient client (ManifestDownloadSdkTests.CreateRequest fileVersion correlationId)

            match result with
            | Error error ->
                Assert.That(error.Error, Does.Contain("Manifest download reconstruction failed"))
                Assert.That(error.Error, Does.Contain("BlockRangeOutOfOrder"))
            | Ok _ -> Assert.Fail("Expected out-of-order manifest ranges to be rejected.")
        }

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

            let client: ManifestDownload.ManifestDownloadClient =
                {
                    GetContentBlockDownloadUri =
                        fun parameters -> Task.FromResult(Ok(GraceReturnValue.Create $"https://example.test/{parameters.ContentBlockAddress}" correlationId))
                    DownloadContentBlock =
                        fun contentBlockAddress _ -> Task.FromResult(Ok(GraceReturnValue.Create blockPayloads[contentBlockAddress] correlationId))
                }

            let! result = ManifestDownload.downloadFileWithClient client (ManifestDownloadSdkTests.CreateRequest fileVersion correlationId)

            match result with
            | Error error ->
                Assert.That(error.Error, Does.Contain("Manifest download reconstruction failed"))
                Assert.That(error.Error, Does.Contain("ContentBlock payload is invalid"))
            | Ok _ -> Assert.Fail("Expected corrupt ContentBlock payload to be rejected.")
        }
