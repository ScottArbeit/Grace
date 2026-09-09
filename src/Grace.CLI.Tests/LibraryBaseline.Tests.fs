namespace Grace.CLI.Tests

open Grace.CLI.LibraryLocalState
open Grace.CLI.Command
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Library
open Microsoft.Data.Sqlite
open NUnit.Framework
open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Exercises production baseline orchestration against real SQLite and Windows filesystem effects.
[<NonParallelizable>]
module LibraryBaselineTests =

    /// Retains only remote fixture inputs and observations; every invocation rereads production client state.
    type private Fixture =
        {
            Configuration: GraceConfiguration
            Pages: LibraryBootstrapPageDto array
            Bytes: byte array
            Remote: LibraryBaseline.Remote
            Catalog: LibraryCatalogDto ref
            Expired: bool ref
            Starts: int ref
            Reads: int ref
        }

    /// Builds unordered immutable pages including a tombstone whose historical parent does not exist.
    let private fixture () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Requires the selected Windows filesystem behavior.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-baseline-{Guid.NewGuid():N}")
            let grace = Directory.CreateDirectory(Path.Combine(root, ".grace"))
            let configuration = GraceConfiguration()
            configuration.RootDirectory <- root
            configuration.ObjectDirectory <- Path.Combine(grace.FullName, "objects")
            configuration.GraceDirectory <- grace.FullName
            configuration.GraceStatusFile <- Path.Combine(grace.FullName, "grace-local.db")
            configuration.RepositoryId <- Guid.NewGuid()
            do! initialize configuration.GraceStatusFile

            let catalog =
                {
                    RepositoryId = configuration.RepositoryId
                    Version = Guid.NewGuid()
                    Libraries = [| "Library" |]
                    CreatedAt = getCurrentInstant ()
                    CreatedBy = "test"
                    PreviousVersion = None
                }

            let rootParent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }

            /// Makes a live directory selected by the immutable fixture baseline.
            let directory parent name =
                {
                    ItemId = Guid.NewGuid()
                    ItemKind = ItemKind.Directory
                    LastChangeCursor = "older-directory-cursor"
                    Namespace = Some { Parent = parent; Name = name; NamespaceVersion = Guid.NewGuid() }
                    Content = None
                    ContentRevision = None
                    Tombstone = None
                }

            let first = directory rootParent "one"
            let second = directory { Kind = "item"; LibraryPath = None; ItemId = Some first.ItemId } "two"
            let bytes = [| 0uy; 127uy; 255uy; 42uy |]
            let content = LibraryFilesystem.content bytes

            let file =
                {
                    ItemId = Guid.NewGuid()
                    ItemKind = ItemKind.File
                    LastChangeCursor = "selected-file-cursor"
                    Namespace =
                        Some
                            {
                                Parent = { Kind = "item"; LibraryPath = None; ItemId = Some second.ItemId }
                                Name = "selected.bin"
                                NamespaceVersion = Guid.NewGuid()
                            }
                    Content =
                        Some
                            {
                                ContentVersionId = Guid.NewGuid()
                                Blake3Hash = content.Blake3Hash
                                Sha256Hash = content.Sha256Hash
                                Size = content.Size
                                CreatedAt = getCurrentInstant ()
                            }
                    ContentRevision = Some "selected-file-revision"
                    Tombstone = None
                }

            let dead =
                { file with
                    ItemId = Guid.NewGuid()
                    Namespace = None
                    Content = None
                    ContentRevision = None
                    Tombstone =
                        Some
                            {
                                DeletedAt = getCurrentInstant ()
                                DeletedBy = "test"
                                DeleteCursor = "old-delete"
                                LastNamespace =
                                    { file.Namespace.Value with
                                        Parent = { Kind = "item"; LibraryPath = None; ItemId = Some(Guid.NewGuid()) }
                                        Name = "gone.bin"
                                    }
                                LastContentVersionId =
                                    file.Content
                                    |> Option.map (fun value -> value.ContentVersionId)
                            }
                }

            let selected =
                {
                    BootstrapId = Guid.NewGuid()
                    BoundaryCursor = "selected-boundary"
                    CursorEpoch = "selected-epoch"
                    LibraryCatalog = catalog
                    Items = [| file; dead |]
                    NextPageToken = Some "second"
                }

            let pages =
                [|
                    selected
                    { selected with Items = [| second |]; NextPageToken = Some "third" }
                    { selected with Items = [| first |]; NextPageToken = None }
                |]

            let currentCatalog = ref catalog
            let expired = ref false
            let starts = ref 0
            let reads = ref 0

            let remote: LibraryBaseline.Remote =
                {
                    Catalog = fun () -> Task.FromResult(currentCatalog.Value)
                    Start =
                        fun () ->
                            starts.Value <- starts.Value + 1
                            Task.FromResult pages[0]
                    Continue =
                        fun id token ->
                            Assert.That(id, Is.EqualTo(selected.BootstrapId))

                            if expired.Value then
                                expired.Value <- false
                                Task.FromResult None
                            else
                                Task.FromResult(Some pages[if token = "second" then 1 else 2])
                    Read =
                        fun item ->
                            Assert.That(item.ContentRevision, Is.EqualTo(file.ContentRevision))
                            reads.Value <- reads.Value + 1
                            Task.FromResult bytes
                }

            return
                {
                    Configuration = configuration
                    Pages = pages
                    Bytes = bytes
                    Remote = remote
                    Catalog = currentCatalog
                    Expired = expired
                    Starts = starts
                    Reads = reads
                }
        }

    /// Reads the persisted selection after the previous invocation has unwound.
    let private state fixture =
        readRepository fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId
        |> Option.get

    /// Reads persisted work independently of the previous installer invocation.
    let private operations fixture = readOperations fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId

    /// Points at the selected nonempty file below two parent directories.
    let private target fixture = Path.Combine(fixture.Configuration.RootDirectory, "Library", "one", "two", "selected.bin")

    /// Runs production restart with optional interruption immediately around its effect boundaries.
    let private resume observe fixture = LibraryBaseline.resumeWith observe fixture.Remote fixture.Configuration CancellationToken.None

    /// Requires a real stopped invocation instead of accepting an accidentally successful failure scenario.
    let private fails (operation: Task) =
        task {
            let mutable failed = false

            try
                do! operation
            with
            | :? InvalidOperationException -> failed <- true

            Assert.That(failed, Is.True, "The affected operation must stop.")
        }

    /// Verifies no acquired metadata masquerades as materialized state or an applied cursor.
    [<Test>]
    let ``received unordered pages remain unapplied until exact selected content and all parents are installed`` () =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            Assert.That((state fixture).AppliedCursor, Is.Empty)
            Assert.That(readItems fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId, Is.Empty)

            Assert.That(
                operations fixture
                |> Array.forall (fun op -> op.Accepted.IsNone && op.BaselineItem.IsSome),
                Is.True
            )

            let! status = LibrarySynchronization.status fixture.Configuration
            Assert.That(status.State, Is.EqualTo("acquiringBaseline"))
            Assert.That(status.AppliedCursor, Is.EqualTo(None))
            do! resume ignore fixture
            Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box fixture.Bytes))
            Assert.That((state fixture).AppliedCursor, Is.EqualTo("selected-boundary"))

            Assert.That(
                readItems fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId
                |> Array.length,
                Is.EqualTo(3)
            )

            Assert.That(
                Directory
                    .GetFiles(
                        Path.Combine(fixture.Configuration.RootDirectory, "Library"),
                        "*",
                        SearchOption.AllDirectories
                    )
                    .Length,
                Is.EqualTo(1)
            )

            Assert.That(fixture.Reads.Value, Is.EqualTo(1))

            let tombstone =
                operations fixture
                |> Array.find (fun operation -> operation.BaselineItem.Value.Tombstone.IsSome)

            Assert.That(tombstone.Terminal, Is.True)
            Assert.That(tombstone.Prepared, Is.False)
            Assert.That(Grace.CLI.LibraryOperation.checkpoint tombstone, Is.EqualTo(None))
        }

    /// Reopens real SQLite after each interrupted stage and checks completion never rewrites published bytes.
    [<TestCase("page", 1)>]
    [<TestCase("page", 2)>]
    [<TestCase("prepare", 1)>]
    [<TestCase("prepare", 3)>]
    [<TestCase("beforePublish", 1)>]
    [<TestCase("publish", 1)>]
    [<TestCase("publish", 2)>]
    [<TestCase("publish", 3)>]
    [<TestCase("item", 1)>]
    [<TestCase("item", 4)>]
    [<TestCase("beforeBoundary", 1)>]
    [<TestCase("boundary", 1)>]
    let ``interrupted installation reuses prepared publication and never advances an individual item cursor`` point occurrence =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            let mutable seen = 0

            /// Interrupts one selected production effect boundary.
            let observe value =
                if value = point then
                    seen <- seen + 1
                    if seen = occurrence then invalidOp "Interrupted"

            do! fails (resume observe fixture)
            Assert.That(seen, Is.EqualTo(occurrence))
            if point <> "boundary" then Assert.That((state fixture).AppliedCursor, Is.Empty)

            let stamp =
                if File.Exists(target fixture) then
                    Some(File.GetLastWriteTimeUtc(target fixture))
                else
                    None

            Assert.That(LibrarySynchronization.captureSaved fixture.Configuration, Is.False)
            do! resume ignore fixture

            stamp
            |> Option.iter (fun stamp -> Assert.That(File.GetLastWriteTimeUtc(target fixture), Is.EqualTo(stamp)))

            let stamp = File.GetLastWriteTimeUtc(target fixture)
            do! resume ignore fixture
            Assert.That(File.GetLastWriteTimeUtc(target fixture), Is.EqualTo(stamp))
            Assert.That(fixture.Reads.Value, Is.EqualTo(1))

            Assert.That(
                operations fixture
                |> Array.forall (fun op -> op.Terminal && op.Accepted.IsNone),
                Is.True
            )
        }

    /// Distinguishes expiry while pages are incomplete from expiry after the exact selected metadata is durable.
    [<TestCase(false)>]
    [<TestCase(true)>]
    let ``only incomplete token expiry restarts acquisition before effects`` completeMetadata =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration

            if completeMetadata then
                let mutable pages = 0

                do!
                    fails (
                        resume
                            (fun point ->
                                if point = "page" then
                                    pages <- pages + 1
                                    if pages = 2 then invalidOp "Interrupted")
                            fixture
                    )

                Assert.That((state fixture).Baseline.Value.MetadataComplete, Is.True)

            fixture.Expired.Value <- true
            do! resume ignore fixture
            Assert.That(fixture.Starts.Value, Is.EqualTo(if completeMetadata then 1 else 2))
            Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box fixture.Bytes))
        }

    /// Changes selected physical or catalog inputs at the last checked point and verifies repeated restart preserves them.
    [<TestCase("zero")>]
    [<TestCase("ordinary")>]
    [<TestCase("catalog")>]
    [<TestCase("completed")>]
    [<TestCase("parent")>]
    [<TestCase("directoryChild")>]
    let ``local obstructions and catalog changes retain unapplied baseline work`` obstruction =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            let mutable changed = false

            /// Interleaves a local or catalog change immediately before the affected production decision.
            let observe point =
                if not changed then
                    if point = "beforePublish"
                       && Directory.Exists(Path.GetDirectoryName(target fixture))
                       && not (File.Exists(target fixture))
                       && (obstruction = "zero"
                           || obstruction = "ordinary"
                           || obstruction = "catalog") then
                        changed <- true

                        match obstruction with
                        | "zero" -> File.WriteAllBytes(target fixture, [||])
                        | "ordinary" -> File.WriteAllBytes(target fixture, [| 99uy |])
                        | _ -> fixture.Catalog.Value <- { fixture.Catalog.Value with Version = Guid.NewGuid() }
                    elif point = "beforeBoundary"
                         && obstruction = "completed" then
                        changed <- true
                        File.WriteAllBytes(target fixture, [| 98uy |])
                    elif
                        point = "prepare" && obstruction = "parent"
                        && Directory.Exists(Path.Combine(fixture.Configuration.RootDirectory, "Library", "one"))
                    then
                        changed <- true
                        let path = Path.Combine(fixture.Configuration.RootDirectory, "Library", "one")
                        Directory.Delete(path)
                        File.WriteAllBytes(path, [| 97uy |])
                    elif point = "publish"
                         && obstruction = "directoryChild" then
                        changed <- true
                        File.WriteAllBytes(Path.Combine(fixture.Configuration.RootDirectory, "Library", "one", "local.bin"), [| 96uy |])
                        invalidOp "Interrupted after directory publication"

            do! fails (resume observe fixture)
            Assert.That(changed, Is.True)
            Assert.That((state fixture).AppliedCursor, Is.Empty)
            Assert.That(LibrarySynchronization.captureSaved fixture.Configuration, Is.False)
            do! fails (resume ignore fixture)
            Assert.That((state fixture).AppliedCursor, Is.Empty)

            Assert.That(
                operations fixture
                |> Array.exists (fun op -> op.Direction = "local"),
                Is.False
            )

            if obstruction = "zero" then
                Assert.That(FileInfo(target fixture).Length, Is.EqualTo(0L))

            if obstruction = "ordinary" then
                Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box [| 99uy |]))

            if obstruction = "completed" then
                Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box [| 98uy |]))
        }

    /// Keeps coincidentally identical bytes and directories outside unprepared baseline work.
    [<TestCase(false)>]
    [<TestCase(true)>]
    let ``unprepared occupied target blocks even when its expected content matches`` fileTarget =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration

            if fileTarget then
                do!
                    fails (
                        resume
                            (fun point ->
                                if
                                    point = "item"
                                    && Directory.Exists(Path.GetDirectoryName(target fixture))
                                then
                                    invalidOp "Interrupted before file")
                            fixture
                    )

                File.WriteAllBytes(target fixture, fixture.Bytes)
            else
                Directory.CreateDirectory(Path.Combine(fixture.Configuration.RootDirectory, "Library", "one"))
                |> ignore

            do! fails (resume ignore fixture)
            Assert.That((state fixture).AppliedCursor, Is.Empty)
        }

    /// Prevents expiry from adopting a catalog changed during metadata acquisition.
    [<Test>]
    let ``changed catalog wins over expired continuation and preserves original selection`` () =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            let before = state fixture
            fixture.Expired.Value <- true
            fixture.Catalog.Value <- { fixture.Catalog.Value with Version = Guid.NewGuid() }
            do! fails (resume ignore fixture)
            Assert.That(state fixture, Is.EqualTo(before))
            Assert.That(fixture.Starts.Value, Is.EqualTo(1))
            Assert.That(fixture.Reads.Value, Is.Zero)
        }

    /// Gives the capture gate a positive control after the initial accepted-change catch-up releases onboarding.
    [<Test>]
    let ``baseline and initial catchup gate capture until completed onboarding then saved edits are captured`` () =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            Assert.That(LibrarySynchronization.captureSaved fixture.Configuration, Is.False)
            do! resume ignore fixture
            File.WriteAllBytes(target fixture, [| 88uy |])
            Assert.That(LibrarySynchronization.captureSaved fixture.Configuration, Is.False)
            finishOnboarding fixture.Configuration.GraceStatusFile (state fixture)
            Assert.That(LibrarySynchronization.captureSaved fixture.Configuration, Is.True)

            Assert.That(
                operations fixture
                |> Array.filter (fun op -> op.Direction = "local")
                |> Array.length,
                Is.EqualTo(1)
            )
        }

    /// Rejects bytes that do not match the selected descriptor before a target file is published.
    [<Test>]
    let ``incorrect retained bytes cannot complete or publish the selected baseline file`` () =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            let remote = { fixture.Remote with Read = fun _ -> Task.FromResult [| 1uy |] }
            do! fails (LibraryBaseline.resumeWith ignore remote fixture.Configuration CancellationToken.None)
            Assert.That(File.Exists(target fixture), Is.False)
            Assert.That((state fixture).AppliedCursor, Is.Empty)

            Assert.That(
                operations fixture
                |> Array.filter (fun op -> not op.Terminal)
                |> Array.length,
                Is.EqualTo(1)
            )

            Assert.That(LibrarySynchronization.captureSaved fixture.Configuration, Is.False)
        }

    /// Checks the catalog again after a continuation response rather than persisting a page under a stale check.
    [<Test>]
    let ``catalog change while a continuation is received prevents page persistence`` () =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            let before = state fixture
            let pending = operations fixture

            let remote =
                { fixture.Remote with
                    Continue =
                        fun _ _ ->
                            fixture.Catalog.Value <- { fixture.Catalog.Value with Version = Guid.NewGuid() }
                            Task.FromResult(Some fixture.Pages[1])
                }

            do! fails (LibraryBaseline.resumeWith ignore remote fixture.Configuration CancellationToken.None)
            Assert.That(state fixture, Is.EqualTo(before))
            Assert.That(operations fixture, Is.EqualTo(box pending))
            Assert.That(fixture.Reads.Value, Is.Zero)
        }

    /// Retains baseline work through partial onboarding and reuses existing Watch classification and bounded pruning afterward.
    [<Test>]
    let ``Watch classifies baseline echoes but pruning retains selection until initial catchup completes`` () =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            do! resume ignore fixture

            let paths =
                operations fixture
                |> Array.filter (fun op -> op.EchoPending)
                |> Array.map (fun op -> Path.Combine(fixture.Configuration.RootDirectory, op.TargetPath.Replace('/', Path.DirectorySeparatorChar)))

            do! LibrarySynchronization.classifyWatchObservations fixture.Configuration paths CancellationToken.None

            Assert.That(
                operations fixture
                |> Array.exists (fun op -> op.EchoPending),
                Is.False
            )

            pruneClassified fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId 0
            Assert.That(operations fixture |> Array.length, Is.EqualTo(4))
            finishOnboarding fixture.Configuration.GraceStatusFile (state fixture)
            pruneClassified fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId 0
            Assert.That(operations fixture, Is.Empty)
            Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box fixture.Bytes))
        }

    /// Injects an actual statement failure inside the production transaction so all partial writes roll back.
    let private sqliteFailure (connection: SqliteConnection) (transaction: SqliteTransaction) =
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- "INSERT INTO library_items(repository_id,item_id,item_json) VALUES('missing','invalid','{}');"
        command.ExecuteNonQuery() |> ignore

    /// Fails inside the first page transaction and requires both selection and work rows to remain absent.
    [<Test>]
    let ``first page failure rolls back participation and received work together`` () =
        task {
            let! fixture = fixture ()
            let page = fixture.Pages[0]

            let selected =
                {
                    RepositoryId = fixture.Configuration.RepositoryId
                    WorkingCopyId = Guid.NewGuid()
                    Catalog = page.LibraryCatalog
                    CursorEpoch = page.CursorEpoch
                    AppliedCursor = ""
                    NextPageToken = None
                    State = "acquiringBaseline"
                    Paused = false
                    Baseline = Some { BootstrapId = page.BootstrapId; BoundaryCursor = page.BoundaryCursor; MetadataComplete = false; Applied = false }
                }

            Assert.Throws<SqliteException>(Action(fun () -> saveBaselinePageWith sqliteFailure fixture.Configuration.GraceStatusFile None selected page false))
            |> ignore

            Assert.That(readRepository fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId, Is.EqualTo(None))
            Assert.That(operations fixture, Is.Empty)
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration
            do! resume ignore fixture
            Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box fixture.Bytes))
        }

    /// Tests page, reset, live-item and final-boundary rollback using the actual F# serializer and SQLite tables.
    [<TestCase("page")>]
    [<TestCase("reset")>]
    [<TestCase("item")>]
    [<TestCase("boundary")>]
    let ``SQLite interruption rolls back each baseline transaction as one unit`` boundary =
        task {
            let! fixture = fixture ()
            do! LibraryBaseline.startWith ignore fixture.Remote fixture.Configuration

            if boundary = "item" then
                do! fails (resume (fun point -> if point = "publish" then invalidOp "Interrupted") fixture)
            elif boundary = "boundary" then
                do! fails (resume (fun point -> if point = "beforeBoundary" then invalidOp "Interrupted") fixture)

            let before = state fixture
            let pending = operations fixture
            let items = readItems fixture.Configuration.GraceStatusFile fixture.Configuration.RepositoryId
            let db = fixture.Configuration.GraceStatusFile

            Assert.Throws<SqliteException>(
                Action (fun () ->
                    match boundary with
                    | "page" -> saveBaselinePageWith sqliteFailure db (Some before) before fixture.Pages[1] false
                    | "reset" -> saveBaselinePageWith sqliteFailure db (Some before) { before with NextPageToken = None } fixture.Pages[0] true
                    | "item" ->
                        completeBaselineItemWith
                            sqliteFailure
                            db
                            before
                            (pending
                             |> Array.find (fun op -> op.Prepared && not op.Terminal))
                    | _ -> completeBaselineWith sqliteFailure db before pending)
            )
            |> ignore

            Assert.That(state fixture, Is.EqualTo(before))
            Assert.That(operations fixture, Is.EqualTo(box pending))
            Assert.That(readItems db fixture.Configuration.RepositoryId, Is.EqualTo(box items))
            do! resume ignore fixture
            Assert.That(File.ReadAllBytes(target fixture), Is.EqualTo(box fixture.Bytes))
        }
