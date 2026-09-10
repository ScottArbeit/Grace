namespace Grace.CLI.Command

open Grace.Shared
open System
open System.IO
open System.Security.Cryptography
open Grace.Shared.Client.Configuration
open Grace.CLI.LibraryOperation

/// Supplies the existing narrow Windows read and same-volume publication mechanics for Library files.
module internal LibraryFilesystem =

    /// Checks every existing directory from a configured Library root through the working root, permitting missing directories only.
    let requireRootPath (configuration: GraceConfiguration) (relative: string) =
        let normalized =
            Grace.Shared.Validation.Library.normalizeRepositoryRelativePath relative
            |> Result.defaultWith invalidOp

        let root =
            Path
                .GetFullPath(configuration.RootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)

        let path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)))

        if not (path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) then
            invalidOp "Library root escaped the working directory."

        let mutable current = path

        while current.Length >= root.Length do
            let attributes =
                try
                    Some(File.GetAttributes current)
                with
                | :? FileNotFoundException
                | :? DirectoryNotFoundException -> None

            match attributes with
            | Some value when
                value.HasFlag(FileAttributes.ReparsePoint)
                || not (value.HasFlag(FileAttributes.Directory))
                ->
                invalidOp "Library roots require ordinary directories and ancestry; local input is retained."
            | _ -> ()

            current <- Path.GetDirectoryName current

        path

    /// Refuses occupied additions before selection so previously unrelated local bytes cannot enter Library synchronization.
    let requireEmptyRoot configuration relative =
        let path = requireRootPath configuration relative

        if
            Directory.Exists path
            && not
                (
                    Directory.EnumerateFileSystemEntries(path)
                    |> Seq.isEmpty
                )
        then
            invalidOp "An added Library root is occupied; local input is retained."

        path

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

    /// Computes both existing content hashes without buffering the whole source file.
    let private streamIdentity (stream: Stream) =
        stream.Position <- 0L

        let sha =
            Grace.Shared.Services.computeSha256ForFile stream ""
            |> fun value -> value.GetAwaiter().GetResult()

        stream.Position <- 0L

        let blake =
            Grace.Shared.Services.computeBlake3ForFile stream
            |> fun value -> value.GetAwaiter().GetResult()

        { Size = stream.Length; Sha256Hash = string sha; Blake3Hash = string blake }

    /// Opens an ordinary source with sharing that excludes changes throughout its stable snapshot.
    let private openStableSource (path: string) =
        if not (OperatingSystem.IsWindows()) then
            invalidOp "Library synchronization requires Windows 11."

        let attributes = File.GetAttributes path

        if
            attributes.HasFlag(FileAttributes.ReparsePoint)
            || attributes.HasFlag(FileAttributes.Directory)
        then
            invalidOp "Library synchronization accepts ordinary files only."

        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan)

    /// Reads a stable complete identity for admission and saved-observation comparison.
    let stableIdentity path =
        use stream = openStableSource path
        streamIdentity stream

    /// Resolves a frozen locator under the configured object directory, independent of working-file placement.
    let objectPath (configuration: GraceConfiguration) (reference: SavedObject) =
        let root =
            Path
                .GetFullPath(configuration.ObjectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        let path = Path.GetFullPath(Path.Combine(root, reference.ObjectPath))

        if not (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) then
            invalidOp "Library object locator escaped the configured object directory."

        let mutable parent = Path.GetDirectoryName path

        while parent.Length
              >= root.TrimEnd(Path.DirectorySeparatorChar).Length do
            if
                (Directory.Exists(parent) || File.Exists(parent))
                && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)
            then
                invalidOp "Library object ancestry is a reparse point."

            parent <- Path.GetDirectoryName parent

        path

    /// Verifies the frozen object and returns a read lease that prevents its replacement during consumption.
    let openObject configuration reference =
        let stream = openStableSource (objectPath configuration reference)

        try
            if streamIdentity stream <> reference.Content
               || reference.Content.Size <= 0L then
                invalidOp "Library saved object is missing, incomplete or corrupt; working-file bytes cannot replace it."

            stream.Position <- 0L
            stream
        with
        | _ ->
            stream.Dispose()
            reraise ()

    /// Publishes a complete verified snapshot before its caller can commit a saved operation.
    let captureObjectWith afterPublication (configuration: GraceConfiguration) relative expected =
        let sourcePath = Path.Combine(configuration.RootDirectory, relative)

        Directory.CreateDirectory(configuration.ObjectDirectory)
        |> ignore

        let temporary = Path.Combine(configuration.ObjectDirectory, $"library-capture-{Guid.NewGuid():N}.tmp")

        try
            use source = openStableSource sourcePath
            if source.Length <= 0L then invalidOp "Library capture excludes empty files."

            do
                use staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)
                source.CopyTo(staged, 65536)
                staged.Flush(true)

            let captured = stableIdentity temporary

            if captured <> expected
               || streamIdentity source <> captured then
                invalidOp "Library source changed before object capture."

            let name = Grace.CLI.Services.getLocalObjectCacheFileName relative captured.Sha256Hash captured.Blake3Hash
            let reference = { ObjectPath = Path.Combine(relative, name); Content = captured }

            let target = objectPath configuration reference

            Directory.CreateDirectory(Path.GetDirectoryName(target))
            |> ignore

            objectPath configuration reference |> ignore

            if File.Exists target then
                use verified = openObject configuration reference
                ()
            else
                try
                    File.Move(temporary, target, false)
                with
                | :? IOException when File.Exists target ->
                    use verified = openObject configuration reference
                    ()

            use verified = openObject configuration reference
            afterPublication reference
            reference
        finally
            if File.Exists temporary then File.Delete temporary

    /// Captures or reuses immutable content under the existing configured object layout.
    let captureObject configuration relative expected = captureObjectWith ignore configuration relative expected

    /// Encodes the full target content precondition, keeping absence distinct from an empty file.
    let fingerprint path =
        if File.Exists(path) then
            let value = stableIdentity path
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
