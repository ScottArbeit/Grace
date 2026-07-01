namespace Grace.Shared

open Blake3
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open Microsoft.Extensions.ObjectPool
open System
open System.Buffers
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Contains services helpers.
module Services =

    /// Adds a property to a GraceResult instance.
    let enhance<'T> key value (result: GraceResult<'T>) =
        if not <| String.IsNullOrEmpty(key) then
            let safeValue = if String.IsNullOrEmpty(value) then String.Empty else value

            match result with
            | Ok result ->
                result.Properties[ key ] <- safeValue
                Ok result
            | Error error ->
                error.Properties[ key ] <- safeValue
                Error error
        else
            result

    /// A custom PooledObjectPolicy for IncrementalHash.
    type IncrementalHashPolicy() =
        interface IPooledObjectPolicy<IncrementalHash> with
            /// Builds the contract value from required caller inputs and generated defaults used by this surface.
            member this.Create() = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)

            /// Wraps a value in the computation expression without adding validation errors.
            member this.Return(hashInstance: IncrementalHash) =
                // Reset the hash instance so it can be reused.
                // We're calling GetHashAndReset() to reset - there's no Reset() function.
                let throwaway = stackalloc<byte> SHA256.HashSizeInBytes
                hashInstance.GetHashAndReset(throwaway) |> ignore
                true // Indicates that the object is okay to be returned to the pool

    /// An ObjectPool for IncrementalHash instances.
    let incrementalHashPool =
        DefaultObjectPoolProvider()
            .Create(IncrementalHashPolicy())

    /// Computes the BLAKE3 value for a given file, presented as a stream.
    ///
    /// FileContentHash values for files are computed by hashing only the file's contents.
    let computeBlake3ForFile (stream: Stream) =
        task {
            if isNull stream then nullArg (nameof stream)

            // I did some informal perf testing on large files. This size was best, larger didn't help, and 64K keeps it on the small object heap.
            let bufferLength = 64 * 1024

            let buffer = ArrayPool<byte>.Shared.Rent (bufferLength)
            let mutable hasher = Hasher.New()

            try
                let mutable moreToRead = true

                while moreToRead do
                    let! bytesRead = stream.ReadAsync(buffer, 0, bufferLength)

                    if bytesRead > 0 then
                        hasher.Update(buffer.AsSpan(0, bytesRead))
                    else
                        moreToRead <- false

                let blake3Bytes = stackalloc<byte> Hash.Size
                hasher.Finalize(blake3Bytes)

                return FileContentHash(byteArrayToString blake3Bytes)
            finally
                if not <| isNull buffer then
                    ArrayPool<byte>.Shared.Return (buffer, clearArray = true)

                hasher.Dispose()
        }

    /// The 0x00 character.
    let nulChar = char (0)

    /// Checks if a file is a binary file by scanning the first 8K for a 0x00 character; if it finds one, we assume the file is binary.
    ///
    /// This is the same algorithm used by Git.
    let isBinaryFile (stream: Stream) =
        task {
            //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}."
            // If the file is smaller than 8K, we'll check the whole file.
            let defaultBytesToCheck = 8 * 1024

            let bytesToCheck =
                if stream.Length > defaultBytesToCheck then
                    defaultBytesToCheck
                else
                    int (stream.Length)

            // Get a buffer to hold the part of the file we're going to check.
            let startingBytes = ArrayPool<byte>.Shared.Rent (bytesToCheck)
            //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. Rented byte array of length {bytesToCheck}."

            try
                try
                    // Read the beginning of the file into the buffer.
                    //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. About to read stream."
                    stream.Position <- 0L
                    let! bytesRead = stream.ReadAsync(startingBytes, 0, bytesToCheck)
                    //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. Finished reading stream."

                    // Search for a 0x00 character.
                    return
                        startingBytes
                            .Take(bytesRead)
                            .Any(fun b -> char (b) = nulChar)
                with
                | ex ->
                    //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. Caught exception: {ExceptionResponse.Create ex}."
                    return false
            finally
                // Return the rented buffer to the pool, even if an exception is thrown.
                if not <| isNull startingBytes then
                    ArrayPool<byte>.Shared.Return (startingBytes)
        }

    /// Computes the SHA-256 value for a given file, presented as a stream.
    ///
    /// Sha256Hash values for files are computed by hashing the file's contents.
    let computeSha256ForFile (stream: Stream) (relativeFilePath: RelativePath) =
        task {
            // I did some informal perf testing on large files. This size was best, larger didn't help, and 64K keeps it on the small object heap.
            let bufferLength = 64 * 1024

            // Using object pooling for both of these.
            let buffer = ArrayPool<byte>.Shared.Rent (bufferLength)
            let hasher = incrementalHashPool.Get()

            try
                // Read bytes from the file and feed them into the hasher.
                let mutable moreToRead = true

                while moreToRead do
                    let! bytesRead = stream.ReadAsync(buffer, 0, bufferLength)

                    if bytesRead > 0 then
                        hasher.AppendData(buffer, 0, bytesRead)
                    else
                        moreToRead <- false

                // Get the SHA-256 hash as a byte array.
                let sha256Bytes = stackalloc<byte> SHA256.HashSizeInBytes
                hasher.GetHashAndReset(sha256Bytes) |> ignore

                // Convert the SHA-256 value from a byte[] to a string, and return it.
                //    Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"

                let sha256Hash = byteArrayToString (sha256Bytes)

                return Sha256Hash sha256Hash
            finally
                if not <| isNull buffer then
                    ArrayPool<byte>.Shared.Return (buffer, clearArray = true)

                if not <| isNull hasher then incrementalHashPool.Return(hasher)
        }

    /// Computes both file content hashes from a single pass over a stream.
    let computeHashesForFile (stream: Stream) (relativeFilePath: RelativePath) =
        task {
            if isNull stream then nullArg (nameof stream)

            // I did some informal perf testing on large files. This size was best, larger didn't help, and 64K keeps it on the small object heap.
            let bufferLength = 64 * 1024

            let buffer = ArrayPool<byte>.Shared.Rent (bufferLength)
            let sha256Hasher = incrementalHashPool.Get()
            let mutable blake3Hasher = Hasher.New()

            try
                let mutable moreToRead = true

                while moreToRead do
                    let! bytesRead = stream.ReadAsync(buffer, 0, bufferLength)

                    if bytesRead > 0 then
                        sha256Hasher.AppendData(buffer, 0, bytesRead)
                        blake3Hasher.Update(buffer.AsSpan(0, bytesRead))
                    else
                        moreToRead <- false

                let sha256Bytes = stackalloc<byte> SHA256.HashSizeInBytes

                sha256Hasher.GetHashAndReset(sha256Bytes)
                |> ignore

                let blake3Bytes = stackalloc<byte> Hash.Size
                blake3Hasher.Finalize(blake3Bytes)

                return Sha256Hash(byteArrayToString sha256Bytes), Blake3Hash(byteArrayToString blake3Bytes)
            finally
                if not <| isNull buffer then
                    ArrayPool<byte>.Shared.Return (buffer, clearArray = true)

                if not <| isNull sha256Hasher then incrementalHashPool.Return(sha256Hasher)

                blake3Hasher.Dispose()
        }

    /// The hash algorithm to use for a DirectoryVersion preimage.
    type DirectoryVersionHashAlgorithm =
        | Blake3
        | Sha256

    /// Identifies the kind of child entry included in a DirectoryVersion preimage.
    type DirectoryVersionPreimageEntryKind =
        | Directory
        | File

    /// A child entry included in a DirectoryVersion preimage.
    type DirectoryVersionPreimageEntry =
        {
            Kind: DirectoryVersionPreimageEntryKind
            RelativePath: RelativePath
            Size: int64
            Blake3Hash: Blake3Hash
            Sha256Hash: Sha256Hash
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create kind relativePath size blake3Hash sha256Hash =
            { Kind = kind; RelativePath = RelativePath(normalizeFilePath $"{relativePath}"); Size = size; Blake3Hash = blake3Hash; Sha256Hash = sha256Hash }

        /// Builds a directory entry preimage with the directory kind discriminator.
        static member Directory relativePath size blake3Hash sha256Hash =
            DirectoryVersionPreimageEntry.Create DirectoryVersionPreimageEntryKind.Directory relativePath size blake3Hash sha256Hash

        /// Builds a file entry preimage with the file kind discriminator.
        static member File relativePath size blake3Hash sha256Hash =
            DirectoryVersionPreimageEntry.Create DirectoryVersionPreimageEntryKind.File relativePath size blake3Hash sha256Hash

    /// Maps a directory-version hash algorithm to its wire-format name.
    let private directoryVersionAlgorithmName algorithm =
        match algorithm with
        | DirectoryVersionHashAlgorithm.Blake3 -> "blake3"
        | DirectoryVersionHashAlgorithm.Sha256 -> "sha256"

    /// Maps a directory-version entry kind to its wire-format name.
    let private directoryVersionEntryKindName kind =
        match kind with
        | DirectoryVersionPreimageEntryKind.Directory -> "directory"
        | DirectoryVersionPreimageEntryKind.File -> "file"

    /// Selects the hash field that represents a directory-version entry.
    let private directoryVersionEntryHash algorithm (entry: DirectoryVersionPreimageEntry) =
        match algorithm with
        | DirectoryVersionHashAlgorithm.Blake3 -> entry.Blake3Hash
        | DirectoryVersionHashAlgorithm.Sha256 -> entry.Sha256Hash

    /// Normalizes a directory-version path before hashing or signing it.
    let private encodeDirectoryVersionPath (path: RelativePath) =
        normalizeFilePath $"{path}"
        |> Encoding.UTF8.GetBytes
        |> Convert.ToBase64String

    /// Builds the versioned DirectoryVersion preimage used by both BLAKE3 and SHA-256 directory hashes.
    let directoryVersionPreimage algorithm (relativeDirectoryPath: RelativePath) (entries: seq<DirectoryVersionPreimageEntry>) =
        if isNull entries then nullArg (nameof entries)

        let sortedEntries =
            entries
            |> Seq.sortWith (fun left right ->
                let pathComparison = StringComparer.Ordinal.Compare(normalizeFilePath $"{left.RelativePath}", normalizeFilePath $"{right.RelativePath}")

                if pathComparison <> 0 then
                    pathComparison
                else
                    StringComparer.Ordinal.Compare(directoryVersionEntryKindName left.Kind, directoryVersionEntryKindName right.Kind))
            |> Seq.toArray

        let lines = List<string>()
        lines.Add("grace.directory-version.v1")
        lines.Add($"algorithm:{directoryVersionAlgorithmName algorithm}")
        lines.Add($"path:{encodeDirectoryVersionPath relativeDirectoryPath}")
        lines.Add($"child-count:{sortedEntries.Length}")

        sortedEntries
        |> Array.iteri (fun index entry ->
            lines.Add(
                $"child:{index}:{directoryVersionEntryKindName entry.Kind}:{encodeDirectoryVersionPath entry.RelativePath}:{entry.Size}:{directoryVersionEntryHash algorithm entry}"
            ))

        String.Join("\n", lines) + "\n"

    /// Computes the BLAKE3 hash for a DirectoryVersion preimage.
    let computeBlake3ForDirectory (relativeDirectoryPath: RelativePath) (entries: seq<DirectoryVersionPreimageEntry>) =
        let preimage = directoryVersionPreimage DirectoryVersionHashAlgorithm.Blake3 relativeDirectoryPath entries
        let preimageBytes = Encoding.UTF8.GetBytes preimage
        use hasher = Hasher.New()
        hasher.Update(preimageBytes.AsSpan())
        let blake3Bytes = stackalloc<byte> Hash.Size
        hasher.Finalize(blake3Bytes)
        Blake3Hash(byteArrayToString blake3Bytes)

    /// Computes the SHA-256 hash for a DirectoryVersion preimage.
    let computeSha256ForDirectoryEntries (relativeDirectoryPath: RelativePath) (entries: seq<DirectoryVersionPreimageEntry>) =
        let preimage = directoryVersionPreimage DirectoryVersionHashAlgorithm.Sha256 relativeDirectoryPath entries
        let preimageBytes = Encoding.UTF8.GetBytes preimage
        let sha256Bytes = SHA256.HashData preimageBytes
        Sha256Hash(byteArrayToString sha256Bytes)

    /// Computes the SHA-256 value for a given relative directory.
    ///
    /// Sha256Hash values for directories are computed from a versioned DirectoryVersion preimage.
    let computeSha256ForDirectory (relativeDirectoryPath: RelativePath) (directories: List<LocalDirectoryVersion>) (files: List<LocalFileVersion>) =
        let directoryEntries =
            directories
            |> Seq.map (fun directory ->
                DirectoryVersionPreimageEntry.Directory directory.RelativePath directory.Size (Blake3Hash String.Empty) directory.Sha256Hash)

        let fileEntries =
            files
            |> Seq.map (fun file -> DirectoryVersionPreimageEntry.File file.RelativePath file.Size (Blake3Hash String.Empty) file.Sha256Hash)

        computeSha256ForDirectoryEntries relativeDirectoryPath (Seq.append directoryEntries fileEntries)

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getDirectorySize (files: IList<FileVersion>) =
        files
        |> Seq.fold (fun (size: int64) file -> size + file.Size) 0L

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getLocalDirectorySize (files: IList<LocalFileVersion>) =
        files
        |> Seq.fold (fun (size: int64) file -> size + file.Size) 0L

    /// Gets the number of path segments for the longest relative path in GraceIndex.
    ///
    /// For example, "/src/Grace.Shared/Services.Shared.fs" has 3 path segments.
    let getLongestRelativePath (directoryVersions: IEnumerable<LocalDirectoryVersion>) =
        if directoryVersions |> Seq.isEmpty then
            0
        else
            //logToConsole $"In getLongestRelativePath:"
            //graceStatus.Index.Values |> Seq.iter(fun localDirectoryVersion -> logToConsole $"  localDirectoryVersion.RelativePath: {localDirectoryVersion.RelativePath}; DirectoryId: {localDirectoryVersion.DirectoryId}")
            Math.Max(30, directoryVersions.Max(fun localDirectoryVersion -> localDirectoryVersion.RelativePath.Length))
