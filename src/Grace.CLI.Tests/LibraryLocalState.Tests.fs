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

    /// Consumes exact echoes once, retains ambiguous repeated bytes, and prunes only safely classified history.
    [<Test>]
    let ``terminal echoes are single consumption and repeated bytes remain ambiguous`` () =
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

            Assert.That(consumeEcho db initial.RepositoryId first.TargetPath fingerprint, Is.False)
            pruneClassified db initial.RepositoryId 0
            let retained = readOperations db initial.RepositoryId
            Assert.That(retained.Length, Is.EqualTo(2))

            Assert.That(
                retained
                |> Array.forall (fun operation -> operation.EchoPending),
                Is.True
            )
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
