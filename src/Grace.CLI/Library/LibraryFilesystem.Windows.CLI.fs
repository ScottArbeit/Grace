namespace Grace.CLI.Command

open Grace.Shared
open System
open System.IO
open System.Security.Cryptography
open Grace.Shared.Client.Configuration
open Grace.CLI.LibraryOperation

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

    /// Names the test-only variable whose text the CLI writes to a Library path just after sync's last check of that path
    /// and just before it displaces it, once per process, and only while GRACE_TESTING is "1". It lets a hosted test
    /// put an editor's save in the window that timing can't reach.
    [<Literal>]
    let SaveBeforeDisplaceVariable = "GRACE_TEST_LIBRARY_SAVE_BEFORE_DISPLACE"

    /// Writes the configured test save once, when the test variables ask for it.
    let private saveBeforeDisplaceFromEnvironment =
        let used = ref false

        fun (path: string) ->
            if not used.Value
               && Environment.GetEnvironmentVariable("GRACE_TESTING") = "1" then
                match Environment.GetEnvironmentVariable(SaveBeforeDisplaceVariable) with
                | null
                | "" -> ()
                | text ->
                    used.Value <- true
                    File.WriteAllText(path, text)

    /// Runs just after sync's last content check of a Library path and just before sync replaces, moves aside or
    /// deletes it. Tests replace it to put an editor's save in that window.
    let mutable internal beforeDisplace: string -> unit = saveBeforeDisplaceFromEnvironment

    /// The most numbered conflict-copy names sync tries next to one file.
    [<Literal>]
    let private MaximumConflictAttempts = 100

    /// Returns the conflict-copy paths next to a Library file, first choice first.
    let private conflictPaths (path: string) operationId =
        let folder = Path.GetDirectoryName path
        let name = Path.GetFileName path

        seq { 0 .. MaximumConflictAttempts - 1 }
        |> Seq.map (fun attempt -> Path.Combine(folder, Grace.Shared.Validation.Library.conflictName name operationId attempt))

    /// Returns the first conflict-copy path next to a Library file that nothing occupies.
    let private vacantConflictPath path operationId =
        conflictPaths path operationId
        |> Seq.tryFind (fun candidate ->
            not (
                File.Exists candidate
                || Directory.Exists candidate
            ))
        |> Option.defaultWith (fun () -> invalidOp $"Every conflict-copy name next to '{path}' is taken.")

    /// Deletes a file sync displaced only when it holds exactly the expected bytes. The file is hashed and deleted
    /// under one handle that lets nobody else write it, so it can't change in between. Returns false, keeping the file,
    /// when its bytes differ or another program still has it open.
    let private discardIfExpected (path: string) expected =
        let stream =
            try
                Some(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete, 65536, FileOptions.SequentialScan))
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match stream with
        | None -> false
        | Some stream ->
            use stream = stream
            let identity = streamIdentity stream

            if Some(Grace.CLI.LibraryOperation.fingerprint identity) = expected then
                File.Delete path
                true
            else
                false

    /// Renames a Library file to the first free conflict-copy name next to it, without overwriting anything. Returns
    /// None when the file is already gone.
    let private moveAside (path: string) operationId =
        conflictPaths path operationId
        |> Seq.filter (fun candidate ->
            not (
                File.Exists candidate
                || Directory.Exists candidate
            ))
        |> Seq.tryPick (fun candidate ->
            try
                File.Move(path, candidate, false)
                Some(Some candidate)
            with
            | :? FileNotFoundException -> Some None
            | :? IOException when
                File.Exists candidate
                || Directory.Exists candidate
                ->
                None)
        |> Option.defaultWith (fun () ->
            if File.Exists path then
                invalidOp $"Every conflict-copy name next to '{path}' is taken."
            else
                None)

    /// Removes a Library file that sync no longer wants, a delete or a rename's old path, without ever destroying bytes
    /// it didn't expect. The file is first renamed aside, so a save that lands after the caller's check goes with it,
    /// and then deleted only if it holds exactly the expected bytes. Otherwise it goes back to its path, or stays under
    /// the conflict-copy name when its path has been taken again. Returns where a kept file is, or None when the file
    /// was removed or was already gone.
    let removeFile operationId (path: string) expected =
        beforeDisplace path

        match moveAside path operationId with
        | None -> None
        | Some aside ->
            if discardIfExpected aside expected then
                None
            else
                try
                    File.Move(aside, path, false)
                    Some path
                with
                | :? IOException -> Some aside

    /// Publishes verified bytes from the same-volume Grace staging directory without ever destroying bytes Grace hasn't
    /// captured. Where nothing is expected, the file moves in without overwriting, so a file that appeared stops the
    /// publication and is kept. Where a file is expected, File.Replace swaps the new bytes in and keeps the displaced
    /// file next to it under the conflict-copy name; it's deleted only if it holds exactly the expected bytes. A save
    /// that landed after the last check therefore survives as a conflict copy, which capture then syncs like any other
    /// file. Returns where a kept file is, if one was kept.
    let publishAtomic beforePublish operationId (staged: string) (targetPath: string) expected (bytes: byte array) =
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

            beforeDisplace targetPath

            let kept =
                match expected with
                | None ->
                    try
                        File.Move(staged, targetPath, false)
                    with
                    | :? IOException when
                        File.Exists targetPath
                        || Directory.Exists targetPath
                        ->
                        invalidOp "A file appeared at the Library target before publication; it was kept."

                    None
                | Some _ ->
                    let aside = vacantConflictPath targetPath operationId

                    try
                        File.Replace(staged, targetPath, aside, true)
                    with
                    | _ ->
                        // If the swap renamed the old file but couldn't move the new one in, put the old one back.
                        if not (File.Exists targetPath) && File.Exists aside then
                            try
                                File.Move(aside, targetPath, false)
                            with
                            | :? IOException -> ()

                        reraise ()

                    if discardIfExpected aside expected then None else Some aside

            if (stableRead targetPath).Bytes <> bytes then
                invalidOp "Library final target verification failed."

            kept
        finally
            if File.Exists(staged) then File.Delete(staged)
