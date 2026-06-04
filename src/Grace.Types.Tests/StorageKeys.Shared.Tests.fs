namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Common
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type StorageKeysSharedTests() =
    [<Test>]
    member _.WholeFileContentObjectKeyMatchesExistingBlobKeyShape() =
        let fileVersion =
            FileVersion.Create
                "src/Grace.Server/Storage.Server.fs"
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                "https://example.test/blob"
                false
                1234L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(key, Is.EqualTo("src/Grace.Server/Storage.Server.fs/Storage.Server_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.fs"))

    [<Test>]
    member _.WholeFileContentObjectKeyPreservesExtensionlessBlobKeyShape() =
        let fileVersion = FileVersion.Create "Dockerfile" "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789" "" false 2048L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(key, Is.EqualTo("Dockerfile/Dockerfile_abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"))

    [<Test>]
    member _.ContentBlockObjectKeyDependsOnlyOnContentBlockAddress() =
        let address = ContentBlockAddress "block-blake3-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let firstKey = StorageKeys.contentBlockObjectKey address
        let secondKey = StorageKeys.contentBlockObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/content-blocks/block-blake3-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))
        Assert.That(secondKey, Is.EqualTo(firstKey))

    [<Test>]
    member _.ManifestObjectKeyDependsOnlyOnManifestAddress() =
        let address = ManifestAddress "manifest-blake3-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let firstKey = StorageKeys.fileManifestObjectKey address
        let secondKey = StorageKeys.fileManifestObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/file-manifests/manifest-blake3-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
        Assert.That(secondKey, Is.EqualTo(firstKey))

    [<Test>]
    member _.ContentBlockMetadataObjectKeyDependsOnlyOnContentBlockAddress() =
        let address = ContentBlockAddress "block-blake3-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"

        let firstKey = StorageKeys.contentBlockMetadataObjectKey address
        let secondKey = StorageKeys.contentBlockMetadataObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/content-block-metadata/block-blake3-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc.json"))

        Assert.That(secondKey, Is.EqualTo(firstKey))
