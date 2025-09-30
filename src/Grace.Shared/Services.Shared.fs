namespace Grace.Shared

open Grace.Types.Types
open Grace.Shared.Utilities
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

module Services =

    /// Adds a property to a GraceResult instance.
    let enhance<'T> key value (result: GraceResult<'T>) =
        if not <| String.IsNullOrEmpty(key) then
            let safeValue = if String.IsNullOrEmpty(value) then String.Empty else value

            match result with
            | Ok result ->
                result.Properties[key] <- safeValue
                Ok result
            | Error error ->
                error.Properties[key] <- safeValue
                Error error
        else
            result

    /// A custom PooledObjectPolicy for IncrementalHash.
    type IncrementalHashPolicy() =
        interface IPooledObjectPolicy<IncrementalHash> with
            member this.Create() = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)

            member this.Return(hashInstance: IncrementalHash) =
                // Reset the hash instance so it can be reused.
                // We're calling GetHashAndReset() to reset - there's no Reset() function.
                let throwaway = stackalloc<byte> SHA256.HashSizeInBytes
                hashInstance.GetHashAndReset(throwaway) |> ignore
                true // Indicates that the object is okay to be returned to the pool

    /// An ObjectPool for IncrementalHash instances.
    let incrementalHashPool = DefaultObjectPoolProvider().Create(IncrementalHashPolicy())

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
            let startingBytes = ArrayPool<byte>.Shared.Rent(bytesToCheck)
            //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. Rented byte array of length {bytesToCheck}."

            try
                try
                    // Read the beginning of the file into the buffer.
                    //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. About to read stream."
                    stream.Position <- 0L
                    let! bytesRead = stream.ReadAsync(startingBytes, 0, bytesToCheck)
                    //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. Finished reading stream."

                    // Search for a 0x00 character.
                    return startingBytes.Take(bytesRead).Any(fun b -> char (b) = nulChar)
                with ex ->
                    //logToConsole $"In isBinaryFile: stream.Length: {stream.Length}. Caught exception: {ExceptionResponse.Create ex}."
                    return false
            finally
                // Return the rented buffer to the pool, even if an exception is thrown.
                if not <| isNull startingBytes then ArrayPool<byte>.Shared.Return(startingBytes)
        }

    /// Computes the SHA-256 value for a given file, presented as a stream.
    ///
    /// Sha256Hash values for files are computed by hashing the file's contents, and then appending the relative path of the file, and the file length.
    let computeSha256ForFile (stream: Stream) (relativeFilePath: RelativePath) =
        task {
            //logToConsole $"In computeSha256ForFile: relativeFilePath: {relativeFilePath}."

            // Did some informal perf testing on large files, this size was best, larger didn't help, and 64K keeps it on the small object heap.
            let bufferLength = 64 * 1024

            // Using object pooling for both of these.
            let buffer = ArrayPool<byte>.Shared.Rent(bufferLength)
            let hasher = incrementalHashPool.Get()
            //logToConsole $"In computeSha256ForFile: relativeFilePath: {relativeFilePath}. Got hasher."

            try
                // 1. Read bytes from the file and feed them into the hasher.
                let mutable moreToRead = true

                while moreToRead do
                    let! bytesRead = stream.ReadAsync(buffer, 0, bufferLength)

                    if bytesRead > 0 then
                        hasher.AppendData(buffer, 0, bytesRead)
                    else
                        moreToRead <- false
                // 2. Convert the relative path of the file to a byte array, and add it to the hasher.
                hasher.AppendData(Encoding.UTF8.GetBytes(relativeFilePath))
                // 3. Convert the Int64 file length into a byte array, and add it to the hasher.
                hasher.AppendData(BitConverter.GetBytes(stream.Length))
                // 4. Get the SHA-256 hash as a byte array.
                let sha256Bytes = stackalloc<byte> SHA256.HashSizeInBytes
                hasher.GetCurrentHash(sha256Bytes) |> ignore
                // 5. Convert the SHA-256 value from a byte[] to a string, and return it.
                //    Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"

                let sha256Hash = byteArrayToString (sha256Bytes)
                //logToConsole $"In computeSha256ForFile: relativeFilePath: {relativeFilePath}. sha256Hash: {sha256Hash}."

                return Sha256Hash sha256Hash
            finally
                //logToConsole $"In computeSha256ForFile (finally clause): relativeFilePath: {relativeFilePath}. About to return buffer to ArrayPool."

                if not <| isNull buffer then
                    ArrayPool<byte>.Shared.Return(buffer, clearArray = true)

                //logToConsole
                //    $"In computeSha256ForFile (finally clause): relativeFilePath: {relativeFilePath}. Returned buffer to ArrayPool. About to return hasher to incrementalHashPool."

                if not <| isNull hasher then incrementalHashPool.Return(hasher)

        //logToConsole $"In computeSha256ForFile (finally clause): relativeFilePath: {relativeFilePath}. Returned hasher to incrementalHashPool."
        }

    /// Computes the SHA-256 value for a given relative directory.
    ///
    /// Sha256Hash values for directories are computed by concatenating the relative path of the directory, and the Sha256Hash values of all subdirectories and files.
    let computeSha256ForDirectory (relativeDirectoryPath: RelativePath) (directories: List<LocalDirectoryVersion>) (files: List<LocalFileVersion>) =
        let hasher = incrementalHashPool.Get()

        try
            hasher.AppendData(Encoding.UTF8.GetBytes(relativeDirectoryPath))

            // We're sorting just to get consistent ordering; inconsistent ordering would produce difference SHA-256 hashes.
            let sortedDirectories = directories |> Seq.sortBy (fun subdirectory -> subdirectory.RelativePath)

            for subdirectory in sortedDirectories do
                hasher.AppendData(Encoding.UTF8.GetBytes(subdirectory.Sha256Hash))

            // Again, sorting to ensure consistent ordering.
            let sortedFiles = files |> Seq.sortBy (fun file -> file.RelativePath)

            for file in sortedFiles do
                hasher.AppendData(Encoding.UTF8.GetBytes(file.Sha256Hash))

            // Get the SHA-256 hash as a byte array.
            let sha256Bytes = stackalloc<byte> SHA256.HashSizeInBytes
            hasher.GetHashAndReset(sha256Bytes) |> ignore

            // Convert the SHA-256 value from a byte[] to a string, and return it.
            //   Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"
            Sha256Hash(byteArrayToString sha256Bytes)
        finally
            if not <| isNull hasher then incrementalHashPool.Return(hasher)

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getDirectorySize (files: IList<FileVersion>) = files |> Seq.fold (fun (size: int64) file -> size + file.Size) 0L

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getLocalDirectorySize (files: IList<LocalFileVersion>) = files |> Seq.fold (fun (size: int64) file -> size + file.Size) 0L

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
