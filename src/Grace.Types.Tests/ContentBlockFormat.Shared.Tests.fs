namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Types
open NUnit.Framework
open System
open System.Buffers.Binary
open System.Text

[<Parallelizable(ParallelScope.All)>]
type ContentBlockFormatSharedTests() =
    static member private Bytes(value: string) = Encoding.UTF8.GetBytes(value)

    static member private ExpectOk(result: Result<'T, ContentBlockFormat.ContentBlockFormatError>) =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected Ok but got {error}.")
            Unchecked.defaultof<'T>

    static member private RehashTrailerFooter(payload: byte array) =
        let footerStart = payload.Length - ContentBlockFormat.FooterLength
        let trailerLength = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(payload, footerStart, 4))
        let trailerStart = footerStart - int trailerLength
        let trailer = payload[trailerStart .. footerStart - 1]

        let trailerHash =
            ContentAddress.computeBlake3Hex trailer
            |> Convert.FromHexString

        Array.Copy(trailerHash, 0, payload, footerStart + 4, trailerHash.Length)

    [<Test>]
    member _.PayloadRoundtripsThroughCompactV1Format() =
        let alpha = ContentBlockFormatSharedTests.Bytes "alpha chunk"
        let beta = ContentBlockFormatSharedTests.Bytes "beta chunk"

        let encoded =
            ContentBlockFormat.encode [ ContentBlockFormat.createChunk 4096L alpha
                                        ContentBlockFormat.createChunk 12288L beta ]
            |> ContentBlockFormatSharedTests.ExpectOk

        let decoded =
            ContentBlockFormat.decode encoded.Payload
            |> ContentBlockFormatSharedTests.ExpectOk

        Assert.That(decoded.Address, Is.EqualTo(encoded.Address))
        Assert.That(decoded.Payload = Array.concat [ alpha; beta ], Is.True)
        Assert.That(decoded.Chunks, Has.Length.EqualTo(2))
        Assert.That(decoded.Chunks[0].PhysicalOffset, Is.EqualTo(4096L))
        Assert.That(decoded.Chunks[0].LogicalOffset, Is.EqualTo(0L))
        Assert.That(decoded.Chunks[0].Length, Is.EqualTo(alpha.Length))
        Assert.That(decoded.Chunks[0].Bytes = alpha, Is.True)
        Assert.That(decoded.Chunks[1].PhysicalOffset, Is.EqualTo(12288L))
        Assert.That(decoded.Chunks[1].LogicalOffset, Is.EqualTo(int64 alpha.Length))
        Assert.That(decoded.Chunks[1].Length, Is.EqualTo(beta.Length))
        Assert.That(decoded.Chunks[1].Bytes = beta, Is.True)

    [<Test>]
    member _.CorruptTrailerIsRejected() =
        let encoded =
            ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L (ContentBlockFormatSharedTests.Bytes "payload") ]
            |> ContentBlockFormatSharedTests.ExpectOk

        let corruptedPayload = Array.copy encoded.Payload

        let trailerByteIndex =
            corruptedPayload.Length
            - ContentBlockFormat.FooterLength
            - 1

        corruptedPayload[trailerByteIndex] <- corruptedPayload[trailerByteIndex] ^^^ 0x01uy

        match ContentBlockFormat.decode corruptedPayload with
        | Error ContentBlockFormat.TrailerChecksumMismatch -> Assert.Pass()
        | result -> Assert.Fail($"Expected TrailerChecksumMismatch but got {result}.")

    [<Test>]
    member _.ChunkAddressMismatchIsRejected() =
        let encoded =
            ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L (ContentBlockFormatSharedTests.Bytes "payload") ]
            |> ContentBlockFormatSharedTests.ExpectOk

        let corruptedPayload = Array.copy encoded.Payload
        corruptedPayload[0] <- corruptedPayload[0] ^^^ 0x01uy

        match ContentBlockFormat.decode corruptedPayload with
        | Error (ContentBlockFormat.ChunkAddressMismatch (0, _, _)) -> Assert.Pass()
        | result -> Assert.Fail($"Expected ChunkAddressMismatch for chunk 0 but got {result}.")

    [<Test>]
    member _.OversizedTrailerChunkCountIsRejectedWithoutThrowing() =
        let encoded =
            ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L (ContentBlockFormatSharedTests.Bytes "payload") ]
            |> ContentBlockFormatSharedTests.ExpectOk

        let corruptedPayload = Array.copy encoded.Payload

        let footerStart =
            corruptedPayload.Length
            - ContentBlockFormat.FooterLength

        let trailerLength = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(corruptedPayload, footerStart, 4))
        let trailerStart = footerStart - int trailerLength
        let chunkCountOffset = trailerStart + 12

        BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(corruptedPayload, chunkCountOffset, 4), UInt32.MaxValue)
        ContentBlockFormatSharedTests.RehashTrailerFooter corruptedPayload

        match ContentBlockFormat.decode corruptedPayload with
        | Error ContentBlockFormat.InvalidTrailer -> Assert.Pass()
        | result -> Assert.Fail($"Expected InvalidTrailer but got {result}.")

    [<Test>]
    member _.PhysicalOffsetsDoNotChangeContentBlockAddress() =
        let alpha = ContentBlockFormatSharedTests.Bytes "alpha chunk"
        let beta = ContentBlockFormatSharedTests.Bytes "beta chunk"

        let firstLayout =
            ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L alpha
                                        ContentBlockFormat.createChunk 4096L beta ]
            |> ContentBlockFormatSharedTests.ExpectOk

        let secondLayout =
            ContentBlockFormat.encode [ ContentBlockFormat.createChunk 1048576L alpha
                                        ContentBlockFormat.createChunk 2097152L beta ]
            |> ContentBlockFormatSharedTests.ExpectOk

        Assert.That(firstLayout.Address, Is.EqualTo(secondLayout.Address))
        Assert.That(ContentAddress.isValidAddress firstLayout.Address, Is.True)
