namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System

/// Contains tests covering storage keys shared behavior.
[<Parallelizable(ParallelScope.All)>]
type StorageKeysSharedTests() =
    /// Verifies that whole file content object key matches existing blob key shape.
    [<Test>]
    member _.WholeFileContentObjectKeyMatchesExistingBlobKeyShape() =
        let fileVersion =
            FileVersion.CreateWithHashes
                "src/Grace.Server/Storage.Server.fs"
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
                "https://example.test/blob"
                false
                1234L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(
            key,
            Is.EqualTo(
                "src/Grace.Server/Storage.Server.fs/Storage.Server_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef_abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789.fs"
            )
        )

    /// Verifies that whole file content object key includes blake3 when present.
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

    /// Verifies that whole file content object key preserves extensionless blob key shape.
    [<Test>]
    member _.WholeFileContentObjectKeyPreservesExtensionlessBlobKeyShape() =
        let fileVersion =
            FileVersion.CreateWithHashes
                "Dockerfile"
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                ""
                false
                2048L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(
            key,
            Is.EqualTo(
                "Dockerfile/Dockerfile_abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            )
        )

    /// Verifies that whole file content object key includes blake3 for extensionless files when present.
    [<Test>]
    member _.WholeFileContentObjectKeyIncludesBlake3ForExtensionlessFilesWhenPresent() =
        let fileVersion =
            FileVersion.CreateWithHashes (RelativePath "Dockerfile") (Sha256Hash "shared-sha256") (Blake3Hash "first-blake3") String.Empty false 2048L

        let key = StorageKeys.wholeFileContentObjectKey fileVersion

        Assert.That(key, Is.EqualTo("Dockerfile/Dockerfile_shared-sha256_first-blake3"))

    /// Verifies that whole file content object key separates same sha256 different blake3.
    [<Test>]
    member _.WholeFileContentObjectKeySeparatesSameSha256DifferentBlake3() =
        let first =
            FileVersion.CreateWithHashes (RelativePath "src/appsettings.json") (Sha256Hash "shared-sha256") (Blake3Hash "first-blake3") String.Empty false 512L

        let second =
            FileVersion.CreateWithHashes (RelativePath "src/appsettings.json") (Sha256Hash "shared-sha256") (Blake3Hash "second-blake3") String.Empty false 512L

        Assert.That(StorageKeys.wholeFileContentObjectKey first, Is.Not.EqualTo(StorageKeys.wholeFileContentObjectKey second))

    /// Verifies that content block object key uses four level digest fanout.
    [<Test>]
    member _.ContentBlockObjectKeyUsesFourLevelDigestFanout() =
        let address = ContentBlockAddress "ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let firstKey = StorageKeys.contentBlockObjectKey address
        let secondKey = StorageKeys.contentBlockObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/content/ab/34/cd/56/ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))
        Assert.That(secondKey, Is.EqualTo(firstKey))

    /// Verifies that content block object key normalizes uppercase digest.
    [<Test>]
    member _.ContentBlockObjectKeyNormalizesUppercaseDigest() =
        let address = ContentBlockAddress "AB34CD567F43AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"

        let key = StorageKeys.contentBlockObjectKey address

        Assert.That(key, Is.EqualTo("cas/content/ab/34/cd/56/ab34cd567f43aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))

    /// Verifies that content block object key does not use legacy flat or typed address shapes.
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

    /// Verifies that content block object key rejects malformed or unsupported addresses.
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

    /// Verifies that manifest object key depends only on manifest address.
    [<Test>]
    member _.ManifestObjectKeyDependsOnlyOnManifestAddress() =
        let address = ManifestAddress "manifest-blake3-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let firstKey = StorageKeys.fileManifestObjectKey address
        let secondKey = StorageKeys.fileManifestObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/file-manifests/manifest-blake3-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
        Assert.That(secondKey, Is.EqualTo(firstKey))

    /// Verifies that content block metadata object key depends only on content block address.
    [<Test>]
    member _.ContentBlockMetadataObjectKeyDependsOnlyOnContentBlockAddress() =
        let address = ContentBlockAddress "block-blake3-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"

        let firstKey = StorageKeys.contentBlockMetadataObjectKey address
        let secondKey = StorageKeys.contentBlockMetadataObjectKey address

        Assert.That(firstKey, Is.EqualTo("cas/content-block-metadata/block-blake3-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc.json"))

        Assert.That(secondKey, Is.EqualTo(firstKey))
