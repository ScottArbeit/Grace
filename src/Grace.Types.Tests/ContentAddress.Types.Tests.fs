namespace Grace.Types.Tests

open System
open System.IO
open System.Text
open System.Threading
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework

type private ChunkedReadStream(bytes: byte array, maxBytesPerRead: int) =
    inherit MemoryStream(bytes)

    override this.ReadAsync(buffer: byte array, offset: int, count: int, cancellationToken: CancellationToken) =
        ``base``.ReadAsync(buffer, offset, Math.Min(count, maxBytesPerRead), cancellationToken)

[<Parallelizable(ParallelScope.All)>]
type ContentAddressTypesTests() =
    let runTask (work: System.Threading.Tasks.Task<'T>) = work.GetAwaiter().GetResult()

    [<Test>]
    member _.Blake3KnownVectorsPass() =
        Assert.That(ContentAddress.computeBlake3Hex Array.empty, Is.EqualTo("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"))

        Assert.That(
            ContentAddress.computeBlake3Hex (Encoding.UTF8.GetBytes("abc")),
            Is.EqualTo("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85")
        )

    [<Test>]
    member _.FileHashKnownVectorsAreByteOnly() =
        let vectors =
            [
                "empty",
                Array.empty,
                "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                "abc",
                Encoding.UTF8.GetBytes("abc"),
                "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                "binary",
                [|
                    0uy
                    1uy
                    2uy
                    3uy
                    4uy
                    255uy
                    128uy
                    64uy
                    10uy
                    13uy
                |],
                "08127a2b7e4a048ef7db8e3a94a4134688b78630e2fed93d58a1aaa14c51402e",
                "75a6070abf8bf13e756be4607e09f22fa9a7e4d737ba4d354dfd85b4437b1ec2"
            ]

        for name, bytes, expectedBlake3, expectedSha256 in vectors do
            use blake3Stream = new ChunkedReadStream(bytes, 3)
            let blake3Hash = runTask (Services.computeBlake3ForFile blake3Stream)

            use sha256Stream = new ChunkedReadStream(bytes, 3)
            let sha256Hash = runTask (Services.computeSha256ForFile sha256Stream (RelativePath $"vectors/{name}.bin"))

            Assert.That(blake3Hash, Is.EqualTo(FileContentHash expectedBlake3), $"BLAKE3 vector failed for {name}.")
            Assert.That(sha256Hash, Is.EqualTo(Sha256Hash expectedSha256), $"SHA-256 vector failed for {name}.")

    [<Test>]
    member _.CombinedFileHashesMatchKnownVectors() =
        let vectors =
            [
                "empty",
                Array.empty,
                "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                "abc",
                Encoding.UTF8.GetBytes("abc"),
                "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                "binary",
                [|
                    0uy
                    1uy
                    2uy
                    3uy
                    4uy
                    255uy
                    128uy
                    64uy
                    10uy
                    13uy
                |],
                "08127a2b7e4a048ef7db8e3a94a4134688b78630e2fed93d58a1aaa14c51402e",
                "75a6070abf8bf13e756be4607e09f22fa9a7e4d737ba4d354dfd85b4437b1ec2"
            ]

        for name, bytes, expectedBlake3, expectedSha256 in vectors do
            use stream = new ChunkedReadStream(bytes, 3)
            let sha256Hash, blake3Hash = runTask (Services.computeHashesForFile stream (RelativePath $"vectors/{name}.bin"))

            Assert.That(blake3Hash, Is.EqualTo(Blake3Hash expectedBlake3), $"BLAKE3 vector failed for {name}.")
            Assert.That(sha256Hash, Is.EqualTo(Sha256Hash expectedSha256), $"SHA-256 vector failed for {name}.")

    [<Test>]
    member _.FileHashesIgnoreRelativePath() =
        let bytes = Encoding.UTF8.GetBytes("same file bytes, different paths")

        use firstBlake3Stream = new ChunkedReadStream(bytes, 5)
        use secondBlake3Stream = new ChunkedReadStream(bytes, 5)
        let firstBlake3 = runTask (Services.computeBlake3ForFile firstBlake3Stream)
        let secondBlake3 = runTask (Services.computeBlake3ForFile secondBlake3Stream)

        use firstSha256Stream = new ChunkedReadStream(bytes, 5)
        use secondSha256Stream = new ChunkedReadStream(bytes, 5)

        let firstSha256 = runTask (Services.computeSha256ForFile firstSha256Stream (RelativePath "src/one.bin"))

        let secondSha256 = runTask (Services.computeSha256ForFile secondSha256Stream (RelativePath "other/two.bin"))

        Assert.That(firstBlake3, Is.EqualTo(secondBlake3))
        Assert.That(firstSha256, Is.EqualTo(secondSha256))

    [<Test>]
    member _.AddressValidationRequiresLowercaseSixtyFourHexCharacters() =
        Assert.That(ContentAddress.isValidAddress ("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"), Is.True)

        Assert.That(ContentAddress.isValidAddress ("AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADC112B7CC9A93CAE41F3262"), Is.False)

        Assert.That(ContentAddress.isValidAddress ("af1349"), Is.False)
        Assert.That(ContentAddress.isValidAddress ("gf1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"), Is.False)
        Assert.That(ContentAddress.isValidAddress (null), Is.False)

    [<Test>]
    member _.ChunkAddressFromBytesUsesLowercaseBlake3Hex() =
        let address = ContentAddress.computeChunkAddress (Encoding.UTF8.GetBytes("BLAKE3"))

        Assert.That(address, Is.EqualTo(ChunkAddress "f890484173e516bfd935ef3d22b912dc9738de38743993cfedf2c9473b3216a4"))
        Assert.That(ContentAddress.isValidAddress address, Is.True)

    [<Test>]
    member _.ContentBlockPreimageAndAddressAreStableAndPathIndependent() =
        let chunkA = ContentAddress.computeChunkAddress (Encoding.UTF8.GetBytes("alpha chunk"))
        let chunkB = ContentAddress.computeChunkAddress (Encoding.UTF8.GetBytes("beta chunk"))

        let expectedPreimage =
            [
                "grace.cas.v1.content-block"
                "format:raw-chunks"
                "chunk-count:2"
                $"chunk:0:{chunkA}"
                $"chunk:1:{chunkB}"
            ]
            |> String.concat "\n"
            |> fun value -> value + "\n"

        let preimage = ContentAddress.contentBlockPreimage "raw-chunks" [ chunkA; chunkB ]
        let address = ContentAddress.computeContentBlockAddress "raw-chunks" [ chunkA; chunkB ]

        Assert.That(preimage, Is.EqualTo(expectedPreimage))
        Assert.That(address, Is.EqualTo(ContentBlockAddress "75359c5d838dda90b12c6cbcdd9b71a1f193daf6c23fa2bfd496065bed57a30f"))
        Assert.That(ContentAddress.isValidAddress address, Is.True)

        let sameContentFromDifferentPath = ContentAddress.contentBlockPreimage "raw-chunks" [ chunkA; chunkB ]
        let reversedContent = ContentAddress.contentBlockPreimage "raw-chunks" [ chunkB; chunkA ]

        Assert.That(sameContentFromDifferentPath, Is.EqualTo(preimage))
        Assert.That(reversedContent, Is.Not.EqualTo(preimage))

    [<Test>]
    member _.ManifestPreimageAndAddressAreStableAndPathIndependent() =
        let blockAddress = ContentBlockAddress "75359c5d838dda90b12c6cbcdd9b71a1f193daf6c23fa2bfd496065bed57a30f"

        let blocks =
            [
                ContentBlock.Create(blockAddress, 0L, 4096L)
                ContentBlock.Create(blockAddress, 4096L, 4096L)
            ]

        let manifest = FileManifest.Create(ManifestAddress "", 8192L, blocks)
        let fileContentHash = FileContentHash "fb283dfc43483eae4cfb9f3eb0d1da0c46c9c557acfab283907eeb3803008bae"
        let manifest = FileManifest.Create(ManifestAddress "", RabinChunking.SuiteName, fileContentHash, manifest.Size, blocks)

        let expectedPreimage =
            [
                "grace.cas.v1.file-manifest"
                $"chunking-suite:{RabinChunking.SuiteName}"
                $"file-content-hash:{fileContentHash}"
                "size:8192"
                "block-count:2"
                $"block:0:{blockAddress}:0:4096"
                $"block:1:{blockAddress}:4096:4096"
            ]
            |> String.concat "\n"
            |> fun value -> value + "\n"

        let preimage = ContentAddress.manifestPreimage manifest.ChunkingSuiteId manifest.FileContentHash manifest.Size manifest.Blocks
        let address = ContentAddress.computeManifestAddressForManifest manifest

        let firstPath = FileVersion.Create "src/assets/movie.bin" "abc123" "https://example.test/a" true manifest.Size
        firstPath.ContentReference <- FileContentReference.FileManifest { manifest with ManifestAddress = address }

        let secondPath = FileVersion.Create "other/path/movie.bin" "abc123" "https://example.test/b" true manifest.Size
        secondPath.ContentReference <- FileContentReference.FileManifest { manifest with ManifestAddress = address }

        Assert.That(preimage, Is.EqualTo(expectedPreimage))
        Assert.That(address, Is.EqualTo(ManifestAddress "5dd11fdb1534c0f1c4fecca3c07498f9b59a7dff26d69fe78e43f7ce50d90895"))
        Assert.That(ContentAddress.isValidAddress address, Is.True)
        Assert.That(firstPath.ContentReference, Is.EqualTo(secondPath.ContentReference))
