namespace Grace.CLI.Tests

open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.LibraryOperation
open Grace.CLI.LibraryLocalState
open Grace.Shared.Utilities
open Grace.Types.Library
open Microsoft.Data.Sqlite
open NUnit.Framework
open System
open System.IO

/// Exercises the real Library SQLite completion boundary and Windows filesystem guards.
[<NonParallelizable>]
module LibraryLocalStateTests =

    /// Selects the NUnit Action overload without the deprecated TestDelegate conversion.
    let private throws<'T when 'T :> exn> action = Assert.Throws<'T>(Action action) |> ignore

    /// Allocates a disposable existing-database location for one focused scenario.
    let private location () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-library-local-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore
        root, Path.Combine(root, "grace-local.db")

    /// Supplies the small nonempty payload used by SQLite-only accepted-operation fixtures.
    let private sampleBytes = [| 0uy; 127uy; 255uy |]

    /// Builds an immutable content reference without writing objects in SQLite-only tests.
    let private objectReference bytes =
        let content = LibraryFilesystem.content bytes
        { ObjectPath = "fixture-object"; Content = { Size = content.Size; Sha256Hash = content.Sha256Hash; Blake3Hash = content.Blake3Hash } }

    /// Constructs a prepared incoming record for tests of completion and echo authority.
    let private incoming cursor source target previous echo (change: LibraryChangeDto) =
        let checkpoint =
            {
                ExpectedCursor = cursor
                ExpectedAncestry = previous |> Option.toArray
                ExpectedTarget = TargetObservation.Absent
                SourcePath = source
                TargetPath = target
                Echo = echo
            }

        {
            OperationId = change.OperationId
            CatalogVersion = change.LibraryCatalogVersion
            CreatedAtTicks = 1L
            Work = OperationWork.Incoming(change, source, previous, ApplicationProgress.Prepared checkpoint)
        }

    /// Gives a selected rename a receipt and prepared checkpoint without duplicating the accepted change.
    let private acceptedRename cursor (change: LibraryChangeDto) (operation: PendingOperation) =
        let checkpoint =
            {
                ExpectedCursor = cursor
                ExpectedAncestry = operation.MaterializedBase |> Option.toArray
                ExpectedTarget = TargetObservation.Absent
                SourcePath = operation.SourcePath
                TargetPath = operation.TargetPath
                Echo = EchoState.Pending
            }

        match operation.Work with
        | OperationWork.ExplicitRename (intent, _) ->
            { operation with
                Work =
                    OperationWork.ExplicitRename(
                        intent,
                        RenameProgress.Accepted("frozen namespace request", { RequestHash = "hash"; Change = change }, ApplicationProgress.Prepared checkpoint)
                    )
            }
        | _ -> invalidOp "Expected a selected rename."

    /// Supplies one exact accepted create with opaque cursors and independent content identity.
    let private prepared () =
        let repositoryId = Guid.NewGuid()
        let parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }

        let state =
            {
                RepositoryId = repositoryId
                WorkingCopyId = Guid.NewGuid()
                Catalog =
                    {
                        RepositoryId = repositoryId
                        Version = Guid.NewGuid()
                        Libraries = [| "Library" |]
                        CreatedAt = getCurrentInstant ()
                        CreatedBy = "library-test"
                        PreviousVersion = None
                    }
                CursorEpoch = "opaque-epoch"
                AppliedCursor = "opaque-before"
                NextPageToken = None
                State = "catchingUp"
                Paused = false
                Baseline = None
            }

        let bytes = sampleBytes
        let source = LibraryFilesystem.content bytes

        let item =
            {
                ItemId = Guid.NewGuid()
                ItemKind = ItemKind.File
                LastChangeCursor = "opaque-after"
                Namespace = Some { Parent = parent; Name = "item.bin"; NamespaceVersion = Guid.NewGuid() }
                Content =
                    Some
                        {
                            ContentVersionId = Guid.NewGuid()
                            Blake3Hash = source.Blake3Hash
                            Sha256Hash = source.Sha256Hash
                            Size = source.Size
                            CreatedAt = getCurrentInstant ()
                        }
                ContentRevision = Some "opaque-after"
                Tombstone = None
            }

        let change =
            {
                OperationId = Guid.NewGuid()
                ChangeKind = ChangeKind.CreateFile
                AcceptedAt = getCurrentInstant ()
                AcceptedBy = "library-test"
                LibraryCatalogVersion = state.Catalog.Version
                Item = item
                Conflict = None
            }

        let pending =
            {
                OperationId = change.OperationId
                CatalogVersion = state.Catalog.Version
                Work =
                    OperationWork.SavedFile(
                        { SourcePath = "Library/item.bin"; Object = objectReference bytes; Base = SavedBase.NewFile { Parent = parent; Name = "item.bin" } },
                        SavedFileProgress.Accepted(
                            "exact-submitted-request",
                            { RequestHash = "hash"; Change = change },
                            ApplicationProgress.Prepared
                                {
                                    ExpectedCursor = state.AppliedCursor
                                    ExpectedAncestry = [||]
                                    ExpectedTarget = TargetObservation.Absent
                                    SourcePath = "Library/item.bin"
                                    TargetPath = "Library/item.bin"
                                    Echo = EchoState.Clear
                                }
                        )
                    )
                CreatedAtTicks = 1L
            }

        state, pending

    /// Creates actual materialized SQLite state and clean Windows bytes for explicit rename admission.
    let private renameCopy () =
        task {
            let root, db = location ()

            Directory.CreateDirectory(Path.Combine(root, "Library"))
            |> ignore

            let configuration = Grace.Shared.Client.Configuration.GraceConfiguration()
            configuration.RootDirectory <- root
            configuration.ObjectDirectory <- Path.Combine(root, ".grace", "objects")
            configuration.GraceStatusFile <- db
            do! initialize db
            let initial, first = prepared ()
            configuration.RepositoryId <- initial.RepositoryId
            enable db initial
            insertOperation db initial.RepositoryId first
            File.WriteAllBytes(Path.Combine(root, first.SourcePath), sampleBytes)
            complete db initial first
            setState db (readRepository db initial.RepositoryId).Value "current"
            return configuration, (readRepository db initial.RepositoryId).Value, first.Accepted.Value.Item
        }

    /// Checks persisted pause against real saved/terminal rows, capture, classification and stale completion callers.
    [<Test>]
    let ``pause preserves progress and exact work while stale callers cannot complete`` () =
        task {
            let! configuration, before, _ = renameCopy ()
            let db, repository = configuration.GraceStatusFile, configuration.RepositoryId
            let retained = readOperations db repository
            let! paused = LibrarySynchronization.pause configuration System.Threading.CancellationToken.None
            Assert.That(paused.Enabled && paused.Paused, Is.True)
            Assert.That(readRepository db repository, Is.EqualTo(Some { before with Paused = true }))
            let! _ = LibrarySynchronization.pause configuration System.Threading.CancellationToken.None
            File.WriteAllText(Path.Combine(configuration.RootDirectory, "Library", "latest.txt"), "paused save")
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.False)

            do!
                LibrarySynchronization.classifyWatchObservations
                    configuration
                    [|
                        Path.Combine(configuration.RootDirectory, "Library", "item.bin")
                    |]
                    System.Threading.CancellationToken.None

            Assert.That(readOperations db repository, Is.EqualTo<PendingOperation>(retained))
            throws<InvalidOperationException> (fun () -> setState db before "blocked")
            throws<InvalidOperationException> (fun () -> recordPage db before (Some "stale-page"))
            throws<InvalidOperationException> (fun () -> setPaused db before false)
            let! _ = LibrarySynchronization.changePause configuration false System.Threading.CancellationToken.None
            Assert.That(readRepository db repository, Is.EqualTo(Some before))
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.True)
            Assert.That((readOperations db repository).Length, Is.EqualTo(retained.Length + 1))
        }

    /// An interrupted SQLite pause commit retains the old value and exact operation rows.
    [<Test>]
    let ``pause update failure rolls back without changing retained work`` () =
        task {
            let! configuration, before, _ = renameCopy ()
            let db = configuration.GraceStatusFile
            let operations = readOperations db configuration.RepositoryId
            use connection = openConnection db
            use command = connection.CreateCommand()

            command.CommandText <-
                "CREATE TRIGGER fail_pause BEFORE UPDATE OF paused ON library_repository_state BEGIN SELECT RAISE(ABORT,'pause interruption'); END;"

            command.ExecuteNonQuery() |> ignore

            try
                let! _ = LibrarySynchronization.pause configuration System.Threading.CancellationToken.None
                Assert.Fail("Injected pause commit unexpectedly succeeded.")
            with
            | :? SqliteException -> ()

            Assert.That(readRepository db configuration.RepositoryId, Is.EqualTo(Some before))
            Assert.That(readOperations db configuration.RepositoryId, Is.EqualTo<PendingOperation>(operations))
        }

    /// Verifies cancellation while waiting changes nothing and a stale Watch tick rereads pause under exclusion.
    [<Test>]
    let ``pause cancellation and competing stale Watch run honor root lease`` () =
        task {
            let! configuration, before, _ = renameCopy ()

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            let! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope System.Threading.CancellationToken.None
            use cancellation = new System.Threading.CancellationTokenSource()
            let waiting = LibrarySynchronization.pause configuration cancellation.Token
            cancellation.Cancel()

            try
                let! _ = waiting
                Assert.Fail("Canceled pause unexpectedly committed.")
            with
            | :? OperationCanceledException -> ()

            Assert.That(readRepository configuration.GraceStatusFile configuration.RepositoryId, Is.EqualTo(Some before))
            let stale = LibrarySynchronization.runFromWatch configuration "stale-watch" System.Threading.CancellationToken.None
            setPaused configuration.GraceStatusFile before true
            (held :> IDisposable).Dispose()
            do! stale

            Assert.That(
                (readRepository configuration.GraceStatusFile configuration.RepositoryId)
                    .Value
                    .Paused,
                Is.True
            )
        }

    /// Rejects toggles before onboarding and rejects non-Boolean SQLite values without adding another table.
    [<TestCase(false); TestCase(true)>]
    let ``pause requires completed participation and constrained SQLite value`` incomplete =
        task {
            let root, db = location ()
            do! initialize db
            let current, _ = prepared ()
            let configuration = Grace.Shared.Client.Configuration.GraceConfiguration()
            configuration.RootDirectory <- root
            configuration.RepositoryId <- current.RepositoryId
            configuration.GraceStatusFile <- db

            if incomplete then
                enable
                    db
                    { current with Baseline = Some { BootstrapId = Guid.NewGuid(); BoundaryCursor = "boundary"; MetadataComplete = true; Applied = true } }

            for paused in [ true; false ] do
                try
                    let! _ = LibrarySynchronization.changePause configuration paused System.Threading.CancellationToken.None
                    Assert.Fail("Incomplete participation admitted a toggle.")
                with
                | :? InvalidOperationException -> ()

            if incomplete then
                use connection = openConnection db
                use command = connection.CreateCommand()
                command.CommandText <- "UPDATE library_repository_state SET paused=2;"
                throws<SqliteException> (fun () -> command.ExecuteNonQuery() |> ignore)
        }

    /// Retains one selected ID across interruption without changing files or replacing it with another requested name.
    [<Test>]
    let ``explicit rename intent survives interruption and exact same invocation resumes`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, item = renameCopy ()

            throws<OperationCanceledException> (fun () ->
                LibrarySynchronization.selectRenameWith (fun () -> raise (OperationCanceledException())) configuration "Library/item.bin" "ne\u0301w.bin"
                |> ignore)

            let selected = LibrarySynchronization.selectRenameWith ignore configuration "Library\\item.bin" "n\u00e9w.bin"
            Assert.That(selected.Rename, Is.True)

            Assert.That(
                selected.RequestJson.IsNone
                && selected.SourceObject.IsNone
                && not selected.Uploaded,
                Is.True
            )

            Assert.That(selected.MaterializedBase, Is.EqualTo(Some item))
            Assert.That(selected.Placement.Name, Is.EqualTo("n\u00e9w.bin"))

            throws<InvalidOperationException> (fun () ->
                LibrarySynchronization.selectRenameWith ignore configuration "Library/item.bin" "other.bin"
                |> ignore)

            let resumed = LibrarySynchronization.selectRenameWith ignore configuration "Library/item.bin" "n\u00e9w.bin"
            Assert.That(serialize resumed, Is.EqualTo(serialize selected))
            Assert.That(readRepository configuration.GraceStatusFile before.RepositoryId, Is.EqualTo(Some before))
            Assert.That(File.Exists(Path.Combine(configuration.RootDirectory, "Library/item.bin")), Is.True)
            Assert.That(File.Exists(Path.Combine(configuration.RootDirectory, selected.TargetPath)), Is.False)
        }

    /// Observes an interrupted rename after root exclusion is released, suppressing exact publication while capturing real saved and untracked bytes.
    [<Test>]
    let ``prepared rename publication is not uploaded and positive saved observations remain capturable`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, item = renameCopy ()
            let selected = LibrarySynchronization.selectRenameWith ignore configuration "Library/item.bin" "new.bin"

            let change =
                {
                    OperationId = selected.OperationId
                    ChangeKind = ChangeKind.Rename
                    AcceptedAt = getCurrentInstant ()
                    AcceptedBy = "test"
                    LibraryCatalogVersion = before.Catalog.Version
                    Item =
                        { item with
                            Namespace = Some { item.Namespace.Value with Name = "new.bin"; NamespaceVersion = Guid.NewGuid() }
                            LastChangeCursor = "rename-after"
                        }
                    Conflict = None
                }

            let prepared = acceptedRename before.AppliedCursor change selected
            let source = Path.Combine(configuration.RootDirectory, "Library/item.bin")
            let destination = Path.Combine(configuration.RootDirectory, "Library/new.bin")

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            do!
                task {
                    use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope Threading.CancellationToken.None
                    updateOperation configuration.GraceStatusFile before.RepositoryId selected prepared
                    File.Copy(source, destination)
                }

            do! LibrarySynchronization.classifyWatchObservations configuration [| destination |] Threading.CancellationToken.None
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.False)

            Assert.That(
                (readOperations configuration.GraceStatusFile before.RepositoryId
                 |> Array.find (fun operation -> operation.OperationId = selected.OperationId))
                    .EchoPending,
                Is.True
            )

            File.WriteAllText(destination, "saved positive target")
            File.WriteAllText(Path.Combine(configuration.RootDirectory, "Library/untracked.bin"), "positive untracked")
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.True)

            let saved =
                readOperations configuration.GraceStatusFile before.RepositoryId
                |> Array.filter (fun operation -> not operation.Terminal && not operation.Rename)

            Assert.That(saved.Length, Is.EqualTo(2))

            Assert.That(
                (saved
                 |> Array.find (fun operation -> operation.SourcePath.EndsWith("new.bin")))
                    .MaterializedBase,
                Is.EqualTo(Some item)
            )

            Assert.That(
                (saved
                 |> Array.find (fun operation -> operation.SourcePath.EndsWith("untracked.bin")))
                    .MaterializedBase
                    .IsNone,
                Is.True
            )

            Assert.That(LibrarySynchronization.captureSaved configuration, Is.False)
            Assert.That(readRepository configuration.GraceStatusFile before.RepositoryId, Is.EqualTo(Some before))
        }

    /// Rejects unsupported file states before a durable namespace operation can exist.
    [<TestCase("dirty");
      TestCase("zero");
      TestCase("absent");
      TestCase("directory");
      TestCase("occupied");
      TestCase("case");
      TestCase("cross-parent");
      TestCase("catalog");
      TestCase("reparse")>]
    let ``explicit rename admission preserves unsupported sources and destinations`` obstruction =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, _ = renameCopy ()
            let path = Path.Combine(configuration.RootDirectory, "Library/item.bin")
            let mutable name = "new.bin"

            match obstruction with
            | "dirty" -> File.WriteAllText(path, "new saved bytes")
            | "zero" -> File.WriteAllBytes(path, [||])
            | "absent" -> File.Delete(path)
            | "directory" ->
                File.Delete(path)
                Directory.CreateDirectory(path) |> ignore
            | "occupied" -> File.WriteAllText(Path.Combine(configuration.RootDirectory, "Library/new.bin"), "occupied")
            | "case" -> name <- "ITEM.BIN"
            | "cross-parent" -> name <- "other/new.bin"
            | "catalog" -> setState configuration.GraceStatusFile before "blocked"
            | "reparse" ->
                let link = Path.Combine(configuration.RootDirectory, "Library")
                let destination = Path.Combine(configuration.RootDirectory, "junction-target")
                Directory.Move(link, destination)
                let start = Diagnostics.ProcessStartInfo("pwsh", UseShellExecute = false, CreateNoWindow = true)
                start.Environment[ "GRACE_REPARSE_TEST_LINK" ] <- link
                start.Environment[ "GRACE_REPARSE_TEST_TARGET" ] <- destination

                [|
                    "-NoProfile"
                    "-NonInteractive"
                    "-Command"
                    "New-Item -ItemType Junction -Path $env:GRACE_REPARSE_TEST_LINK -Target $env:GRACE_REPARSE_TEST_TARGET | Out-Null"
                |]
                |> Array.iter start.ArgumentList.Add

                use junctionProcess = Diagnostics.Process.Start(start)
                junctionProcess.WaitForExit()
                Assert.That(junctionProcess.ExitCode, Is.Zero)
            | _ -> invalidOp "Unknown admission case."

            let previous = readOperations configuration.GraceStatusFile before.RepositoryId

            Assert.That(
                Action (fun () ->
                    LibrarySynchronization.selectRenameWith ignore configuration "Library/item.bin" name
                    |> ignore),
                Throws.Exception
            )

            Assert.That(readOperations configuration.GraceStatusFile before.RepositoryId, Is.EqualTo<PendingOperation>(previous))
        }

    /// Separates all four command outcomes while retiring only receipt-backed unprepared namespace work.
    [<Test>]
    let ``rename rejection retirement retains receipt without item cursor or echo effects`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, _ = renameCopy ()
            let db = configuration.GraceStatusFile
            let selected = LibrarySynchronization.selectRenameWith ignore configuration "Library/item.bin" "new.bin"
            let request = LibraryOperation.freezeRequest "frozen namespace request" selected
            updateOperation db before.RepositoryId selected request

            let receipt =
                {
                    OperationId = selected.OperationId
                    RequestHash = "exact hash"
                    Outcome = "rejected"
                    Change = None
                    ReasonCode = Some "NamespaceVersionMismatch"
                    CurrentLibraryCatalog = None
                    Rebaseline = None
                }

            let rejected = LibraryOperation.receive receipt request
            updateOperation db before.RepositoryId request rejected
            let items = readItems db before.RepositoryId
            use connection = openConnection db
            use command = connection.CreateCommand()

            command.CommandText <-
                "CREATE TRIGGER fail_rename_retirement BEFORE UPDATE OF terminal ON library_operations BEGIN SELECT RAISE(ABORT, 'retirement interrupted'); END;"

            command.ExecuteNonQuery() |> ignore
            throws<SqliteException> (fun () -> retireRejectedRename db before.RepositoryId rejected)

            Assert.That(
                readOperations db before.RepositoryId
                |> Array.find (fun operation -> operation.OperationId = selected.OperationId),
                Is.EqualTo(rejected)
            )

            Assert.That(readRepository db before.RepositoryId, Is.EqualTo(Some before))
            command.CommandText <- "DROP TRIGGER fail_rename_retirement;"
            command.ExecuteNonQuery() |> ignore
            retireRejectedRename db before.RepositoryId rejected

            let terminal =
                readOperations db before.RepositoryId
                |> Array.find (fun operation -> operation.OperationId = selected.OperationId)

            Assert.That(terminal.Terminal && not terminal.EchoPending, Is.True)
            Assert.That(terminal.Receipt, Is.EqualTo(Some receipt))
            Assert.That(readItems db before.RepositoryId, Is.EqualTo<LibraryItemDto>(items))
            Assert.That(readRepository db before.RepositoryId, Is.EqualTo(Some before))

            Assert.That(
                (LibrarySynchronization.renameResult terminal None)
                    .Outcome,
                Is.EqualTo(LibrarySynchronization.RenameOutcome.Rejected(RejectionCode.Unknown "NamespaceVersionMismatch"))
            )

            Assert.That(
                (LibrarySynchronization.renameResult selected None)
                    .Outcome,
                Is.EqualTo(LibrarySynchronization.RenameOutcome.Ambiguous None)
            )

            let _, accepted = prepared ()

            Assert.That(
                (LibrarySynchronization.renameResult (acceptedRename before.AppliedCursor accepted.Accepted.Value selected) None)
                    .Outcome,
                Is.EqualTo(LibrarySynchronization.RenameOutcome.AcceptedButObstructed None)
            )

            Assert.That(
                (LibrarySynchronization.renameResult
                    (acceptedRename before.AppliedCursor accepted.Accepted.Value selected
                     |> LibraryOperation.complete)
                    None)
                    .Outcome,
                Is.EqualTo(LibrarySynchronization.RenameOutcome.Completed)
            )

            throws<InvalidOperationException> (fun () -> retireRejectedRename db before.RepositoryId rejected)
            pruneClassified db before.RepositoryId 0

            Assert.That(
                readOperations db before.RepositoryId
                |> Array.exists (fun operation -> operation.OperationId = selected.OperationId),
                Is.False
            )

            let saved = { accepted with OperationId = Guid.NewGuid() }
            insertOperation db before.RepositoryId saved
            throws<InvalidOperationException> (fun () -> retireRejectedRename db before.RepositoryId saved)

            Assert.That(
                readOperations db before.RepositoryId
                |> Array.find (fun operation -> operation.OperationId = saved.OperationId),
                Is.EqualTo(saved)
            )
        }

    /// Rolls back completion if repository or operation records change between the item write and progress commit.
    [<TestCase("repository"); TestCase("operation")>]
    let ``completion exact record changes roll back item operation and cursor`` changed =
        task {
            let _, db = location ()
            do! initialize db
            let state, pending = prepared ()
            enable db state
            insertOperation db state.RepositoryId pending

            throws<InvalidOperationException> (fun () ->
                completeWith
                    (fun connection transaction ->
                        use command = connection.CreateCommand()
                        command.Transaction <- transaction

                        command.CommandText <-
                            if changed = "repository" then
                                "UPDATE library_repository_state SET lifecycle_state='changed';"
                            else
                                "UPDATE library_operations SET operation_json=$changed;"

                        command.Parameters.AddWithValue("$changed", serialize (LibraryOperation.setEcho true pending))
                        |> ignore

                        command.ExecuteNonQuery() |> ignore)
                    db
                    state
                    pending)

            Assert.That(readItems db state.RepositoryId, Is.Empty)
            Assert.That(readRepository db state.RepositoryId, Is.EqualTo(Some state))
            Assert.That(readOperations db state.RepositoryId, Is.EqualTo<PendingOperation>([| pending |]))
        }

    /// Verifies the active Library connection, exact table count, and rollback after a real transaction statement fails.
    [<Test>]
    let ``completion rolls back item terminal and cursor after actual SQLite failure`` () =
        task {
            let _, db = location ()
            do! initialize db
            let state, pending = prepared ()
            enable db state
            insertOperation db state.RepositoryId pending
            use connection = openConnection db
            use command = connection.CreateCommand()

            for pragma, expected in
                [
                    "synchronous", "2"
                    "foreign_keys", "1"
                    "journal_mode", "wal"
                ] do
                command.CommandText <- $"PRAGMA {pragma};"
                Assert.That(string (command.ExecuteScalar()), Is.EqualTo(expected))

            command.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'library_%';"
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(3))

            let fail (connection: SqliteConnection) (transaction: SqliteTransaction) =
                use invalid = connection.CreateCommand()
                invalid.Transaction <- transaction
                invalid.CommandText <- "INSERT INTO library_items(repository_id,item_id,item_json) VALUES('unknown','invalid','{}');"
                invalid.ExecuteNonQuery() |> ignore

            throws<SqliteException> (fun () -> completeWith fail db state pending)
            |> ignore

            Assert.That(
                (readRepository db state.RepositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(state.AppliedCursor)
            )

            Assert.That((readItems db state.RepositoryId).Length, Is.Zero)

            Assert.That(
                (readOperations db state.RepositoryId).[0]
                    .Terminal,
                Is.False
            )

            complete db state pending

            Assert.That(
                (readRepository db state.RepositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo("opaque-after")
            )

            Assert.That(
                (readItems db state.RepositoryId).[0]
                    .ContentRevision
                    .Value,
                Is.EqualTo("opaque-after")
            )

            Assert.That(
                (readOperations db state.RepositoryId).[0]
                    .Terminal,
                Is.True
            )

            throws<InvalidOperationException> (fun () -> complete db state pending)
            |> ignore
        }

    /// Rejects stale predecessor/catalog and replacement of frozen saved bytes or request identity.
    [<Test>]
    let ``pending identity and completion authority remain exact`` () =
        task {
            let _, db = location ()
            do! initialize db
            let state, pending = prepared ()
            enable db state
            insertOperation db state.RepositoryId pending

            let changedObject =
                match pending.Work with
                | OperationWork.SavedFile (intent, progress) ->
                    { pending with Work = OperationWork.SavedFile({ intent with Object = objectReference [| 42uy |] }, progress) }
                | _ -> invalidOp "Expected a saved file fixture."

            throws<InvalidOperationException> (fun () -> updateOperation db state.RepositoryId pending changedObject)
            |> ignore

            let changedRequest =
                match pending.Work with
                | OperationWork.SavedFile (intent, SavedFileProgress.Accepted (_, receipt, progress)) ->
                    { pending with Work = OperationWork.SavedFile(intent, SavedFileProgress.Accepted("replacement-request", receipt, progress)) }
                | _ -> invalidOp "Expected an accepted saved file fixture."

            throws<InvalidOperationException> (fun () -> updateOperation db state.RepositoryId pending changedRequest)
            |> ignore

            throws<InvalidOperationException> (fun () -> complete db { state with AppliedCursor = "unrelated-predecessor" } pending)
            |> ignore

            throws<InvalidOperationException> (fun () -> complete db { state with Catalog = { state.Catalog with Version = Guid.NewGuid() } } pending)
            |> ignore

            Assert.That((readItems db state.RepositoryId).Length, Is.Zero)

            Assert.That(
                (readRepository db state.RepositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(state.AppliedCursor)
            )
        }

    /// Consumes the latest completed echo once even when earlier publications contain the same bytes.
    [<Test>]
    let ``terminal echoes follow completed publication rather than repeated byte ambiguity`` () =
        task {
            let _, db = location ()
            do! initialize db
            let initial, first = prepared ()
            let first = LibraryOperation.setEcho true first
            enable db initial
            insertOperation db initial.RepositoryId first
            complete db initial first

            let fingerprint =
                first.Accepted.Value.Item.Content
                |> Option.map (fun content -> $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}")

            Assert.That(consumeEcho db initial.RepositoryId first.TargetPath (Some "different"), Is.False)
            Assert.That(consumeEcho db initial.RepositoryId first.TargetPath fingerprint, Is.True)
            Assert.That(consumeEcho db initial.RepositoryId first.TargetPath fingerprint, Is.False)
            let mutable current = (readRepository db initial.RepositoryId).Value

            for ordinal in [ 2L; 3L ] do
                let operationId = Guid.NewGuid()

                let nextChange =
                    { first.Accepted.Value with
                        OperationId = operationId
                        Item = { first.Accepted.Value.Item with LastChangeCursor = $"opaque-{ordinal}"; ContentRevision = Some $"opaque-{ordinal}" }
                    }

                let next = { incoming current.AppliedCursor first.SourcePath first.TargetPath None EchoState.Pending nextChange with CreatedAtTicks = ordinal }

                insertOperation db initial.RepositoryId next
                complete db current next
                current <- (readRepository db initial.RepositoryId).Value

            Assert.That(consumeEcho db initial.RepositoryId first.TargetPath fingerprint, Is.True)
            Assert.That(consumeEcho db initial.RepositoryId first.TargetPath fingerprint, Is.False)
            pruneClassified db initial.RepositoryId 0
            let retained = readOperations db initial.RepositoryId
            Assert.That(retained.Length, Is.EqualTo(1))
            Assert.That(retained[0].Accepted.Value.Item.LastChangeCursor, Is.EqualTo(current.AppliedCursor))

            Assert.That(
                retained
                |> Array.forall (fun operation -> not operation.EchoPending),
                Is.True
            )
        }

    /// Exercises more than 128 coalesced Y/X pairs through actual SQLite completion and echo classification.
    [<Test>]
    let ``coalesced publications bound terminal history while preserving pending origins and the applied tip`` () =
        task {
            let root, db = location ()

            Directory.CreateDirectory(Path.Combine(root, "Library"))
            |> ignore

            let target = Path.Combine(root, "Library", "item.bin")
            let configuration = Grace.Shared.Client.Configuration.GraceConfiguration()
            configuration.RootDirectory <- root
            configuration.ObjectDirectory <- Path.Combine(root, ".grace", "objects")
            configuration.GraceStatusFile <- db
            do! initialize db
            let initial, first = prepared ()
            let first = { LibraryOperation.setEcho true first with CreatedAtTicks = -1000000L }
            configuration.RepositoryId <- initial.RepositoryId
            enable db initial
            insertOperation db initial.RepositoryId first
            complete db initial first

            let dependent =
                { first with
                    OperationId = Guid.NewGuid()
                    Work =
                        OperationWork.SavedFile(
                            {
                                SourcePath = first.SourcePath
                                Object = objectReference [| 99uy |]
                                Base = SavedBase.PendingCreate(first.OperationId, first.Placement)
                            },
                            SavedFileProgress.Captured
                        )
                    CreatedAtTicks = 1000000L
                }

            insertOperation db initial.RepositoryId dependent
            let mutable current = (readRepository db initial.RepositoryId).Value
            let otherId = Guid.NewGuid()

            let otherChange =
                { first.Accepted.Value with
                    OperationId = otherId
                    Item =
                        { first.Accepted.Value.Item with
                            ItemId = Guid.NewGuid()
                            LastChangeCursor = Guid.NewGuid().ToString("N")
                            Namespace = Some { first.Accepted.Value.Item.Namespace.Value with Name = "other.bin" }
                        }
                }

            let other =
                { incoming current.AppliedCursor "Library/other.bin" "Library/other.bin" None EchoState.Pending otherChange with CreatedAtTicks = -999999L }

            insertOperation db initial.RepositoryId other
            complete db current other
            File.WriteAllBytes(Path.Combine(root, "Library", "other.bin"), sampleBytes)
            current <- (readRepository db initial.RepositoryId).Value
            let mutable last = first

            for ordinal in 1..260 do
                let bytes =
                    [|
                        if ordinal % 2 = 0 then 88uy else 89uy
                    |]

                let content = LibraryFilesystem.content bytes
                let operationId = Guid.NewGuid()
                let cursor = Guid.NewGuid().ToString("N")

                let item =
                    { first.Accepted.Value.Item with
                        LastChangeCursor = cursor
                        ContentRevision = Some cursor
                        Content =
                            Some
                                { first.Accepted.Value.Item.Content.Value with
                                    Blake3Hash = content.Blake3Hash
                                    Sha256Hash = content.Sha256Hash
                                    Size = content.Size
                                }
                    }

                let next =
                    { incoming
                          current.AppliedCursor
                          first.SourcePath
                          first.TargetPath
                          None
                          EchoState.Pending
                          { first.Accepted.Value with OperationId = operationId; Item = item } with
                        CreatedAtTicks = -(int64 ordinal)
                    }

                insertOperation db initial.RepositoryId next
                File.WriteAllBytes(target, bytes)
                complete db current next
                current <- (readRepository db initial.RepositoryId).Value
                last <- next

                if ordinal % 2 = 0 then
                    let fingerprint = Some $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}"

                    if OperatingSystem.IsWindows() then
                        do! LibrarySynchronization.classifyWatchObservations configuration [| target |] Threading.CancellationToken.None

                        Assert.That(
                            (readOperations db initial.RepositoryId
                             |> Array.find (fun operation -> operation.OperationId = next.OperationId))
                                .EchoPending,
                            Is.False
                        )
                    else
                        Assert.That(consumeEcho db initial.RepositoryId next.TargetPath fingerprint, Is.True)

                    pruneClassified db initial.RepositoryId 128

            let retained = readOperations db initial.RepositoryId

            Assert.That(
                retained
                |> Array.filter (fun operation -> operation.Terminal)
                |> Array.length,
                Is.LessThanOrEqualTo(131)
            )

            Assert.That(
                retained
                |> Array.find (fun operation -> operation.OperationId = dependent.OperationId),
                Is.EqualTo(dependent)
            )

            Assert.That(
                retained
                |> Array.exists (fun operation ->
                    operation.OperationId = first.OperationId
                    && operation.Terminal),
                Is.True
            )

            Assert.That(
                retained
                |> Array.exists (fun operation ->
                    operation.OperationId = last.OperationId
                    && operation.Terminal),
                Is.True
            )

            Assert.That(
                retained
                |> Array.filter (fun operation -> operation.Terminal && operation.EchoPending)
                |> Array.map (fun operation -> operation.OperationId),
                Is.EqualTo<Guid>([| otherId |])
            )

            if OperatingSystem.IsWindows() then
                let laterBytes = [| 89uy |]
                File.WriteAllBytes(target, laterBytes)
                Assert.That(consumeEcho db initial.RepositoryId last.TargetPath (LibraryFilesystem.fingerprint target), Is.False)
                Assert.That(LibrarySynchronization.captureSaved configuration, Is.True)
                Assert.That(LibrarySynchronization.captureSaved configuration, Is.False)

                let captured =
                    readOperations db initial.RepositoryId
                    |> Array.find (fun operation ->
                        not operation.Terminal
                        && operation.OperationId <> dependent.OperationId)

                Assert.That(File.ReadAllBytes(LibraryFilesystem.objectPath configuration captured.SourceObject.Value), Is.EqualTo<byte>(laterBytes))
                Assert.That(captured.MaterializedBase, Is.EqualTo(Some last.Accepted.Value.Item))
                Assert.That(captured.RequestJson.IsNone, Is.True)

                Assert.That(
                    readOperations db initial.RepositoryId
                    |> Array.find (fun operation -> operation.OperationId = dependent.OperationId),
                    Is.EqualTo(dependent)
                )
        }

    /// Retires echoes only with committed item progress and carries matching observable evidence through a no-rewrite completion.
    [<Test>]
    let ``echo retirement rolls back with completion and preserves an observable no rewrite result`` () =
        task {
            let _, db = location ()
            do! initialize db
            let initial, first = prepared ()
            let first = LibraryOperation.setEcho true first
            enable db initial
            insertOperation db initial.RepositoryId first
            complete db initial first
            let current = (readRepository db initial.RepositoryId).Value
            let id = Guid.NewGuid()
            let change = { first.Accepted.Value with OperationId = id; Item = { first.Accepted.Value.Item with LastChangeCursor = "not-sortable-next" } }

            let next = { incoming current.AppliedCursor first.SourcePath first.TargetPath None EchoState.Clear change with CreatedAtTicks = -1L }

            insertOperation db initial.RepositoryId next
            use connection = openConnection db
            use command = connection.CreateCommand()

            command.CommandText <-
                "CREATE TRIGGER fail_echo_cursor BEFORE UPDATE OF applied_cursor ON library_repository_state BEGIN SELECT RAISE(ABORT, 'echo rollback'); END;"

            command.ExecuteNonQuery() |> ignore
            throws<SqliteException> (fun () -> complete db current next)

            Assert.That(
                (readOperations db initial.RepositoryId
                 |> Array.find (fun operation -> operation.OperationId = first.OperationId))
                    .EchoPending,
                Is.True
            )

            Assert.That(
                (readOperations db initial.RepositoryId
                 |> Array.find (fun operation -> operation.OperationId = id))
                    .Terminal,
                Is.False
            )

            Assert.That(readRepository db initial.RepositoryId, Is.EqualTo(Some current))
            command.CommandText <- "DROP TRIGGER fail_echo_cursor;"
            command.ExecuteNonQuery() |> ignore
            complete db current next

            let terminal =
                readOperations db initial.RepositoryId
                |> Array.find (fun operation -> operation.OperationId = id)

            Assert.That(terminal.EchoPending, Is.True)

            Assert.That(
                (readOperations db initial.RepositoryId
                 |> Array.find (fun operation -> operation.OperationId = first.OperationId))
                    .EchoPending,
                Is.False
            )

            pruneClassified db initial.RepositoryId 0
            Assert.That((readOperations db initial.RepositoryId).Length, Is.EqualTo(1))
            let descriptor = change.Item.Content.Value

            Assert.That(
                consumeEcho db initial.RepositoryId next.TargetPath (Some $"{descriptor.Blake3Hash}:{descriptor.Sha256Hash}:{descriptor.Size}"),
                Is.True
            )

            Assert.That(
                consumeEcho db initial.RepositoryId next.TargetPath (Some $"{descriptor.Blake3Hash}:{descriptor.Sha256Hash}:{descriptor.Size}"),
                Is.False
            )
        }

    /// Retires obsolete directory and descendant placements while preserving an unrelated observable publication.
    [<Test>]
    let ``completed directory placement retires old subtree echoes without losing another path`` () =
        task {
            let _, db = location ()
            do! initialize db
            let initial, template = prepared ()
            enable db initial

            let folder =
                { template.Accepted.Value.Item with
                    ItemKind = ItemKind.Directory
                    Namespace = Some { template.Accepted.Value.Item.Namespace.Value with Name = "old" }
                    Content = None
                    ContentRevision = None
                }

            let directory =
                incoming
                    initial.AppliedCursor
                    "Library/old"
                    "Library/old"
                    None
                    EchoState.Pending
                    { template.Accepted.Value with ChangeKind = ChangeKind.CreateDirectory; Item = folder }

            insertOperation db initial.RepositoryId directory
            complete db initial directory
            let mutable current = (readRepository db initial.RepositoryId).Value
            let childId = Guid.NewGuid()

            let childItem =
                { template.Accepted.Value.Item with
                    ItemId = Guid.NewGuid()
                    Namespace =
                        Some { template.Accepted.Value.Item.Namespace.Value with Parent = { Kind = "item"; LibraryPath = None; ItemId = Some folder.ItemId } }
                    LastChangeCursor = "child-cursor"
                }

            let child =
                incoming
                    current.AppliedCursor
                    "Library/old/item.bin"
                    "Library/old/item.bin"
                    None
                    EchoState.Pending
                    { template.Accepted.Value with OperationId = childId; Item = childItem }

            insertOperation db initial.RepositoryId child
            complete db current child
            current <- (readRepository db initial.RepositoryId).Value
            let unrelatedId = Guid.NewGuid()

            let unrelatedItem =
                { template.Accepted.Value.Item with
                    ItemId = Guid.NewGuid()
                    Namespace = Some { template.Accepted.Value.Item.Namespace.Value with Name = "unrelated.bin" }
                    LastChangeCursor = "unrelated-cursor"
                }

            let unrelated =
                incoming
                    current.AppliedCursor
                    "Library/unrelated.bin"
                    "Library/unrelated.bin"
                    None
                    EchoState.Pending
                    { template.Accepted.Value with OperationId = unrelatedId; Item = unrelatedItem }

            insertOperation db initial.RepositoryId unrelated
            complete db current unrelated
            current <- (readRepository db initial.RepositoryId).Value
            let moveId = Guid.NewGuid()

            let moved =
                { folder with
                    Namespace = Some { folder.Namespace.Value with Name = "new"; NamespaceVersion = Guid.NewGuid() }
                    LastChangeCursor = "moved-cursor"
                }

            let move =
                incoming
                    current.AppliedCursor
                    "Library/old"
                    "Library/new"
                    (Some folder)
                    EchoState.Pending
                    { directory.Accepted.Value with OperationId = moveId; ChangeKind = ChangeKind.Rename; Item = moved }

            insertOperation db initial.RepositoryId move
            complete db current move
            pruneClassified db initial.RepositoryId 0
            let remaining = readOperations db initial.RepositoryId
            Assert.That(remaining.Length, Is.EqualTo(2))

            Assert.That(
                remaining
                |> Array.exists (fun operation ->
                    operation.OperationId = unrelatedId
                    && operation.EchoPending),
                Is.True
            )

            Assert.That(
                remaining
                |> Array.exists (fun operation ->
                    operation.OperationId = moveId
                    && operation.EchoPending),
                Is.True
            )

            Assert.That(
                (readItems db initial.RepositoryId
                 |> Array.find (fun item -> item.ItemId = childItem.ItemId))
                    .Namespace,
                Is.EqualTo(childItem.Namespace)
            )

            Assert.That(consumeEcho db initial.RepositoryId "Library/new" (Some "directory"), Is.True)
            Assert.That(consumeEcho db initial.RepositoryId "Library/old" None, Is.False)
        }

    /// Proves Library and Branch-style work share the same root exclusion without a Library WDU completion.
    [<Test>]
    let ``Library shares the existing working root lease`` () =
        task {
            let root, _ = location ()

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (Guid.NewGuid()) root
                |> Result.defaultWith invalidOp

            use! library = WorkingDirectoryUpdateCoordination.Lease.acquire scope System.Threading.CancellationToken.None
            use cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(80.0))
            let branch = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellation.Token

            try
                use! unexpected = branch
                Assert.Fail("Branch entered the Library publication exclusion.")
            with
            | :? OperationCanceledException -> ()

            (library :> IDisposable).Dispose()
            use! restarted = WorkingDirectoryUpdateCoordination.Lease.acquire scope System.Threading.CancellationToken.None
            Assert.That(restarted, Is.Not.Null)
        }

    /// Retains empty-page continuation across connection restart and abandons the old page only with atomic item completion.
    [<Test>]
    let ``page continuation survives an empty gap and partial page completion`` () =
        task {
            let _, db = location ()
            do! initialize db
            let state, pending = prepared ()
            enable db state
            insertOperation db state.RepositoryId pending
            recordPage db state (Some "opaque-gap-page")
            let restarted = (readRepository db state.RepositoryId).Value
            Assert.That(restarted.NextPageToken, Is.EqualTo(Some "opaque-gap-page"))
            Assert.That(restarted.AppliedCursor, Is.EqualTo(state.AppliedCursor))
            Assert.That(restarted.State, Is.EqualTo("catchingUp"))
            throws<InvalidOperationException> (fun () -> recordPage db { restarted with AppliedCursor = "wrong" } None)
            complete db restarted pending
            let completed = (readRepository db state.RepositoryId).Value
            Assert.That(completed.NextPageToken, Is.EqualTo(None))
            Assert.That(completed.AppliedCursor, Is.EqualTo(pending.Accepted.Value.Item.LastChangeCursor))

            Assert.That(
                (readOperations db state.RepositoryId).[0]
                    .Terminal,
                Is.True
            )

            recordPage db completed (Some "opaque-next-page")

            Assert.That(
                (readRepository db state.RepositoryId)
                    .Value
                    .NextPageToken,
                Is.EqualTo(Some "opaque-next-page")
            )
        }

    /// Keeps the shared payload-free kind explicit while leaving existing Library wire strings unchanged.
    [<TestCase("file"); TestCase("directory")>]
    let ``shared item kind adapters preserve Library wire values`` wire =
        let kind = LibraryOperation.kindFromWire wire
        Assert.That(LibraryOperation.kindToWire kind, Is.EqualTo(wire))
        Assert.That((kind = Grace.Types.Common.ItemKind.File), Is.EqualTo((wire = "file")))
        throws<InvalidOperationException> (fun () -> LibraryOperation.kindFromWire "unknown" |> ignore)

    /// Rejects changing operation families or advancing without genuine acceptance, and keeps receipt replay idempotent.
    [<Test>]
    let ``typed operation transitions preserve intent checkpoint and retained receipt`` () =
        task {
            let _, db = location ()
            do! initialize db
            let state, accepted = prepared ()
            enable db state
            insertOperation db state.RepositoryId accepted
            let checkpoint = LibraryOperation.checkpoint accepted |> Option.get

            let changed =
                { accepted with Work = OperationWork.Incoming(accepted.Accepted.Value, accepted.SourcePath, None, ApplicationProgress.Prepared checkpoint) }

            throws<InvalidOperationException> (fun () -> updateOperation db state.RepositoryId accepted changed)

            let drifted =
                match accepted.Work with
                | OperationWork.SavedFile (intent, SavedFileProgress.Accepted (request, receipt, _)) ->
                    { accepted with
                        Work =
                            OperationWork.SavedFile(
                                intent,
                                SavedFileProgress.Accepted(request, receipt, ApplicationProgress.Prepared { checkpoint with ExpectedCursor = "changed" })
                            )
                    }
                | _ -> invalidOp "Expected an accepted saved file."

            throws<InvalidOperationException> (fun () -> updateOperation db state.RepositoryId accepted drifted)
            Assert.That(LibraryOperation.receive accepted.Receipt.Value accepted, Is.EqualTo(accepted))

            throws<InvalidOperationException> (fun () ->
                LibraryOperation.receive { accepted.Receipt.Value with Outcome = "rejected" } accepted
                |> ignore)

            throws<InvalidOperationException> (fun () ->
                LibraryOperation.freezeRequest "replacement" accepted
                |> ignore)

            Assert.That(readOperations db state.RepositoryId, Is.EqualTo<PendingOperation>([| accepted |]))
        }

    /// Serializes only envelope and typed work, with one accepted change and derived SQLite indexes on insert.
    [<Test>]
    let ``typed operations serialize one source and derive terminal indexes on insertion`` () =
        task {
            let _, db = location ()
            do! initialize db
            let state, accepted = prepared ()
            enable db state

            let terminal =
                accepted
                |> LibraryOperation.setEcho true
                |> LibraryOperation.complete

            let intent =
                match accepted.Work with
                | OperationWork.SavedFile (intent, _) -> intent
                | _ -> invalidOp "Expected a saved file."

            let captured = { accepted with Work = OperationWork.SavedFile(intent, SavedFileProgress.Captured) }

            let uploaded =
                captured
                |> LibraryOperation.freezeRequest "frozen"
                |> LibraryOperation.uploaded

            let rejection =
                {
                    OperationId = uploaded.OperationId
                    RequestHash = "hash"
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some RejectionReason.PreparedContentExpired
                    CurrentLibraryCatalog = None
                    Rebaseline = None
                }

            let rejected = LibraryOperation.receive rejection uploaded

            for operation in
                [
                    captured
                    uploaded
                    rejected
                    accepted
                    terminal
                ] do
                let json = serialize operation
                use document = System.Text.Json.JsonDocument.Parse json

                Assert.That(
                    document.RootElement.EnumerateObject()
                    |> Seq.map (fun property -> property.Name)
                    |> Seq.toArray,
                    Is.EquivalentTo(
                        [|
                            "OperationId"
                            "CatalogVersion"
                            "CreatedAtTicks"
                            "Work"
                        |]
                    )
                )

                Assert.That(deserialize<PendingOperation> json, Is.EqualTo(operation))
                Assert.That(json, Does.Not.Contain("SourceBytes"))

                if operation.Accepted.IsSome then
                    Assert.That(
                        System
                            .Text
                            .RegularExpressions
                            .Regex
                            .Matches(
                                json,
                                "\"AcceptedAt\""
                            )
                            .Count,
                        Is.EqualTo(1)
                    )

            Assert.That(rejected.Terminal, Is.False)
            Assert.That(rejected.SourceObject, Is.EqualTo(captured.SourceObject))
            insertOperation db state.RepositoryId terminal
            Assert.That(readOperations db state.RepositoryId, Is.EqualTo<PendingOperation>([| terminal |]))
            use connection = openConnection db
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT direction || ':' || terminal || ':' || echo_pending FROM library_operations;"
            Assert.That(command.ExecuteScalar(), Is.EqualTo("local:1:1"))
            command.CommandText <- "UPDATE library_operations SET echo_pending=0;"
            command.ExecuteNonQuery() |> ignore
            throws<InvalidOperationException> (fun () -> readOperations db state.RepositoryId |> ignore)
        }

    /// Restarts capture from SQLite alone after the insert committed, then keeps a later save distinct.
    [<Test>]
    let ``saved object capture restart finds committed operation without caller identity`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, _ = renameCopy ()
            configuration.ObjectDirectory <- Path.Combine(configuration.RootDirectory, "configured-object-store")
            let source = Path.Combine(configuration.RootDirectory, "Library/item.bin")
            File.WriteAllBytes(source, Array.init 196609 (fun index -> byte (index % 251)))

            throws<OperationCanceledException> (fun () ->
                LibrarySynchronization.captureSavedWith (fun _ -> raise (OperationCanceledException())) configuration
                |> ignore)

            let pending =
                readOperations configuration.GraceStatusFile before.RepositoryId
                |> Array.filter (fun operation -> not operation.Terminal)

            Assert.That(pending.Length, Is.EqualTo(1))
            let saved = pending[0].SourceObject.Value
            let locator = LibraryFilesystem.objectPath configuration saved
            let lastWrite = File.GetLastWriteTimeUtc locator
            Assert.That(locator.StartsWith(configuration.ObjectDirectory, StringComparison.OrdinalIgnoreCase), Is.True)
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.False)

            Assert.That(
                readOperations configuration.GraceStatusFile before.RepositoryId
                |> Array.filter (fun operation -> not operation.Terminal),
                Is.EqualTo<PendingOperation>(pending)
            )

            Assert.That(File.GetLastWriteTimeUtc locator, Is.EqualTo(lastWrite))
            File.WriteAllText(source, "a distinct later save")
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.True)

            let later =
                readOperations configuration.GraceStatusFile before.RepositoryId
                |> Array.filter (fun operation -> not operation.Terminal)

            Assert.That(later.Length, Is.EqualTo(2))
            Assert.That(later[0], Is.EqualTo(pending[0]))
            Assert.That(later[1].SourceObject, Is.Not.EqualTo(later[0].SourceObject))
            Assert.That((FileInfo(locator)).Length, Is.EqualTo(196609L))
            Assert.That((serialize later[0]).Length, Is.LessThan(10000))
        }

    /// Reuses a published object after interruption and keeps its locator independent of a renamed working file.
    [<Test>]
    let ``object publication interruption reuses complete content without rewriting shared object`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, _ = renameCopy ()
            let relative = "Library/item.bin"
            let source = Path.Combine(configuration.RootDirectory, relative)
            let identity = LibraryFilesystem.stableIdentity source

            throws<OperationCanceledException> (fun () ->
                LibraryFilesystem.captureObjectWith (fun _ -> raise (OperationCanceledException())) configuration relative identity
                |> ignore)

            let reference = LibraryFilesystem.captureObject configuration relative identity
            let locator = LibraryFilesystem.objectPath configuration reference
            let timestamp = File.GetLastWriteTimeUtc locator
            Assert.That(LibraryFilesystem.captureObject configuration relative identity, Is.EqualTo(reference))
            Assert.That(File.GetLastWriteTimeUtc locator, Is.EqualTo(timestamp))
            File.Move(source, Path.Combine(configuration.RootDirectory, "Library/renamed.bin"))
            use verified = LibraryFilesystem.openObject configuration reference
            Assert.That(verified.Length, Is.EqualTo(identity.Size))
            throws<IOException> (fun () -> File.WriteAllText(locator, "must not replace leased content"))
            pruneClassified configuration.GraceStatusFile before.RepositoryId 0
            Assert.That(File.Exists(locator), Is.True)
            Assert.That(Directory.GetFiles(configuration.ObjectDirectory, "library-capture-*.tmp"), Is.Empty)
        }

    /// Never repairs a committed content reference from newer working bytes, including a partial existing object.
    [<TestCase("missing"); TestCase("corrupt")>]
    let ``missing or corrupt saved object cannot recapture changed working bytes`` failure =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! configuration, before, _ = renameCopy ()
            let source = Path.Combine(configuration.RootDirectory, "Library/item.bin")
            File.WriteAllText(source, "frozen saved content")
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.True)

            let pending =
                readOperations configuration.GraceStatusFile before.RepositoryId
                |> Array.find (fun operation -> not operation.Terminal)

            let locator = LibraryFilesystem.objectPath configuration pending.SourceObject.Value

            if failure = "missing" then
                File.Delete(locator)
            else
                File.WriteAllText(locator, "partial")

            File.WriteAllText(source, "new working content")
            Assert.That(Action(fun () -> use stream = LibraryFilesystem.openObject configuration pending.SourceObject.Value in ()), Throws.Exception)

            Assert.That(
                readOperations configuration.GraceStatusFile before.RepositoryId
                |> Array.find (fun operation -> not operation.Terminal),
                Is.EqualTo(pending)
            )

            Assert.That(File.ReadAllText(source), Is.EqualTo("new working content"))

            if failure = "corrupt" then
                File.WriteAllText(source, "frozen saved content")

                throws<InvalidOperationException> (fun () ->
                    LibraryFilesystem.captureObject configuration "Library/item.bin" pending.SourceObject.Value.Content
                    |> ignore)

                Assert.That(File.ReadAllText(locator), Is.EqualTo("partial"))
        }

    /// Detects a stable-read interleaving and refuses to publish over bytes changed after preparation.
    [<Test>]
    let ``Windows source and publication guards preserve changed bytes`` () =
        if not (OperatingSystem.IsWindows()) then
            Assert.Ignore("Windows filesystem contract.")

        let root, _ = location ()
        let target = Path.Combine(root, "item.bin")
        let staged = Path.Combine(root, "staged.tmp")
        File.WriteAllBytes(target, [| 1uy |])

        throws<InvalidOperationException> (fun () ->
            LibraryFilesystem.stableReadWith (fun () -> File.WriteAllBytes(target, [| 2uy |])) target
            |> ignore)
        |> ignore

        let expected = LibraryFilesystem.fingerprint target

        throws<InvalidOperationException> (fun () ->
            LibraryFilesystem.publishAtomic (fun () -> File.WriteAllBytes(target, [| 3uy |])) staged target expected [| 4uy |])
        |> ignore

        Assert.That(File.ReadAllBytes(target)[0], Is.EqualTo(3uy))
        Assert.That(File.Exists(staged), Is.False)
        LibraryFilesystem.publishAtomic ignore staged target (LibraryFilesystem.fingerprint target) [| 0uy; 255uy |]

        Assert.That(
            File
                .ReadAllBytes(target)
                .AsSpan()
                .SequenceEqual([| 0uy; 255uy |]),
            Is.True
        )
