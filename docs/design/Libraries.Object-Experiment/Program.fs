module ObjectCaptureExperiment

open System
open System.IO
open System.Threading
open Grace.Shared.Utilities
open Microsoft.Data.Sqlite

/// Freezes the existing object locator and complete identity independently of working-file placement.
type ObjectReference = { Locator: string; Size: int64; Sha256: string; Blake3: string }

/// Represents only the saved-file stages needed to exercise durable object retention, without server modeling.
type SavedProgress = Captured | Frozen of request: string | Rejected of request: string * reasonCode: string | Completed of request: string

/// Stores a small durable saved-file record; this disposable shape is not a production operation contract.
type SavedOperation = { Id: Guid; SourcePath: string; Object: ObjectReference; Progress: SavedProgress }

/// Captures deterministic results and filesystem residue for each selected interruption.
type CaseResult = { Name: string; Passed: bool; Detail: string }

/// Stops at named effects while leaving the disk and SQLite state for a new invocation.
exception Interrupted of string

let results = ResizeArray<CaseResult>()
let basePath = Path.Combine(__SOURCE_DIRECTORY__, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff"))

/// Fails the experiment at the first violated invariant inside a case.
let require condition message = if not condition then failwith message

/// Runs one finite case and retains a readable success or failure instead of hiding later cases.
let case name action =
    try
        action ()
        results.Add { Name = name; Passed = true; Detail = "Assertions passed." }
    with ex -> results.Add { Name = name; Passed = false; Detail = ex.ToString() }

/// Requires a failure without mistaking an assertion from the harness for the expected exception.
let rejects action =
    let mutable failed = false
    try action () with _ -> failed <- true
    require failed "Expected the selected operation to reject."

/// Executes one parameterized SQLite statement on a freshly opened or transactional connection.
let sql (connection: SqliteConnection) transaction text parameters =
    use command = connection.CreateCommand()
    command.CommandText <- text
    transaction |> Option.iter (fun value -> command.Transaction <- value)
    parameters |> List.iter (fun (name, value: obj) -> command.Parameters.AddWithValue(name, value) |> ignore)
    command.ExecuteNonQuery() |> ignore

/// Opens the real scratch SQLite database with the production Library durability settings.
let connect root =
    let dbPath = Path.Combine(root, ".grace", "grace-local.db")
    let connection = new SqliteConnection($"Data Source={dbPath};Pooling=False")
    connection.Open()
    sql connection None "PRAGMA journal_mode=WAL;PRAGMA synchronous=FULL;" []
    connection

/// Creates only disposable files and a scratch operation table for the capture experiment.
let setup name =
    let root = Path.Combine(basePath, name)
    Directory.CreateDirectory(Path.Combine(root, ".grace", "objects")) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "Library")) |> ignore
    do
        use input = File.Create(Path.Combine(root, "Library", "original.txt"))
        let block = Array.init 65536 (fun index -> byte (index % 251))
        for _ in 1..3 do input.Write(block)
        input.WriteByte(17uy)
    use connection = connect root
    sql connection None "CREATE TABLE saved_operations(id TEXT PRIMARY KEY,json TEXT NOT NULL);" []
    root

/// Hashes a stream with the production SHA-256 and BLAKE3 helpers, each using a fixed 64 KiB buffer.
let identity (stream: Stream) relative =
    stream.Position <- 0L
    let sha = Grace.Shared.Services.computeSha256ForFile stream relative |> fun task -> task.GetAwaiter().GetResult()
    stream.Position <- 0L
    let blake = Grace.Shared.Services.computeBlake3ForFile stream |> fun task -> task.GetAwaiter().GetResult()
    stream.Length, string sha, string blake

/// Verifies a frozen locator without falling back to any working-file path.
let verify root reference =
    let path = Path.Combine(root, reference.Locator)
    require (not (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))) "Referenced object is a reparse point."
    use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan)
    let size, sha, blake = identity stream reference.Locator
    require ((size, sha, blake) = (reference.Size, reference.Sha256, reference.Blake3)) "Referenced object is incomplete or mismatched."

/// Looks up an uncertain commit by its operation identity using a new connection and the real Grace JSON serializer.
let load root id =
    use connection = connect root
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT json FROM saved_operations WHERE id=$id;"
    command.Parameters.AddWithValue("$id", id.ToString()) |> ignore
    let value = command.ExecuteScalar()
    if isNull value then None else Some(deserialize<SavedOperation> (string value))

