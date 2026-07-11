namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Threading

/// Covers normal File Materialization Server behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type NormalFileMaterializationServerTests() =

    let correlationId = "corr-normal-file-materialization"

    let bytes (text: string) = Encoding.UTF8.GetBytes text

    /// Builds gunzip Bytes test data for the server unit normal File Materialization scenarios in this file.
    let gunzipBytes (payload: byte array) =
        use source = new MemoryStream(payload)
        use gzipStream = new GZipStream(source, CompressionMode.Decompress, leaveOpen = false)
        use output = new MemoryStream()
        gzipStream.CopyTo(output)
        output.ToArray()

    /// Builds looks Like Gzip test data for the server unit normal File Materialization scenarios in this file.
    let looksLikeGzip (payload: byte array) =
        payload.Length >= 2
        && payload[0] = 0x1fuy
        && payload[1] = 0x8buy

    /// Builds sha256 Hex test data for the server unit normal File Materialization scenarios in this file.
    let sha256Hex (payload: byte array) =
        SHA256.HashData(payload)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Builds expect Encoded Ok test data for the server unit normal File Materialization scenarios in this file.
    let expectEncodedOk result =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected ContentBlockFormat.encode Ok but got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Exercises these tests by encoding Block coverage for the server unit normal File Materialization scenario.
    let encodeBlock payload =
        ContentBlockFormat.encode [ ContentBlockFormat.createChunk 0L payload ]
        |> expectEncodedOk

    /// Builds file Version test data for the server unit normal File Materialization scenarios in this file.
    let fileVersion (relativePath: string) (payload: byte array) =
        FileVersion.CreateWithHashes
            (RelativePath relativePath)
            (Sha256Hash(sha256Hex payload))
            (Blake3Hash(ContentAddress.computeBlake3Hex payload))
            String.Empty
            true
            (int64 payload.Length)

    /// Builds manifest For Blocks test data for the server unit normal File Materialization scenarios in this file.
    let manifestForBlocks (storagePoolId: StoragePoolId) (payload: byte array) (blocks: (ContentBlockFormat.EncodedContentBlock * int64 * int64) array) =
        let manifestBlocks =
            blocks
            |> Array.map (fun (block, offset, size) -> ContentBlock.Create(block.Address, offset, size))
            |> Array.toList

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex payload),
                int64 payload.Length,
                storagePoolId,
                manifestBlocks
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Builds manifest File test data for the server unit normal File Materialization scenarios in this file.
    let manifestFile (relativePath: string) (storagePoolId: StoragePoolId) (payload: byte array) =
        let block = encodeBlock payload
        let manifest = manifestForBlocks storagePoolId payload [| block, 0L, int64 payload.Length |]
        let fileVersion = fileVersion relativePath payload
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion, manifest, [| block |]

    /// Builds placement For test data for the server unit normal File Materialization scenarios in this file.
    let placementFor (storagePoolId: StoragePoolId) (address: ContentBlockAddress) (objectKey: string) =
        {
            StorageAccountName = $"account-{storagePoolId}"
            StorageContainerName = StorageContainerName $"container-{storagePoolId}"
            ObjectKey = objectKey
            ETag = Some $"etag-{address}"
        }

    /// Builds metadata For test data for the server unit normal File Materialization scenarios in this file.
    let metadataFor (storagePoolId: StoragePoolId) (block: ContentBlock) (placement: ContentBlockStoragePlacement) =
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
        (requests: ResizeArray<ContentBlockAddress array>)
        : NormalFileMaterialization.ContentBlockMetadataBatchResolver
        =
        fun manifest addresses correlationId _ ->
            task {
                requests.Add(Array.copy addresses)
                let records = ResizeArray<ContentBlockMetadata>()
                let mutable error = None
                let mutable index = 0

                while index < addresses.Length && Option.isNone error do
                    let key = (manifest.StoragePoolId, manifest.ManifestAddress, addresses[index])

                    match metadata.TryGetValue key with
                    | true, value -> records.Add value
                    | false, _ ->
                        error <-
                            Some(GraceError.Create $"missing metadata for {manifest.StoragePoolId}/{manifest.ManifestAddress}/{addresses[index]}" correlationId)

                    index <- index + 1

                match error with
                | Some error -> return Error error
                | None -> return Ok(records.ToArray())
            }

    let contentBlockReaderFrom
        (objects: IDictionary<string, byte array>)
        (readPlacements: ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>)
        : NormalFileMaterialization.ContentBlockPlacementPayloadReader
        =
        fun placement contentBlockAddress correlationId _ ->
            task {
                readPlacements.Add(contentBlockAddress, placement)

                match objects.TryGetValue placement.ObjectKey with
                | true, payload -> return Ok payload
                | false, _ -> return Error(GraceError.Create $"missing placement object {placement.ObjectKey}" correlationId)
            }

    /// Builds whole File Reader From test data for the server unit normal File Materialization scenarios in this file.
    let wholeFileReaderFrom (objects: IDictionary<string, byte array>) : NormalFileMaterialization.ObjectPayloadReader =
        fun objectKey correlationId _ ->
            task {
                match objects.TryGetValue objectKey with
                | true, payload -> return Ok payload
                | false, _ -> return Error(GraceError.Create $"missing object {objectKey}" correlationId)
            }

    let failingWholeFileReader: NormalFileMaterialization.ObjectPayloadReader =
        fun objectKey correlationId _ -> task { return Error(GraceError.Create $"whole-file reader should not be called for {objectKey}" correlationId) }

    /// Builds writer To test data for the server unit normal File Materialization scenarios in this file.
    let writerTo (objects: IDictionary<string, byte array>) (writes: ResizeArray<string * byte array>) : NormalFileMaterialization.ObjectPayloadWriter =
        fun objectKey payload _ _ ->
            task {
                writes.Add(objectKey, Array.copy payload)
                objects[objectKey] <- Array.copy payload
                return Ok()
            }

    /// Builds materialize Bytes test data for the server unit normal File Materialization scenarios in this file.
    let materializeBytes wholeFileReader metadataResolver contentBlockReader fileVersion objectKey =
        NormalFileMaterialization.materializeBytesWithReaders
            wholeFileReader
            metadataResolver
            contentBlockReader
            fileVersion
            objectKey
            correlationId
            CancellationToken.None
        |> fun operation -> operation.GetAwaiter().GetResult()

    /// Builds materialize For Download test data for the server unit normal File Materialization scenarios in this file.
    let materializeForDownload wholeFileReader writer metadataResolver contentBlockReader fileVersion objectKey =
        NormalFileMaterialization.materializeForDownloadWithReaders
            wholeFileReader
            writer
            metadataResolver
            contentBlockReader
            fileVersion
            objectKey
            correlationId
            CancellationToken.None
        |> fun operation -> operation.GetAwaiter().GetResult()

    /// Builds expect Ok test data for the server unit normal File Materialization scenarios in this file.
    let expectOk result =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected normal file materialization to succeed, got {error.Error}.")
            Unchecked.defaultof<byte array>

    /// Builds expect Unit Ok test data for the server unit normal File Materialization scenarios in this file.
    let expectUnitOk result =
        match result with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected normal file materialization to succeed, got {error.Error}.")

    /// Builds expect Error Contains test data for the server unit normal File Materialization scenarios in this file.
    let expectErrorContains expected result =
        match result with
        | Ok _ -> Assert.Fail("Expected normal file materialization to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain(expected))

    let addMetadata
        (metadata: IDictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>)
        (manifest: FileManifest)
        (storagePoolId: StoragePoolId)
        (block: ContentBlock)
        (placement: ContentBlockStoragePlacement)
        =
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, block.Address)] <- metadataFor storagePoolId block placement

    /// Verifies that whole File Content Materializes Repository Object Including Empty File.
    [<Test>]
    member _.WholeFileContentMaterializesRepositoryObjectIncludingEmptyFile() =
        let payload = Array.empty<byte>
        let target = fileVersion "/empty.bin" payload
        let objectKey = StorageKeys.wholeFileContentObjectKey target
        let objects = Dictionary<string, byte array>()
        objects[objectKey] <- payload
        let metadataRequests = ResizeArray<ContentBlockAddress array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        let result =
            materializeBytes
                (wholeFileReaderFrom objects)
                (metadataResolverFrom (Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()) metadataRequests)
                (contentBlockReaderFrom objects readPlacements)
                target
                objectKey
            |> expectOk

        Assert.That(result, Is.Empty)
        Assert.That(metadataRequests, Is.Empty)
        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Binary Normal Download Publishes Raw Bytes From Stored Placement.
    [<Test>]
    member _.ManifestBackedBinaryNormalDownloadPublishesRawBytesFromStoredPlacement() =
        let payload = bytes "manifest normal download bytes"
        let target, manifest, blocks = manifestFile "/nested/historical/file.bin" (StoragePoolId "pool-download") payload
        let objectKey = "normal/materialized/historical-file.bin"
        let recordedPlacement = placementFor manifest.StoragePoolId blocks[0].Address "recorded/compacted/block"
        let addressOnlyPlacement = placementFor manifest.StoragePoolId blocks[0].Address (StorageKeys.contentBlockObjectKey blocks[0].Address)
        let objects = Dictionary<string, byte array>()
        objects[recordedPlacement.ObjectKey] <- blocks[0].Payload
        objects[addressOnlyPlacement.ObjectKey] <- bytes "address-only fallback must not be read"
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest manifest.StoragePoolId manifest.Blocks[0] recordedPlacement
        let metadataRequests = ResizeArray<ContentBlockAddress array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()
        let writes = ResizeArray<string * byte array>()

        materializeForDownload
            failingWholeFileReader
            (writerTo objects writes)
            (metadataResolverFrom metadata metadataRequests)
            (contentBlockReaderFrom objects readPlacements)
            target
            objectKey
        |> expectUnitOk

        Assert.That(writes, Has.Count.EqualTo(1))
        Assert.That(fst writes[0], Is.EqualTo(objectKey))
        Assert.That(((snd writes[0]) = payload), Is.True)
        Assert.That(looksLikeGzip (snd writes[0]), Is.False)
        Assert.That(readPlacements, Has.Count.EqualTo(1))
        Assert.That(snd readPlacements[0], Is.EqualTo(recordedPlacement))
        Assert.That(metadataRequests, Has.Count.EqualTo(1))
        Assert.That((metadataRequests[0] = [| blocks[0].Address |]), Is.True)

    /// Verifies that manifest-backed materialization rejects a missing SHA-256 before accepting reconstructed bytes.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsMissingSha256() =
        let payload = bytes "manifest missing sha256"
        let target, manifest, blocks = manifestFile "/missing-sha256.bin" (StoragePoolId "pool-missing-sha256") payload
        target.Sha256Hash <- Sha256Hash String.Empty
        let placement = placementFor manifest.StoragePoolId blocks[0].Address "stored/missing-sha256"
        let objects = Dictionary<string, byte array>()
        objects[placement.ObjectKey] <- blocks[0].Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest manifest.StoragePoolId manifest.Blocks[0] placement

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom metadata (ResizeArray()))
            (contentBlockReaderFrom objects (ResizeArray()))
            target
            "normal/missing-sha256.bin"
        |> expectErrorContains "FileVersion.Sha256Hash"

    /// Verifies that manifest Backed Non Binary Normal Download Publishes Gzip Payload For Whole File Compatibility.
    [<Test>]
    member _.ManifestBackedNonBinaryNormalDownloadPublishesGzipPayloadForWholeFileCompatibility() =
        let payload = bytes "manifest text download bytes\nline two\n"
        let target, manifest, blocks = manifestFile "/nested/historical/file.txt" (StoragePoolId "pool-download-text") payload
        target.IsBinary <- false
        let objectKey = "normal/materialized/historical-file.txt"
        let recordedPlacement = placementFor manifest.StoragePoolId blocks[0].Address "recorded/compacted/text-block"
        let objects = Dictionary<string, byte array>()
        objects[recordedPlacement.ObjectKey] <- blocks[0].Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest manifest.StoragePoolId manifest.Blocks[0] recordedPlacement
        let metadataRequests = ResizeArray<ContentBlockAddress array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()
        let writes = ResizeArray<string * byte array>()

        materializeForDownload
            failingWholeFileReader
            (writerTo objects writes)
            (metadataResolverFrom metadata metadataRequests)
            (contentBlockReaderFrom objects readPlacements)
            target
            objectKey
        |> expectUnitOk

        Assert.That(writes, Has.Count.EqualTo(1))
        Assert.That(fst writes[0], Is.EqualTo(objectKey))
        Assert.That(((snd writes[0]) = payload), Is.False)
        Assert.That(looksLikeGzip (snd writes[0]), Is.True)
        Assert.That((gunzipBytes (snd writes[0]) = payload), Is.True)
        Assert.That(readPlacements, Has.Count.EqualTo(1))
        Assert.That(snd readPlacements[0], Is.EqualTo(recordedPlacement))
        Assert.That(metadataRequests, Has.Count.EqualTo(1))
        Assert.That((metadataRequests[0] = [| blocks[0].Address |]), Is.True)

    /// Verifies that manifest Backed Multi Block Download Batches Metadata And Reads Each Distinct Block Once.
    [<Test>]
    member _.ManifestBackedMultiBlockDownloadBatchesMetadataAndReadsEachDistinctBlockOnce() =
        let first = bytes "first block "
        let second = bytes "second block "
        let payload = Array.concat [ first; second; first ]
        let firstBlock = encodeBlock first
        let secondBlock = encodeBlock second
        let storagePoolId = StoragePoolId "pool-multi"

        let manifest =
            manifestForBlocks
                storagePoolId
                payload
                [|
                    firstBlock, 0L, int64 first.Length
                    secondBlock, int64 first.Length, int64 second.Length
                    firstBlock, int64 first.Length + int64 second.Length, int64 first.Length
                |]

        let target = fileVersion "/history/repeated.bin" payload
        target.ContentReference <- FileContentReference.FileManifest manifest
        let firstPlacement = placementFor storagePoolId firstBlock.Address "recorded/first"
        let secondPlacement = placementFor storagePoolId secondBlock.Address "recorded/second"
        let objects = Dictionary<string, byte array>()
        objects[firstPlacement.ObjectKey] <- firstBlock.Payload
        objects[secondPlacement.ObjectKey] <- secondBlock.Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest storagePoolId manifest.Blocks[0] firstPlacement
        addMetadata metadata manifest storagePoolId manifest.Blocks[1] secondPlacement
        let metadataRequests = ResizeArray<ContentBlockAddress array>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        let result =
            materializeBytes
                failingWholeFileReader
                (metadataResolverFrom metadata metadataRequests)
                (contentBlockReaderFrom objects readPlacements)
                target
                "normal/repeated.bin"
            |> expectOk

        Assert.That((result = payload), Is.True)
        Assert.That(metadataRequests, Has.Count.EqualTo(1))

        let expectedMetadataRequest =
            [|
                firstBlock.Address
                secondBlock.Address
            |]

        Assert.That((metadataRequests[0] = expectedMetadataRequest), Is.True)

        Assert.That(readPlacements, Has.Count.EqualTo(2))

        let expectedReadAddresses =
            [|
                firstBlock.Address
                secondBlock.Address
            |]

        Assert.That(((readPlacements |> Seq.map fst |> Seq.toArray) = expectedReadAddresses), Is.True)

    /// Verifies that manifest Backed Normal Download Rejects Wrong Pool Metadata.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsWrongPoolMetadata() =
        let payload = bytes "wrong pool"
        let target, manifest, blocks = manifestFile "/wrong-pool.bin" (StoragePoolId "pool-correct") payload
        let wrongPool = StoragePoolId "pool-wrong"
        let placement = placementFor wrongPool blocks[0].Address "wrong-pool/object"
        let objects = Dictionary<string, byte array>()
        objects[placement.ObjectKey] <- blocks[0].Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest wrongPool manifest.Blocks[0] placement
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom metadata (ResizeArray<ContentBlockAddress array>()))
            (contentBlockReaderFrom objects readPlacements)
            target
            "normal/wrong-pool.bin"
        |> expectErrorContains "StoragePoolId does not match FileManifest.StoragePoolId"

        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Normal Download Rejects Missing Block Metadata.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsMissingBlockMetadata() =
        let payload = bytes "missing metadata"
        let target, _, _ = manifestFile "/missing.bin" (StoragePoolId "pool-missing") payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        let readPlacements = ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom metadata (ResizeArray<ContentBlockAddress array>()))
            (contentBlockReaderFrom (Dictionary<string, byte array>()) readPlacements)
            target
            "normal/missing.bin"
        |> expectErrorContains "missing metadata"

        Assert.That(readPlacements, Is.Empty)

    /// Verifies that manifest Backed Normal Download Rejects Wrong Block Hash.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsWrongBlockHash() =
        let payload = bytes "expected payload"
        let target, manifest, blocks = manifestFile "/wrong-hash.bin" (StoragePoolId "pool-hash") payload
        let otherBlock = encodeBlock (bytes "different valid block")
        let placement = placementFor manifest.StoragePoolId blocks[0].Address "stored/wrong-hash"
        let objects = Dictionary<string, byte array>()
        objects[placement.ObjectKey] <- otherBlock.Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest manifest.StoragePoolId manifest.Blocks[0] placement

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom metadata (ResizeArray<ContentBlockAddress array>()))
            (contentBlockReaderFrom objects (ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()))
            target
            "normal/wrong-hash.bin"
        |> expectErrorContains "Invalid manifest reconstruction"

    /// Verifies that manifest Backed Normal Download Rejects Invalid Range Order.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsInvalidRangeOrder() =
        let first = bytes "first"
        let second = bytes "second"
        let payload = Array.concat [ first; second ]
        let firstBlock = encodeBlock first
        let secondBlock = encodeBlock second
        let storagePoolId = StoragePoolId "pool-range-order"

        let manifest =
            manifestForBlocks
                storagePoolId
                payload
                [|
                    firstBlock, 0L, int64 first.Length
                    secondBlock, 99L, int64 second.Length
                |]

        let target = fileVersion "/invalid-range.bin" payload
        target.ContentReference <- FileContentReference.FileManifest manifest
        let firstPlacement = placementFor storagePoolId firstBlock.Address "stored/range/first"
        let secondPlacement = placementFor storagePoolId secondBlock.Address "stored/range/second"
        let objects = Dictionary<string, byte array>()
        objects[firstPlacement.ObjectKey] <- firstBlock.Payload
        objects[secondPlacement.ObjectKey] <- secondBlock.Payload
        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        addMetadata metadata manifest storagePoolId manifest.Blocks[0] firstPlacement
        addMetadata metadata manifest storagePoolId manifest.Blocks[1] secondPlacement

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom metadata (ResizeArray<ContentBlockAddress array>()))
            (contentBlockReaderFrom objects (ResizeArray<ContentBlockAddress * ContentBlockStoragePlacement>()))
            target
            "normal/invalid-range.bin"
        |> expectErrorContains "BlockRangeOutOfOrder"

    /// Verifies that manifest Backed Normal Download Rejects Size Mismatch.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsSizeMismatch() =
        let payload = bytes "size mismatch"
        let target, manifest, _ = manifestFile "/size-mismatch.bin" (StoragePoolId "pool-size") payload
        let mismatchedManifest = { manifest with Size = manifest.Size + 1L }
        target.ContentReference <- FileContentReference.FileManifest mismatchedManifest

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom (Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()) (ResizeArray()))
            (contentBlockReaderFrom (Dictionary<string, byte array>()) (ResizeArray()))
            target
            "normal/size-mismatch.bin"
        |> expectErrorContains "FileManifest.Size"

    /// Verifies that manifest Backed Normal Download Rejects Stale Active Range Evidence.
    [<Test>]
    member _.ManifestBackedNormalDownloadRejectsStaleActiveRangeEvidence() =
        let payload = bytes "stale placement range"
        let target, manifest, blocks = manifestFile "/stale-range.bin" (StoragePoolId "pool-stale") payload
        let placement = placementFor manifest.StoragePoolId blocks[0].Address "recorded/stale-range"

        let staleMetadata =
            { metadataFor manifest.StoragePoolId manifest.Blocks[0] placement with
                Ranges =
                    [|
                        { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = manifest.Blocks[0].Size }
                    |]
            }

        let metadata = Dictionary<StoragePoolId * ManifestAddress * ContentBlockAddress, ContentBlockMetadata>()
        metadata[(manifest.StoragePoolId, manifest.ManifestAddress, blocks[0].Address)] <- staleMetadata

        materializeBytes
            failingWholeFileReader
            (metadataResolverFrom metadata (ResizeArray<ContentBlockAddress array>()))
            (contentBlockReaderFrom (Dictionary<string, byte array>()) (ResizeArray()))
            target
            "normal/stale-range.bin"
        |> expectErrorContains "active ranges do not cover"
