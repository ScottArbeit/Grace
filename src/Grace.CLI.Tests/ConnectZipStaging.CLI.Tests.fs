namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module WorkingDirectoryUpdate = WorkingDirectoryUpdateContracts

/// Covers complete local staging and validation of Connect zip archives.
module ConnectZipStagingTests =
    /// Extracts a valid test value or fails with its rejection reason.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Computes the two manifest hashes from logical uncompressed bytes.
    let private hashes (bytes: byte array) =
        let sha256 =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        sha256, Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Builds one prepared-manifest file declaration for fixed bytes.
    let private fileEntry (path: string) (bytes: byte array) =
        let sha256, blake3 = hashes bytes
        WorkingDirectoryUpdate.PreparedManifestEntry.File(RelativePath path, sha256, blake3)

    /// Creates the nested gzip representation used by existing Connect text entries.
    let private gzip (bytes: byte array) =
        use output = new MemoryStream()

        do
            use compressed = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen = true)
            compressed.Write(bytes, 0, bytes.Length)

        output.ToArray()

    /// Creates a real zip fixture from ordered path and optional payload entries.
    let private createZip (entries: (string * byte array option) list) =
        use output = new MemoryStream()

        do
            use archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen = true)

            entries
            |> List.iter (fun (path, bytes) ->
                let entry = archive.CreateEntry(path, CompressionLevel.NoCompression)

                match bytes with
                | None -> ()
                | Some bytes ->
                    use stream = entry.Open()
                    stream.Write(bytes, 0, bytes.Length))

        output.ToArray()

    /// Streams fixed bytes in bounded chunks without exposing seek or length metadata.
    type private ChunkedReadStream(bytes: byte array, maxChunkSize: int, onDispose: unit -> unit) =
        inherit Stream()

        let mutable position = 0
        let mutable disposed = false
        let mutable largestRead = 0
        let mutable readCount = 0

        /// Reports the largest byte count returned by one read.
        member _.LargestRead = largestRead

        /// Reports how many reads the downloader needed to reach end of stream.
        member _.ReadCount = readCount

        /// Reports whether callers may still read this stream.
        override _.CanRead = not disposed

        /// Prevents archive code from seeking the network-shaped source.
        override _.CanSeek = false

        /// Prevents writes to the fixed fixture bytes.
        override _.CanWrite = false

        /// Rejects metadata-only length access from the source seam.
        override _.Length = raise (NotSupportedException())

        /// Rejects source-position access because this fixture is forward-only.
        override _.Position
            with get () = raise (NotSupportedException())
            and set _ = raise (NotSupportedException())

        /// Flushes no state because the fixture is read-only.
        override _.Flush() = ()

        /// Reads at most the configured chunk size from the remaining fixture bytes.
        override _.Read(buffer, offset, count) =
            if disposed then raise (ObjectDisposedException(nameof ChunkedReadStream))

            let bytesToRead = min (min count maxChunkSize) (bytes.Length - position)

            if bytesToRead > 0 then
                Array.Copy(bytes, position, buffer, offset, bytesToRead)
                position <- position + bytesToRead
                largestRead <- max largestRead bytesToRead
                readCount <- readCount + 1

            bytesToRead

        /// Provides the asynchronous read shape used by Stream.CopyToAsync.
        override this.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            let temporary = Array.zeroCreate<byte> buffer.Length
            let bytesRead = this.Read(temporary, 0, temporary.Length)
            temporary.AsMemory(0, bytesRead).CopyTo(buffer)
            ValueTask<int>(bytesRead)

        /// Rejects seeking on the forward-only source.
        override _.Seek(_, _) = raise (NotSupportedException())

        /// Rejects changing the fixed source length.
        override _.SetLength(_) = raise (NotSupportedException())

        /// Rejects writes to the fixed source.
        override _.Write(_, _, _) = raise (NotSupportedException())

        /// Records source release exactly once after the completed or failed download.
        override this.Dispose(disposing) =
            if disposing && not disposed then
                disposed <- true
                onDispose ()

            ``base``.Dispose(disposing)

    /// Runs one staging scenario in an isolated directory and captures its cleanup state.
    let private stage (manifest: WorkingDirectoryUpdate.PreparedManifest) (zipBytes: byte array) =
        let tempDirectory = Path.Combine(Path.GetTempPath(), $"grace-connect-zip-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDirectory) |> ignore

        try
            let source = new MemoryStream(zipBytes, writable = false)

            let result =
                ConnectZipStaging.prepareInTempDirectory manifest source tempDirectory CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()

            let sourceDisposed = not source.CanRead

            let residueCount =
                Directory.EnumerateFileSystemEntries(tempDirectory)
                |> Seq.length

            result, sourceDisposed, residueCount
        finally
            Directory.Delete(tempDirectory, recursive = true)

    /// Builds a valid prepared manifest or fails the fixture with its rejection reason.
    let private manifest entries =
        WorkingDirectoryUpdate.PreparedManifest.create entries
        |> required

    /// Verifies a staged failure consumed the source and removed the random local zip.
    let private shouldRejectWithoutResidue manifest zipBytes =
        let result, sourceDisposed, residueCount = stage manifest zipBytes
        result |> Result.isError |> should equal true
        sourceDisposed |> should equal true
        residueCount |> should equal 0

    /// Verifies binary and nested-gzip entries become immutable logical bytes only after full archive validation.
    [<Test>]
    let ``staging accepts exact binary and compressed text entries`` () =
        let binaryBytes = [| 0uy; 1uy; 2uy; 3uy |]
        let textBytes = Encoding.UTF8.GetBytes("Om Sai Ram")

        let manifest =
            manifest [ WorkingDirectoryUpdate.PreparedManifestEntry.Directory(RelativePath "src")
                       fileEntry "image.bin" binaryBytes
                       fileEntry "src/readme.txt" textBytes ]

        let zipBytes =
            createZip [ "src/", None
                        "image.bin", Some binaryBytes
                        "src/readme.txt", Some(gzip textBytes) ]

        let result, sourceDisposed, residueCount = stage manifest zipBytes
        let preparedContent = result |> required
        sourceDisposed |> should equal true
        residueCount |> should equal 0

        use binary =
            WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "image.bin")
            |> required

        use binaryCopy = new MemoryStream()
        binary.CopyTo(binaryCopy)
        binaryCopy.ToArray() |> should equal binaryBytes

        use text =
            WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "src/readme.txt")
            |> required

        use textCopy = new MemoryStream()
        text.CopyTo(textCopy)
        textCopy.ToArray() |> should equal textBytes

        WorkingDirectoryUpdate.PreparedContent.dispose preparedContent

    /// Verifies missing, extra, and duplicate non-directory entries cannot produce prepared content.
    [<Test>]
    let ``staging requires exact archive file coverage`` () =
        let bytes = Encoding.UTF8.GetBytes("selected")
        let selectedManifest = manifest [ fileEntry "selected.txt" bytes ]

        [
            createZip []
            createZip [ "selected.txt", Some bytes
                        "extra.txt", Some bytes ]
            createZip [ "selected.txt", Some bytes
                        "selected.txt", Some bytes ]
        ]
        |> List.iter (shouldRejectWithoutResidue selectedManifest)

    /// Verifies traversal entries reject before any archive bytes can become prepared content.
    [<Test>]
    let ``staging rejects unsafe archive traversal paths`` () =
        let bytes = Encoding.UTF8.GetBytes("selected")
        let selectedManifest = manifest [ fileEntry "selected.txt" bytes ]

        [
            createZip [ "../selected.txt", Some bytes ]
            createZip [ "../", None
                        "selected.txt", Some bytes ]
        ]
        |> List.iter (shouldRejectWithoutResidue selectedManifest)

    /// Verifies Windows-equivalent file names cannot collapse to one selected manifest path.
    [<Test>]
    let ``staging rejects Windows case-colliding archive files`` () =
        let bytes = Encoding.UTF8.GetBytes("selected")
        let selectedManifest = manifest [ fileEntry "Case.txt" bytes ]

        let zipBytes =
            createZip [ "Case.txt", Some bytes
                        "case.txt", Some bytes ]

        shouldRejectWithoutResidue selectedManifest zipBytes

    /// Verifies one changed raw payload byte invalidates both archive preparation and cleanup.
    [<Test>]
    let ``staging rejects a one-byte archive payload corruption`` () =
        let expected = Encoding.UTF8.GetBytes("immutable payload")
        let corrupted = Array.copy expected
        corrupted[corrupted.Length / 2] <- corrupted[corrupted.Length / 2] ^^^ 1uy
        let selectedManifest = manifest [ fileEntry "payload.bin" expected ]
        let zipBytes = createZip [ "payload.bin", Some corrupted ]
        shouldRejectWithoutResidue selectedManifest zipBytes

    /// Verifies malformed nested-GZip content fails instead of becoming a third byte interpretation.
    [<Test>]
    let ``staging rejects corrupt nested GZip content`` () =
        let expected = Encoding.UTF8.GetBytes(String.replicate 64 "logical text ")
        let corruptedGZip = gzip expected
        corruptedGZip[corruptedGZip.Length / 2] <- corruptedGZip[corruptedGZip.Length / 2] ^^^ 1uy
        let selectedManifest = manifest [ fileEntry "readme.txt" expected ]
        let zipBytes = createZip [ "readme.txt", Some corruptedGZip ]
        shouldRejectWithoutResidue selectedManifest zipBytes

    /// Verifies matching either declared hash alone is insufficient for raw or nested-GZip bytes.
    [<Test>]
    let ``staging requires SHA-256 and BLAKE3 from the same logical bytes`` () =
        let bytes = Encoding.UTF8.GetBytes("dual hash payload")
        let sha256, blake3 = hashes bytes
        let wrongSha256 = Sha256Hash(String.replicate 64 "0")
        let wrongBlake3 = Blake3Hash(String.replicate 64 "f")

        [
            WorkingDirectoryUpdate.PreparedManifestEntry.File(RelativePath "payload.bin", wrongSha256, blake3)
            WorkingDirectoryUpdate.PreparedManifestEntry.File(RelativePath "payload.bin", sha256, wrongBlake3)
        ]
        |> List.iter (fun declaration ->
            let selectedManifest = manifest [ declaration ]
            let zipBytes = createZip [ "payload.bin", Some bytes ]
            shouldRejectWithoutResidue selectedManifest zipBytes)

    /// Verifies cancellation after the completed download aborts validation and removes the staged zip.
    [<Test>]
    let ``cancellation during archive validation disposes source and removes temp zip`` () =
        let bytes = Encoding.UTF8.GetBytes("cancel before logical bytes")
        let selectedManifest = manifest [ fileEntry "payload.bin" bytes ]
        let zipBytes = createZip [ "payload.bin", Some bytes ]
        let tempDirectory = Path.Combine(Path.GetTempPath(), $"grace-connect-zip-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDirectory) |> ignore

        use cancellation = new CancellationTokenSource()
        let source = new ChunkedReadStream(zipBytes, 1024, cancellation.Cancel)

        try
            let operation = Func<Task>(fun () -> ConnectZipStaging.prepareInTempDirectory selectedManifest source tempDirectory cancellation.Token :> Task)

            Assert.ThrowsAsync<OperationCanceledException>(operation)
            |> ignore

            source.CanRead |> should equal false

            Directory.EnumerateFileSystemEntries(tempDirectory)
            |> Seq.isEmpty
            |> should equal true
        finally
            Directory.Delete(tempDirectory, recursive = true)

    /// Verifies invalid zip structure is handled only after source disposal and temp-file cleanup.
    [<Test>]
    let ``invalid archive cleanup releases source and removes temp zip`` () =
        let bytes = Encoding.UTF8.GetBytes("selected")
        let selectedManifest = manifest [ fileEntry "selected.txt" bytes ]
        let invalidZip = Encoding.UTF8.GetBytes("not a zip archive")
        shouldRejectWithoutResidue selectedManifest invalidZip

    /// Verifies a large entry downloads through a forward-only bounded stream and retains every actual byte.
    [<Test>]
    let ``staging streams a large entry before validating its complete logical bytes`` () =
        let largeBytes = Array.init (4 * 1024 * 1024) (fun index -> byte (index % 251))
        let selectedManifest = manifest [ fileEntry "large.bin" largeBytes ]
        let zipBytes = createZip [ "large.bin", Some largeBytes ]
        let tempDirectory = Path.Combine(Path.GetTempPath(), $"grace-connect-zip-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDirectory) |> ignore
        let source = new ChunkedReadStream(zipBytes, 1024, ignore)

        try
            let result =
                ConnectZipStaging.prepareInTempDirectory selectedManifest source tempDirectory CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()

            let preparedContent = result |> required
            source.CanRead |> should equal false

            source.LargestRead
            |> should be (lessThanOrEqualTo 1024)

            source.ReadCount |> should be (greaterThan 1)

            Directory.EnumerateFileSystemEntries(tempDirectory)
            |> Seq.isEmpty
            |> should equal true

            use prepared =
                WorkingDirectoryUpdate.PreparedContent.openRead preparedContent (RelativePath "large.bin")
                |> required

            use actual = new MemoryStream()
            prepared.CopyTo(actual)
            actual.ToArray() |> should equal largeBytes
            WorkingDirectoryUpdate.PreparedContent.dispose preparedContent
        finally
            Directory.Delete(tempDirectory, recursive = true)
