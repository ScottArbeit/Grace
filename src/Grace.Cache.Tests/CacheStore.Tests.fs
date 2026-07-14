namespace Grace.Cache.Tests

open System
open System.IO
open System.Threading.Tasks
open Grace.Cache
open Microsoft.Data.Sqlite
open NUnit.Framework

/// Provides isolated cache database paths and descriptor fixtures for SQLite store proof.
module private CacheStoreTestSupport =

    /// Creates a unique temporary database path unrelated to a working-copy local-state database.
    let createDatabasePath () =
        let directory = Path.Combine(Path.GetTempPath(), "grace-cache-store-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        Path.Combine(directory, "cache-state.db")

    /// Removes an isolated SQLite test directory after clearing pooled handles that could retain WAL sidecar files.
    let deleteDatabasePath (databasePath: string) =
        let directory = Path.GetDirectoryName(databasePath)
        SqliteConnection.ClearAllPools()

        if Directory.Exists(directory) then Directory.Delete(directory, true)

    /// Creates a deterministic SHA-256-shaped digest accepted by the store's source-integrity validation.
    let digest (suffix: int) = suffix.ToString("x").PadLeft(64, '0')

    /// Creates a descriptor whose fill token is revalidated immediately before valid publication.
    let descriptor (artifactId: string) (rootId: string) (suffix: int) : CacheArtifactDescriptor =
        {
            ArtifactId = artifactId
            Digest = digest suffix
            UncompressedSize = 4096L
            RootDirectoryVersionId = rootId
            FillToken = $"fill-{artifactId}-{rootId}"
        }

    /// Creates recursive metadata containing the root and one nested directory that both retain the promoted artifact.
    let metadata (rootId: string) (childId: string) (artifactId: string) : CacheRecursiveMetadata =
        {
            Directories =
                [
                    { DirectoryVersionId = rootId; ChildDirectoryVersionIds = [ childId ]; ArtifactIds = [ artifactId ] }
                    { DirectoryVersionId = childId; ChildDirectoryVersionIds = []; ArtifactIds = [ artifactId ] }
                ]
        }

    /// Executes a raw read-only assertion over the test database without becoming a production store surface.
    let readScalarInt (databasePath: string) (sql: string) =
        use connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar() |> Convert.ToInt32

    /// Executes test-only controlled corruption to prove the production store fails closed at its integrity boundary.
    let executeNonQuery (databasePath: string) (sql: string) =
        use connection = new SqliteConnection($"Data Source={databasePath}")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    /// Recognizes a rejected begin result without depending on F# union-case runtime implementation details.
    let isBeginRejected result =
        match result with
        | CacheIngestBeginResult.Rejected _ -> true
        | _ -> false

    /// Recognizes a rejected commit result without depending on F# union-case runtime implementation details.
    let isCommitRejected result =
        match result with
        | CacheIngestCommitResult.Rejected _ -> true
        | _ -> false

/// Verifies cache-local SQLite schema, publication, recovery, integrity, and concurrency contracts.
[<TestFixture>]
type CacheStoreTests() =

    /// Verifies schema initialization configures and reports required SQLite connection invariants.
    [<Test>]
    member _.OpenCreatesVersionedWalForeignKeyStore() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()

        try
            let opened = CacheStore.openStore databasePath
            let diagnostics = CacheStore.getDiagnostics opened.Store
            Assert.That(diagnostics.SchemaVersion, Is.EqualTo(1))
            Assert.That(diagnostics.JournalMode, Is.EqualTo("wal"))
            Assert.That(diagnostics.ForeignKeysEnabled, Is.True)
            Assert.That(diagnostics.BusyTimeoutMilliseconds, Is.GreaterThan(0))
            Assert.That(opened.RecoveredIncomplete, Is.Empty)
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies an unconfirmed or stale source descriptor cannot turn a pending artifact into a valid result.
    [<Test>]
    member _.MismatchedPromotionIsRejectedAndNeverPublished() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 1

        try
            let opened = CacheStore.openStore databasePath
            Assert.That(CacheStore.beginIngest opened.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))

            let changed = { source with FillToken = "replacement-token" }
            let result = CacheStore.commitPromotedIngest opened.Store changed (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
            Assert.That(CacheStoreTestSupport.isCommitRejected result, Is.True)
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies confirmed promotion atomically publishes recursive metadata and an eligible artifact.
    [<Test>]
    member _.ConfirmedPromotionPublishesRecursiveMetadata() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 2

        try
            let opened = CacheStore.openStore databasePath

            CacheStore.beginIngest opened.Store source
            |> ignore

            let result = CacheStore.commitPromotedIngest opened.Store source (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
            let artifacts = CacheStore.getValidArtifacts opened.Store
            Assert.That(result, Is.EqualTo(CacheIngestCommitResult.Published))
            Assert.That(artifacts, Has.Length.EqualTo(1))
            Assert.That(artifacts[0].ArtifactId, Is.EqualTo("artifact-a"))
            Assert.That(artifacts[0].RootDirectoryVersionId, Is.EqualTo("root-a"))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies overlapping roots preserve one canonical directory row while retaining both root memberships.
    [<Test>]
    member _.OverlappingRootsShareCanonicalRowsAndKeepMemberships() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 3
        let second = CacheStoreTestSupport.descriptor "artifact-b" "root-b" 4

        try
            let opened = CacheStore.openStore databasePath

            CacheStore.beginIngest opened.Store first
            |> ignore

            CacheStore.commitPromotedIngest opened.Store first (CacheStoreTestSupport.metadata "root-a" "shared" "artifact-a")
            |> ignore

            CacheStore.beginIngest opened.Store second
            |> ignore

            CacheStore.commitPromotedIngest opened.Store second (CacheStoreTestSupport.metadata "root-b" "shared" "artifact-b")
            |> ignore

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_versions;", Is.EqualTo(3))

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM root_directory_memberships WHERE directory_version_id = 'shared';",
                Is.EqualTo(2)
            )

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts WHERE lifecycle_state = 'Valid';", Is.EqualTo(2))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies the same immutable artifact can be retained under two roots without duplicating its canonical artifact row.
    [<Test>]
    member _.OverlappingRootsShareCanonicalArtifactRows() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.descriptor "artifact-shared" "root-a" 12
        let second = { first with RootDirectoryVersionId = "root-b"; FillToken = "fill-artifact-shared-root-b" }

        try
            let opened = CacheStore.openStore databasePath

            CacheStore.beginIngest opened.Store first
            |> ignore

            CacheStore.commitPromotedIngest opened.Store first (CacheStoreTestSupport.metadata "root-a" "shared-a" "artifact-shared")
            |> ignore

            Assert.That(CacheStore.beginIngest opened.Store second, Is.EqualTo(CacheIngestBeginResult.Pending))

            Assert.That(
                CacheStore.commitPromotedIngest opened.Store second (CacheStoreTestSupport.metadata "root-b" "shared-b" "artifact-shared"),
                Is.EqualTo(CacheIngestCommitResult.Published)
            )

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts WHERE artifact_id = 'artifact-shared';", Is.EqualTo(1))

            Assert.That(
                CacheStoreTestSupport.readScalarInt
                    databasePath
                    "SELECT COUNT(*) FROM root_directory_memberships WHERE root_directory_version_id IN ('root-a', 'root-b');",
                Is.EqualTo(4)
            )

            Assert.That(CacheStore.getValidArtifacts opened.Store, Has.Length.EqualTo(2))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a repeated confirmed ingest is idempotent and does not duplicate artifact or membership rows.
    [<Test>]
    member _.RepeatedConfirmedIngestIsIdempotent() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 5
        let recursiveMetadata = CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a"

        try
            let opened = CacheStore.openStore databasePath

            CacheStore.beginIngest opened.Store source
            |> ignore

            CacheStore.commitPromotedIngest opened.Store source recursiveMetadata
            |> ignore

            Assert.That(CacheStore.beginIngest opened.Store source, Is.EqualTo(CacheIngestBeginResult.AlreadyValid))
            Assert.That(CacheStore.commitPromotedIngest opened.Store source recursiveMetadata, Is.EqualTo(CacheIngestCommitResult.AlreadyPublished))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts;", Is.EqualTo(1))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_artifact_memberships;", Is.EqualTo(2))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies malformed digest and size inputs are rejected before any pending artifact state is persisted.
    [<Test>]
    member _.InvalidDescriptorDoesNotCreatePendingState() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()

        let invalid: CacheArtifactDescriptor =
            { ArtifactId = "artifact-a"; Digest = "not-a-digest"; UncompressedSize = -1L; RootDirectoryVersionId = "root-a"; FillToken = "fill-a" }

        try
            let opened = CacheStore.openStore databasePath
            Assert.That(CacheStoreTestSupport.isBeginRejected (CacheStore.beginIngest opened.Store invalid), Is.True)
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies incomplete metadata remains non-publishable and restart returns its cleanup identity after transactional removal.
    [<Test>]
    member _.RestartRecoversIncompleteIngestWithoutPublishingIt() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 6

        try
            let firstOpen = CacheStore.openStore databasePath

            CacheStore.beginIngest firstOpen.Store source
            |> ignore

            let invalidMetadata: CacheRecursiveMetadata = { Directories = [] }
            let result = CacheStore.commitPromotedIngest firstOpen.Store source invalidMetadata
            let restarted = CacheStore.openStore databasePath
            Assert.That(CacheStoreTestSupport.isCommitRejected result, Is.True)
            Assert.That(restarted.RecoveredIncomplete, Has.Length.EqualTo(1))
            Assert.That(restarted.RecoveredIncomplete[0].ArtifactId, Is.EqualTo("artifact-a"))
            Assert.That(CacheStore.getValidArtifacts restarted.Store, Is.Empty)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a rejected recursive graph leaves no dangling membership and preserves shared valid artifacts through recovery.
    [<Test>]
    member _.RejectedRecursiveGraphCannotRemoveSharedValidArtifact() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let valid = CacheStoreTestSupport.descriptor "artifact-valid" "root-valid" 7
        let incomplete = CacheStoreTestSupport.descriptor "artifact-pending" "root-pending" 8

        try
            let opened = CacheStore.openStore databasePath

            CacheStore.beginIngest opened.Store valid
            |> ignore

            CacheStore.commitPromotedIngest opened.Store valid (CacheStoreTestSupport.metadata "root-valid" "shared" "artifact-valid")
            |> ignore

            CacheStore.beginIngest opened.Store incomplete
            |> ignore

            let missingChild: CacheRecursiveMetadata =
                {
                    Directories =
                        [
                            { DirectoryVersionId = "root-pending"; ChildDirectoryVersionIds = [ "absent-child" ]; ArtifactIds = [ "artifact-pending" ] }
                        ]
                }

            Assert.That(CacheStoreTestSupport.isCommitRejected (CacheStore.commitPromotedIngest opened.Store incomplete missingChild), Is.True)
            let restarted = CacheStore.openStore databasePath
            let artifacts = CacheStore.getValidArtifacts restarted.Store
            Assert.That(restarted.RecoveredIncomplete, Has.Length.EqualTo(1))
            Assert.That(artifacts, Has.Length.EqualTo(1))
            Assert.That(artifacts[0].ArtifactId, Is.EqualTo("artifact-valid"))

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_artifact_memberships WHERE artifact_id = 'artifact-pending';",
                Is.EqualTo(0)
            )
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies concurrent overlapping ingests serialize through SQLite without duplicate canonical metadata.
    [<Test>]
    member _.ConcurrentOverlappingIngestsRemainCanonical() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 9
        let second = CacheStoreTestSupport.descriptor "artifact-b" "root-b" 10

        let ingest (source: CacheArtifactDescriptor) root child =
            Task.Run (fun () ->
                let opened = CacheStore.openStore databasePath

                CacheStore.beginIngest opened.Store source
                |> ignore

                CacheStore.commitPromotedIngest opened.Store source (CacheStoreTestSupport.metadata root child source.ArtifactId)
                |> ignore)

        try
            Task
                .WhenAll(
                    [|
                        ingest first "root-a" "shared"
                        ingest second "root-b" "shared"
                    |]
                )
                .GetAwaiter()
                .GetResult()

            let opened = CacheStore.openStore databasePath
            Assert.That(CacheStore.getValidArtifacts opened.Store, Has.Length.EqualTo(2))

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_versions WHERE directory_version_id = 'shared';",
                Is.EqualTo(1)
            )

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM root_directory_memberships WHERE directory_version_id = 'shared';",
                Is.EqualTo(2)
            )
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies schema initialization is repeatable and does not introduce durable prefetch workflow tables.
    [<Test>]
    member _.SchemaInitializationIsRepeatableWithoutPrefetchState() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()

        try
            CacheStore.openStore databasePath |> ignore
            CacheStore.openStore databasePath |> ignore
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM cache_schema;", Is.EqualTo(1))

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE '%prefetch%';",
                Is.EqualTo(0)
            )

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE '%queue%';",
                Is.EqualTo(0)
            )
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a reopened store rejects SQLite foreign-key corruption rather than serving a partially damaged valid graph.
    [<Test>]
    member _.ReopenFailsClosedOnForeignKeyCorruption() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 11

        try
            let opened = CacheStore.openStore databasePath

            CacheStore.beginIngest opened.Store source
            |> ignore

            CacheStore.commitPromotedIngest opened.Store source (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
            |> ignore

            CacheStoreTestSupport.executeNonQuery databasePath "PRAGMA foreign_keys = OFF;"
            CacheStoreTestSupport.executeNonQuery databasePath "DELETE FROM directory_versions WHERE directory_version_id = 'root-a';"
            Assert.That(Action(fun () -> CacheStore.openStore databasePath |> ignore), Throws.TypeOf<InvalidOperationException>())
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath
