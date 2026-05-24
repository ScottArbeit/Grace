namespace Grace.Types.Tests

open FsCheck
open Grace.Shared
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type RabinChunkingSharedTests() =
    static member private PseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        let mutable state = 0x12345678u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    [<Test>]
    member _.PinnedSuiteExposesConstantsForClientImplementations() =
        Assert.That(RabinChunking.SuiteName, Is.EqualTo("grace-rabin-blake3-64k-v1"))
        Assert.That(RabinChunking.MinimumChunkSize, Is.EqualTo(8 * 1024))
        Assert.That(RabinChunking.TargetChunkSize, Is.EqualTo(64 * 1024))
        Assert.That(RabinChunking.MaximumChunkSize, Is.EqualTo(128 * 1024))
        Assert.That(RabinChunking.WindowSize, Is.EqualTo(64))
        Assert.That(RabinChunking.Polynomial, Is.EqualTo(0x3DA3358B4DC173UL))

    [<Test>]
    member _.EmptyPayloadHasNoChunks() = Assert.That(RabinChunking.chunkBytes Array.empty, Is.Empty)

    [<Test>]
    member _.PayloadBelowMinimumEmitsOneChunkWithItsBlake3Address() =
        let payload = RabinChunkingSharedTests.PseudoRandomBytes 4096
        let chunks = RabinChunking.chunkBytes payload

        Assert.That(chunks, Has.Length.EqualTo(1))
        Assert.That(chunks[0].Offset, Is.EqualTo(0L))
        Assert.That(chunks[0].Length, Is.EqualTo(payload.Length))
        Assert.That(chunks[0].Address, Is.EqualTo(ContentAddress.computeChunkAddress payload))

    [<Test>]
    member _.ForcedMaximumCutsWhenNoNaturalBoundaryAppears() =
        let payload = Array.zeroCreate<byte> ((128 * 1024) + 17)
        let chunks = RabinChunking.chunkBytes payload

        Assert.That((chunks |> Array.map (fun chunk -> chunk.Length)) = [| 128 * 1024; 17 |], Is.True)
        Assert.That((chunks |> Array.map (fun chunk -> chunk.Offset)) = [| 0L; 128L * 1024L |], Is.True)

    [<Test>]
    member _.SeededPayloadHasPinnedChunkBoundaries() =
        let payload = RabinChunkingSharedTests.PseudoRandomBytes 220000
        let chunks = RabinChunking.chunkBytes payload

        Assert.That((chunks |> Array.map (fun chunk -> chunk.Offset)) = [| 0L; 119720L; 128931L; 210902L |], Is.True)
        Assert.That((chunks |> Array.map (fun chunk -> chunk.Length)) = [| 119720; 9211; 81971; 9098 |], Is.True)

        let expectedAddresses =
            [|
                "112f66f6756ae61adf847406c2a9e435278c6d29814a537347bf690e99c4e15b"
                "455d9abeff18abb4a12a1af098191009a42e2a0bc313e525d40740d88e57bf91"
                "d7507a7baa204fcd27ef724c9657af25ae879f6bde31fc0007782e7546eaf0a2"
                "73c207c781be308ce9b38d173c3d09cf53cb2922f181ae6d1af214837df402e0"
            |]

        Assert.That((chunks |> Array.map (fun chunk -> chunk.Address)) = expectedAddresses, Is.True)

    [<Test>]
    member _.ChunkingIsDeterministic() =
        let payload = RabinChunkingSharedTests.PseudoRandomBytes 220000

        Assert.That(RabinChunking.chunkBytes payload = RabinChunking.chunkBytes payload, Is.True)

    [<Test>]
    member _.FsCheckPropertiesHoldForChunkCoverageAndSizing() =
        let property (NonNegativeInt requestedLength) =
            let length = Math.Min(requestedLength, 512 * 1024)
            let payload = RabinChunkingSharedTests.PseudoRandomBytes length
            let chunks = RabinChunking.chunkBytes payload

            let lengths = chunks |> Array.map (fun chunk -> chunk.Length)
            let offsets = chunks |> Array.map (fun chunk -> chunk.Offset)

            let reconstructed =
                chunks
                |> Array.collect (fun chunk -> payload[int chunk.Offset .. int chunk.Offset + chunk.Length - 1])

            let expectedOffsets =
                lengths
                |> Array.scan (fun offset length -> offset + int64 length) 0L
                |> Array.take chunks.Length

            (length = 0 && chunks.Length = 0
             || (chunks.Length > 0
                 && (lengths |> Array.sum) = length
                 && offsets = expectedOffsets
                 && reconstructed = payload
                 && (chunks
                     |> Array.forall (fun chunk ->
                         chunk.Length > 0
                         && chunk.Length <= RabinChunking.MaximumChunkSize))
                 && (chunks
                     |> Array.truncate (Math.Max(0, chunks.Length - 1))
                     |> Array.forall (fun chunk -> chunk.Length >= RabinChunking.MinimumChunkSize))))

        Check.QuickThrowOnFailure property
