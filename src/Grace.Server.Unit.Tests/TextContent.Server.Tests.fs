namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Types.Common
open Grace.Types.TextContent
open NUnit.Framework
open System
open System.IO
open System.IO.Compression
open System.Text

/// Covers immutable work-item description text validation and retry identity behavior without external storage.
[<Parallelizable(ParallelScope.All)>]
type TextContentServerTests() =
    /// Verifies that Product V1 uses a documented default when the environment setting is absent.
    [<Test>]
    member _.MaximumCharactersDefaultsToProductV1Limit() =
        match TextContentStorage.parseMaximumCharacters null with
        | Ok maximum -> Assert.That(maximum, Is.EqualTo(65_536))
        | Error error -> Assert.Fail($"Expected the default limit, but got '{error}'.")

    /// Verifies that malformed and non-positive configured limits fail before storage is contacted.
    [<TestCase("0")>]
    [<TestCase("-1")>]
    [<TestCase("not-a-number")>]
    member _.MaximumCharactersRejectsInvalidConfiguration(configured: string) =
        match TextContentStorage.parseMaximumCharacters configured with
        | Ok value -> Assert.Fail($"Expected '{configured}' to be rejected, but got {value}.")
        | Error error -> Assert.That(error, Does.Contain("positive integer"))

    /// Verifies that Unicode scalar counting does not charge a supplementary-plane character twice.
    [<Test>]
    member _.UnicodeScalarCountingTreatsSupplementaryCharactersAsOne() = Assert.That(TextContentStorage.countUnicodeScalars "A😀", Is.EqualTo(2))

    /// Verifies the inclusive Product V1 character boundary and rejection of the next Unicode scalar.
    [<Test>]
    member _.TextValidationAcceptsTheConfiguredBoundaryOnly() =
        match TextContentStorage.validateTextForMaximum 65_536 (String.replicate 65_536 "😀") with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected the configured boundary to be accepted, but got '{error}'.")

        match TextContentStorage.validateTextForMaximum 65_536 (String.replicate 65_537 "😀") with
        | Ok () -> Assert.Fail("Expected the first scalar beyond the configured maximum to be rejected.")
        | Error error -> Assert.That(error, Does.Contain("65536"))

    /// Verifies compressed storage preserves the exact Unicode UTF-8 bytes, BLAKE3, and byte length.
    [<Test>]
    member _.CompressedTextVerifiesUncompressedUtf8Integrity() =
        let text = "A😀\r\nRésumé"
        let bytes = UTF8Encoding(false, true).GetBytes(text)

        let reference = { TextContentId = Guid.NewGuid(); Blake3Hash = Blake3Hash(ContentAddress.computeBlake3Hex bytes); Utf8ByteLength = int64 bytes.Length }

        use compressed = new MemoryStream(TextContentStorage.compressText text)

        match TextContentStorage.verifyCompressedText 65_536 reference compressed with
        | Ok actual -> Assert.That(actual, Is.EqualTo(text))
        | Error error -> Assert.Fail($"Expected compressed text to verify, but got '{error}'.")

    /// Verifies corrupt bytes, invalid UTF-8, mismatched hashes, and mismatched lengths remain unreadable.
    [<Test>]
    member _.CompressedTextRejectsCorruptionAndIntegrityMismatches() =
        let text = "verified"
        let bytes = UTF8Encoding(false, true).GetBytes(text)

        let reference = { TextContentId = Guid.NewGuid(); Blake3Hash = Blake3Hash(ContentAddress.computeBlake3Hex bytes); Utf8ByteLength = int64 bytes.Length }

        use corrupt = new MemoryStream([| 0uy; 1uy; 2uy |])

        match TextContentStorage.verifyCompressedText 65_536 reference corrupt with
        | Ok value -> Assert.Fail($"Expected corrupt GZip to fail, but got '{value}'.")
        | Error error -> Assert.That(error, Does.Contain("GZip"))

        use invalidUtf8 = new MemoryStream()

        use writer = new GZipStream(invalidUtf8, CompressionLevel.SmallestSize, true)
        writer.Write([| 0xC3uy; 0x28uy |], 0, 2)
        writer.Dispose()
        invalidUtf8.Position <- 0L

        let invalidUtf8Reference =
            { reference with
                Blake3Hash =
                    Blake3Hash(
                        ContentAddress.computeBlake3Hex [| 0xC3uy
                                                           0x28uy |]
                    )
                Utf8ByteLength = 2L
            }

        match TextContentStorage.verifyCompressedText 65_536 invalidUtf8Reference invalidUtf8 with
        | Ok value -> Assert.Fail($"Expected invalid UTF-8 to fail, but got '{value}'.")
        | Error error -> Assert.That(error, Does.Contain("UTF-8"))

        use hashMismatch = new MemoryStream(TextContentStorage.compressText text)

        match TextContentStorage.verifyCompressedText 65_536 { reference with Blake3Hash = Blake3Hash(String.replicate 64 "0") } hashMismatch with
        | Ok value -> Assert.Fail($"Expected hash mismatch to fail, but got '{value}'.")
        | Error error -> Assert.That(error, Does.Contain("hash"))

        use lengthMismatch = new MemoryStream(TextContentStorage.compressText text)

        match TextContentStorage.verifyCompressedText 65_536 { reference with Utf8ByteLength = reference.Utf8ByteLength + 1L } lengthMismatch with
        | Ok value -> Assert.Fail($"Expected length mismatch to fail, but got '{value}'.")
        | Error error -> Assert.That(error, Does.Contain("length"))

    /// Verifies that retry identity is stable for the same normalized correlation and separates each text purpose.
    [<Test>]
    member _.TextContentRetryIdentityIsStableAndPurposeSeparated() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let firstDescriptionId, firstContentId = TextContentStorage.createIds repositoryId workItemId "corr-description"
        let replayDescriptionId, replayContentId = TextContentStorage.createIds repositoryId workItemId " CoRR-DeScRiPtIoN "

        Assert.That(replayDescriptionId, Is.EqualTo(firstDescriptionId))
        Assert.That(replayContentId, Is.EqualTo(firstContentId))
        Assert.That(firstDescriptionId, Is.Not.EqualTo(firstContentId))

        let nextDescriptionId, nextContentId = TextContentStorage.createIds repositoryId workItemId "corr-description-next"

        Assert.That(nextDescriptionId, Is.Not.EqualTo(firstDescriptionId))
        Assert.That(nextContentId, Is.Not.EqualTo(firstContentId))
