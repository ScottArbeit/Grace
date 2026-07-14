namespace Grace.Cache.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Grace.Cache
open Microsoft.Data.Sqlite
open NUnit.Framework

/// Provides isolated cache database paths and descriptor fixtures for SQLite store proof.
module private CacheStoreTestSupport =

    let private openedStores = ConcurrentBag<CacheStore>()

    /// Opens one cache store and tracks its lease so fixture cleanup cannot retain ownership locks across tests.
    let openStore databasePath =
        let result = CacheStore.openStore databasePath

        match result with
        | Opened (store, _) -> openedStores.Add(store)
        | CacheDatabaseInUse -> ()

        result

    /// Creates a unique temporary database path unrelated to a working-copy local-state database.
    let createDatabasePath () =
        let directory = Path.Combine(Path.GetTempPath(), "grace-cache-store-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        Path.Combine(directory, "cache-state.db")

    /// Removes an isolated SQLite test directory after clearing pooled handles that could retain WAL sidecar files.
    let deleteDatabasePath (databasePath: string) =
        let directory = Path.GetDirectoryName(databasePath)

        for store in openedStores do
            CacheStore.disposeStore store

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

    /// Creates overlapping-root metadata whose shared directory has one immutable empty membership set.
    let metadataWithSharedDirectory (rootId: string) (sharedDirectoryId: string) (artifactId: string) : CacheRecursiveMetadata =
        {
            Directories =
                [
                    { DirectoryVersionId = rootId; ChildDirectoryVersionIds = [ sharedDirectoryId ]; ArtifactIds = [ artifactId ] }
                    { DirectoryVersionId = sharedDirectoryId; ChildDirectoryVersionIds = []; ArtifactIds = [] }
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

    /// Starts this test assembly in a separate process so ownership behavior is proven across OS process boundaries.
    let startStoreProcess mode (databasePath: string) =
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location)
        startInfo.ArgumentList.Add(mode)
        startInfo.ArgumentList.Add(databasePath)
        Process.Start(startInfo)

    /// Reads a child-process readiness line within the bounded test timeout.
    let readProcessLine (childProcess: Process) =
        let lineTask = childProcess.StandardOutput.ReadLineAsync()

        if not (lineTask.Wait(TimeSpan.FromSeconds(10.0))) then
            Assert.Fail("The cache ownership child process did not report readiness within ten seconds.")

        lineTask.Result

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
            let opened = CacheStoreTestSupport.openStore databasePath
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
            let opened = CacheStoreTestSupport.openStore databasePath
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
            let opened = CacheStoreTestSupport.openStore databasePath

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
            let opened = CacheStoreTestSupport.openStore databasePath

            CacheStore.beginIngest opened.Store first
            |> ignore

            CacheStore.commitPromotedIngest opened.Store first (CacheStoreTestSupport.metadataWithSharedDirectory "root-a" "shared" "artifact-a")
            |> ignore

            CacheStore.beginIngest opened.Store second
            |> ignore

            CacheStore.commitPromotedIngest opened.Store second (CacheStoreTestSupport.metadataWithSharedDirectory "root-b" "shared" "artifact-b")
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
            let opened = CacheStoreTestSupport.openStore databasePath

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
            let opened = CacheStoreTestSupport.openStore databasePath

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
            let opened = CacheStoreTestSupport.openStore databasePath
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
            let firstOpen = CacheStoreTestSupport.openStore databasePath

            CacheStore.beginIngest firstOpen.Store source
            |> ignore

            let invalidMetadata: CacheRecursiveMetadata = { Directories = [] }
            let result = CacheStore.commitPromotedIngest firstOpen.Store source invalidMetadata
            CacheStore.disposeStore firstOpen.Store
            let restarted = CacheStoreTestSupport.openStore databasePath
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
            let opened = CacheStoreTestSupport.openStore databasePath

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
            CacheStore.disposeStore opened.Store
            let restarted = CacheStoreTestSupport.openStore databasePath
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
                let opened = CacheStoreTestSupport.openStore databasePath

                CacheStore.beginIngest opened.Store source
                |> ignore

                CacheStore.commitPromotedIngest opened.Store source (CacheStoreTestSupport.metadataWithSharedDirectory root child source.ArtifactId)
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

            let opened = CacheStoreTestSupport.openStore databasePath
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
            CacheStoreTestSupport.openStore databasePath
            |> ignore

            CacheStoreTestSupport.openStore databasePath
            |> ignore

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

    /// Verifies a second root cannot claim a pending artifact id with divergent immutable identity.
    [<Test>]
    member _.CrossRootPendingIdentityConflictIsRejected() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.descriptor "artifact-shared" "root-a" 21

        let conflicting = { first with RootDirectoryVersionId = "root-b"; Digest = CacheStoreTestSupport.digest 22; FillToken = "fill-artifact-shared-root-b" }

        try
            let opened = CacheStoreTestSupport.openStore databasePath
            Assert.That(CacheStore.beginIngest opened.Store first, Is.EqualTo(CacheIngestBeginResult.Pending))
            Assert.That(CacheStoreTestSupport.isBeginRejected (CacheStore.beginIngest opened.Store conflicting), Is.True)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies an immutable directory version rejects a later metadata graph with different memberships.
    [<Test>]
    member _.DivergentExistingDirectoryMetadataIsRejected() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 23
        let second = CacheStoreTestSupport.descriptor "artifact-b" "root-b" 24

        let divergent: CacheRecursiveMetadata =
            {
                Directories =
                    [
                        { DirectoryVersionId = "root-b"; ChildDirectoryVersionIds = [ "shared" ]; ArtifactIds = [ "artifact-b" ] }
                        { DirectoryVersionId = "shared"; ChildDirectoryVersionIds = []; ArtifactIds = [ "artifact-b" ] }
                    ]
            }

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            CacheStore.beginIngest opened.Store first
            |> ignore

            CacheStore.commitPromotedIngest opened.Store first (CacheStoreTestSupport.metadata "root-a" "shared" "artifact-a")
            |> ignore

            CacheStore.beginIngest opened.Store second
            |> ignore

            Assert.That(CacheStoreTestSupport.isCommitRejected (CacheStore.commitPromotedIngest opened.Store second divergent), Is.True)
            Assert.That(CacheStore.getValidArtifacts opened.Store, Has.Length.EqualTo(1))

            Assert.That(
                CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_artifact_memberships WHERE directory_version_id = 'shared';",
                Is.EqualTo(1)
            )
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies recursive metadata containing an indirect cycle cannot become valid cache state.
    [<Test>]
    member _.CyclicRecursiveMetadataIsRejected() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 25

        let cyclic: CacheRecursiveMetadata =
            {
                Directories =
                    [
                        { DirectoryVersionId = "root-a"; ChildDirectoryVersionIds = [ "child-a" ]; ArtifactIds = [ "artifact-a" ] }
                        { DirectoryVersionId = "child-a"; ChildDirectoryVersionIds = [ "root-a" ]; ArtifactIds = [ "artifact-a" ] }
                    ]
            }

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            CacheStore.beginIngest opened.Store source
            |> ignore

            Assert.That(CacheStoreTestSupport.isCommitRejected (CacheStore.commitPromotedIngest opened.Store source cyclic), Is.True)
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_versions;", Is.EqualTo(0))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies an idempotent publish result establishes the requested root membership for a canonical artifact.
    [<Test>]
    member _.ExistingArtifactCommitCreatesMissingRootMembership() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.descriptor "artifact-shared" "root-a" 26
        let second = { first with RootDirectoryVersionId = "root-b"; FillToken = "fill-artifact-shared-root-b" }

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            CacheStore.beginIngest opened.Store first
            |> ignore

            CacheStore.commitPromotedIngest opened.Store first (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-shared")
            |> ignore

            Assert.That(
                CacheStore.commitPromotedIngest opened.Store second (CacheStoreTestSupport.metadata "root-b" "child-b" "artifact-shared"),
                Is.EqualTo(CacheIngestCommitResult.AlreadyPublished)
            )

            Assert.That(CacheStore.getValidArtifacts opened.Store, Has.Length.EqualTo(2))

            Assert.That(
                CacheStoreTestSupport.readScalarInt
                    databasePath
                    "SELECT COUNT(*) FROM root_artifact_memberships WHERE root_directory_version_id = 'root-b' AND artifact_id = 'artifact-shared';",
                Is.EqualTo(1)
            )
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies null digests fail closed through the normal begin and commit result unions.
    [<Test>]
    member _.NullDigestIsRejectedWithoutThrowing() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let malformed = { CacheStoreTestSupport.descriptor "artifact-a" "root-a" 27 with Digest = null }

        try
            let opened = CacheStoreTestSupport.openStore databasePath
            Assert.That(CacheStoreTestSupport.isBeginRejected (CacheStore.beginIngest opened.Store malformed), Is.True)

            Assert.That(
                CacheStoreTestSupport.isCommitRejected (
                    CacheStore.commitPromotedIngest opened.Store malformed (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
                ),
                Is.True
            )

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a null descriptor and each nullable descriptor identity field reject through both write boundaries without state changes.
    [<Test>]
    member _.NullDescriptorAndFieldsAreRejectedWithoutMutation() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let canonical = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 32

        let malformedDescriptors: CacheArtifactDescriptor list =
            [
                Unchecked.defaultof<CacheArtifactDescriptor>
                { canonical with ArtifactId = null }
                { canonical with Digest = null }
                { canonical with RootDirectoryVersionId = null }
                { canonical with FillToken = null }
            ]

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            for malformed in malformedDescriptors do
                Assert.That(CacheStoreTestSupport.isBeginRejected (CacheStore.beginIngest opened.Store malformed), Is.True)

                Assert.That(
                    CacheStoreTestSupport.isCommitRejected (
                        CacheStore.commitPromotedIngest opened.Store malformed (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
                    ),
                    Is.True
                )

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts;", Is.EqualTo(0))
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies malformed source identity rejects before a hostile metadata graph can be traversed or mutate cache state.
    [<Test>]
    member _.DescriptorValidationShortCircuitsHostileMetadataWithoutMutation() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let malformed = { CacheStoreTestSupport.descriptor "artifact-a" "root-a" 33 with RootDirectoryVersionId = null }
        let hostile: CacheRecursiveMetadata = { Directories = Unchecked.defaultof<CacheDirectoryMetadata list> }

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            Assert.That(CacheStoreTestSupport.isCommitRejected (CacheStore.commitPromotedIngest opened.Store malformed hostile), Is.True)

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts;", Is.EqualTo(0))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_versions;", Is.EqualTo(0))
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies every nullable recursive metadata collection, entry, and identifier rejects without publishing metadata.
    [<Test>]
    member _.NullRecursiveMetadataShapesAreRejectedWithoutMetadataMutation() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 34

        let root: CacheDirectoryMetadata = { DirectoryVersionId = "root-a"; ChildDirectoryVersionIds = []; ArtifactIds = [ "artifact-a" ] }

        let malformedMetadata: CacheRecursiveMetadata list =
            [
                Unchecked.defaultof<CacheRecursiveMetadata>
                { Directories = Unchecked.defaultof<CacheDirectoryMetadata list> }
                {
                    Directories =
                        [
                            Unchecked.defaultof<CacheDirectoryMetadata>
                        ]
                }
                {
                    Directories =
                        [
                            { root with DirectoryVersionId = null }
                        ]
                }
                {
                    Directories =
                        [
                            { root with ChildDirectoryVersionIds = Unchecked.defaultof<string list> }
                        ]
                }
                {
                    Directories =
                        [
                            { root with ArtifactIds = Unchecked.defaultof<string list> }
                        ]
                }
                {
                    Directories =
                        [
                            { root with ChildDirectoryVersionIds = [ Unchecked.defaultof<string> ] }
                        ]
                }
                {
                    Directories =
                        [
                            { root with ArtifactIds = [ Unchecked.defaultof<string> ] }
                        ]
                }
            ]

        try
            let opened = CacheStoreTestSupport.openStore databasePath
            Assert.That(CacheStore.beginIngest opened.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))

            for metadata in malformedMetadata do
                Assert.That(CacheStoreTestSupport.isCommitRejected (CacheStore.commitPromotedIngest opened.Store source metadata), Is.True)

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts;", Is.EqualTo(0))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM directory_versions;", Is.EqualTo(0))
            Assert.That(CacheStore.getValidArtifacts opened.Store, Is.Empty)
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies non-lowercase SHA-256 text cannot create durable identity and a canonical retry remains publishable.
    [<Test>]
    member _.NonCanonicalDigestIsRejectedBeforePersistenceAndCanonicalRetrySucceeds() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()

        let canonical = { CacheStoreTestSupport.descriptor "artifact-a" "root-a" 27 with Digest = String.replicate 64 "a" }

        let uppercase = { canonical with Digest = canonical.Digest.ToUpperInvariant() }
        let mixedCase = { canonical with Digest = canonical.Digest.Substring(0, 63) + "A" }

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            Assert.That(CacheStoreTestSupport.isBeginRejected (CacheStore.beginIngest opened.Store uppercase), Is.True)

            Assert.That(
                CacheStoreTestSupport.isCommitRejected (
                    CacheStore.commitPromotedIngest opened.Store mixedCase (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
                ),
                Is.True
            )

            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM artifacts;", Is.EqualTo(0))
            Assert.That(CacheStore.beginIngest opened.Store canonical, Is.EqualTo(CacheIngestBeginResult.Pending))

            Assert.That(
                CacheStore.commitPromotedIngest opened.Store canonical (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a"),
                Is.EqualTo(CacheIngestCommitResult.Published)
            )

            let artifacts = CacheStore.getValidArtifacts opened.Store
            Assert.That(artifacts, Has.Length.EqualTo(1))
            Assert.That(artifacts[0].Digest, Is.EqualTo(canonical.Digest))
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies cache ownership registry keys follow the runtime platform's case-sensitive path behavior.
    [<Test>]
    member _.CanonicalPathRegistryUsesPlatformCaseSemantics() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let caseVariant = Path.Combine(Path.GetDirectoryName(databasePath), "CACHE-STATE.DB")
        let first = CacheStoreTestSupport.openStore databasePath

        try
            let second = CacheStoreTestSupport.openStore caseVariant

            try
                if OperatingSystem.IsWindows() then
                    let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 28
                    Assert.That(CacheStore.beginIngest first.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))
                    Assert.That(CacheStore.beginIngest second.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))
                    Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))
                else
                    let firstSource = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 28
                    let secondSource = CacheStoreTestSupport.descriptor "artifact-b" "root-b" 29
                    Assert.That(CacheStore.beginIngest first.Store firstSource, Is.EqualTo(CacheIngestBeginResult.Pending))
                    Assert.That(CacheStore.beginIngest second.Store secondSource, Is.EqualTo(CacheIngestBeginResult.Pending))
                    Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))
                    Assert.That(CacheStoreTestSupport.readScalarInt caseVariant "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))
            finally
                CacheStore.disposeStore second.Store
        finally
            CacheStore.disposeStore first.Store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies canonical aliases share one in-process owner, skip recovery, and survive another caller's disposal.
    [<Test>]
    member _.SameProcessReopenReusesOwnershipAndPreservesLivePendingIngest() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let alias = Path.Combine(Path.GetDirectoryName(databasePath), ".", Path.GetFileName(databasePath))
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 28
        let first = CacheStoreTestSupport.openStore databasePath

        try
            Assert.That(CacheStore.beginIngest first.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))
            let second = CacheStoreTestSupport.openStore alias

            try
                Assert.That(second.RecoveredIncomplete, Is.Empty)
                CacheStore.disposeStore first.Store

                Assert.That(
                    CacheStore.commitPromotedIngest second.Store source (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a"),
                    Is.EqualTo(CacheIngestCommitResult.Published)
                )

                let third = CacheStoreTestSupport.openStore databasePath

                try
                    Assert.That(third.RecoveredIncomplete, Is.Empty)
                    Assert.That(CacheStore.getValidArtifacts third.Store, Has.Length.EqualTo(1))
                finally
                    CacheStore.disposeStore third.Store
            finally
                CacheStore.disposeStore second.Store
        finally
            CacheStore.disposeStore first.Store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies parent-directory symlink aliases retain one physical database owner before and after SQLite creates the database file.
    [<Test>]
    member _.SymlinkedParentAliasSharesStablePreAndPostCreationOwnership() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let physicalDirectory = Path.GetDirectoryName(databasePath)
        let aliasDirectory = physicalDirectory + "-alias"
        let aliasDatabasePath = Path.Combine(aliasDirectory, Path.GetFileName(databasePath))
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 35

        try
            Directory.CreateSymbolicLink(aliasDirectory, physicalDirectory)
            |> ignore

            let beforeCreation = CacheStoreTestSupport.openStore aliasDatabasePath

            try
                Assert.That(CacheStore.beginIngest beforeCreation.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))

                let resolvedPath = CacheStoreTestSupport.openStore databasePath

                try
                    Assert.That(resolvedPath.RecoveredIncomplete, Is.Empty)

                    let afterCreation = CacheStoreTestSupport.openStore aliasDatabasePath

                    try
                        Assert.That(afterCreation.RecoveredIncomplete, Is.Empty)
                        Assert.That(CacheStore.beginIngest afterCreation.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))
                        Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))

                        CacheStore.disposeStore beforeCreation.Store
                        CacheStore.disposeStore resolvedPath.Store

                        Assert.That(
                            CacheStore.commitPromotedIngest afterCreation.Store source (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a"),
                            Is.EqualTo(CacheIngestCommitResult.Published)
                        )
                    finally
                        CacheStore.disposeStore afterCreation.Store
                finally
                    CacheStore.disposeStore resolvedPath.Store
            finally
                CacheStore.disposeStore beforeCreation.Store
        finally
            if Directory.Exists(aliasDirectory) then Directory.Delete(aliasDirectory)
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a competing process receives the stable in-use result before it can clean or mutate live pending state.
    [<Test>]
    member _.CompetingProcessIsRejectedWithoutMutatingLivePendingIngest() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 29
        let owner = CacheStoreTestSupport.openStore databasePath

        try
            Assert.That(CacheStore.beginIngest owner.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))
            use competingProcess = CacheStoreTestSupport.startStoreProcess "--attempt-store-open" databasePath
            Assert.That(CacheStoreTestSupport.readProcessLine competingProcess, Is.EqualTo("DATABASE-IN-USE"))
            Assert.That(competingProcess.WaitForExit(10000), Is.True)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))
            Assert.That(CacheStore.getValidArtifacts owner.Store, Is.Empty)
        finally
            CacheStore.disposeStore owner.Store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies final lease disposal retains process ownership through an in-flight exception before recovery may run.
    [<Test>]
    member _.FinalLeaseDisposalWaitsForInFlightOperationAndExceptionReleasesOwnership() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 31
        let owner = CacheStoreTestSupport.openStore databasePath
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)

        try
            Assert.That(CacheStore.beginIngest owner.Store source, Is.EqualTo(CacheIngestBeginResult.Pending))

            let operation = Task.Run(fun () -> CacheStore.runBlockedOperationForTest owner.Store entered release true)

            Assert.That(entered.Wait(TimeSpan.FromSeconds(10.0)), Is.True)
            CacheStore.disposeStore owner.Store

            use competingProcess = CacheStoreTestSupport.startStoreProcess "--attempt-store-open" databasePath
            Assert.That(CacheStoreTestSupport.readProcessLine competingProcess, Is.EqualTo("DATABASE-IN-USE"))
            Assert.That(competingProcess.WaitForExit(10000), Is.True)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(1))

            release.Set()

            Assert.That(
                Action (fun () ->
                    operation.Wait(TimeSpan.FromSeconds(10.0))
                    |> ignore),
                Throws.TypeOf<AggregateException>()
            )

            Assert.That(operation.IsFaulted, Is.True)

            use recoveredProcess = CacheStoreTestSupport.startStoreProcess "--attempt-store-open" databasePath
            Assert.That(CacheStoreTestSupport.readProcessLine recoveredProcess, Is.EqualTo("OPENED"))
            Assert.That(recoveredProcess.WaitForExit(10000), Is.True)
            Assert.That(CacheStoreTestSupport.readScalarInt databasePath "SELECT COUNT(*) FROM pending_ingests;", Is.EqualTo(0))
        finally
            release.Set()
            CacheStore.disposeStore owner.Store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a released caller lease stays unusable while repeated disposal leaves the ownership lifecycle stable.
    [<Test>]
    member _.DisposedLeaseRejectsNewOperationsAndDisposalIsIdempotent() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let owner = CacheStoreTestSupport.openStore databasePath

        try
            CacheStore.disposeStore owner.Store
            CacheStore.disposeStore owner.Store

            Assert.That(Action(fun () -> CacheStore.getValidArtifacts owner.Store |> ignore), Throws.TypeOf<InvalidOperationException>())

            use reopenedProcess = CacheStoreTestSupport.startStoreProcess "--attempt-store-open" databasePath
            Assert.That(CacheStoreTestSupport.readProcessLine reopenedProcess, Is.EqualTo("OPENED"))
            Assert.That(reopenedProcess.WaitForExit(10000), Is.True)
        finally
            CacheStore.disposeStore owner.Store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies process termination releases ownership so the next owner recovers abandoned pending state without removing valid rows.
    [<Test>]
    member _.ProcessExitReleasesOwnershipAndNextOwnerRecoversOnlyAbandonedPendingState() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let valid = CacheStoreTestSupport.descriptor "artifact-valid" "root-valid" 30
        let initialOwner = CacheStoreTestSupport.openStore databasePath

        try
            CacheStore.beginIngest initialOwner.Store valid
            |> ignore

            CacheStore.commitPromotedIngest initialOwner.Store valid (CacheStoreTestSupport.metadata "root-valid" "child-valid" "artifact-valid")
            |> ignore

            CacheStore.disposeStore initialOwner.Store

            use childProcess = CacheStoreTestSupport.startStoreProcess "--hold-store" databasePath
            Assert.That(CacheStoreTestSupport.readProcessLine childProcess, Is.EqualTo("READY"))
            childProcess.Kill(true)
            Assert.That(childProcess.WaitForExit(10000), Is.True)

            let recoveredOwner = CacheStoreTestSupport.openStore databasePath

            try
                Assert.That(recoveredOwner.RecoveredIncomplete, Has.Length.EqualTo(1))
                Assert.That(recoveredOwner.RecoveredIncomplete[0].ArtifactId, Is.EqualTo("child-pending-artifact"))
                let artifacts = CacheStore.getValidArtifacts recoveredOwner.Store
                Assert.That(artifacts, Has.Length.EqualTo(1))
                Assert.That(artifacts[0].ArtifactId, Is.EqualTo("artifact-valid"))
            finally
                CacheStore.disposeStore recoveredOwner.Store
        finally
            CacheStore.disposeStore initialOwner.Store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Verifies a reopened store rejects SQLite foreign-key corruption rather than serving a partially damaged valid graph.
    [<Test>]
    member _.ReopenFailsClosedOnForeignKeyCorruption() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let source = CacheStoreTestSupport.descriptor "artifact-a" "root-a" 11

        try
            let opened = CacheStoreTestSupport.openStore databasePath

            CacheStore.beginIngest opened.Store source
            |> ignore

            CacheStore.commitPromotedIngest opened.Store source (CacheStoreTestSupport.metadata "root-a" "child-a" "artifact-a")
            |> ignore

            CacheStore.disposeStore opened.Store
            CacheStoreTestSupport.executeNonQuery databasePath "PRAGMA foreign_keys = OFF;"
            CacheStoreTestSupport.executeNonQuery databasePath "DELETE FROM directory_versions WHERE directory_version_id = 'root-a';"

            Assert.That(
                Action (fun () ->
                    CacheStoreTestSupport.openStore databasePath
                    |> ignore),
                Throws.TypeOf<InvalidOperationException>()
            )
        finally
            CacheStoreTestSupport.deleteDatabasePath databasePath
