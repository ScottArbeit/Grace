namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open MessagePack
open NUnit.Framework

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

[<Parallelizable(ParallelScope.All)>]
type TypesTypesTests() =
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
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest

        let jsonRoundTrip = deserialize<FileVersion> (serialize fileVersion)
        let messagePackBytes = MessagePackSerializer.Serialize(fileVersion, Constants.messagePackSerializerOptions)
        let messagePackRoundTrip = MessagePackSerializer.Deserialize<FileVersion>(messagePackBytes, Constants.messagePackSerializerOptions)

        Assert.That(jsonRoundTrip.ContentReference, Is.EqualTo(FileContentReference.FileManifest manifest))
        Assert.That(messagePackRoundTrip.ContentReference, Is.EqualTo(FileContentReference.FileManifest manifest))
