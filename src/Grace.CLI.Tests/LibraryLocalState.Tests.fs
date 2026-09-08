namespace Grace.CLI.Tests

open Grace.CLI
open Grace.CLI.Command
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
                Baseline = None
            }

        let bytes = [| 0uy; 127uy; 255uy |]
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
                Direction = "local"
                SourcePath = "Library/item.bin"
                SourceBytes = Some bytes
                MaterializedBase = None
                OriginatingCreateId = None
                Parent = parent
                Name = "item.bin"
                ItemKind = ItemKind.File
                RequestJson = Some "exact-submitted-request"
                Uploaded = true
                Accepted = Some change
                BaselineItem = None
                Prepared = true
                ExpectedCatalogVersion = state.Catalog.Version
                ExpectedCursor = state.AppliedCursor
                ExpectedAncestry = [||]
                ExpectedTarget = None
                TargetPath = "Library/item.bin"
                Terminal = false
                EchoPending = false
                CreatedAtTicks = 1L
            }

        state, pending

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

            throws<InvalidOperationException> (fun () -> updateOperation db state.RepositoryId pending { pending with SourceBytes = Some [| 42uy |] })
            |> ignore

            throws<InvalidOperationException> (fun () -> updateOperation db state.RepositoryId pending { pending with RequestJson = Some "replacement-request" })
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
            let first = { first with EchoPending = true }
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

                let next =
                    { first with OperationId = operationId; Accepted = Some nextChange; ExpectedCursor = current.AppliedCursor; CreatedAtTicks = ordinal }

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
            configuration.GraceStatusFile <- db
            do! initialize db
            let initial, first = prepared ()
            let first = { first with EchoPending = true; CreatedAtTicks = -1000000L }
            configuration.RepositoryId <- initial.RepositoryId
            enable db initial
            insertOperation db initial.RepositoryId first
            complete db initial first

            let dependent =
                { first with
                    OperationId = Guid.NewGuid()
                    SourceBytes = Some [| 99uy |]
                    OriginatingCreateId = Some first.OperationId
                    Accepted = None
                    Prepared = false
                    RequestJson = None
                    EchoPending = false
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
                { first with
                    OperationId = otherId
                    Accepted = Some otherChange
                    SourcePath = "Library/other.bin"
                    TargetPath = "Library/other.bin"
                    ExpectedCursor = current.AppliedCursor
                    CreatedAtTicks = -999999L
                }

            insertOperation db initial.RepositoryId other
            complete db current other
            File.WriteAllBytes(Path.Combine(root, "Library", "other.bin"), other.SourceBytes.Value)
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
                    { first with
                        OperationId = operationId
                        SourceBytes = Some bytes
                        Accepted = Some { first.Accepted.Value with OperationId = operationId; Item = item }
                        ExpectedCursor = current.AppliedCursor
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

                Assert.That(captured.SourceBytes, Is.EqualTo(Some laterBytes))
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
            let first = { first with EchoPending = true }
            enable db initial
            insertOperation db initial.RepositoryId first
            complete db initial first
            let current = (readRepository db initial.RepositoryId).Value
            let id = Guid.NewGuid()
            let change = { first.Accepted.Value with OperationId = id; Item = { first.Accepted.Value.Item with LastChangeCursor = "not-sortable-next" } }

            let next =
                { first with OperationId = id; Accepted = Some change; ExpectedCursor = current.AppliedCursor; EchoPending = false; CreatedAtTicks = -1L }

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
                { template with
                    ItemKind = ItemKind.Directory
                    SourceBytes = None
                    SourcePath = "Library/old"
                    TargetPath = "Library/old"
                    Accepted = Some { template.Accepted.Value with ChangeKind = ChangeKind.CreateDirectory; Item = folder }
                    EchoPending = true
                }

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
                { template with
                    OperationId = childId
                    SourcePath = "Library/old/item.bin"
                    TargetPath = "Library/old/item.bin"
                    Accepted = Some { template.Accepted.Value with OperationId = childId; Item = childItem }
                    ExpectedCursor = current.AppliedCursor
                    EchoPending = true
                }

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
                { template with
                    OperationId = unrelatedId
                    SourcePath = "Library/unrelated.bin"
                    TargetPath = "Library/unrelated.bin"
                    Accepted = Some { template.Accepted.Value with OperationId = unrelatedId; Item = unrelatedItem }
                    ExpectedCursor = current.AppliedCursor
                    EchoPending = true
                }

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
                { directory with
                    OperationId = moveId
                    MaterializedBase = Some folder
                    TargetPath = "Library/new"
                    Accepted = Some { directory.Accepted.Value with OperationId = moveId; ChangeKind = ChangeKind.Rename; Item = moved }
                    ExpectedCursor = current.AppliedCursor
                }

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
