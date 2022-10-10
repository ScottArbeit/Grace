namespace Grace.Shared

open Grace.Shared.Dto
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Buffers
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Security.Cryptography
open System.Text

module Services =

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

    /// Computes the SHA-256 value for a given file, presented as a stream.
    let computeSha256ForFile (stream: Stream) (relativeFilePath: RelativePath) =
        task {
            let bufferLength = 64 * 1024 // Did some informal perf testing on large files, this size was best, larger didn't help, and 64K is still on the small object heap.
            let buffer = ArrayPool<byte>.Shared.Rent(bufferLength)

            try
                // 1. Create an IncrementalHash instance.
                use hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                // 2. Read bytes from the file and feed them into the hasher.
                let mutable loop = true
                while loop do
                    let! bytesRead = stream.ReadAsync(buffer.AsMemory(0, bufferLength))
                    if bytesRead > 0 then
                        hasher.AppendData(buffer.AsSpan(0, bytesRead))
                    else
                        loop <- false
                // 3. Convert the relative path of the file to a byte array, and add it to the hasher.
                hasher.AppendData(Encoding.UTF8.GetBytes(relativeFilePath))
                // 4. Convert the Int64 file length into a byte array, and add it to the hasher.
                hasher.AppendData(BitConverter.GetBytes(stream.Length))
                // 5. Get the SHA-256 hash as a byte[].
                let sha256Bytes = hasher.GetHashAndReset()
                // 6. Convert the SHA-256 value from a byte[] to a string, and return it.
                //    Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"
                return byteArrayAsString(sha256Bytes)
            finally
                ArrayPool<byte>.Shared.Return(buffer, clearArray = true)
        }

    let computeSha256ForDirectory (relativeDirectoryPath: RelativePath) (directories: List<LocalDirectoryVersion>) (files: List<LocalFileVersion>) =
        use hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        hasher.AppendData(ReadOnlySpan(Encoding.UTF8.GetBytes(relativeDirectoryPath)))
        
        // We're sorting just to get consistent ordering; inconsistent ordering would produce difference SHA-256 hashes.
        let sortedDirectories = directories |> Seq.sortBy(fun subdirectory -> subdirectory.RelativePath)
        for subdirectory in sortedDirectories do
            hasher.AppendData(ReadOnlySpan(Encoding.UTF8.GetBytes(subdirectory.Sha256Hash)))

        // Again, sorting to ensure consistent ordering.
        let sortedFiles = files |> Seq.sortBy(fun file -> file.RelativePath)
        for file in sortedFiles do
            hasher.AppendData(ReadOnlySpan(Encoding.UTF8.GetBytes(file.Sha256Hash)))

        let sha256Bytes = hasher.GetHashAndReset()
        byteArrayAsString sha256Bytes

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getDirectorySize (files: IList<FileVersion>) =
        files |> Seq.fold(fun (size: uint64) file -> size + file.Size ) 0UL

    /// Gets the total size of the files contained within this specific directory. This does not include the size of any subdirectories.
    let getLocalDirectorySize (files: IList<LocalFileVersion>) =
        files |> Seq.fold(fun (size: uint64) file -> size + file.Size) 0UL
