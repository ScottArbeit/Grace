namespace Grace.CLI.Tests

open Grace.SDK
open Grace.Shared
open Grace.Types.Types
open NUnit.Framework
open System
open System.IO
open System.Text

[<Parallelizable(ParallelScope.All)>]
type LocalPlannerSdkTests() =
    static member private PseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        let mutable state = 0x12345678u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    static member private BinaryPolicy thresholdBytes = { ManifestEligibilityPolicy.Default with ThresholdBytes = thresholdBytes; BinaryScanBytes = 16 }

    [<Test>]
    member _.FallbackPlanAlwaysCarriesTheOriginalBytes() =
        let payload = Encoding.UTF8.GetBytes("small source file")
        let options = { LocalPlanner.Options.Default with EligibilityPolicy = LocalPlannerSdkTests.BinaryPolicy 1024L }

        let plan = LocalPlanner.analyzeBytes options payload

        Assert.That(plan.ReferenceType, Is.EqualTo(FileContentReferenceType.WholeFileContent))
        Assert.That(plan.FileContentHash, Is.EqualTo(FileContentHash(ContentAddress.computeBlake3Hex payload)))
        Assert.That(plan.FallbackUpload, Is.Not.Null)
        Assert.That(plan.FallbackUpload.Value.Bytes = payload, Is.True)
        Assert.That(plan.Chunks, Is.Empty)
        Assert.That(plan.Blocks, Is.Empty)
        Assert.That(plan.ContentBlockUploads, Is.Empty)

    [<Test>]
    member _.EmptyPayloadFallsBackEvenWhenThresholdWouldAllowManifest() =
        let payload = Array.empty<byte>
        let options = { LocalPlanner.Options.Default with EligibilityPolicy = LocalPlannerSdkTests.BinaryPolicy 0L }

        let plan = LocalPlanner.analyzeBytes options payload

        Assert.That(plan.ReferenceType, Is.EqualTo(FileContentReferenceType.WholeFileContent))
        Assert.That(plan.ManifestAddress, Is.EqualTo(None))
        Assert.That(plan.FallbackUpload, Is.Not.Null)
        Assert.That(plan.FallbackUpload.Value.Bytes = payload, Is.True)
        Assert.That(plan.Chunks, Is.Empty)
        Assert.That(plan.Blocks, Is.Empty)
        Assert.That(plan.ContentBlockUploads, Is.Empty)

    [<Test>]
    member _.EligibleBinaryPayloadPlansDeterministicChunksBlocksAndManifest() =
        let payload = LocalPlannerSdkTests.PseudoRandomBytes 220000
        payload[0] <- 0uy

        let options = { LocalPlanner.Options.Default with EligibilityPolicy = LocalPlannerSdkTests.BinaryPolicy 1024L }

        let firstPlan = LocalPlanner.analyzeBytes options payload
        let secondPlan = LocalPlanner.analyzeBytes options payload

        Assert.That(firstPlan.ReferenceType, Is.EqualTo(FileContentReferenceType.FileManifest))
        Assert.That(firstPlan.FileContentHash, Is.EqualTo(secondPlan.FileContentHash))
        Assert.That(firstPlan.ManifestAddress, Is.EqualTo(secondPlan.ManifestAddress))
        Assert.That(firstPlan.FallbackUpload, Is.EqualTo(None))
        Assert.That(firstPlan.ChunkingSuiteId, Is.EqualTo(ChunkingSuiteId RabinChunking.SuiteName))

        Assert.That(
            (firstPlan.Chunks
             |> Array.map (fun chunk -> chunk.Address)) =
                (secondPlan.Chunks
                 |> Array.map (fun chunk -> chunk.Address)),
            Is.True
        )

        Assert.That(
            (firstPlan.Blocks
             |> Array.map (fun block -> block.Address)) =
                (secondPlan.Blocks
                 |> Array.map (fun block -> block.Address)),
            Is.True
        )

        let expectedChunks = RabinChunking.chunkBytes payload

        Assert.That(
            (firstPlan.Chunks
             |> Array.map (fun chunk -> chunk.Offset)) =
                (expectedChunks
                 |> Array.map (fun chunk -> chunk.Offset)),
            Is.True
        )

        Assert.That(
            (firstPlan.Chunks
             |> Array.map (fun chunk -> chunk.Length)) =
                (expectedChunks
                 |> Array.map (fun chunk -> chunk.Length)),
            Is.True
        )

        Assert.That(
            (firstPlan.Chunks
             |> Array.map (fun chunk -> chunk.Address)) =
                (expectedChunks
                 |> Array.map (fun chunk -> chunk.Address)),
            Is.True
        )

        Assert.That(
            (firstPlan.Blocks
             |> Array.map (fun block -> block.Offset)) =
                (expectedChunks
                 |> Array.map (fun chunk -> chunk.Offset)),
            Is.True
        )

        Assert.That(
            (firstPlan.Blocks
             |> Array.map (fun block -> block.Size)) =
                (expectedChunks
                 |> Array.map (fun chunk -> int64 chunk.Length)),
            Is.True
        )

    [<Test>]
    member _.DuplicateLocalChunksProduceOneUploadPlanAndOneKeyChunk() =
        let payload = Array.zeroCreate<byte> (RabinChunking.MaximumChunkSize * 2)
        let options = { LocalPlanner.Options.Default with EligibilityPolicy = LocalPlannerSdkTests.BinaryPolicy 1024L }

        let plan = LocalPlanner.analyzeBytes options payload

        Assert.That(plan.ReferenceType, Is.EqualTo(FileContentReferenceType.FileManifest))
        Assert.That(plan.Chunks, Has.Length.EqualTo(2))
        Assert.That(plan.Chunks[0].Address, Is.EqualTo(plan.Chunks[1].Address))
        Assert.That(plan.Chunks[0].IsKeyChunk, Is.True)
        Assert.That(plan.Chunks[1].IsKeyChunk, Is.False)
        Assert.That(plan.KeyChunks, Has.Length.EqualTo(1))
        Assert.That(plan.KeyChunks[0].ChunkIndex, Is.EqualTo(0))
        Assert.That(plan.ContentBlockUploads, Has.Length.EqualTo(1))
        Assert.That(plan.ContentBlockUploads[0].BlockAddress, Is.EqualTo(plan.Blocks[0].Address))
        Assert.That(plan.Blocks[0].Address, Is.EqualTo(plan.Blocks[1].Address))

    [<Test>]
    member _.AnalyzeFileUsesFileBytesWithoutServerState() =
        let tempPath = Path.Combine(Path.GetTempPath(), $"grace-local-planner-{Guid.NewGuid():N}.bin")
        let payload = LocalPlannerSdkTests.PseudoRandomBytes 160000
        payload[0] <- 0uy

        try
            File.WriteAllBytes(tempPath, payload)

            let options = { LocalPlanner.Options.Default with EligibilityPolicy = LocalPlannerSdkTests.BinaryPolicy 1024L }

            let plan = LocalPlanner.analyzeFile options tempPath

            Assert.That(plan.ExpectedSize, Is.EqualTo(int64 payload.Length))
            Assert.That(plan.FileContentHash, Is.EqualTo(FileContentHash(ContentAddress.computeBlake3Hex payload)))
            Assert.That(plan.ReferenceType, Is.EqualTo(FileContentReferenceType.FileManifest))
            Assert.That(plan.ContentBlockUploads, Is.Not.Empty)
        finally
            if File.Exists(tempPath) then File.Delete(tempPath)
