namespace Grace.CLI.Command

open Grace.Shared
open System
open System.IO
open System.Security.Cryptography

/// Supplies the existing narrow Windows read and same-volume publication mechanics for Library files.
module internal LibraryFilesystem =

    /// Holds immutable bytes and their complete content identity for one captured save.
    type StableContent = { Bytes: byte array; Blake3Hash: string; Sha256Hash: string; Size: int64 }

    /// Hashes complete immutable binary bytes using the same content identities as the server.
    let content bytes =
        {
            Bytes = bytes
            Blake3Hash = ContentAddress.computeBlake3Hex bytes
            Sha256Hash =
                SHA256.HashData(bytes)
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()
            Size = int64 bytes.LongLength
        }

    /// Rejects a nonordinary file or a source that changes during a complete read.
    let stableReadWith afterRead (path: string) =
        if not (OperatingSystem.IsWindows()) then
            invalidOp "Library synchronization requires Windows 11."

        let before = FileInfo(path)

        if
            before.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || before.Attributes.HasFlag(FileAttributes.Directory)
        then
            invalidOp "Library synchronization accepts ordinary files only."

        let stamp = before.LastWriteTimeUtc
        let length = before.Length
        let bytes = File.ReadAllBytes(path)
        afterRead ()
        let after = FileInfo(path)

        if stamp <> after.LastWriteTimeUtc
           || length <> after.Length
           || bytes <> File.ReadAllBytes(path) then
            invalidOp "Library source changed during its stable read."

        content bytes

    /// Captures one immutable saved file without injecting an interleaving.
    let stableRead path = stableReadWith ignore path

    /// Encodes the full target content precondition, keeping absence distinct from an empty file.
    let fingerprint path =
        if File.Exists(path) then
            let value = stableRead path
            Some $"{value.Blake3Hash}:{value.Sha256Hash}:{value.Size}"
        elif Directory.Exists(path) then
            Some "directory"
        else
            None

    /// Flushes verified bytes in the same-volume Grace staging directory and publishes with the File.Move primitive used by WDU.
    let publishAtomic beforePublish (staged: string) (targetPath: string) expected (bytes: byte array) =
        try
            do
                use stream = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)
                stream.Write(bytes)
                stream.Flush(true)

            if File.ReadAllBytes(staged) <> bytes then
                invalidOp "Library staging verification failed."

            beforePublish ()

            if fingerprint targetPath <> expected then
                invalidOp "Library target changed before atomic publication."

            File.Move(staged, targetPath, true)

            if (stableRead targetPath).Bytes <> bytes then
                invalidOp "Library final target verification failed."
        finally
            if File.Exists(staged) then File.Delete(staged)
