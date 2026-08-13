namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

module WorkingDirectoryUpdate = WorkingDirectoryUpdateContracts

/// Covers exact prepared-content manifests and byte-validation boundaries.
module WorkingDirectoryUpdatePreparedContentTests =
    /// Extracts a valid test value or fails with the contract rejection reason.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Builds declared SHA-256 and BLAKE3 values for fixed bytes.
    let private hashes (bytes: byte array) =
        let sha256 =
            System.Security.Cryptography.SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        let blake3 = Blake3Hash(ContentAddress.computeBlake3Hex bytes)
        sha256, blake3

    /// Builds a file manifest entry with hashes that independently describe the supplied bytes.
    let private fileEntry (path: string) (bytes: byte array) =
        let sha256, blake3 = hashes bytes
        WorkingDirectoryUpdate.PreparedManifestEntry.File(RelativePath path, sha256, blake3)

    /// Supplies deterministic byte streams and records whether preparation released the reader.
    type private TrackingReader(paths: string list, contents: (string * byte array) list) =
        let bytesByPath = Dictionary<string, byte array>(StringComparer.Ordinal)
        let mutable disposed = false
        let mutable openReadCount = 0

        do
            contents
            |> List.iter (fun (path, bytes) -> bytesByPath[path] <- bytes)

        member _.Disposed = disposed

        /// Reports the number of byte streams preparation attempted to open.
        member _.OpenReadCount = openReadCount

        interface WorkingDirectoryUpdate.IPreparedContentReader with
            /// Lists the uncompressed file paths made available by this test reader.
            member _.FilePaths = paths :> seq<string>

            /// Opens the bytes associated with one declared prepared-content path.
            member _.OpenReadAsync((relativePath: RelativePath), _cancellationToken) =
                openReadCount <- openReadCount + 1

                match bytesByPath.TryGetValue(string relativePath) with
                | true, bytes -> Task.FromResult<Stream>(new MemoryStream(bytes, writable = false))
                | false, _ -> Task.FromException<Stream>(FileNotFoundException($"Missing test bytes for '{relativePath}'."))

            /// Records source disposal after successful or rejected preparation.
            member _.Dispose() = disposed <- true

    /// Builds a manifest or fails the scenario with its validation error.
    let private manifest entries =
        WorkingDirectoryUpdate.PreparedManifest.create entries
        |> required

    /// Runs byte preparation with cancellation disabled for a deterministic test scenario.
    let private prepare preparedManifest reader =
        WorkingDirectoryUpdate.PreparedContent.create preparedManifest reader CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    /// Lists direct and nested superscript device aliases that Windows reserves before any reader opens bytes.
    let private superscriptDevicePaths =
        [
            "COM¹"
            "COM¹.txt"
            "COM²"
            "COM².txt"
            "COM³"
            "COM³.tar.gz"
            "LPT¹"
            "LPT¹.txt"
            "LPT²"
            "LPT².txt"
            "LPT³"
            "LPT³.tar.gz"
            "nested/COM¹"
            "nested/COM¹.txt"
            "nested/COM²"
            "nested/COM².txt"
            "nested/COM³"
            "nested/COM³.tar.gz"
            "nested/LPT¹"
            "nested/LPT¹.txt"
            "nested/LPT²"
            "nested/LPT².txt"
            "nested/LPT³"
            "nested/LPT³.tar.gz"
        ]

    /// Verifies exact deterministic content preserves empty files and releases its input reader.
    [<Test>]
    let ``prepared content validates exact bytes including an empty file`` () =
        let alpha = Encoding.UTF8.GetBytes("alpha")
        let empty = Array.empty<byte>

        let preparedManifest =
            manifest [ WorkingDirectoryUpdate.PreparedManifestEntry.Directory(RelativePath "src")
                       fileEntry "src/alpha.txt" alpha
                       fileEntry "empty.txt" empty ]

        let reader =
            new TrackingReader(
                [ "src/alpha.txt"; "empty.txt" ],
                [
                    "src/alpha.txt", alpha
                    "empty.txt", empty
                ]
            )

        let preparedContent = prepare preparedManifest reader |> required
        reader.Disposed |> should equal true

        use firstAlphaStream =
            WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "src/alpha.txt")
            |> required

        let firstAlphaByte = firstAlphaStream.ReadByte()
        firstAlphaByte |> should equal (int alpha[0])

        use secondAlphaStream =
            WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "src/alpha.txt")
            |> required

        let secondAlphaByte = secondAlphaStream.ReadByte()
        secondAlphaByte |> should equal (int alpha[0])

        use firstAlphaCopy = new MemoryStream()
        firstAlphaStream.CopyTo(firstAlphaCopy)

        let firstAlphaBytes = Array.append [| byte firstAlphaByte |] (firstAlphaCopy.ToArray())

        firstAlphaBytes |> should equal alpha

        use secondAlphaCopy = new MemoryStream()
        secondAlphaStream.CopyTo(secondAlphaCopy)

        let secondAlphaBytes = Array.append [| byte secondAlphaByte |] (secondAlphaCopy.ToArray())

        secondAlphaBytes |> should equal alpha

        use emptyStream =
            WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "empty.txt")
            |> required

        emptyStream.Length |> should equal 0L

        WorkingDirectoryUpdate.PreparedContent.dispose preparedContent

        WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "empty.txt")
        |> Result.isError
        |> should equal true

    /// Verifies unsafe and noncanonical manifest paths reject before a reader can be used.
    [<TestCase("")>]
    [<TestCase("/")>]
    [<TestCase("C:\\repo\\file.txt")>]
    [<TestCase("a/../b")>]
    [<TestCase("a//b")>]
    [<TestCase("a/./b")>]
    [<TestCase("CON")>]
    [<TestCase("con.txt")>]
    [<TestCase("AUX")>]
    [<TestCase("COM1.ext")>]
    [<TestCase("folder./item.txt")>]
    [<TestCase("folder /item.txt")>]
    [<TestCase("control\u0001.txt")>]
    let ``prepared manifest rejects unsafe paths`` path =
        let bytes = Encoding.UTF8.GetBytes("content")

        WorkingDirectoryUpdate.PreparedManifest.create [ fileEntry path bytes ]
        |> Result.isError
        |> should equal true

    /// Verifies all direct and nested superscript device aliases reject as manifest paths.
    [<Test>]
    let ``prepared manifest rejects superscript Windows device aliases`` () =
        let bytes = Encoding.UTF8.GetBytes("content")

        superscriptDevicePaths
        |> List.iter (fun path ->
            WorkingDirectoryUpdate.PreparedManifest.create [ fileEntry path bytes ]
            |> Result.isError
            |> should equal true)

    /// Verifies reserved reader paths reject before preparation opens any declared file bytes.
    [<Test>]
    let ``prepared content rejects superscript Windows device aliases before reader use`` () =
        let bytes = Encoding.UTF8.GetBytes("content")
        let preparedManifest = manifest [ fileEntry "safe.txt" bytes ]

        superscriptDevicePaths
        |> List.iter (fun path ->
            let reader = new TrackingReader([ path ], [ path, bytes ])

            prepare preparedManifest reader
            |> Result.isError
            |> should equal true

            reader.OpenReadCount |> should equal 0
            reader.Disposed |> should equal true)

    /// Verifies manifest construction rejects duplicate names, Windows case collisions, and file-directory conflicts.
    [<Test>]
    let ``prepared manifest rejects duplicate case-colliding and conflicting entries`` () =
        let bytes = Encoding.UTF8.GetBytes("content")
        let sha256, blake3 = hashes bytes

        let duplicate =
            [
                fileEntry "same.txt" bytes
                fileEntry "same.txt" bytes
            ]

        let caseCollision =
            [
                fileEntry "Same.txt" bytes
                fileEntry "same.txt" bytes
            ]

        let separatorAliasCollision =
            [
                fileEntry "folder\\item.txt" bytes
                fileEntry "folder/item.txt" bytes
            ]

        let directConflict =
            [
                WorkingDirectoryUpdate.PreparedManifestEntry.Directory(RelativePath "folder")
                fileEntry "folder" bytes
            ]

        let nestedConflict =
            [
                WorkingDirectoryUpdate.PreparedManifestEntry.File(RelativePath "folder", sha256, blake3)
                fileEntry "FOLDER/item.txt" bytes
            ]

        [
            duplicate
            caseCollision
            separatorAliasCollision
            directConflict
            nestedConflict
        ]
        |> List.iter (fun entries ->
            WorkingDirectoryUpdate.PreparedManifest.create entries
            |> Result.isError
            |> should equal true)

    /// Verifies content coverage rejects missing, extra, and duplicate readable files.
    [<Test>]
    let ``prepared content requires exact readable file coverage`` () =
        let alpha = Encoding.UTF8.GetBytes("alpha")
        let preparedManifest = manifest [ fileEntry "alpha.txt" alpha ]

        let readers =
            [
                new TrackingReader([], [ "alpha.txt", alpha ])
                new TrackingReader(
                    [ "alpha.txt"; "extra.txt" ],
                    [
                        "alpha.txt", alpha
                        "extra.txt", alpha
                    ]
                )
                new TrackingReader([ "alpha.txt"; "alpha.txt" ], [ "alpha.txt", alpha ])
            ]

        readers
        |> List.iter (fun reader ->
            prepare preparedManifest reader
            |> Result.isError
            |> should equal true

            reader.Disposed |> should equal true)

    /// Verifies a declared hash cannot substitute for byte-level dual-hash validation.
    [<Test>]
    let ``prepared content rejects bytes that mismatch either declared hash`` () =
        let declaredBytes = Encoding.UTF8.GetBytes("declared bytes")
        let corruptBytes = Encoding.UTF8.GetBytes("corrupt bytes")
        let sha256, blake3 = hashes declaredBytes
        let corruptSha256, corruptBlake3 = hashes corruptBytes

        let shaMismatch =
            manifest [ WorkingDirectoryUpdate.PreparedManifestEntry.File(
                           RelativePath "content.txt",
                           Sha256Hash "0000000000000000000000000000000000000000000000000000000000000000",
                           corruptBlake3
                       ) ]

        let blakeMismatch =
            manifest [ WorkingDirectoryUpdate.PreparedManifestEntry.File(
                           RelativePath "content.txt",
                           corruptSha256,
                           Blake3Hash "0000000000000000000000000000000000000000000000000000000000000000"
                       ) ]

        [
            shaMismatch
            blakeMismatch
            manifest [ WorkingDirectoryUpdate.PreparedManifestEntry.File(RelativePath "content.txt", sha256, blake3) ]
        ]
        |> List.iter (fun preparedManifest ->
            let reader = new TrackingReader([ "content.txt" ], [ "content.txt", corruptBytes ])

            prepare preparedManifest reader
            |> Result.isError
            |> should equal true

            reader.Disposed |> should equal true)

    /// Verifies diagnostic correlations cannot redefine or merge Watch operation identities.
    [<Test>]
    let ``diagnostic correlations preserve operation identity boundaries`` () =
        let bytes = Encoding.UTF8.GetBytes("request bytes")
        let preparedManifest = manifest [ fileEntry "content.txt" bytes ]

        let preparedContent =
            prepare preparedManifest (new TrackingReader([ "content.txt" ], [ "content.txt", bytes ]))
            |> required

        let target =
            WorkingDirectoryUpdate.Target.create
                (Guid.Parse("5f48b9a7-5537-4d2d-aeda-16c6d66a1bbc"))
                (Guid.Parse("f191d2d1-8194-4e48-b4e0-9f183dab177e"))
                (Guid.Parse("b1f5373a-7303-4dc1-b085-113e8efed444"))
                (Sha256Hash "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9")
                (Blake3Hash "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")
            |> required

        let repositoryId = WorkingDirectoryUpdate.Target.repositoryId target
        let branchId = WorkingDirectoryUpdate.Target.branchId target

        let first =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-001"
            |> required

        let second =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-002"
            |> required

        WorkingDirectoryUpdate.Operation.value first
        |> should
            not'
            (equal (
                WorkingDirectoryUpdate.Operation.value second
            ))

        WorkingDirectoryUpdate.PreparedContent.dispose preparedContent
