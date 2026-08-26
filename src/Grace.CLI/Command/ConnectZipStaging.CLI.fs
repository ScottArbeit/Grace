namespace Grace.CLI.Command

open Grace.Shared
open Grace.Types.Common
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

/// Stages one complete Connect archive locally and prepares only exact dual-hashed logical file bytes.
module internal ConnectZipStaging =
    /// Bounds every source, archive-entry, and nested-GZip copy without changing logical content.
    let private copyBufferSize = 64 * 1024

    /// Identifies archive directory entries without misclassifying empty files.
    let private isDirectoryEntry (entry: ZipArchiveEntry) =
        entry.FullName.EndsWith("/", StringComparison.Ordinal)
        || entry.FullName.EndsWith("\\", StringComparison.Ordinal)

    /// Builds the Windows-equivalent lookup key used between manifest and archive paths.
    let private pathKey (path: string) = path.Replace('\\', '/')

    /// Computes both content hashes from the same logical byte array.
    let private hashes (bytes: byte array) =
        let sha256 =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        sha256, Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Tests whether one logical byte representation matches both declared hashes.
    let private matchesHashes expectedSha256 expectedBlake3 bytes =
        let actualSha256, actualBlake3 = hashes bytes

        actualSha256 = expectedSha256
        && actualBlake3 = expectedBlake3

    /// Reads all bytes exposed by one archive entry so validation cannot succeed from metadata alone.
    let private readArchiveEntry (entry: ZipArchiveEntry) (cancellationToken: CancellationToken) =
        task {
            use source = entry.Open()
            use destination = new MemoryStream()
            do! source.CopyToAsync(destination, copyBufferSize, cancellationToken)
            return destination.ToArray()
        }

    /// Expands the existing nested-GZip Connect text representation for hash comparison.
    let private tryExpandGZip (bytes: byte array) (cancellationToken: CancellationToken) =
        task {
            try
                use source = new MemoryStream(bytes, writable = false)
                use gzip = new GZipStream(source, CompressionMode.Decompress)
                use destination = new MemoryStream()
                do! gzip.CopyToAsync(destination, copyBufferSize, cancellationToken)
                return Ok(destination.ToArray())
            with
            | :? OperationCanceledException as ex -> return raise ex
            | ex -> return Error ex.Message
        }

    /// Validates every explicit archive directory with the prepared manifest's Windows path rules.
    let private validateDirectoryPaths (archive: ZipArchive) =
        archive.Entries
        |> Seq.filter isDirectoryEntry
        |> Seq.tryPick (fun entry ->
            let directoryPath = entry.FullName.TrimEnd([| '/'; '\\' |])

            match
                WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(
                                                                              RelativePath directoryPath
                                                                          ) ]
                with
            | Ok _ -> None
            | Error error -> Some error)
        |> function
            | Some error -> Error $"Connect zip contains an unsafe directory entry: {error}"
            | None -> Ok()

    /// Supplies normalized logical bytes for every non-directory archive entry.
    type private ArchivePreparedContentReader(archive: ZipArchive, manifest: WorkingDirectoryUpdateContracts.PreparedManifest) =
        let archiveFiles =
            archive.Entries
            |> Seq.filter (isDirectoryEntry >> not)
            |> Seq.toArray

        let entriesByPath = Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase)
        let declarationsByPath = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)

        do
            archiveFiles
            |> Array.iter (fun entry -> entriesByPath[pathKey entry.FullName] <- entry)

            WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
            |> Seq.iter (function
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256, blake3) ->
                    declarationsByPath[pathKey (string path)] <- sha256, blake3
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory _ -> ())

        interface WorkingDirectoryUpdateContracts.IPreparedContentReader with
            /// Lists every non-directory archive entry, including duplicates, for exact coverage validation.
            member _.FilePaths =
                archiveFiles
                |> Seq.map (fun entry -> entry.FullName)

            /// Opens the sole raw or nested-GZip representation whose actual bytes match both manifest hashes.
            member _.OpenReadAsync((relativePath: RelativePath), cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let key = pathKey (string relativePath)

                    match entriesByPath.TryGetValue(key), declarationsByPath.TryGetValue(key) with
                    | (true, entry), (true, (expectedSha256, expectedBlake3)) ->
                        let! rawBytes = readArchiveEntry entry cancellationToken

                        if matchesHashes expectedSha256 expectedBlake3 rawBytes then
                            return new MemoryStream(rawBytes, writable = false) :> Stream
                        else
                            match! tryExpandGZip rawBytes cancellationToken with
                            | Ok expandedBytes when matchesHashes expectedSha256 expectedBlake3 expandedBytes ->
                                return new MemoryStream(expandedBytes, writable = false) :> Stream
                            | Ok _ -> return raise (InvalidDataException($"Connect zip entry '{relativePath}' matches neither accepted byte representation."))
                            | Error error -> return raise (InvalidDataException($"Connect zip entry '{relativePath}' failed nested-GZip validation: {error}"))
                    | _ -> return raise (InvalidDataException($"Connect zip entry '{relativePath}' has no exact archive and manifest declaration."))
                }

            /// Leaves archive ownership with the staging scope that created this reader.
            member _.Dispose() = ()

    /// Stages to a random zip under the supplied temp directory for isolated validation and cleanup testing.
    let internal prepareInTempDirectory
        (manifest: WorkingDirectoryUpdateContracts.PreparedManifest)
        (zipSource: Stream)
        (tempDirectory: string)
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull zipSource then
                return Error "Connect zip source must not be null."
            else
                use zipSource = zipSource

                if String.IsNullOrWhiteSpace(tempDirectory) then
                    return Error "Connect zip temp directory must not be empty."
                else
                    let tempZipPath = Path.Combine(tempDirectory, $"grace-connect-{Guid.NewGuid():N}.zip")

                    try
                        try
                            do!
                                task {
                                    use tempZip =
                                        new FileStream(
                                            tempZipPath,
                                            FileMode.CreateNew,
                                            FileAccess.Write,
                                            FileShare.None,
                                            copyBufferSize,
                                            FileOptions.Asynchronous
                                            ||| FileOptions.SequentialScan
                                        )

                                    do! zipSource.CopyToAsync(tempZip, copyBufferSize, cancellationToken)
                                    do! tempZip.FlushAsync(cancellationToken)
                                }

                            zipSource.Dispose()

                            use tempZip = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, copyBufferSize, FileOptions.Asynchronous)

                            use archive = new ZipArchive(tempZip, ZipArchiveMode.Read)

                            match validateDirectoryPaths archive with
                            | Error error -> return Error error
                            | Ok () ->
                                let reader = new ArchivePreparedContentReader(archive, manifest)

                                let! result = WorkingDirectoryUpdateContracts.PreparedContent.create manifest reader cancellationToken

                                cancellationToken.ThrowIfCancellationRequested()
                                return result
                        with
                        | :? OperationCanceledException as ex -> return raise ex
                        | ex -> return Error $"Connect zip staging failed: {ex.Message}"
                    finally
                        if File.Exists(tempZipPath) then File.Delete(tempZipPath)
        }

    /// Downloads and validates one Connect archive through a random zip in the system temp directory.
    let internal prepare manifest zipSource cancellationToken = prepareInTempDirectory manifest zipSource (Path.GetTempPath()) cancellationToken
