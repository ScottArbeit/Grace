namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open Grace.Types.Repository
open MessagePack
open NUnit.Framework
open System.IO
open System.IO.Compression

[<MessagePackObject>]
type LegacyFileVersion =
    {
        [<Key(0)>]
        Class: string
        [<Key(1)>]
        RelativePath: RelativePath
        [<Key(2)>]
        Sha256Hash: Sha256Hash
        [<Key(3)>]
        IsBinary: bool
        [<Key(4)>]
        Size: int64
        [<Key(5)>]
        CreatedAt: NodaTime.Instant
        [<Key(6)>]
        BlobUri: string
    }

module ManifestEligibilityTestData =
    let thresholdBytes = 1024 * 1024

    let private gzipSize (content: byte array) =
        use compressed = new MemoryStream()

        use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)

        gzipStream.Write(content, 0, content.Length)
        gzipStream.Dispose()
        compressed.Length

    let private pseudoRandomTextBytes length =
        let mutable state = 0x6C8E9CF5u

        Array.init length (fun _ ->
            state <- (state * 1664525u) + 1013904223u
            byte ((state >>> 16) % 255u + 1u))

    let binaryBelowThreshold =
        let bytes = Array.create (thresholdBytes - 1) 0xFFuy
        bytes[0] <- 0uy
        bytes

    let binaryAtThreshold =
        let bytes = Array.create thresholdBytes 0xFFuy
        bytes[0] <- 0uy
        bytes

    let textCompressesBelowThreshold = Array.create thresholdBytes (byte 'a')

    let textCompressesAtOrAboveThreshold =
        let bytes = pseudoRandomTextBytes thresholdBytes
        Assert.That(gzipSize bytes, Is.GreaterThanOrEqualTo(int64 thresholdBytes))
        bytes

    let nulAtLastScannedByte =
        let bytes = Array.create (thresholdBytes - 1) 0xFFuy
        bytes[8191] <- 0uy
        bytes

    let nulAfterScanWindow =
        let bytes = Array.create thresholdBytes (byte 'a')
        bytes[8192] <- 0uy
        bytes

    let eligibilityCases =
        [|
            "binary below threshold stays whole-file", binaryBelowThreshold, FileContentReferenceType.WholeFileContent
            "binary at threshold becomes manifest-backed", binaryAtThreshold, FileContentReferenceType.FileManifest
            "text at uncompressed threshold stays whole-file when Grace gzip size is below threshold",
            textCompressesBelowThreshold,
            FileContentReferenceType.WholeFileContent
            "text becomes manifest-backed when Grace gzip size reaches threshold", textCompressesAtOrAboveThreshold, FileContentReferenceType.FileManifest
        |]

    let binaryDetectionCases =
        [|
            "NUL at byte 8191 is binary", nulAtLastScannedByte, true
            "NUL at byte 8192 is outside the scan window", nulAfterScanWindow, false
        |]

