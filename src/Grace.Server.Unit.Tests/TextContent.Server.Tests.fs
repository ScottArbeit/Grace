namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Types.Common
open Grace.Types.TextContent
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text

/// Covers immutable work-item description text validation and retry identity behavior without external storage.
[<Parallelizable(ParallelScope.All)>]
type TextContentServerTests() =
    /// Rejects absent, unsupported and noncanonical original-content evidence without accepting a retry.
    [<TestCase("grace_textcontent_format", "")>]
    [<TestCase("grace_textcontent_format", "2")>]
    [<TestCase("grace_utf8_byte_length", "")>]
    [<TestCase("grace_utf8_byte_length", "06")>]
    [<TestCase("grace_utf8_byte_length", "+6")>]
    [<TestCase("grace_utf8_byte_length", "6.0")>]
    [<TestCase("grace_utf8_byte_length", "-6")>]
    [<TestCase("grace_utf8_byte_length", "7")>]
    [<TestCase("grace_utf8_byte_length", "9223372036854775808")>]
    [<TestCase("grace_blake3_hash", "")>]
    [<TestCase("grace_blake3_hash", "bad-hash")>]
    [<TestCase("grace_blake3_hash", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")>]
    member _.RetryMetadataRejectsMissingOrConflictingEvidence(key: string, value: string) =
        let reference =
            (TextContentStorage.createDescription (Guid.NewGuid()) (Guid.NewGuid()) "metadata" "é😀")
                .TextContent
                .Value

        let metadata = Dictionary<string, string>()
        metadata.Add("grace_textcontent_format", "1")
        metadata.Add("grace_utf8_byte_length", "6")
        metadata.Add("grace_blake3_hash", reference.Blake3Hash)
        Assert.That(TextContentStorage.verifyMetadata reference metadata, Is.EqualTo(Ok(): Result<unit, string>))

        if value = "" then metadata.Remove(key) |> ignore else metadata[key] <- value

        let before =
            metadata
            |> Seq.map (fun pair -> pair.Key, pair.Value)
            |> Seq.toArray

        Assert.That(TextContentStorage.verifyMetadata reference metadata, Is.EqualTo(Error "Text content metadata verification failed.": Result<unit, string>))

        Assert.That(
            metadata
            |> Seq.map (fun pair -> pair.Key, pair.Value)
            |> Seq.toArray,
            Is.EqualTo(box before)
        )

    /// Keeps custom Unicode scalar limits inclusive while byte verification uses original multibyte text.
    [<Test>]
    member _.CustomCharacterBoundaryPreservesUtf8Evidence() =
        let text = String.replicate 70_000 "😀"

        let reference =
            (TextContentStorage.createDescription (Guid.NewGuid()) (Guid.NewGuid()) "custom-limit" text)
                .TextContent
                .Value

        Assert.That(reference.Utf8ByteLength, Is.EqualTo(280_000L))
        Assert.That(TextContentStorage.validateTextForMaximum 70_000 text, Is.EqualTo(Ok(): Result<unit, string>))

        Assert.That(
            TextContentStorage.validateTextForMaximum 69_999 text
            |> Result.isError,
            Is.True
        )

        use compressed = new MemoryStream(TextContentStorage.compressText text)
        Assert.That(TextContentStorage.verifyCompressedText 70_000 reference compressed, Is.EqualTo(Ok text: Result<string, string>))

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

    /// Verifies retry identity uses exact actor correlation text, including case and whitespace, while separating purposes.
    [<Test>]
    member _.TextContentRetryIdentityIsStableAndPurposeSeparated() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let firstDescriptionId, firstContentId = TextContentStorage.createIds repositoryId workItemId "corr-description"
        let replayDescriptionId, replayContentId = TextContentStorage.createIds repositoryId workItemId "corr-description"
        let caseDistinctDescriptionId, caseDistinctContentId = TextContentStorage.createIds repositoryId workItemId "Corr-Description"
        let whitespaceDistinctDescriptionId, whitespaceDistinctContentId = TextContentStorage.createIds repositoryId workItemId " corr-description "

        Assert.That(replayDescriptionId, Is.EqualTo(firstDescriptionId))
        Assert.That(replayContentId, Is.EqualTo(firstContentId))
        Assert.That(caseDistinctDescriptionId, Is.Not.EqualTo(firstDescriptionId))
        Assert.That(caseDistinctContentId, Is.Not.EqualTo(firstContentId))
        Assert.That(whitespaceDistinctDescriptionId, Is.Not.EqualTo(firstDescriptionId))
        Assert.That(whitespaceDistinctContentId, Is.Not.EqualTo(firstContentId))
        Assert.That(firstDescriptionId, Is.Not.EqualTo(firstContentId))

        let nextDescriptionId, nextContentId = TextContentStorage.createIds repositoryId workItemId "corr-description-next"

        Assert.That(nextDescriptionId, Is.Not.EqualTo(firstDescriptionId))
        Assert.That(nextContentId, Is.Not.EqualTo(firstContentId))
