namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Repository
open MessagePack
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression

/// Contains tests covering legacy file version behavior.
[<MessagePackObject>]
type LegacyFileVersion =
    {
        /// Stores the  value.
        [<Key(0)>]
        Class: string
        /// Stores the  value.
        [<Key(1)>]
        RelativePath: RelativePath
        /// Stores the  value.
        [<Key(2)>]
        Sha256Hash: Sha256Hash
        /// Stores the  value.
        [<Key(3)>]
        IsBinary: bool
        /// Stores the  value.
        [<Key(4)>]
        Size: int64
        /// Stores the  value.
        [<Key(5)>]
        CreatedAt: NodaTime.Instant
        /// Stores the  value.
        [<Key(6)>]
        BlobUri: string
    }

/// Contains hash alias test data helpers.
module HashAliasTestData =
    let sha256Hash: Sha256Hash = Sha256Hash "67a1790dca55b8803ad024ee28f616a284df5dd7b8ba5f68b4b252a5e925af79"
    let blake3Hash: Blake3Hash = Blake3Hash "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"

/// Contains manifest eligibility test data helpers.
module ManifestEligibilityTestData =
    let thresholdBytes = 1024 * 1024

    /// Exercises gzip size coverage for the types common contract.
    let private gzipSize (content: byte array) =
        use compressed = new MemoryStream()

        use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)

        gzipStream.Write(content, 0, content.Length)
        gzipStream.Dispose()
        compressed.Length

    /// Exercises pseudo random text bytes coverage for the types common contract.
    let private pseudoRandomTextBytes length =
        /// Tracks state changes so this scenario can assert the resulting side effect explicitly.
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

