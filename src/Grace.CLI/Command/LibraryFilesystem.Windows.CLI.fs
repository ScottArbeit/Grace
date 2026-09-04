namespace Grace.CLI.Command

open Grace.Shared
open Grace.Shared.Utilities
open System
open System.IO
open System.Security.Cryptography

/// Provides the narrow Windows filesystem effects required by the two-copy Library tracer.
module internal LibraryFilesystem =

    /// Carries immutable bytes and both exact content hashes from one stable local read.
    type StableContent = { Bytes: byte array; Blake3Hash: string; Sha256Hash: string; Size: int64 }

    /// Captures the metadata reread on both sides of a local file read.
    type private Observation = { Length: int64; LastWriteTimeUtc: DateTime; CreationTimeUtc: DateTime; Attributes: FileAttributes }

    /// Reads the metadata used to reject a file that changes while its bytes are being prepared.
    let private observe path =
        let file = FileInfo(path)
        file.Refresh()

        if not file.Exists then
            raise (FileNotFoundException("Library publication source does not exist.", path))

        { Length = file.Length; LastWriteTimeUtc = file.LastWriteTimeUtc; CreationTimeUtc = file.CreationTimeUtc; Attributes = file.Attributes }

    /// Computes the accepted lower-case SHA-256 hash for exact bytes.
    let private sha256 (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    /// Reads a regular file only when metadata is identical before and after the complete-byte read.
    let stableReadWith (afterRead: unit -> unit) (path: string) =
        if not (OperatingSystem.IsWindows()) then
            invalidOp "Library synchronization execution is supported only on Windows 11."

        let before = observe path

        if
            before.Attributes.HasFlag(FileAttributes.Directory)
            || before.Attributes.HasFlag(FileAttributes.ReparsePoint)
        then
            invalidOp "Library publication accepts only ordinary files."

        let bytes = File.ReadAllBytes(path)
        afterRead ()
        let after = observe path

        if before <> after
           || int64 bytes.LongLength <> before.Length then
            invalidOp "Library publication source changed during its stable read."

        { Bytes = bytes; Blake3Hash = ContentAddress.computeBlake3Hex bytes; Sha256Hash = sha256 bytes; Size = int64 bytes.LongLength }

    /// Reads stable local bytes without injecting a test boundary.
    let stableRead (path: string) = stableReadWith ignore path

    /// Reports whether current target bytes match one exact accepted content identity.
    let matchesContent path expectedBlake3 expectedSha256 expectedSize =
        if not (File.Exists(path)) then
            false
        else
            let content = stableRead path

            content.Blake3Hash = expectedBlake3
            && content.Sha256Hash = expectedSha256
            && content.Size = expectedSize

    /// Publishes verified remote bytes by a same-directory atomic move and verifies terminal target bytes.
    let publishAtomic (targetPath: string) (expectedBlake3: string) (expectedSha256: string) (expectedSize: int64) (bytes: byte array) =
        if not (OperatingSystem.IsWindows()) then
            invalidOp "Library synchronization execution is supported only on Windows 11."

        if int64 bytes.LongLength <> expectedSize
           || ContentAddress.computeBlake3Hex bytes
              <> expectedBlake3
           || sha256 bytes <> expectedSha256 then
            invalidOp "Downloaded Library content did not match its accepted exact-byte identity."

        let targetDirectory = Path.GetDirectoryName(targetPath)

        if String.IsNullOrWhiteSpace(targetDirectory) then
            invalidArg (nameof targetPath) "Library target must have a parent directory."

        Directory.CreateDirectory(targetDirectory)
        |> ignore

        let stagingPath = Path.Combine(targetDirectory, $".grace-library-{Guid.NewGuid():N}.tmp")

        try
            use stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush(true)
            stream.Dispose()
            File.Move(stagingPath, targetPath, true)
            let terminal = stableRead targetPath

            if terminal.Blake3Hash <> expectedBlake3
               || terminal.Sha256Hash <> expectedSha256
               || terminal.Size <> expectedSize then
                invalidOp "Library atomic publication did not leave the accepted terminal bytes."
        finally
            if File.Exists(stagingPath) then File.Delete(stagingPath)
