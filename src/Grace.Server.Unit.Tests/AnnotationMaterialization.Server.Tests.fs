namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type AnnotationMaterializationServerTests() =

    let correlationId = "corr-annotation-materialization"

    let sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let textBytes (text: string) = Encoding.UTF8.GetBytes text

    let gzipBytes (bytes: byte array) =
        use compressed = new MemoryStream()
        use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)
        gzipStream.Write(bytes, 0, bytes.Length)
        gzipStream.Dispose()
        compressed.ToArray()

    let fileVersion relativePath isBinary (bytes: byte array) = FileVersion.Create relativePath (sha256Hex bytes) String.Empty isBinary (int64 bytes.Length)

    let readerFrom (objects: IDictionary<string, byte array>) : AnnotationMaterialization.ObjectPayloadReader =
        fun objectKey correlationId _ ->
            task {
                match objects.TryGetValue objectKey with
                | true, payload -> return Ok payload
                | false, _ -> return Error(GraceError.Create $"missing object {objectKey}" correlationId)
            }

    let expectOk result =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected materialization to succeed, got {error.Error}.")
            Unchecked.defaultof<AnnotationMaterialization.MaterializedTextContent>

    let expectErrorContains expected result =
        match result with
        | Ok _ -> Assert.Fail("Expected materialization to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain(expected))

    let encodeBlock bytes =
        match ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L bytes ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test ContentBlock to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    let manifestFor (bytes: byte array) (block: ContentBlockFormat.EncodedContentBlock) =
        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                [
                    ContentBlock.Create(block.Address, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestFile relativePath bytes =
        let block = encodeBlock bytes
        let manifest = manifestFor bytes block
        let fileVersion = fileVersion relativePath false bytes
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion, block

    let materialize reader fileVersion =
        AnnotationMaterialization.materializeTextWithObjectReader reader fileVersion correlationId CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    [<Test>]
    member _.WholeFileContentMaterializesExactUtf8Text() =
        let bytes = textBytes "line one\nline two\n"
        let target = fileVersion "/src/File.fs" false bytes
        let objectKey = StorageKeys.wholeFileContentObjectKey target
        let objects = Dictionary<string, byte array>()
        objects[objectKey] <- bytes

        let result =
            materialize (readerFrom objects) target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("line one\nline two\n"))
        Assert.That(result.Bytes = bytes, Is.True)

    [<Test>]
    member _.WholeFileContentMaterializesGzipCompressedUtf8Text() =
        let bytes = textBytes "gzip line one\ngzip line two\n"
        let target = fileVersion "/src/Compressed.fs" false bytes
        let objectKey = StorageKeys.wholeFileContentObjectKey target
        let objects = Dictionary<string, byte array>()
        objects[objectKey] <- gzipBytes bytes

        let result =
            materialize (readerFrom objects) target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("gzip line one\ngzip line two\n"))
        Assert.That(result.Bytes = bytes, Is.True)

    [<Test>]
    member _.ManifestBackedContentMaterializesExactUtf8Text() =
        let bytes = textBytes "manifest line one\nmanifest line two\n"
        let target, block = manifestFile "/src/Large.fs" bytes
        target.Blake3Hash <- Blake3Hash(ContentAddress.computeBlake3Hex bytes)
        let objects = Dictionary<string, byte array>()
        objects[StorageKeys.contentBlockObjectKey block.Address] <- block.Payload

        let result =
            materialize (readerFrom objects) target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("manifest line one\nmanifest line two\n"))
        Assert.That(result.Bytes = bytes, Is.True)

    [<Test>]
    member _.MaterializationRejectsMismatchedFileBlake3WhenPresent() =
        let bytes = textBytes "manifest blake3 payload"
        let target, block = manifestFile "/src/Large.fs" bytes
        target.Blake3Hash <- Blake3Hash "wrong-blake3"
        let objects = Dictionary<string, byte array>()
        objects[StorageKeys.contentBlockObjectKey block.Address] <- block.Payload

        materialize (readerFrom objects) target
        |> expectErrorContains "FileVersion.Blake3Hash"

    [<Test>]
    member _.MaterializationAllowsLegacyEmptyFileBlake3() =
        let bytes = textBytes "legacy manifest payload"
        let target, block = manifestFile "/src/LegacyLarge.fs" bytes
        target.Blake3Hash <- Blake3Hash String.Empty
        let objects = Dictionary<string, byte array>()
        objects[StorageKeys.contentBlockObjectKey block.Address] <- block.Payload

        let result =
            materialize (readerFrom objects) target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("legacy manifest payload"))

    [<Test>]
    member _.BinaryTargetIsRejectedBeforeStorageRead() =
        let bytes = textBytes "binary flag wins"
        let target = fileVersion "/src/Binary.dat" true bytes
        let reader _ _ _ = task { return Error(GraceError.Create "reader should not be called" correlationId) }

        materialize reader target
        |> expectErrorContains "is binary"

    [<Test>]
    member _.UndecodableUtf8TargetIsRejected() =
        let bytes = [| 0xffuy; 0xfeuy; 0xfduy |]
        let target = fileVersion "/src/Invalid.txt" false bytes
        let objects = Dictionary<string, byte array>()
        objects[StorageKeys.wholeFileContentObjectKey target] <- bytes

        materialize (readerFrom objects) target
        |> expectErrorContains "not valid UTF-8"

    [<Test>]
    member _.TooLargeTargetIsRejectedBeforeStorageRead() =
        let target =
            FileVersion.Create
                "/src/Huge.txt"
                String.Empty
                String.Empty
                false
                (AnnotationMaterialization.MaxMaterializedTextBytes
                 + 1L)

        let reader _ _ _ = task { return Error(GraceError.Create "reader should not be called" correlationId) }

        materialize reader target
        |> expectErrorContains "too large"

    [<Test>]
    member _.MissingOrUnreadableContentIsReturnedAsMaterializationError() =
        let bytes = textBytes "missing"
        let target = fileVersion "/src/Missing.txt" false bytes
        let objects = Dictionary<string, byte array>()

        materialize (readerFrom objects) target
        |> expectErrorContains "missing object"

    [<Test>]
    member _.InvalidManifestReconstructionIsRejectedClearly() =
        let bytes = textBytes "manifest payload"
        let target, block = manifestFile "/src/Manifest.fs" bytes
        let objects = Dictionary<string, byte array>()
        let corrupted = Array.copy block.Payload
        corrupted[0] <- corrupted[0] ^^^ 0xffuy
        objects[StorageKeys.contentBlockObjectKey block.Address] <- corrupted

        materialize (readerFrom objects) target
        |> expectErrorContains "Invalid manifest reconstruction"