/// Contains tests covering common types behavior.
[<Parallelizable(ParallelScope.All)>]
type CommonTypesTests() =
    /// Exercises message pack key for coverage for the types common contract.
    let messagePackKeyFor (targetType: Type) propertyName =
        let property = targetType.GetProperty(propertyName)
        let keyAttribute = Attribute.GetCustomAttribute(property, typeof<KeyAttribute>) :?> KeyAttribute
        keyAttribute.IntKey

    /// Verifies that blake3 hash alias exists beside sha256 hash.
    [<Test>]
    member _.Blake3HashAliasExistsBesideSha256Hash() =
        Assert.That(HashAliasTestData.blake3Hash, Is.EqualTo("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"))
        Assert.That(HashAliasTestData.sha256Hash, Is.EqualTo("67a1790dca55b8803ad024ee28f616a284df5dd7b8ba5f68b4b252a5e925af79"))

    /// Verifies that version hash fields use distinct message pack keys.
    [<Test>]
    member _.VersionHashFieldsUseDistinctMessagePackKeys() =
        Assert.That(messagePackKeyFor typeof<FileVersion> "Sha256Hash", Is.EqualTo(2))
        Assert.That(messagePackKeyFor typeof<FileVersion> "Blake3Hash", Is.EqualTo(8))
        Assert.That(messagePackKeyFor typeof<LocalFileVersion> "Sha256Hash", Is.EqualTo(2))
        Assert.That(messagePackKeyFor typeof<LocalFileVersion> "Blake3Hash", Is.EqualTo(8))
        Assert.That(messagePackKeyFor typeof<DirectoryVersion> "Sha256Hash", Is.EqualTo(6))
        Assert.That(messagePackKeyFor typeof<DirectoryVersion> "Blake3Hash", Is.EqualTo(12))
        Assert.That(messagePackKeyFor typeof<LocalDirectoryVersion> "Sha256Hash", Is.EqualTo(6))
        Assert.That(messagePackKeyFor typeof<LocalDirectoryVersion> "Blake3Hash", Is.EqualTo(12))

    /// Verifies that manifest eligibility policy default uses one mi b threshold and eight ki b scan window.
    [<Test>]
    member _.ManifestEligibilityPolicyDefaultUsesOneMiBThresholdAndEightKiBScanWindow() =
        let policy = ManifestEligibilityPolicy.Default

        Assert.That(policy.ThresholdBytes, Is.EqualTo(int64 ManifestEligibilityTestData.thresholdBytes))
        Assert.That(policy.BinaryScanBytes, Is.EqualTo(8 * 1024))
        Assert.That(RepositoryDto.Default.ManifestEligibilityPolicy, Is.EqualTo(policy))

    /// Verifies that manifest eligibility evaluator uses threshold and compression policy table.
    [<Test>]
    member _.ManifestEligibilityEvaluatorUsesThresholdAndCompressionPolicyTable() =
        let policy = ManifestEligibilityPolicy.Default

        for caseName, content, expectedReferenceType in ManifestEligibilityTestData.eligibilityCases do
            let actualReferenceType = ManifestEligibility.evaluateContentReferenceType policy content

            Assert.That(actualReferenceType, Is.EqualTo(expectedReferenceType), caseName)

    /// Verifies that manifest eligibility binary detector scans only the first eight ki b for nul.
    [<Test>]
    member _.ManifestEligibilityBinaryDetectorScansOnlyTheFirstEightKiBForNul() =
        let policy = ManifestEligibilityPolicy.Default

        for caseName, content, expectedIsBinary in ManifestEligibilityTestData.binaryDetectionCases do
            let actualIsBinary = ManifestEligibility.detectBinaryContent policy content

            Assert.That(actualIsBinary, Is.EqualTo(expectedIsBinary), caseName)

    /// Verifies that file version create defaults content reference to whole file content.
    [<Test>]
    member _.FileVersionCreateDefaultsContentReferenceToWholeFileContent() =
        let fileVersion = FileVersion.Create "src/app.fs" "abc123" "https://example.test/blob" false 123L

        Assert.That(fileVersion.ContentReference, Is.EqualTo(FileContentReference.WholeFileContent))
        Assert.That(fileVersion.Sha256Hash, Is.EqualTo(Sha256Hash "abc123"))
        Assert.That(fileVersion.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))

    /// Verifies that file version create populates both hash fields.
    [<Test>]
    member _.FileVersionCreatePopulatesBothHashFields() =
        let fileVersion =
            FileVersion.CreateWithHashes "src/app.fs" (Sha256Hash "sha256-abc123") (Blake3Hash "blake3-def456") "https://example.test/blob" false 123L

        Assert.That(fileVersion.Sha256Hash, Is.EqualTo(Sha256Hash "sha256-abc123"))
        Assert.That(fileVersion.Blake3Hash, Is.EqualTo(Blake3Hash "blake3-def456"))
        Assert.That(fileVersion.ContentReference, Is.EqualTo(FileContentReference.WholeFileContent))

    /// Verifies that file version default uses empty blake3 scaffold value.
    [<Test>]
    member _.FileVersionDefaultUsesEmptyBlake3ScaffoldValue() =
        Assert.That(FileVersion.Default.Sha256Hash, Is.EqualTo(Sha256Hash String.Empty))
        Assert.That(FileVersion.Default.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
        Assert.That(FileVersion.Default.Blake3Hash, Is.Not.EqualTo(HashAliasTestData.blake3Hash))

    /// Verifies that file version json without content reference defaults to whole file content.
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
        Assert.That(fileVersion.Sha256Hash, Is.EqualTo(Sha256Hash "abc123"))
        Assert.That(fileVersion.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))

    /// Verifies that file version message pack without content reference defaults to whole file content.
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
        Assert.That(fileVersion.Sha256Hash, Is.EqualTo(Sha256Hash "abc123"))
        Assert.That(fileVersion.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))

    /// Verifies that directory version json without blake3 hash defaults to empty scaffold value.
    [<Test>]
    member _.DirectoryVersionJsonWithoutBlake3HashDefaultsToEmptyScaffoldValue() =
        let legacyJson =
            """
{
  "Class": "DirectoryVersion",
  "DirectoryVersionId": "11111111-1111-1111-1111-111111111111",
  "OwnerId": "22222222-2222-2222-2222-222222222222",
  "OrganizationId": "33333333-3333-3333-3333-333333333333",
  "RepositoryId": "44444444-4444-4444-4444-444444444444",
  "RelativePath": "src",
  "Sha256Hash": "sha256-directory",
  "Directories": [],
  "Files": [],
  "Size": 123,
  "CreatedAt": "2025-01-01T00:00:00Z",
  "HashesValidated": false
}
"""

        let directoryVersion = deserialize<DirectoryVersion> legacyJson

        Assert.That(directoryVersion.Sha256Hash, Is.EqualTo(Sha256Hash "sha256-directory"))
        Assert.That(directoryVersion.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
        Assert.That(directoryVersion.Directories.Count, Is.EqualTo(0))
        Assert.That(directoryVersion.Files.Count, Is.EqualTo(0))

    /// Verifies that local directory version json without blake3 hash defaults to empty scaffold value.
    [<Test>]
    member _.LocalDirectoryVersionJsonWithoutBlake3HashDefaultsToEmptyScaffoldValue() =
        let legacyJson =
            """
{
  "Class": "LocalDirectoryVersion",
  "DirectoryVersionId": "11111111-1111-1111-1111-111111111111",
  "OwnerId": "22222222-2222-2222-2222-222222222222",
  "OrganizationId": "33333333-3333-3333-3333-333333333333",
  "RepositoryId": "44444444-4444-4444-4444-444444444444",
  "RelativePath": "src",
  "Sha256Hash": "sha256-directory",
  "Directories": [],
  "Files": [],
  "Size": 123,
  "CreatedAt": "2025-01-01T00:00:00Z",
  "LastWriteTimeUtc": "2025-01-01T00:00:00Z"
}
"""

        let directoryVersion = deserialize<LocalDirectoryVersion> legacyJson

        Assert.That(directoryVersion.Sha256Hash, Is.EqualTo(Sha256Hash "sha256-directory"))
        Assert.That(directoryVersion.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
        Assert.That(directoryVersion.Directories.Count, Is.EqualTo(0))
        Assert.That(directoryVersion.Files.Count, Is.EqualTo(0))

    /// Verifies that file version manifest content reference round trips through json and message pack.
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

        let fileVersion =
            FileVersion.CreateWithHashes "src/large.bin" (Sha256Hash "sha256-abc123") (Blake3Hash "blake3-def456") "https://example.test/blob" true 4096L

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
        Assert.That(jsonRoundTrip.Blake3Hash, Is.EqualTo(fileVersion.Blake3Hash))
        Assert.That(jsonRoundTrip.IsBinary, Is.EqualTo(fileVersion.IsBinary))
        Assert.That(jsonRoundTrip.Size, Is.EqualTo(fileVersion.Size))
        Assert.That(jsonRoundTrip.CreatedAt, Is.EqualTo(fileVersion.CreatedAt))
        Assert.That(jsonRoundTrip.BlobUri, Is.EqualTo(fileVersion.BlobUri))
        Assert.That(jsonRoundTrip.Equals(fileVersion), Is.True)
        Assert.That(messagePackRoundTrip.Equals(fileVersion), Is.True)
        Assert.That(jsonRoundTrip.GetHashCode(), Is.EqualTo(fileVersion.GetHashCode()))
        Assert.That(messagePackRoundTrip.GetHashCode(), Is.EqualTo(fileVersion.GetHashCode()))

    /// Verifies that file version equality and hash code include blake3 hash.
    [<Test>]
    member _.FileVersionEqualityAndHashCodeIncludeBlake3Hash() =
        let left = FileVersion.CreateWithHashes "src/app.fs" (Sha256Hash "sha256") (Blake3Hash "left-blake3") "blob" false 123L

        let right = FileVersion.CreateWithHashes "src/app.fs" (Sha256Hash "sha256") (Blake3Hash "right-blake3") "blob" false 123L
        let createdAt = NodaTime.Instant.FromUtc(2025, 1, 1, 0, 0)

        left.CreatedAt <- createdAt
        right.CreatedAt <- createdAt

        Assert.That(left.Equals(right), Is.False)
        Assert.That(left.GetHashCode(), Is.Not.EqualTo(right.GetHashCode()))

    /// Verifies that local file version conversions preserve both hash fields.
    [<Test>]
    member _.LocalFileVersionConversionsPreserveBothHashFields() =
        let createdAt = NodaTime.Instant.FromUtc(2025, 1, 1, 0, 0)
        let lastWriteTimeUtc = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

        let localFileVersion =
            LocalFileVersion.CreateWithHashes "src/app.fs" (Sha256Hash "sha256-abc123") (Blake3Hash "blake3-def456") false 123L createdAt true lastWriteTimeUtc

        let fileVersion = localFileVersion.ToFileVersion
        let localRoundTrip = fileVersion.ToLocalFileVersion lastWriteTimeUtc

        Assert.That(localFileVersion.Sha256Hash, Is.EqualTo(Sha256Hash "sha256-abc123"))
        Assert.That(localFileVersion.Blake3Hash, Is.EqualTo(Blake3Hash "blake3-def456"))
        Assert.That(fileVersion.Sha256Hash, Is.EqualTo(localFileVersion.Sha256Hash))
        Assert.That(fileVersion.Blake3Hash, Is.EqualTo(localFileVersion.Blake3Hash))
        Assert.That(localRoundTrip.Sha256Hash, Is.EqualTo(localFileVersion.Sha256Hash))
        Assert.That(localRoundTrip.Blake3Hash, Is.EqualTo(localFileVersion.Blake3Hash))

    /// Verifies that directory version defaults use empty hash scaffold values.
    [<Test>]
    member _.DirectoryVersionDefaultsUseEmptyHashScaffoldValues() =
        Assert.That(DirectoryVersion.Default.Sha256Hash, Is.EqualTo(Sha256Hash String.Empty))
        Assert.That(DirectoryVersion.Default.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
        Assert.That(LocalDirectoryVersion.Default.Sha256Hash, Is.EqualTo(Sha256Hash String.Empty))
        Assert.That(LocalDirectoryVersion.Default.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
        Assert.That(DirectoryVersion.Default.Blake3Hash, Is.Not.EqualTo(HashAliasTestData.blake3Hash))

    /// Verifies that directory version conversions preserve both hash fields.
    [<Test>]
    member _.DirectoryVersionConversionsPreserveBothHashFields() =
        let directories = List<DirectoryVersionId>()
        let files = List<FileVersion>()
        let lastWriteTimeUtc = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

        let directoryVersion =
            DirectoryVersion.CreateWithHashes
                (Guid.Parse("11111111-1111-1111-1111-111111111111"))
                (Guid.Parse("22222222-2222-2222-2222-222222222222"))
                (Guid.Parse("33333333-3333-3333-3333-333333333333"))
                (Guid.Parse("44444444-4444-4444-4444-444444444444"))
                (RelativePath "src")
                (Sha256Hash "sha256-directory")
                (Blake3Hash "blake3-directory")
                directories
                files
                123L

        let localDirectoryVersion = directoryVersion.ToLocalDirectoryVersion lastWriteTimeUtc
        let directoryRoundTrip = localDirectoryVersion.ToDirectoryVersion

        Assert.That(directoryVersion.Sha256Hash, Is.EqualTo(Sha256Hash "sha256-directory"))
        Assert.That(directoryVersion.Blake3Hash, Is.EqualTo(Blake3Hash "blake3-directory"))
        Assert.That(localDirectoryVersion.Sha256Hash, Is.EqualTo(directoryVersion.Sha256Hash))
        Assert.That(localDirectoryVersion.Blake3Hash, Is.EqualTo(directoryVersion.Blake3Hash))
        Assert.That(directoryRoundTrip.Sha256Hash, Is.EqualTo(directoryVersion.Sha256Hash))
        Assert.That(directoryRoundTrip.Blake3Hash, Is.EqualTo(directoryVersion.Blake3Hash))