/// Streams a stable source into a private same-volume temporary file and publishes only a verified complete object.
let captureObject root relative boundary =
    let sourcePath = Path.Combine(root, relative)
    let temporary = Path.Combine(root, ".grace", $"library-capture-{Guid.NewGuid():N}.tmp")
    boundary "before-stage"
    let info = FileInfo(sourcePath)
    require (not (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory))) "Source must be ordinary."
    use source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan)
    require (source.Length > 0L) "Empty input is excluded."
    let initialStamp = File.GetLastWriteTimeUtc(sourcePath)
    do
        use target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)
        let buffer = Array.zeroCreate<byte> 65536
        let mutable count = source.Read(buffer, 0, buffer.Length)
        let mutable first = true
        while count > 0 do
            target.Write(buffer, 0, count)
            if first then
                first <- false
                boundary "after-partial-stage"
            count <- source.Read(buffer, 0, buffer.Length)
        target.Flush(true)
    boundary "after-flush"
    let size, sha, blake =
        use staged = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan)
        identity staged relative
    require (identity source relative = (size, sha, blake) && File.GetLastWriteTimeUtc(sourcePath) = initialStamp) "Source changed during stable capture."
    let name = Grace.CLI.Services.getLocalObjectCacheFileName relative sha blake
    let locator = Path.Combine(".grace", "objects", relative, name)
    let reference = { Locator = locator; Size = size; Sha256 = sha; Blake3 = blake }
    let final = Path.Combine(root, locator)
    Directory.CreateDirectory(Path.GetDirectoryName(final)) |> ignore
    boundary "before-publication"
    if File.Exists(final) then
        verify root reference
        File.Delete(temporary)
    else
        try File.Move(temporary, final, false)
        with :? IOException when File.Exists(final) ->
            verify root reference
            File.Delete(temporary)
    boundary "after-publication"
    verify root reference
    reference

/// Commits only after the complete object exists; restart of an uncertain commit returns the original frozen record.
let captureAndCommit root id relative boundary =
    match load root id with
    | Some operation -> verify root operation.Object; operation
    | None ->
        let reference = captureObject root relative boundary
        boundary "before-commit"
        verify root reference
        let operation = { Id = id; SourcePath = relative; Object = reference; Progress = Captured }
        use connection = connect root
        use transaction = connection.BeginTransaction()
        sql connection (Some transaction) "INSERT INTO saved_operations(id,json) VALUES($id,$json);" [ "$id", box (id.ToString()); "$json", box (serialize operation) ]
        boundary "inside-transaction"
        transaction.Commit()
        boundary "after-commit"
        operation

/// Saves a modeled progress transition without replacing the immutable object reference.
let transition root operation progress =
    use connection = connect root
    let next = { operation with Progress = progress }
    sql connection None "UPDATE saved_operations SET json=$json WHERE id=$id;" [ "$id", box (operation.Id.ToString()); "$json", box (serialize next) ]
    next

