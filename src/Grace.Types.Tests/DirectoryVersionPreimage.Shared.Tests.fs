namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Common
open NUnit.Framework

/// Contains tests covering directory version preimage shared behavior.
[<Parallelizable(ParallelScope.All)>]
type DirectoryVersionPreimageSharedTests() =
    /// Verifies that file.
    static member private File (path: string) size (blake3: string) (sha256: string) =
        Services.DirectoryVersionPreimageEntry.File (RelativePath path) size (Blake3Hash blake3) (Sha256Hash sha256)

    /// Verifies that directory.
    static member private Directory (path: string) size (blake3: string) (sha256: string) =
        Services.DirectoryVersionPreimageEntry.Directory (RelativePath path) size (Blake3Hash blake3) (Sha256Hash sha256)

    /// Verifies that root entries.
    static member private RootEntries =
        [
            DirectoryVersionPreimageSharedTests.File
                "README.md"
                12L
                "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        ]

    /// Verifies that nested entries.
    static member private NestedEntries =
        [
            DirectoryVersionPreimageSharedTests.File
                "src/App.fs"
                27L
                "9f3a6d1a5aa02b523a9240993cd141a2103a2ff96f24fa470e6de980d45cd966"
                "086f7e4373e8509d3db67f333fda8cf2cef4e883030282da1e6fe7dbb7653bb2"
        ]

    /// Verifies that mixed tree entries.
    static member private MixedTreeEntries =
        [
            DirectoryVersionPreimageSharedTests.Directory
                "src"
                27L
                "0d6f7b0418c61086d4be1a0c5d44dc8abcb4d0d5c08f3556e455891aefde7d41"
                "27a1f6b711aa88117824253726920929081e2bc06e66b40bdd62f75c12d1c809"
            DirectoryVersionPreimageSharedTests.File
                "README.md"
                12L
                "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            DirectoryVersionPreimageSharedTests.File
                "docs/README.md"
                12L
                "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        ]

    /// Verifies that directory version preimage uses version algorithm and encoded paths.
    [<Test>]
    member _.DirectoryVersionPreimageUsesVersionAlgorithmAndEncodedPaths() =
        let entry =
            DirectoryVersionPreimageSharedTests.File
                "src\\colon:name\nfile.txt"
                99L
                "1111111111111111111111111111111111111111111111111111111111111111"
                "2222222222222222222222222222222222222222222222222222222222222222"

        let preimage = Services.directoryVersionPreimage Services.DirectoryVersionHashAlgorithm.Sha256 (RelativePath ".") [ entry ]

        let expected =
            [
                "grace.directory-version.v1"
                "algorithm:sha256"
                "path:Lg=="
                "child-count:1"
                "child:0:file:c3JjL2NvbG9uOm5hbWUKZmlsZS50eHQ=:99:2222222222222222222222222222222222222222222222222222222222222222"
            ]
            |> String.concat "\n"
            |> fun value -> value + "\n"

        Assert.That(preimage, Is.EqualTo(expected))

    /// Verifies that directory version golden vectors are stable.
    [<Test>]
    member _.DirectoryVersionGoldenVectorsAreStable() =
        let cases =
            [
                "empty-root",
                ".",
                [],
                "6c2f433e475de7dbe60583e374fabdc098eb7a23bab24bd871820fe5c9dd7fc0",
                "69fbcbbe1e5203155bbc0a1d537628319107726d54b77ea33e91ec503120f712"
                "root-with-file",
                ".",
                DirectoryVersionPreimageSharedTests.RootEntries,
                "6c03bb1f203dd4632db6a30966cc40c70a28c6f66aaa19c42fa6ccc84b81a896",
                "7fa9438440decafc86602975f376fe1220ba420d9084ed9427dcb85b4e61c46b"
                "nested-directory",
                "src",
                DirectoryVersionPreimageSharedTests.NestedEntries,
                "8d4f8fdb0657859e2b15dd506396d015821c91d3dd511f637fbdd44f00c1d7d2",
                "02634546989fdf7ba6910dc7473922ef4df5e8eef055667359156deb6d7763a1"
                "mixed-tree",
                ".",
                DirectoryVersionPreimageSharedTests.MixedTreeEntries,
                "d93d3a2eb1c5c5f1b4919d3b8283fb348248f57cf0904e6adfd26fe7bb6b76d1",
                "bd5ba4daccd0fa41c5e5998c41ffac46f6ddbf5d5d83688ed46c3a22dd60e4eb"
            ]

        for name, path, entries, expectedBlake3, expectedSha256 in cases do
            let actualBlake3 = Services.computeBlake3ForDirectory (RelativePath path) entries
            let actualSha256 = Services.computeSha256ForDirectoryEntries (RelativePath path) entries

            Assert.That(actualBlake3, Is.EqualTo(Blake3Hash expectedBlake3), $"{name} BLAKE3")
            Assert.That(actualSha256, Is.EqualTo(Sha256Hash expectedSha256), $"{name} SHA-256")

    /// Verifies that directory version hashes change for semantic directory changes.
    [<Test>]
    member _.DirectoryVersionHashesChangeForSemanticDirectoryChanges() =
        let baseline = DirectoryVersionPreimageSharedTests.MixedTreeEntries
        let baselineBlake3 = Services.computeBlake3ForDirectory (RelativePath ".") baseline
        let baselineSha256 = Services.computeSha256ForDirectoryEntries (RelativePath ".") baseline

        let cases =
            [
                "directory path", RelativePath "different", baseline
                "renamed child",
                RelativePath ".",
                [
                    DirectoryVersionPreimageSharedTests.Directory
                        "source"
                        27L
                        "0d6f7b0418c61086d4be1a0c5d44dc8abcb4d0d5c08f3556e455891aefde7d41"
                        "27a1f6b711aa88117824253726920929081e2bc06e66b40bdd62f75c12d1c809"
                    baseline[1]
                    baseline[2]
                ]
                "file size",
                RelativePath ".",
                [
                    baseline[0]
                    DirectoryVersionPreimageSharedTests.File
                        "README.md"
                        13L
                        "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
                        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                    baseline[2]
                ]
                "child kind",
                RelativePath ".",
                [
                    DirectoryVersionPreimageSharedTests.File
                        "src"
                        27L
                        "0d6f7b0418c61086d4be1a0c5d44dc8abcb4d0d5c08f3556e455891aefde7d41"
                        "27a1f6b711aa88117824253726920929081e2bc06e66b40bdd62f75c12d1c809"
                    baseline[1]
                    baseline[2]
                ]
            ]

        for name, path, entries in cases do
            Assert.That(Services.computeBlake3ForDirectory path entries, Is.Not.EqualTo(baselineBlake3), $"{name} BLAKE3")
            Assert.That(Services.computeSha256ForDirectoryEntries path entries, Is.Not.EqualTo(baselineSha256), $"{name} SHA-256")

    /// Verifies that directory version hashes are order independent and kind sensitive.
    [<Test>]
    member _.DirectoryVersionHashesAreOrderIndependentAndKindSensitive() =
        let ordered = DirectoryVersionPreimageSharedTests.MixedTreeEntries
        let reordered = [ ordered[2]; ordered[0]; ordered[1] ]

        Assert.That(Services.computeBlake3ForDirectory (RelativePath ".") reordered, Is.EqualTo(Services.computeBlake3ForDirectory (RelativePath ".") ordered))

        Assert.That(
            Services.computeSha256ForDirectoryEntries (RelativePath ".") reordered,
            Is.EqualTo(Services.computeSha256ForDirectoryEntries (RelativePath ".") ordered)
        )

        let samePathDifferentKind =
            [
                DirectoryVersionPreimageSharedTests.Directory
                    "same-name"
                    1L
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                DirectoryVersionPreimageSharedTests.File
                    "same-name"
                    1L
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            ]

        Assert.That(
            Services.directoryVersionPreimage Services.DirectoryVersionHashAlgorithm.Sha256 (RelativePath ".") samePathDifferentKind,
            Does
                .Contain("child:0:directory:")
                .And.Contain("child:1:file:")
        )

    /// Verifies that blake3 directory hash uses blake3 child hashes.
    [<Test>]
    member _.Blake3DirectoryHashUsesBlake3ChildHashes() =
        let baseline = DirectoryVersionPreimageSharedTests.MixedTreeEntries

        let shaOnlyChanged =
            [
                baseline[0]
                DirectoryVersionPreimageSharedTests.File
                    "README.md"
                    12L
                    "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
                    "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                baseline[2]
            ]

        let blake3Changed =
            [
                baseline[0]
                DirectoryVersionPreimageSharedTests.File
                    "README.md"
                    12L
                    "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                baseline[2]
            ]

        Assert.That(
            Services.computeBlake3ForDirectory (RelativePath ".") shaOnlyChanged,
            Is.EqualTo(Services.computeBlake3ForDirectory (RelativePath ".") baseline)
        )

        Assert.That(
            Services.computeBlake3ForDirectory (RelativePath ".") blake3Changed,
            Is.Not.EqualTo(Services.computeBlake3ForDirectory (RelativePath ".") baseline)
        )

        Assert.That(
            Services.computeSha256ForDirectoryEntries (RelativePath ".") shaOnlyChanged,
            Is.Not.EqualTo(Services.computeSha256ForDirectoryEntries (RelativePath ".") baseline)
        )