[<Parallelizable(ParallelScope.All)>]
type TypesTypesTests() =
    [<Test>]
    member _.ManifestEligibilityPolicyDefaultUsesOneMiBThresholdAndEightKiBScanWindow() =
        let policy = ManifestEligibilityPolicy.Default

        Assert.That(policy.ThresholdBytes, Is.EqualTo(int64 ManifestEligibilityTestData.thresholdBytes))
        Assert.That(policy.BinaryScanBytes, Is.EqualTo(8 * 1024))
        Assert.That(RepositoryDto.Default.ManifestEligibilityPolicy, Is.EqualTo(policy))

    [<Test>]
    member _.ManifestEligibilityEvaluatorUsesThresholdAndCompressionPolicyTable() =
        let policy = ManifestEligibilityPolicy.Default

        for caseName, content, expectedReferenceType in ManifestEligibilityTestData.eligibilityCases do
            let actualReferenceType = ManifestEligibility.evaluateContentReferenceType policy content

            Assert.That(actualReferenceType, Is.EqualTo(expectedReferenceType), caseName)

    [<Test>]
    member _.ManifestEligibilityBinaryDetectorScansOnlyTheFirstEightKiBForNul() =
        let policy = ManifestEligibilityPolicy.Default

        for caseName, content, expectedIsBinary in ManifestEligibilityTestData.binaryDetectionCases do
            let actualIsBinary = ManifestEligibility.detectBinaryContent policy content

            Assert.That(actualIsBinary, Is.EqualTo(expectedIsBinary), caseName)

    [<Test>]
    member _.FileVersionCreateDefaultsContentReferenceToWholeFileContent() =
        let fileVersion = FileVersion.Create "src/app.fs" "abc123" "https://example.test/blob" false 123L

        Assert.That(fileVersion.ContentReference, Is.EqualTo(FileContentReference.WholeFileContent))

    [<Test>]
    member _.FileVersionJsonWithoutContentReferenceDefaultsToWholeFileContent() =
        let legacyJson =
            """
{
  "Class": "FileVersion",
  "RelativePath": "src/app.fs",
  "Sha256Hash": "abc123",
  "IsBinary": false,
  "Size": 123,
  "CreatedAt": "2025-01-01T00:00:00Z",
  "BlobUri": "https://example.test/blob"
}
"""

        let fileVersion = deserialize<FileVersion> legacyJson

        Assert.That(fileVersion.ContentReference, Is.EqualTo(FileContentReference.WholeFileContent))

    [<Test>]
    member _.FileVersionMessagePackWithoutContentReferenceDefaultsToWholeFileContent() =
        let legacyFileVersion =
            {
                Class = "FileVersion"
                RelativePath = "src/app.fs"
                Sha256Hash = "abc123"
                IsBinary = false
                Size = 123L
                CreatedAt = NodaTime.Instant.FromUtc(2025, 1, 1, 0, 0)
                BlobUri = "https://example.test/blob"
            }

        let bytes = MessagePackSerializer.Serialize(legacyFileVersion, Constants.messagePackSerializerOptions)
        let fileVersion = MessagePackSerializer.Deserialize<FileVersion>(bytes, Constants.messagePackSerializerOptions)

        Assert.That(fileVersion.ContentReference, Is.EqualTo(FileContentReference.WholeFileContent))

    [<Test>]
    member _.FileVersionManifestContentReferenceRoundTripsThroughJsonAndMessagePack() =
        let manifest =
            FileManifest.Create(
                ManifestAddress "manifest-blake3-abc123",
                4096L,
                [
                    ContentBlock.Create(ContentBlockAddress "block-blake3-def456", 0L, 4096L)
                ]
            )

        let fileVersion = FileVersion.Create "src/large.bin" "abc123" "https://example.test/blob" true 4096L
        fileVersion.CreatedAt <- NodaTime.Instant.FromUtc(2025, 1, 1, 0, 0)
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest

        let jsonRoundTrip = deserialize<FileVersion> (serialize fileVersion)
        let messagePackBytes = MessagePackSerializer.Serialize(fileVersion, Constants.messagePackSerializerOptions)
        let messagePackRoundTrip = MessagePackSerializer.Deserialize<FileVersion>(messagePackBytes, Constants.messagePackSerializerOptions)

        Assert.That(jsonRoundTrip.ContentReference, Is.EqualTo(FileContentReference.FileManifest manifest))
        Assert.That(messagePackRoundTrip.ContentReference, Is.EqualTo(FileContentReference.FileManifest manifest))
        Assert.That(jsonRoundTrip.ContentReference, Is.EqualTo(fileVersion.ContentReference))
        Assert.That(jsonRoundTrip.ContentReference = fileVersion.ContentReference, Is.True)
        Assert.That(jsonRoundTrip.Class, Is.EqualTo(fileVersion.Class))
        Assert.That(jsonRoundTrip.RelativePath, Is.EqualTo(fileVersion.RelativePath))
        Assert.That(jsonRoundTrip.Sha256Hash, Is.EqualTo(fileVersion.Sha256Hash))
        Assert.That(jsonRoundTrip.IsBinary, Is.EqualTo(fileVersion.IsBinary))
        Assert.That(jsonRoundTrip.Size, Is.EqualTo(fileVersion.Size))
        Assert.That(jsonRoundTrip.CreatedAt, Is.EqualTo(fileVersion.CreatedAt))
        Assert.That(jsonRoundTrip.BlobUri, Is.EqualTo(fileVersion.BlobUri))
        Assert.That(jsonRoundTrip.Equals(fileVersion), Is.True)
        Assert.That(messagePackRoundTrip.Equals(fileVersion), Is.True)
        Assert.That(jsonRoundTrip.GetHashCode(), Is.EqualTo(fileVersion.GetHashCode()))
        Assert.That(messagePackRoundTrip.GetHashCode(), Is.EqualTo(fileVersion.GetHashCode()))