/// Confirms current production pruning and cache-metadata removal do not delete a shared physical object.
let actualCleanup root reference =
    let db = Path.Combine(root, ".grace", "cleanup.db")
    Grace.CLI.LibraryLocalState.initialize db |> fun task -> task.GetAwaiter().GetResult()
    let repositoryId = Guid.NewGuid()
    let catalog = Grace.Types.Library.LibraryCatalogDto.CreateInitial(repositoryId, getCurrentInstant(), "preflight")
    let state: Grace.CLI.LibraryLocalState.RepositoryState =
        { RepositoryId = repositoryId; WorkingCopyId = Guid.NewGuid(); Catalog = catalog; CursorEpoch = "epoch"; AppliedCursor = "tip"; NextPageToken = None; State = "current"; Baseline = None }
    Grace.CLI.LibraryLocalState.enable db state
    let operation: Grace.CLI.LibraryLocalState.PendingOperation =
        { OperationId = Guid.NewGuid(); Direction = "local"; Rename = false; Receipt = None; SourcePath = "Library/original.txt"; SourceBytes = Some [| 1uy |]; MaterializedBase = None; OriginatingCreateId = None; Parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }; Name = "original.txt"; ItemKind = "file"; RequestJson = None; Uploaded = false; Accepted = None; BaselineItem = None; Prepared = false; ExpectedCatalogVersion = catalog.Version; ExpectedCursor = "tip"; ExpectedAncestry = [||]; ExpectedTarget = None; TargetPath = "Library/original.txt"; Terminal = false; EchoPending = false; CreatedAtTicks = 1L }
    Grace.CLI.LibraryLocalState.insertOperation db repositoryId operation
    let terminal = { operation with OperationId = Guid.NewGuid(); SourceBytes = None; Terminal = true }
    use connection = Grace.CLI.LibraryLocalState.openConnection db
    sql connection None "INSERT INTO library_operations(repository_id,operation_id,direction,terminal,echo_pending,created_at_ticks,operation_json) VALUES($repo,$id,'local',1,0,2,$json);" [ "$repo", box (repositoryId.ToString()); "$id", box (terminal.OperationId.ToString()); "$json", box (serialize terminal) ]
    Grace.CLI.LibraryLocalState.pruneClassified db repositoryId 0
    require ((Grace.CLI.LibraryLocalState.readOperations db repositoryId) = [| operation |]) "Production pruning changed pending input or failed to prune terminal history."
    let directoryId = Guid.NewGuid()
    sql connection None "INSERT INTO object_cache_directories VALUES($id,'Library','directory-sha','directory-blake',0,0,0);" [ "$id", box (directoryId.ToString()) ]
    sql connection None "INSERT INTO object_cache_directory_files VALUES($id,'Library/original.txt',$sha,$blake,1,$size,0,0,0);" [ "$id", box (directoryId.ToString()); "$sha", box reference.Sha256; "$blake", box reference.Blake3; "$size", box reference.Size ]
    require (Grace.CLI.LocalStateDb.isDirectoryVersionInObjectCache db directoryId |> fun task -> task.GetAwaiter().GetResult()) "Cache directory was not seeded."
    Grace.CLI.LocalStateDb.removeObjectCacheDirectory db directoryId |> fun task -> task.GetAwaiter().GetResult()
    require (not (Grace.CLI.LocalStateDb.isDirectoryVersionInObjectCache db directoryId |> fun task -> task.GetAwaiter().GetResult())) "Actual cache removal retained the directory row."
    use check = connection.CreateCommand()
    check.CommandText <- "SELECT COUNT(*) FROM object_cache_directory_files;"
    require (Convert.ToInt64(check.ExecuteScalar()) = 0L) "File metadata did not cascade with real directory removal."
    require ((Grace.CLI.LibraryLocalState.readOperations db repositoryId) = [| operation |]) "Cache removal affected pending Library input."
    verify root reference

