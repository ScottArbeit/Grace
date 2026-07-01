namespace Grace.Types.Tests

open FsCheck
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.Text

/// Contains tests covering manifest validation shared behavior.
[<Parallelizable(ParallelScope.All)>]
type ManifestValidationSharedTests() =
    /// Verifies that bytes.
    static member private Bytes(value: string) = Encoding.UTF8.GetBytes(value)

    /// Verifies that expect encoded ok.
    static member private ExpectEncodedOk(result: Result<ContentBlockFormat.EncodedContentBlock, ContentBlockFormat.ContentBlockFormatError>) =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected Ok but got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Verifies that content block payload.
    static member private ContentBlockPayload physicalOffset bytes : ContentBlockFormat.EncodedContentBlock =
        ContentBlockFormat.encode [ ContentBlockFormat.createChunk physicalOffset bytes ]
        |> ManifestValidationSharedTests.ExpectEncodedOk

    /// Verifies that build manifest.
    static member private BuildManifest chunkingSuiteId bytes (blockPayloads: ContentBlockFormat.EncodedContentBlock array) =
        /// Tracks offset changes so this scenario can assert the resulting side effect explicitly.
        let mutable offset = 0L

        let blocks =
            blockPayloads
            |> Array.map (fun payload ->
                let contentLength =
                    payload.Chunks
                    |> Array.sumBy (fun chunk -> chunk.Length)
                    |> int64

                let block = ContentBlock.Create(payload.Address, offset, contentLength)
                offset <- offset + contentLength
                block)
            |> Array.toList

        let fileContentHash = FileContentHash(ContentAddress.computeBlake3Hex bytes)

        let manifest = FileManifest.Create(ManifestAddress String.Empty, chunkingSuiteId, fileContentHash, int64 bytes.Length, blocks)

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Verifies that payload reference.
    static member private PayloadReference(block: ContentBlockFormat.EncodedContentBlock) = ManifestValidation.createBlockPayload block.Address block.Payload

    /// Verifies that valid manifest reconstructs bytes and validates stable address.
    [<Test>]
    member _.ValidManifestReconstructsBytesAndValidatesStableAddress() =
        let alpha = ManifestValidationSharedTests.Bytes "alpha logical range"
        let beta = ManifestValidationSharedTests.Bytes "beta logical range"
        let fileBytes = Array.concat [ alpha; beta ]

        let alphaBlock = ManifestValidationSharedTests.ContentBlockPayload 4096L alpha
        let betaBlock = ManifestValidationSharedTests.ContentBlockPayload 8192L beta

        let manifest = ManifestValidationSharedTests.BuildManifest RabinChunking.SuiteName fileBytes [| alphaBlock; betaBlock |]

        let expectedAddress = ContentAddress.computeManifestAddressForManifest manifest

        let validation =
            ManifestValidation.validate
                RabinChunking.SuiteName
                manifest
                [
                    ManifestValidationSharedTests.PayloadReference alphaBlock
                    ManifestValidationSharedTests.PayloadReference betaBlock
                ]

        match validation with
        | Ok reconstructed ->
            Assert.That((reconstructed = fileBytes), Is.True)
            Assert.That(manifest.ManifestAddress, Is.EqualTo(expectedAddress))
            Assert.That(ContentAddress.isValidAddress (manifest.ManifestAddress), Is.True)
        | Error error -> Assert.Fail($"Expected valid manifest but got {error}.")

    /// Verifies that invalid manifest invariants are rejected.
    [<Test>]
    member _.InvalidManifestInvariantsAreRejected() =
        let alpha = ManifestValidationSharedTests.Bytes "alpha"
        let beta = ManifestValidationSharedTests.Bytes "beta"
        let fileBytes = Array.concat [ alpha; beta ]

        let alphaBlock = ManifestValidationSharedTests.ContentBlockPayload 0L alpha
        let betaBlock = ManifestValidationSharedTests.ContentBlockPayload 1024L beta

        let validManifest = ManifestValidationSharedTests.BuildManifest RabinChunking.SuiteName fileBytes [| alphaBlock; betaBlock |]

        let payloads =
            [
                ManifestValidationSharedTests.PayloadReference alphaBlock
                ManifestValidationSharedTests.PayloadReference betaBlock
            ]

        /// Exercises with address coverage for the types manifest Validation contract.
        let withAddress manifest = { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

        let unordered =
            { validManifest with
                Blocks =
                    [
                        ContentBlock.Create(betaBlock.Address, int64 alpha.Length, int64 beta.Length)
                        ContentBlock.Create(alphaBlock.Address, 0L, int64 alpha.Length)
                    ]
                    |> System.Collections.Generic.List<ContentBlock>
            }
            |> withAddress

        let zeroSized =
            { validManifest with
                Blocks =
                    [
                        ContentBlock.Create(alphaBlock.Address, 0L, 0L)
                        ContentBlock.Create(betaBlock.Address, 0L, int64 beta.Length)
                    ]
                    |> System.Collections.Generic.List<ContentBlock>
            }
            |> withAddress

        let gap =
            { validManifest with
                Blocks =
                    [
                        ContentBlock.Create(alphaBlock.Address, 0L, int64 alpha.Length)
                        ContentBlock.Create(betaBlock.Address, int64 alpha.Length + 1L, int64 beta.Length)
                    ]
                    |> System.Collections.Generic.List<ContentBlock>
            }
            |> withAddress

        let undersized =
            { validManifest with Size = validManifest.Size + 1L }
            |> withAddress

        let wrongSuite =
            { validManifest with ChunkingSuiteId = ChunkingSuiteId "different-suite" }
            |> withAddress

        let wrongHash =
            { validManifest with FileContentHash = FileContentHash(ContentAddress.computeBlake3Hex (ManifestValidationSharedTests.Bytes "wrong")) }
            |> withAddress

        let cases =
            [
                "unordered", unordered, ManifestValidation.BlockRangeOutOfOrder 0
                "zero-sized", zeroSized, ManifestValidation.BlockRangeNotPositive 0
                "gap", gap, ManifestValidation.BlockRangeOutOfOrder 1
                "undersized", undersized, ManifestValidation.ManifestSizeMismatch(undersized.Size, validManifest.Size)
                "wrong-suite", wrongSuite, ManifestValidation.ChunkingSuiteMismatch(RabinChunking.SuiteName, wrongSuite.ChunkingSuiteId)
                "wrong-hash", wrongHash, ManifestValidation.FileContentHashMismatch(wrongHash.FileContentHash, validManifest.FileContentHash)
            ]

        for name, manifest, expectedError in cases do
            match ManifestValidation.validate RabinChunking.SuiteName manifest payloads with
            | Error error -> Assert.That(error, Is.EqualTo(expectedError), $"{name}")
            | Ok _ -> Assert.Fail($"Expected {name} manifest to be rejected.")

    /// Verifies that malformed manifest fields return validation errors.
    [<Test>]
    member _.MalformedManifestFieldsReturnValidationErrors() =
        let bytes = ManifestValidationSharedTests.Bytes "payload"
        let block = ManifestValidationSharedTests.ContentBlockPayload 0L bytes
        let validManifest = ManifestValidationSharedTests.BuildManifest RabinChunking.SuiteName bytes [| block |]

        let payloads =
            [
                ManifestValidationSharedTests.PayloadReference block
            ]

        let invalidManifestAddress = { validManifest with ManifestAddress = ManifestAddress "not-a-blake3-address" }

        let invalidFileHash = { validManifest with FileContentHash = FileContentHash "not-a-blake3-address" }

        let invalidBlockAddress =
            { validManifest with
                Blocks =
                    [
                        ContentBlock.Create(ContentBlockAddress "not-a-blake3-address", 0L, int64 bytes.Length)
                    ]
                    |> System.Collections.Generic.List<ContentBlock>
            }

        let nullBlock =
            { validManifest with
                Blocks =
                    [ Unchecked.defaultof<ContentBlock> ]
                    |> System.Collections.Generic.List<ContentBlock>
            }

        let cases =
            [
                "empty-expected-suite", ChunkingSuiteId String.Empty, validManifest, ManifestValidation.InvalidChunkingSuiteId(ChunkingSuiteId String.Empty)
                "invalid-manifest-address",
                RabinChunking.SuiteName,
                invalidManifestAddress,
                ManifestValidation.InvalidManifestAddress invalidManifestAddress.ManifestAddress
                "invalid-file-hash", RabinChunking.SuiteName, invalidFileHash, ManifestValidation.InvalidFileContentHash invalidFileHash.FileContentHash
                "invalid-block-address",
                RabinChunking.SuiteName,
                invalidBlockAddress,
                ManifestValidation.InvalidContentBlockAddress(0, ContentBlockAddress "not-a-blake3-address")
                "null-block", RabinChunking.SuiteName, nullBlock, ManifestValidation.NullContentBlock 0
            ]

        for name, suite, manifest, expectedError in cases do
            match ManifestValidation.validate suite manifest payloads with
            | Error error -> Assert.That(error, Is.EqualTo(expectedError), $"{name}")
            | Ok _ -> Assert.Fail($"Expected {name} manifest to be rejected.")

    /// Verifies that fs check reconstruction and address properties hold.
    [<Test>]
    member _.FsCheckReconstructionAndAddressPropertiesHold() =
        /// Defines the property assertion used to explore generated inputs for the types manifest Validation invariant.
        let property (NonNegativeInt requestedLength) =
            let length = Math.Min(requestedLength, 256 * 1024)
            let bytes = Array.init length (fun index -> byte ((index * 31 + length) % 251))

            let blockPayloads =
                RabinChunking.chunkBytes bytes
                |> Array.map (fun chunk ->
                    let chunkBytes = bytes[int chunk.Offset .. int chunk.Offset + chunk.Length - 1]
                    ManifestValidationSharedTests.ContentBlockPayload chunk.Offset chunkBytes)

            if length = 0 then
                blockPayloads.Length = 0
            else
                let manifest = ManifestValidationSharedTests.BuildManifest RabinChunking.SuiteName bytes blockPayloads

                let rerunManifest = ManifestValidationSharedTests.BuildManifest RabinChunking.SuiteName bytes blockPayloads

                let payloads =
                    blockPayloads
                    |> Array.rev
                    |> Array.map ManifestValidationSharedTests.PayloadReference

                match ManifestValidation.validate RabinChunking.SuiteName manifest payloads with
                | Ok reconstructed ->
                    reconstructed = bytes
                    && manifest.ManifestAddress = rerunManifest.ManifestAddress
                | Error _ -> false

        Check.QuickThrowOnFailure property
