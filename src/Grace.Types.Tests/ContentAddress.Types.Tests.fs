namespace Grace.Types.Tests

open System.Text
open Grace.Shared
open Grace.Types.Types
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type ContentAddressTypesTests() =
    [<Test>]
    member _.Blake3KnownVectorsPass() =
        Assert.That(ContentAddress.computeBlake3Hex Array.empty, Is.EqualTo("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"))

        Assert.That(
            ContentAddress.computeBlake3Hex (Encoding.UTF8.GetBytes("abc")),
            Is.EqualTo("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85")
        )

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
