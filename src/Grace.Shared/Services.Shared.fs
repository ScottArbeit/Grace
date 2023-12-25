namespace Grace.Shared

open Grace.Shared.Dto
open Grace.Shared.Types
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
open Microsoft.FSharp.NativeInterop

module Services =

    /// Adds a property to a GraceResult instance.
    let enhance<'T> (key: string, value: string) (result: GraceResult<'T>) =
        if not <| String.IsNullOrEmpty(key) then
            match result with
            | Ok result ->
                result.Properties.Add(key, value)
                Ok result
            | Error error ->
                error.Properties.Add(key, value)
                Error error
        else
            result

    /// A custom PooledObjectPolicy for IncrementalHash.
    type IncrementalHashPolicy() =
        interface IPooledObjectPolicy<IncrementalHash> with
            member this.Create() =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
    
            member this.Return(hashInstance: IncrementalHash) =
                // Reset the hash instance so it can be reused.
                // We're calling GetHashAndReset() to reset - there's no Reset() - but because we're also calling GetHashAndReset()
                //   when we compute the hash values, calling it here on empty IncrementalHash instances will be as fast as possible.
                //   I'm betting that GetHashAndReset() on an empty IncrementalHash instance is faster than creating new instances.
                let throwaway = stackalloc<byte> SHA256.HashSizeInBytes
                hashInstance.GetHashAndReset(throwaway) |> ignore
                true // Indicates that the object is okay to be returned to the pool
    
    /// An ObjectPool for IncrementalHash instances.
    let incrementalHashPool = DefaultObjectPoolProvider().Create(IncrementalHashPolicy())

    /// Computes the SHA-256 value for a given file, presented as a stream.
    ///
    /// Sha256Hash values for files are computed by hashing the file's contents, and then appending the relative path of the file, and the file length.
    let computeSha256ForFile (stream: Stream) (relativeFilePath: RelativePath) =
        task {
            let bufferLength = 64 * 1024 // Did some informal perf testing on large files, this size was best, larger didn't help, and 64K is still on the small object heap.

            // Using object pooling for both of these.
            let buffer = ArrayPool<byte>.Shared.Rent(bufferLength)
            let hasher = incrementalHashPool.Get()

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
                hasher.GetHashAndReset(sha256Bytes) |> ignore
                // 5. Convert the SHA-256 value from a byte[] to a string, and return it.
                //    Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"
                return byteArrayToString(sha256Bytes)
            finally
                if not <| isNull(buffer) then 
                    ArrayPool<byte>.Shared.Return(buffer, clearArray = true)

                if not <| isNull hasher then
                    incrementalHashPool.Return(hasher)
        }

    /// Computes the SHA-256 value for a given relative directory.
    ///
    /// Sha256Hash values for directories are computed by concatenating the relative path of the directory, and the Sha256Hash values of all subdirectories and files.
    let computeSha256ForDirectory (relativeDirectoryPath: RelativePath) (directories: List<LocalDirectoryVersion>) (files: List<LocalFileVersion>) =
        let hasher = incrementalHashPool.Get()

        try
            hasher.AppendData(Encoding.UTF8.GetBytes(relativeDirectoryPath))
        
            // We're sorting just to get consistent ordering; inconsistent ordering would produce difference SHA-256 hashes.
            let sortedDirectories = directories |> Seq.sortBy(fun subdirectory -> subdirectory.RelativePath)
            for subdirectory in sortedDirectories do
                hasher.AppendData(Encoding.UTF8.GetBytes(subdirectory.Sha256Hash))

            // Again, sorting to ensure consistent ordering.
            let sortedFiles = files |> Seq.sortBy(fun file -> file.RelativePath)
            for file in sortedFiles do
                hasher.AppendData(Encoding.UTF8.GetBytes(file.Sha256Hash))

            // Get the SHA-256 hash as a byte array.
            let sha256Bytes = stackalloc<byte> SHA256.HashSizeInBytes
            hasher.GetHashAndReset(sha256Bytes) |> ignore

            // Convert the SHA-256 value from a byte[] to a string, and return it.
            //   Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"
            byteArrayToString sha256Bytes
        finally
            if not <| isNull hasher then
                incrementalHashPool.Return(hasher)

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getDirectorySize (files: IList<FileVersion>) =
        files |> Seq.fold(fun (size: uint64) file -> size + file.Size ) 0UL

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getLocalDirectorySize (files: IList<LocalFileVersion>) =
        files |> Seq.fold(fun (size: uint64) file -> size + file.Size) 0UL

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
