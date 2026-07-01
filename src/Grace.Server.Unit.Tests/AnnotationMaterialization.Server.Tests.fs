namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
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

/// Covers annotation Materialization Server behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type AnnotationMaterializationServerTests() =

    let correlationId = "corr-annotation-materialization"

    /// Builds sha256 Hex test data for the server unit annotation Materialization scenarios in this file.
    let sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let textBytes (text: string) = Encoding.UTF8.GetBytes text

    /// Builds gzip Bytes test data for the server unit annotation Materialization scenarios in this file.
    let gzipBytes (bytes: byte array) =
        use compressed = new MemoryStream()
        use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)
        gzipStream.Write(bytes, 0, bytes.Length)
        gzipStream.Dispose()
        compressed.ToArray()

    let fileVersion relativePath isBinary (bytes: byte array) = FileVersion.Create relativePath (sha256Hex bytes) String.Empty isBinary (int64 bytes.Length)

    /// Builds whole File Reader From test data for the server unit annotation Materialization scenarios in this file.
    let wholeFileReaderFrom (objects: IDictionary<string, byte array>) : AnnotationMaterialization.ObjectPayloadReader =
        fun objectKey correlationId _ ->
            task {
                match objects.TryGetValue objectKey with
                | true, payload -> return Ok payload
                | false, _ -> return Error(GraceError.Create $"missing object {objectKey}" correlationId)
            }

    let failingWholeFileReader: AnnotationMaterialization.ObjectPayloadReader =
        fun objectKey correlationId _ -> task { return Error(GraceError.Create $"whole-file reader should not be called for {objectKey}" correlationId) }

    /// Builds placement For test data for the server unit annotation Materialization scenarios in this file.
    let placementFor storagePoolId address =
        {
            StorageAccountName = $"account-{storagePoolId}"
            StorageContainerName = StorageContainerName $"container-{storagePoolId}"
            ObjectKey = $"stored/{storagePoolId}/{StorageKeys.contentBlockObjectKey address}"
            ETag = Some $"etag-{address}"
        }

    /// Builds metadata For test data for the server unit annotation Materialization scenarios in this file.
    let metadataFor storagePoolId (block: ContentBlock) placement =
        { ContentBlockMetadata.Empty with
            StoragePoolId = storagePoolId
            ContentBlockAddress = block.Address
            BlockFormatVersion = 1s
            StoragePlacement = placement
            Ranges =
                [|
                    { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = block.Size }
                |]
        }

    let metadataResolverFrom
        (metadata: IDictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>)
        : AnnotationMaterialization.ContentBlockMetadataResolver
        =
        fun manifest contentBlockAddress correlationId _ ->
            task {
                let key = (manifest.StoragePoolId, manifest.ManifestAddress, contentBlockAddress)

                match metadata.TryGetValue key with
                | true, value -> return Ok value
                | false, _ ->
                    return
                        Error(GraceError.Create $"missing metadata for {manifest.StoragePoolId}/{manifest.ManifestAddress}/{contentBlockAddress}" correlationId)
            }

    let contentBlockReaderFrom
        (objects: IDictionary<string, byte array>)
        (readPlacements: ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>)
        : AnnotationMaterialization.ContentBlockPlacementPayloadReader
        =
        fun placement contentBlockAddress correlationId _ ->
            task {
                readPlacements.Add(contentBlockAddress, placement)

                match objects.TryGetValue placement.ObjectKey with
                | true, payload -> return Ok payload
                | false, _ -> return Error(GraceError.Create $"missing placement object {placement.ObjectKey}" correlationId)
            }

    /// Builds expect Ok test data for the server unit annotation Materialization scenarios in this file.
    let expectOk result =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected materialization to succeed, got {error.Error}.")
            Unchecked.defaultof<AnnotationMaterialization.MaterializedTextContent>

    /// Builds expect Error Contains test data for the server unit annotation Materialization scenarios in this file.
    let expectErrorContains expected result =
        match result with
        | Ok _ -> Assert.Fail("Expected materialization to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain(expected))

    /// Exercises these tests by encoding Block coverage for the server unit annotation Materialization scenario.
    let encodeBlock bytes =
        match ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L bytes ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test ContentBlock to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Builds manifest For With Storage Pool test data for the server unit annotation Materialization scenarios in this file.
    let manifestForWithStoragePool storagePoolId (bytes: byte array) (block: ContentBlockFormat.EncodedContentBlock) =
        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                storagePoolId,
                [
                    ContentBlock.Create(block.Address, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestFor bytes block = manifestForWithStoragePool (StoragePoolId "pool-annotation") bytes block

    /// Builds manifest File test data for the server unit annotation Materialization scenarios in this file.
    let manifestFile relativePath bytes =
        let block = encodeBlock bytes
        let manifest = manifestFor bytes block
        let fileVersion = fileVersion relativePath false bytes
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion, block, manifest

    /// Builds materialize With Readers test data for the server unit annotation Materialization scenarios in this file.
    let materializeWithReaders wholeFileReader metadataResolver contentBlockReader fileVersion =
        AnnotationMaterialization.materializeTextWithReaders
            wholeFileReader
            metadataResolver
            contentBlockReader
            fileVersion
            correlationId
            CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    /// Builds materialize Whole File test data for the server unit annotation Materialization scenarios in this file.
    let materializeWholeFile reader fileVersion =
        AnnotationMaterialization.materializeTextWithObjectReader reader fileVersion correlationId CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    /// Builds materialize Manifest test data for the server unit annotation Materialization scenarios in this file.
    let materializeManifest metadata objects readPlacements fileVersion =
        materializeWithReaders failingWholeFileReader (metadataResolverFrom metadata) (contentBlockReaderFrom objects readPlacements) fileVersion

    /// Verifies that whole File Content Materializes Exact Utf8 Text.
    [<Test>]
    member _.WholeFileContentMaterializesExactUtf8Text() =
        let bytes = textBytes "line one\nline two\n"
        let target = fileVersion "/src/File.fs" false bytes
        let objectKey = StorageKeys.wholeFileContentObjectKey target
        let objects = Dictionary<string, byte array>()
        objects[objectKey] <- bytes

        let result =
            materializeWholeFile (wholeFileReaderFrom objects) target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("line one\nline two\n"))
        Assert.That(result.Bytes = bytes, Is.True)

    /// Verifies that whole File Content Materializes Gzip Compressed Utf8 Text.
    [<Test>]
    member _.WholeFileContentMaterializesGzipCompressedUtf8Text() =
        let bytes = textBytes "gzip line one\ngzip line two\n"
        let target = fileVersion "/src/Compressed.fs" false bytes
        let objectKey = StorageKeys.wholeFileContentObjectKey target
        let objects = Dictionary<string, byte array>()
        objects[objectKey] <- gzipBytes bytes

        let result =
            materializeWholeFile (wholeFileReaderFrom objects) target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("gzip line one\ngzip line two\n"))
        Assert.That(result.Bytes = bytes, Is.True)

    /// Verifies that manifest Backed Content Materializes Exact Utf8 Text From Stored Placement.
    [<Test>]
    member _.ManifestBackedContentMaterializesExactUtf8TextFromStoredPlacement() =
        let bytes = textBytes "manifest line one\nmanifest line two\n"
        let target, block, manifest = manifestFile "/src/Large.fs" bytes
        target.Blake3Hash <- Blake3Hash(ContentAddress.computeBlake3Hex bytes)
        let objects = Dictionary<string, byte array>()
        let placement = placementFor manifest.StoragePoolId block.Address
        objects[placement.ObjectKey] <- block.Payload
        objects[StorageKeys.contentBlockObjectKey block.Address] <- textBytes "repository fallback must not be read"
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor manifest.StoragePoolId manifest.Blocks[0] placement
        let otherPool = StoragePoolId "pool-other"
        let otherPlacement = placementFor otherPool block.Address
        metadata[(otherPool, manifest.ManifestAddress, block.Address)] <- metadataFor otherPool manifest.Blocks[0] otherPlacement
        objects[otherPlacement.ObjectKey] <- textBytes "same address in another pool must not be read"
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        let result =
            materializeManifest metadata objects readPlacements target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("manifest line one\nmanifest line two\n"))
        Assert.That(result.Bytes = bytes, Is.True)
        Assert.That(readPlacements, Has.Count.EqualTo(1))
        Assert.That(snd readPlacements[0], Is.EqualTo(placement))

    /// Verifies that manifest Backed Content Rejects Missing Finalized Placement Evidence.
    [<Test>]
    member _.ManifestBackedContentRejectsMissingFinalizedPlacementEvidence() =
        let bytes = textBytes "manifest placement evidence"
        let target, _, _ = manifestFile "/src/MissingPlacement.fs" bytes
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        let objects = Dictionary<string, byte array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "missing metadata"

        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Content Rejects Blank Stored Pool.
    [<Test>]
    member _.ManifestBackedContentRejectsBlankStoredPool() =
        let bytes = textBytes "manifest blank pool"
        let block = encodeBlock bytes
        let manifest = manifestForWithStoragePool (StoragePoolId String.Empty) bytes block
        let target = fileVersion "/src/BlankPool.fs" false bytes
        target.ContentReference <- FileContentReference.FileManifest manifest
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        let objects = Dictionary<string, byte array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "FileManifest StoragePoolId is required"

        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Content Rejects Wrong Pool Metadata.
    [<Test>]
    member _.ManifestBackedContentRejectsWrongPoolMetadata() =
        let bytes = textBytes "manifest wrong pool"
        let target, block, manifest = manifestFile "/src/WrongPool.fs" bytes
        let placement = placementFor (StoragePoolId "pool-other") block.Address
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor (StoragePoolId "pool-other") manifest.Blocks[0] placement
        let objects = Dictionary<string, byte array>()
        objects[placement.ObjectKey] <- block.Payload
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "StoragePoolId does not match FileManifest.StoragePoolId"

        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Content Rejects Incomplete Stored Placement.
    [<Test>]
    member _.ManifestBackedContentRejectsIncompleteStoredPlacement() =
        let bytes = textBytes "manifest incomplete placement"
        let target, block, manifest = manifestFile "/src/IncompletePlacement.fs" bytes
        let placement = { placementFor manifest.StoragePoolId block.Address with ObjectKey = String.Empty }
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor manifest.StoragePoolId manifest.Blocks[0] placement
        let objects = Dictionary<string, byte array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "StoragePlacement is incomplete"

        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Content Returns Stored Placement Read Errors.
    [<Test>]
    member _.ManifestBackedContentReturnsStoredPlacementReadErrors() =
        let bytes = textBytes "manifest missing stored blob"
        let target, block, manifest = manifestFile "/src/MissingStoredBlob.fs" bytes
        let placement = placementFor manifest.StoragePoolId block.Address
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor manifest.StoragePoolId manifest.Blocks[0] placement
        let objects = Dictionary<string, byte array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "missing placement object"

        Assert.That(readPlacements, Has.Count.EqualTo(1))

    /// Verifies that materialization Rejects Mismatched File Blake3 When Present.
    [<Test>]
    member _.MaterializationRejectsMismatchedFileBlake3WhenPresent() =
        let bytes = textBytes "manifest blake3 payload"
        let target, block, manifest = manifestFile "/src/Large.fs" bytes
        target.Blake3Hash <- Blake3Hash "wrong-blake3"
        let objects = Dictionary<string, byte array>()
        let placement = placementFor manifest.StoragePoolId block.Address
        objects[placement.ObjectKey] <- block.Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor manifest.StoragePoolId manifest.Blocks[0] placement
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "FileVersion.Blake3Hash"

    /// Verifies that materialization Allows Legacy Empty File Blake3.
    [<Test>]
    member _.MaterializationAllowsLegacyEmptyFileBlake3() =
        let bytes = textBytes "legacy manifest payload"
        let target, block, manifest = manifestFile "/src/LegacyLarge.fs" bytes
        target.Blake3Hash <- Blake3Hash String.Empty
        let objects = Dictionary<string, byte array>()
        let placement = placementFor manifest.StoragePoolId block.Address
        objects[placement.ObjectKey] <- block.Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor manifest.StoragePoolId manifest.Blocks[0] placement
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        let result =
            materializeManifest metadata objects readPlacements target
            |> expectOk

        Assert.That(result.Text, Is.EqualTo("legacy manifest payload"))

    /// Verifies that binary Target Is Rejected Before Storage Read.
    [<Test>]
    member _.BinaryTargetIsRejectedBeforeStorageRead() =
        let bytes = textBytes "binary flag wins"
        let target = fileVersion "/src/Binary.dat" true bytes
        let reader _ _ _ = task { return Error(GraceError.Create "reader should not be called" correlationId) }

        materializeWholeFile reader target
        |> expectErrorContains "is binary"

    /// Verifies that undecodable Utf8 Target Is Rejected.
    [<Test>]
    member _.UndecodableUtf8TargetIsRejected() =
        let bytes = [| 0xffuy; 0xfeuy; 0xfduy |]
        let target = fileVersion "/src/Invalid.txt" false bytes
        let objects = Dictionary<string, byte array>()
        objects[StorageKeys.wholeFileContentObjectKey target] <- bytes

        materializeWholeFile (wholeFileReaderFrom objects) target
        |> expectErrorContains "not valid UTF-8"

    /// Verifies that too Large Target Is Rejected Before Storage Read.
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

        materializeWholeFile reader target
        |> expectErrorContains "too large"

    /// Verifies that missing Or Unreadable Content Is Returned As Materialization Error.
    [<Test>]
    member _.MissingOrUnreadableContentIsReturnedAsMaterializationError() =
        let bytes = textBytes "missing"
        let target = fileVersion "/src/Missing.txt" false bytes
        let objects = Dictionary<string, byte array>()

        materializeWholeFile (wholeFileReaderFrom objects) target
        |> expectErrorContains "missing object"

    /// Verifies that invalid Manifest Reconstruction Is Rejected Clearly.
    [<Test>]
    member _.InvalidManifestReconstructionIsRejectedClearly() =
        let bytes = textBytes "manifest payload"
        let target, block, manifest = manifestFile "/src/Manifest.fs" bytes
        let objects = Dictionary<string, byte array>()
        let corrupted = Array.copy block.Payload
        corrupted[0] <- corrupted[0] ^^^ 0xffuy
        let placement = placementFor manifest.StoragePoolId block.Address
        objects[placement.ObjectKey] <- corrupted
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor manifest.StoragePoolId manifest.Blocks[0] placement
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeManifest metadata objects readPlacements target
        |> expectErrorContains "Invalid manifest reconstruction"
