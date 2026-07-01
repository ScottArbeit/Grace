namespace Grace.Types.Tests

open FsCheck
open Grace.Shared
open NUnit.Framework
open System

/// Contains tests covering rabin chunking shared behavior.
[<Parallelizable(ParallelScope.All)>]
type RabinChunkingSharedTests() =
    /// Verifies that pseudo random bytes.
    static member private PseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        /// Tracks state changes so this scenario can assert the resulting side effect explicitly.
        let mutable state = 0x12345678u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    /// Verifies that pinned suite exposes constants for client implementations.
    [<Test>]
    member _.PinnedSuiteExposesConstantsForClientImplementations() =
        Assert.That(RabinChunking.SuiteName, Is.EqualTo("grace-rabin-blake3-64k-v1"))
        Assert.That(RabinChunking.MinimumChunkSize, Is.EqualTo(8 * 1024))
        Assert.That(RabinChunking.TargetChunkSize, Is.EqualTo(64 * 1024))
        Assert.That(RabinChunking.MaximumChunkSize, Is.EqualTo(128 * 1024))
        Assert.That(RabinChunking.WindowSize, Is.EqualTo(64))
        Assert.That(RabinChunking.Polynomial, Is.EqualTo(0x3DA3358B4DC173UL))

    /// Verifies that empty payload has no chunks.
    [<Test>]
    member _.EmptyPayloadHasNoChunks() = Assert.That(RabinChunking.chunkBytes Array.empty, Is.Empty)

    /// Verifies that payload below minimum emits one chunk with its blake3 address.
    [<Test>]
    member _.PayloadBelowMinimumEmitsOneChunkWithItsBlake3Address() =
        let payload = RabinChunkingSharedTests.PseudoRandomBytes 4096
        let chunks = RabinChunking.chunkBytes payload

        Assert.That(chunks, Has.Length.EqualTo(1))
        Assert.That(chunks[0].Offset, Is.EqualTo(0L))
        Assert.That(chunks[0].Length, Is.EqualTo(payload.Length))
        Assert.That(chunks[0].Address, Is.EqualTo(ContentAddress.computeChunkAddress payload))

    /// Verifies that forced maximum cuts when no natural boundary appears.
    [<Test>]
    member _.ForcedMaximumCutsWhenNoNaturalBoundaryAppears() =
        let payload = Array.zeroCreate<byte> ((128 * 1024) + 17)
        let chunks = RabinChunking.chunkBytes payload

        Assert.That((chunks |> Array.map (fun chunk -> chunk.Length)) = [| 128 * 1024; 17 |], Is.True)
        Assert.That((chunks |> Array.map (fun chunk -> chunk.Offset)) = [| 0L; 128L * 1024L |], Is.True)

    /// Verifies that seeded payload has pinned chunk boundaries.
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

    /// Verifies that chunking is deterministic.
    [<Test>]
    member _.ChunkingIsDeterministic() =
        let payload = RabinChunkingSharedTests.PseudoRandomBytes 220000

        Assert.That(RabinChunking.chunkBytes payload = RabinChunking.chunkBytes payload, Is.True)

    /// Verifies that fs check properties hold for chunk coverage and sizing.
    [<Test>]
    member _.FsCheckPropertiesHoldForChunkCoverageAndSizing() =
        /// Defines the property assertion used to explore generated inputs for the types rabin Chunking invariant.
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
