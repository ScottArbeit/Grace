namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System

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
    member _.WholeFileContentObjectKeyIncludesBlake3WhenPresent() =
        let fileVersion =
            FileVersion.CreateWithHashes
                (RelativePath "src/Grace.Server/Storage.Server.fs")
                (Sha256Hash "shared-sha256")
                (Blake3Hash "first-blake3")
                String.Empty
                false
                1234L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(key, Is.EqualTo("src/Grace.Server/Storage.Server.fs/Storage.Server_shared-sha256_first-blake3.fs"))

    [<Test>]
    member _.WholeFileContentObjectKeyPreservesExtensionlessBlobKeyShape() =
        let fileVersion = FileVersion.Create "Dockerfile" "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789" "" false 2048L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(key, Is.EqualTo("Dockerfile/Dockerfile_abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"))

    [<Test>]
    member _.WholeFileContentObjectKeyIncludesBlake3ForExtensionlessFilesWhenPresent() =
        let fileVersion =
            FileVersion.CreateWithHashes (RelativePath "Dockerfile") (Sha256Hash "shared-sha256") (Blake3Hash "first-blake3") String.Empty false 2048L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(key, Is.EqualTo("Dockerfile/Dockerfile_shared-sha256_first-blake3"))

    [<Test>]
    member _.WholeFileContentObjectKeySeparatesSameSha256DifferentBlake3() =
        let first =
            FileVersion.CreateWithHashes (RelativePath "src/appsettings.json") (Sha256Hash "shared-sha256") (Blake3Hash "first-blake3") String.Empty false 512L

        let second =
            FileVersion.CreateWithHashes (RelativePath "src/appsettings.json") (Sha256Hash "shared-sha256") (Blake3Hash "second-blake3") String.Empty false 512L

        Assert.That(StorageKeys.wholeFileContentObjectKey first, Is.Not.EqualTo(StorageKeys.wholeFileContentObjectKey second))

    [<Test>]
    member _.ContentBlockObjectKeyUsesFourLevelDigestFanout() =
        let address = ContentBlockAddress "ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let firstKey = StorageKeys.contentBlockObjectKey address
        let secondKey = StorageKeys.contentBlockObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/content/ab/34/cd/56/ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))
        Assert.That(secondKey, Is.EqualTo(firstKey))

    [<Test>]
    member _.ContentBlockObjectKeyNormalizesUppercaseDigest() =
        let address = ContentBlockAddress "AB34CD567F43AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"

        let key = StorageKeys.contentBlockObjectKey address

        Assert.That(key, Is.EqualTo("cas/content/ab/34/cd/56/ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))

    [<Test>]
    member _.ContentBlockObjectKeyDoesNotUseLegacyFlatOrTypedAddressShapes() =
        let digest = "ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let address = ContentBlockAddress digest

        let key = StorageKeys.contentBlockObjectKey address

        Assert.That(key, Is.Not.EqualTo($"cas/content/{digest}"))
        Assert.That(key, Is.Not.EqualTo($"cas/content/ab/34/{digest}"))
        Assert.That(key, Is.Not.EqualTo($"cas/content-blocks/{digest}"))
        Assert.That(key, Does.Not.Contain("content-blocks"))
        Assert.That(key, Does.Not.Contain("block-blake3"))

    [<TestCase("block-blake3-ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")>]
    [<TestCase("ab34cd")>]
    [<TestCase("ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")>]
    [<TestCase("sha256-ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")>]
    member _.ContentBlockObjectKeyRejectsMalformedOrUnsupportedAddresses(address: string) =
        Assert.Throws<ArgumentException>(
            Action (fun () ->
                StorageKeys.contentBlockObjectKey (ContentBlockAddress address)
                |> ignore)
        )
        |> ignore

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
