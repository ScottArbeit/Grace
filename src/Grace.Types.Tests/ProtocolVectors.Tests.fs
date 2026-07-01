namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

/// Contains tests covering protocol vectors behavior.
[<Parallelizable(ParallelScope.All)>]
type ProtocolVectorsTests() =
    /// Verifies that find repo root.
    static member private FindRepoRoot() =
        /// Tracks directory changes so this scenario can assert the resulting side effect explicitly.
        let mutable directory = DirectoryInfo(TestContext.CurrentContext.TestDirectory)
        /// Tracks result changes so this scenario can assert the resulting side effect explicitly.
        let mutable result = None

        while Option.isNone result && not (isNull directory) do
            let vectorPath = Path.Combine(directory.FullName, "test-vectors", "protocol")

            if Directory.Exists vectorPath then
                result <- Some directory.FullName
            else
                directory <- directory.Parent

        match result with
        | Some path -> path
        | None -> failwith "Could not find repository root containing test-vectors/protocol."

    /// Verifies that vector path.
    static member private VectorPath(fileName: string) = Path.Combine(ProtocolVectorsTests.FindRepoRoot(), "test-vectors", "protocol", fileName)

    /// Verifies that load json.
    static member private LoadJson(fileName: string) =
        File.ReadAllText(ProtocolVectorsTests.VectorPath fileName)
        |> JsonDocument.Parse

    /// Verifies that required property.
    static member private RequiredProperty (name: string) (element: JsonElement) = element.GetProperty(name)

    /// Verifies that string property.
    static member private StringProperty name element =
        (ProtocolVectorsTests.RequiredProperty name element)
            .GetString()

    /// Verifies that int64 property.
    static member private Int64Property name element =
        (ProtocolVectorsTests.RequiredProperty name element)
            .GetInt64()

    /// Verifies that array property.
    static member private ArrayProperty name element =
        (ProtocolVectorsTests.RequiredProperty name element)
            .EnumerateArray()
        |> Seq.toArray

    /// Verifies that bytes.
    static member private Bytes(value: string) = Encoding.UTF8.GetBytes(value)

    /// Verifies that expect encoded ok.
    static member private ExpectEncodedOk(result: Result<ContentBlockFormat.EncodedContentBlock, ContentBlockFormat.ContentBlockFormatError>) =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected ContentBlockFormat.encode Ok but got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Verifies that encoded block.
    static member private EncodedBlock physicalOffset utf8 =
        ContentBlockFormat.encode [ ContentBlockFormat.createChunk physicalOffset (ProtocolVectorsTests.Bytes utf8) ]
        |> ProtocolVectorsTests.ExpectEncodedOk

    /// Verifies that build manifest.
    static member private BuildManifest chunkingSuiteId fileBytes (blocks: (ContentBlockFormat.EncodedContentBlock * int64 * int64) array) =
        let contentBlocks =
            blocks
            |> Array.map (fun (block, offset, size) -> ContentBlock.Create(block.Address, offset, size))
            |> Array.toList

        let fileContentHash =
            ContentAddress.computeBlake3Hex fileBytes
            |> FileContentHash

        let manifest = FileManifest.Create(ManifestAddress String.Empty, chunkingSuiteId, fileContentHash, int64 fileBytes.Length, contentBlocks)

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Verifies that payload reference.
    static member private PayloadReference(block: ContentBlockFormat.EncodedContentBlock) = ManifestValidation.createBlockPayload block.Address block.Payload

    /// Verifies that error kind.
    static member private ErrorKind(error: ManifestValidation.ManifestValidationError) =
        match error with
        | ManifestValidation.NullManifest -> "NullManifest"
        | ManifestValidation.NullBlockPayloadSequence -> "NullBlockPayloadSequence"
        | ManifestValidation.NullBlockPayload _ -> "NullBlockPayload"
        | ManifestValidation.NullBlockPayloadBytes _ -> "NullBlockPayloadBytes"
        | ManifestValidation.EmptyManifest -> "EmptyManifest"
        | ManifestValidation.InvalidManifestSize -> "InvalidManifestSize"
        | ManifestValidation.InvalidChunkingSuiteId _ -> "InvalidChunkingSuiteId"
        | ManifestValidation.InvalidFileContentHash _ -> "InvalidFileContentHash"
        | ManifestValidation.InvalidManifestAddress _ -> "InvalidManifestAddress"
        | ManifestValidation.NullContentBlock _ -> "NullContentBlock"
        | ManifestValidation.InvalidContentBlockAddress _ -> "InvalidContentBlockAddress"
        | ManifestValidation.ChunkingSuiteMismatch _ -> "ChunkingSuiteMismatch"
        | ManifestValidation.ManifestAddressMismatch _ -> "ManifestAddressMismatch"
        | ManifestValidation.BlockRangeOutOfOrder _ -> "BlockRangeOutOfOrder"
        | ManifestValidation.BlockRangeNotPositive _ -> "BlockRangeNotPositive"
        | ManifestValidation.MissingContentBlockPayload _ -> "MissingContentBlockPayload"
        | ManifestValidation.ContentBlockPayloadInvalid _ -> "ContentBlockPayloadInvalid"
        | ManifestValidation.ContentBlockPayloadSizeMismatch _ -> "ContentBlockPayloadSizeMismatch"
        | ManifestValidation.FileContentHashMismatch _ -> "FileContentHashMismatch"
        | ManifestValidation.ManifestSizeMismatch _ -> "ManifestSizeMismatch"

    /// Verifies that content address vectors match deterministic helpers.
    [<Test>]
    member _.ContentAddressVectorsMatchDeterministicHelpers() =
        use document = ProtocolVectorsTests.LoadJson "content-addresses.v1.json"
        let root = document.RootElement
        Assert.That(ProtocolVectorsTests.Int64Property "schemaVersion" root, Is.EqualTo(1L))

        let vectors = root.GetProperty("vectors")

        for vector in ProtocolVectorsTests.ArrayProperty "blake3" vectors do
            let utf8 = ProtocolVectorsTests.StringProperty "utf8" vector
            let expectedHash = ProtocolVectorsTests.StringProperty "hash" vector
            Assert.That(ContentAddress.computeBlake3Hex (ProtocolVectorsTests.Bytes utf8), Is.EqualTo(expectedHash))

        let validation = vectors.GetProperty("addressValidation")

        for address in ProtocolVectorsTests.ArrayProperty "valid" validation do
            Assert.That(ContentAddress.isValidAddress (address.GetString()), Is.True)

        for address in ProtocolVectorsTests.ArrayProperty "invalid" validation do
            Assert.That(ContentAddress.isValidAddress (address.GetString()), Is.False)

        let contentBlock = vectors.GetProperty("contentBlock")
        let chunks = ProtocolVectorsTests.ArrayProperty "chunks" contentBlock

        let chunkAddresses =
            chunks
            |> Array.map (fun chunk ->
                let utf8 = ProtocolVectorsTests.StringProperty "utf8" chunk
                let address = ContentAddress.computeChunkAddress (ProtocolVectorsTests.Bytes utf8)
                Assert.That(address, Is.EqualTo(ChunkAddress(ProtocolVectorsTests.StringProperty "address" chunk)))
                address)

        let blockFormat = ProtocolVectorsTests.StringProperty "format" contentBlock
        Assert.That(blockFormat, Is.EqualTo(ContentBlockFormat.FormatName))

        Assert.That(ContentAddress.contentBlockPreimage blockFormat chunkAddresses, Is.EqualTo(ProtocolVectorsTests.StringProperty "preimage" contentBlock))

        Assert.That(
            ContentAddress.computeContentBlockAddress blockFormat chunkAddresses,
            Is.EqualTo(ContentBlockAddress(ProtocolVectorsTests.StringProperty "address" contentBlock))
        )

        Assert.That(
            ContentAddress.computeContentBlockAddress blockFormat (Array.rev chunkAddresses),
            Is.EqualTo(ContentBlockAddress(ProtocolVectorsTests.StringProperty "reorderedAddress" contentBlock))
        )

        let fileManifest = vectors.GetProperty("fileManifest")

        let blocks =
            ProtocolVectorsTests.ArrayProperty "blocks" fileManifest
            |> Array.map (fun block ->
                ContentBlock.Create(
                    ContentBlockAddress(ProtocolVectorsTests.StringProperty "address" block),
                    ProtocolVectorsTests.Int64Property "offset" block,
                    ProtocolVectorsTests.Int64Property "size" block
                ))
            |> Array.toList

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId(ProtocolVectorsTests.StringProperty "chunkingSuiteId" fileManifest),
                FileContentHash(ProtocolVectorsTests.StringProperty "fileContentHash" fileManifest),
                ProtocolVectorsTests.Int64Property "size" fileManifest,
                blocks
            )

        Assert.That(
            ContentAddress.manifestPreimage manifest.ChunkingSuiteId manifest.FileContentHash manifest.Size manifest.Blocks,
            Is.EqualTo(ProtocolVectorsTests.StringProperty "preimage" fileManifest)
        )

        Assert.That(
            ContentAddress.computeManifestAddressForManifest manifest,
            Is.EqualTo(ManifestAddress(ProtocolVectorsTests.StringProperty "manifestAddress" fileManifest))
        )

    /// Verifies that manifest validation vectors match payload and rejection contract.
    [<Test>]
    member _.ManifestValidationVectorsMatchPayloadAndRejectionContract() =
        use document = ProtocolVectorsTests.LoadJson "manifest-validation.v1.json"
        let root = document.RootElement
        Assert.That(ProtocolVectorsTests.Int64Property "schemaVersion" root, Is.EqualTo(1L))

        let chunkingSuiteId = ChunkingSuiteId(ProtocolVectorsTests.StringProperty "chunkingSuiteId" root)
        let file = root.GetProperty("file")
        let fileBytes = ProtocolVectorsTests.Bytes(ProtocolVectorsTests.StringProperty "utf8" file)

        Assert.That(ContentAddress.computeBlake3Hex fileBytes, Is.EqualTo(ProtocolVectorsTests.StringProperty "fileContentHash" file))
        Assert.That(int64 fileBytes.Length, Is.EqualTo(ProtocolVectorsTests.Int64Property "size" file))

        let encodedByName = Dictionary<string, ContentBlockFormat.EncodedContentBlock>(StringComparer.Ordinal)

        for blockVector in ProtocolVectorsTests.ArrayProperty "contentBlocks" root do
            let name = ProtocolVectorsTests.StringProperty "name" blockVector

            let encoded =
                ProtocolVectorsTests.EncodedBlock
                    (ProtocolVectorsTests.Int64Property "physicalOffset" blockVector)
                    (ProtocolVectorsTests.StringProperty "utf8" blockVector)

            encodedByName.Add(name, encoded)
            Assert.That(encoded.Address, Is.EqualTo(ContentBlockAddress(ProtocolVectorsTests.StringProperty "address" blockVector)))
            Assert.That(Convert.ToBase64String encoded.Payload, Is.EqualTo(ProtocolVectorsTests.StringProperty "payloadBase64" blockVector))
            Assert.That(int64 encoded.Payload.Length, Is.GreaterThan(ProtocolVectorsTests.Int64Property "logicalSize" blockVector))

        let manifestVector = root.GetProperty("manifest")

        let manifestBlocks =
            ProtocolVectorsTests.ArrayProperty "blocks" manifestVector
            |> Array.map (fun block ->
                let encoded = encodedByName[ProtocolVectorsTests.StringProperty "name" block]
                encoded, ProtocolVectorsTests.Int64Property "offset" block, ProtocolVectorsTests.Int64Property "size" block)

        let manifest = ProtocolVectorsTests.BuildManifest chunkingSuiteId fileBytes manifestBlocks

        Assert.That(manifest.ManifestAddress, Is.EqualTo(ManifestAddress(ProtocolVectorsTests.StringProperty "manifestAddress" manifestVector)))
        Assert.That(manifest.Size, Is.EqualTo(ProtocolVectorsTests.Int64Property "size" manifestVector))

        let payloads =
            encodedByName.Values
            |> Seq.map ProtocolVectorsTests.PayloadReference
            |> Seq.toList

        match ManifestValidation.validate chunkingSuiteId manifest payloads with
        | Ok reconstructed ->
            Assert.That(
                Encoding.UTF8.GetString reconstructed,
                Is.EqualTo(
                    root
                        .GetProperty("success")
                        .GetProperty("expectedUtf8")
                        .GetString()
                )
            )
        | Error error -> Assert.Fail($"Expected manifest vector to validate but got {error}.")

        let expectedCases =
            ProtocolVectorsTests.ArrayProperty "rejectionCases" root
            |> Array.map (fun item -> ProtocolVectorsTests.StringProperty "name" item, ProtocolVectorsTests.StringProperty "expectedErrorKind" item)
            |> dict

        let alpha = encodedByName["alpha"]
        let beta = encodedByName["beta"]

        let tamperedPayload = Array.copy alpha.Payload
        tamperedPayload[0] <- tamperedPayload[0] ^^^ 0x01uy

        let invalidFooter = Array.copy alpha.Payload
        invalidFooter[invalidFooter.Length - 1] <- invalidFooter[invalidFooter.Length - 1] ^^^ 0x01uy

        let staleManifest = { manifest with FileContentHash = FileContentHash(ContentAddress.computeBlake3Hex (ProtocolVectorsTests.Bytes "different bytes")) }

        let hashMismatch = { staleManifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest staleManifest }

        let reordered =
            { manifest with
                Blocks =
                    [
                        ContentBlock.Create(beta.Address, int64 alpha.Chunks[0].Length, int64 beta.Chunks[0].Length)
                        ContentBlock.Create(alpha.Address, 0L, int64 alpha.Chunks[0].Length)
                    ]
                    |> List<ContentBlock>
            }

        let mismatchedPayload =
            ManifestValidation.createBlockPayload (ContentBlockAddress(ProtocolVectorsTests.StringProperty "manifestAddress" manifestVector)) alpha.Payload

        let cases =
            [
                "tampered-payload-byte",
                manifest,
                [
                    ManifestValidation.createBlockPayload alpha.Address tamperedPayload
                    ProtocolVectorsTests.PayloadReference beta
                ]
                "invalid-compact-block-footer",
                manifest,
                [
                    ManifestValidation.createBlockPayload alpha.Address invalidFooter
                    ProtocolVectorsTests.PayloadReference beta
                ]
                "reordered-manifest-blocks", reordered, payloads
                "stale-manifest-address", staleManifest, payloads
                "file-content-hash-mismatch", hashMismatch, payloads
                "missing-content-block",
                manifest,
                [
                    ProtocolVectorsTests.PayloadReference alpha
                ]
                "content-block-address-mismatch",
                manifest,
                [
                    mismatchedPayload
                    ProtocolVectorsTests.PayloadReference beta
                ]
            ]

        for name, manifest, payloads in cases do
            match ManifestValidation.validate chunkingSuiteId manifest payloads with
            | Error error -> Assert.That(ProtocolVectorsTests.ErrorKind error, Is.EqualTo(expectedCases[name]), name)
            | Ok _ -> Assert.Fail($"Expected {name} to be rejected.")

    /// Verifies that eligibility vectors document default boundary.
    [<Test>]
    member _.EligibilityVectorsDocumentDefaultBoundary() =
        use document = ProtocolVectorsTests.LoadJson "eligibility.v1.json"
        let root = document.RootElement
        let policy = root.GetProperty("policy")

        Assert.That(ProtocolVectorsTests.Int64Property "thresholdBytes" policy, Is.EqualTo(ManifestEligibilityPolicy.Default.ThresholdBytes))
        Assert.That(int (ProtocolVectorsTests.Int64Property "binaryScanBytes" policy), Is.EqualTo(ManifestEligibilityPolicy.Default.BinaryScanBytes))

        /// Exercises decide coverage for the types protocol Vectors contract.
        let decide kind uncompressedSize compressedSize =
            let isBinary = String.Equals(kind, "binary", StringComparison.Ordinal)
            let relevantSize = if isBinary then uncompressedSize else compressedSize

            if relevantSize
               >= ManifestEligibilityPolicy.Default.ThresholdBytes then
                "FileManifest"
            else
                "WholeFileContent"

        for vector in ProtocolVectorsTests.ArrayProperty "vectors" root do
            let kind = ProtocolVectorsTests.StringProperty "kind" vector
            let uncompressedSize = ProtocolVectorsTests.Int64Property "uncompressedSize" vector

            let compressedSize =
                match vector.GetProperty("compressedSize").ValueKind with
                | JsonValueKind.Null -> uncompressedSize
                | _ -> ProtocolVectorsTests.Int64Property "compressedSize" vector

            Assert.That(
                decide kind uncompressedSize compressedSize,
                Is.EqualTo(ProtocolVectorsTests.StringProperty "expectedReferenceType" vector),
                ProtocolVectorsTests.StringProperty "name" vector
            )