/// Executes the finite platform experiment and returns a failing process status if any assertion fails.
[<EntryPoint>]
let main _ =
    require (OperatingSystem.IsWindows()) "This experiment requires Windows."
    Directory.CreateDirectory(basePath) |> ignore
    for boundary in [ "before-stage"; "after-partial-stage"; "after-flush"; "before-publication"; "after-publication"; "before-commit"; "inside-transaction"; "after-commit" ] do
        case ("restart-" + boundary) (fun () ->
            let root = setup boundary
            let id = Guid.NewGuid()
            rejects (fun () -> captureAndCommit root id "Library/original.txt" (fun effect -> if effect = boundary then raise (Interrupted effect)) |> ignore)
            require ((load root id).IsSome = (boundary = "after-commit")) "Unexpected SQLite commit residue."
            if boundary = "after-partial-stage" then
                let staged = Directory.GetFiles(Path.Combine(root, ".grace"), "library-capture-*.tmp") |> Array.exactlyOne
                require (FileInfo(staged).Length = 65536L && FileInfo(staged).Length < FileInfo(Path.Combine(root, "Library/original.txt")).Length) "Stage was not genuinely partial."
            let operation = captureAndCommit root id "Library/original.txt" ignore
            verify root operation.Object
            require ((load root id) = Some operation) "Restart did not retain the complete object reference.")
    case "reuse-without-rewrite-and-two-references" (fun () ->
        let root = setup "reuse"
        let first = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" ignore
        let path = Path.Combine(root, first.Object.Locator)
        File.SetLastWriteTimeUtc(path, DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc))
        let stamp = File.GetLastWriteTimeUtc(path)
        let second = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" ignore
        require (first.Object = second.Object && File.GetLastWriteTimeUtc(path) = stamp) "Reuse rewrote or relocated content."
        actualCleanup root first.Object)
    case "working-file-rename-keeps-frozen-locator" (fun () ->
        let root = setup "rename"
        let operation = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" ignore
        File.Move(Path.Combine(root, "Library/original.txt"), Path.Combine(root, "Library/renamed.txt"))
        require (captureAndCommit root operation.Id "Library/renamed.txt" ignore = operation) "Restart derived a new locator from renamed input.")
    for mutation in [ "missing"; "corrupt" ] do
        case (mutation + "-reference-never-recaptures-changed-source") (fun () ->
            let root = setup mutation
            let operation = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" ignore
            let frozen = transition root operation (Rejected("frozen request", "ItemTombstoned"))
            if mutation = "missing" then File.Delete(Path.Combine(root, operation.Object.Locator))
            else File.WriteAllText(Path.Combine(root, operation.Object.Locator), "corrupt")
            File.WriteAllText(Path.Combine(root, "Library/original.txt"), "new working content")
            rejects (fun () -> captureAndCommit root operation.Id operation.SourcePath ignore |> ignore)
            require (load root operation.Id = Some frozen) "Failure replaced rejected saved content metadata.")
    case "partial-final-object-is-never-overwritten" (fun () ->
        let root = setup "partial-final"
        let reference = captureObject root "Library/original.txt" ignore
        File.WriteAllText(Path.Combine(root, reference.Locator), "partial")
        let id = Guid.NewGuid()
        rejects (fun () -> captureAndCommit root id "Library/original.txt" ignore |> ignore)
        require ((load root id).IsNone && File.ReadAllText(Path.Combine(root, reference.Locator)) = "partial") "Mismatch committed or overwritten.")
    case "source-writer-is-excluded-during-snapshot" (fun () ->
        let root = setup "writer"
        let mutable excluded = false
        captureAndCommit root (Guid.NewGuid()) "Library/original.txt" (fun effect ->
            if effect = "after-flush" then
                try File.WriteAllText(Path.Combine(root, "Library/original.txt"), "racing edit")
                with :? IOException -> excluded <- true) |> ignore
        require excluded "A writer changed the selected source while it was captured.")
    case "source-change-after-capture-does-not-replace-selected-object" (fun () ->
        let root = setup "later-source"
        let operation = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" (fun effect -> if effect = "before-commit" then File.WriteAllText(Path.Combine(root, "Library/original.txt"), "later saved bytes"))
        let next = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" ignore
        require (operation.Object <> next.Object) "Later save replaced the earlier selected object."
        verify root operation.Object)
    case "empty-source-does-not-commit" (fun () ->
        let root = setup "empty"
        File.WriteAllText(Path.Combine(root, "Library/original.txt"), "")
        let id = Guid.NewGuid()
        rejects (fun () -> captureAndCommit root id "Library/original.txt" ignore |> ignore)
        require ((load root id).IsNone) "Empty input committed.")
    case "cancellation-before-commit-retains-only-complete-object" (fun () ->
        let root = setup "cancel"
        let id = Guid.NewGuid()
        rejects (fun () -> captureAndCommit root id "Library/original.txt" (fun effect -> if effect = "before-commit" then raise (OperationCanceledException())) |> ignore)
        require ((load root id).IsNone) "Canceled capture committed."
        captureAndCommit root id "Library/original.txt" ignore |> ignore)
    case "large-input-stays-out-of-json-and-stream-read-is-stable" (fun () ->
        let root = setup "large"
        do
            use stream = File.Create(Path.Combine(root, "Library/original.txt"))
            let block = Array.init 65536 (fun index -> byte (index % 251))
            for _ in 1..1024 do stream.Write(block)
        let operation = captureAndCommit root (Guid.NewGuid()) "Library/original.txt" ignore
        require ((serialize operation).Length < 1024 && operation.Object.Size = 67108864L) "JSON payload grows with saved content."
        use source = new FileStream(Path.Combine(root, operation.Object.Locator), FileMode.Open, FileAccess.Read, FileShare.Read)
        source.CopyTo(Stream.Null, 65536)
        require (identity source operation.SourcePath = (operation.Object.Size, operation.Object.Sha256, operation.Object.Blake3)) "Stream upload input changed.")
    let output = Path.Combine(__SOURCE_DIRECTORY__, "results.json")
    File.WriteAllText(output, serialize {| Root = basePath; Runtime = Environment.Version.ToString(); Cases = results.ToArray(); Passed = results |> Seq.filter (fun item -> item.Passed) |> Seq.length; Failed = results |> Seq.filter (fun item -> not item.Passed) |> Seq.length |})
    for result in results do printfn "%s %s%s" (if result.Passed then "PASS" else "FAIL") result.Name (if result.Passed then "" else ": " + result.Detail)
    printfn "Results: %s" output
    if results |> Seq.forall (fun item -> item.Passed) then 0 else 1
