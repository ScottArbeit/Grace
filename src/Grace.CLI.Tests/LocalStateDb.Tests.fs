namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared.Client.Configuration
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Microsoft.Data.Sqlite
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Text
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

module WorkingDirectoryUpdate = WorkingDirectoryUpdateContracts

/// Groups local state db coverage for the CLI test project.
[<NonParallelizable>]
module LocalStateDbTests =
    /// Configures verbose logging for the test scenario.
    let private configureVerboseLogging () =
        let value = Environment.GetEnvironmentVariable("GRACE_LOCALSTATE_DB_VERBOSE")

        if not (String.IsNullOrWhiteSpace(value)) then
            let enabled =
                value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

            LocalStateDb.setVerbose enabled

    /// Configures for root for the test scenario.
    let private configureForRoot (root: string) =
        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.NewGuid()
        configuration.OrganizationId <- Guid.NewGuid()
        configuration.RepositoryId <- Guid.NewGuid()
        configuration.BranchId <- Guid.NewGuid()
        configuration.RootDirectory <- root
        configuration.StandardizedRootDirectory <- normalizeFilePath root
        configuration.GraceDirectory <- Path.Combine(root, Constants.GraceConfigDirectory)
        configuration.ObjectDirectory <- Path.Combine(configuration.GraceDirectory, Constants.GraceObjectsDirectory)
        configuration.GraceStatusFile <- Path.Combine(configuration.GraceDirectory, Constants.GraceLocalStateDbFileName)
        configuration.GraceObjectCacheFile <- configuration.GraceStatusFile
        configuration.ConfigurationDirectory <- configuration.GraceDirectory
        configuration.IsPopulated <- true
        updateConfiguration configuration
        configuration

    /// Builds ensure grace config test data used to exercise CLI local State Db behavior.
    let private ensureGraceConfig (root: string) =
        let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)
        let configPath = Path.Combine(graceDir, Constants.GraceConfigFileName)

        if not (Directory.Exists(graceDir)) then
            Directory.CreateDirectory(graceDir) |> ignore

        if not (File.Exists(configPath)) then File.WriteAllText(configPath, "{}")

    /// Runs the supplied action with temp dir applied.
    let private withTempDir (action: string -> GraceConfiguration -> Task<'T>) =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"grace-tests-{Guid.NewGuid()}")
            Directory.CreateDirectory(root) |> ignore
            let previousDirectory = Environment.CurrentDirectory
            let previousConfiguration = if configurationFileExists () then Some(Current()) else None

            try
                Environment.CurrentDirectory <- root
                configureVerboseLogging ()
                ensureGraceConfig root
                let configuration = configureForRoot root
                return! action root configuration
            finally
                Environment.CurrentDirectory <- previousDirectory

                match previousConfiguration with
                | Some configuration -> updateConfiguration configuration
                | None -> resetConfiguration ()

                if Directory.Exists(root) then
                    try
                        SqliteConnection.ClearAllPools()
                        Directory.Delete(root, true)
                    with
                    | _ -> ()
        }

    /// Builds a deterministic file version for test scenarios fixture for the CLI local State Db assertions.
    let private createFileVersion relativePath sha256Hash isBinary size createdAt lastWriteTime =
        LocalFileVersion.CreateWithHashes relativePath sha256Hash (Blake3Hash "fixture-blake3") isBinary size createdAt true lastWriteTime

    /// Builds a deterministic file version with hashes for test scenarios fixture for the CLI local State Db assertions.
    let private createFileVersionWithHashes relativePath sha256Hash blake3Hash isBinary size createdAt lastWriteTime =
        LocalFileVersion.CreateWithHashes relativePath sha256Hash blake3Hash isBinary size createdAt true lastWriteTime

    let private createDirectoryVersion
        (configuration: GraceConfiguration)
        (directoryVersionId: DirectoryVersionId)
        relativePath
        sha256Hash
        (directoryIds: DirectoryVersionId array)
        (files: LocalFileVersion array)
        sizeBytes
        lastWriteTimeUtc
        : LocalDirectoryVersion
        =
        LocalDirectoryVersion.CreateWithHashes
            directoryVersionId
            configuration.OwnerId
            configuration.OrganizationId
            configuration.RepositoryId
            relativePath
            sha256Hash
            (Blake3Hash $"{sha256Hash}-blake3")
            (List<DirectoryVersionId>(directoryIds))
            (List<LocalFileVersion>(files))
            sizeBytes
            lastWriteTimeUtc

    /// Builds open raw connection test data used to exercise CLI local State Db behavior.
    let private openRawConnection (dbPath: string) =
        let connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        connection

    /// Builds execute scalar string test data used to exercise CLI local State Db behavior.
    let private executeScalarString (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar() :?> string

    /// Builds execute scalar int test data used to exercise CLI local State Db behavior.
    let private executeScalarInt (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar() |> Convert.ToInt32

    /// Builds execute scalar int with text parameter test data used to exercise CLI local State Db behavior.
    let private executeScalarIntWithTextParameter (connection: SqliteConnection) (sql: string) parameterName parameterValue =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql

        cmd.Parameters.AddWithValue(parameterName, parameterValue)
        |> ignore

        cmd.ExecuteScalar() |> Convert.ToInt32

    /// Counts status file rows for test assertions.
    let private countStatusFileRows connection relativePath =
        executeScalarIntWithTextParameter connection "SELECT COUNT(*) FROM status_files WHERE relative_path = $relative_path;" "$relative_path" relativePath

    /// Counts status directory rows for test assertions.
    let private countStatusDirectoryRows connection relativePath =
        executeScalarIntWithTextParameter
            connection
            "SELECT COUNT(*) FROM status_directories WHERE relative_path = $relative_path;"
            "$relative_path"
            relativePath

    /// Builds execute scalar int64 test data used to exercise CLI local State Db behavior.
    let private executeScalarInt64 (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar() |> Convert.ToInt64

    /// Builds execute non query test data used to exercise CLI local State Db behavior.
    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    /// Closes every SQLite handle so a pending-finalization assertion exercises the production restart reconstruction path.
    let private assertPendingFinalizationReopenRejects (configuration: GraceConfiguration) expectedMessage =
        SqliteConnection.ClearAllPools()
        LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile

        let corruptedRead = Func<Task>(fun () -> LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile :> Task)

        let thrownException = Assert.ThrowsAsync<InvalidOperationException>(corruptedRead)

        thrownException.Message
        |> should equal expectedMessage

    /// Allocates Watch journal sequences without adding replay semantics beyond the schema scaffold.
    let private insertWatchJournalRows (connection: SqliteConnection) throughSequence =
        [| 1L .. throughSequence |]
        |> Array.iter (fun sequence ->
            executeNonQuery
                connection
                $"INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path) VALUES ({sequence}, {sequence}, 'Change', 'File', 'file-{sequence}.txt');")

    /// Builds a replayable Watch journal observation for local-state ordering tests.
    let private watchJournalScope (configuration: GraceConfiguration) : LocalStateDb.WatchJournalScope =
        {
            RepositoryId = configuration.RepositoryId
            BranchId = configuration.BranchId
            WorkspaceRoot = configuration.RootDirectory
            WatchRoot = configuration.RootDirectory
            PathComparison = StringComparison.Ordinal
            RootDirectoryId = DirectoryVersionId("11111111-1111-1111-1111-111111111111")
            RootDirectorySha256Hash = Sha256Hash "test-root-sha256"
            RootDirectoryBlake3Hash = Blake3Hash "test-root-blake3"
            WatchMode = "repository-root"
        }

    /// Builds a replayable Watch journal observation for local-state ordering tests.
    let private watchJournalObservation differenceType entryType (relativePath: string) : LocalStateDb.WatchJournalObservation =
        { Scope = watchJournalScope (Current()); DifferenceType = differenceType; EntryType = entryType; RelativePath = RelativePath relativePath }

    /// Builds a scoped replayable Watch journal observation for startup recovery tests.
    let private scopedWatchJournalObservation scope differenceType entryType (relativePath: string) : LocalStateDb.WatchJournalObservation =
        { Scope = scope; DifferenceType = differenceType; EntryType = entryType; RelativePath = RelativePath relativePath }

    /// Gets corrupt backups needed by the test scenario.
    let private getCorruptBackups (dbPath: string) =
        let directoryPath = Path.GetDirectoryName(dbPath)

        if String.IsNullOrWhiteSpace(directoryPath) then
            Array.Empty<string>()
        else
            Directory.GetFiles(directoryPath, "grace-local.corrupt.*.db")

    /// Builds snapshot file test data used to exercise CLI local State Db behavior.
    let private snapshotFile (path: string) =
        if File.Exists(path) then
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use reader = new BinaryReader(stream)
            Some(reader.ReadBytes(int stream.Length), File.GetLastWriteTimeUtc(path))
        else
            None

    /// Builds seed schema version only test data used to exercise CLI local State Db behavior.
    let private seedSchemaVersionOnly (dbPath: string) (schemaVersion: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
        |> ignore

        use connection = openRawConnection dbPath
        executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
        executeNonQuery connection $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '{schemaVersion}');"

    /// Builds seed current schema with status meta test data used to exercise CLI local State Db behavior.
    let private seedCurrentSchemaWithStatusMeta (dbPath: string) (rootId: Guid) rootSha256Hash rootBlake3Hash ticks =
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
        |> ignore

        use connection = openRawConnection dbPath
        executeNonQuery connection "PRAGMA journal_mode = WAL;"
        executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, root_directory_blake3_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS status_directories (relative_path TEXT PRIMARY KEY, parent_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_status_directories_parent ON status_directories(parent_path);"
        executeNonQuery connection "CREATE UNIQUE INDEX IF NOT EXISTS ix_status_directories_directory_version_id ON status_directories(directory_version_id);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS status_files (relative_path TEXT PRIMARY KEY, directory_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, FOREIGN KEY (directory_version_id) REFERENCES status_directories(directory_version_id) ON DELETE CASCADE);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS remote_reference_boundaries (repository_id TEXT NOT NULL, branch_id TEXT NOT NULL, root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, root_directory_blake3_hash TEXT NOT NULL, event_cursor TEXT NOT NULL, PRIMARY KEY (repository_id, branch_id));"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS working_directory_update_completions (operation_value TEXT PRIMARY KEY, caller_kind TEXT NOT NULL CHECK (caller_kind IN ('Watch', 'Branch', 'Connect')), target_canonical TEXT NOT NULL, target_repository_id TEXT NOT NULL, target_branch_id TEXT NOT NULL, target_root_directory_version_id TEXT NOT NULL, target_root_directory_sha256_hash TEXT NOT NULL, target_root_directory_blake3_hash TEXT NOT NULL, branch_previous_branch_id TEXT NULL, branch_selection_kind TEXT NULL CHECK (branch_selection_kind IN ('Reference', 'DirectoryVersion')), branch_selected_reference_id TEXT NULL, watch_event_cursor TEXT NULL, finalization_state TEXT NOT NULL CHECK (finalization_state IN ('Pending', 'Terminal')), completed_at_unix_ticks INTEGER NOT NULL, CHECK ((caller_kind = 'Branch' AND branch_previous_branch_id IS NOT NULL AND ((branch_selection_kind = 'Reference' AND branch_selected_reference_id IS NOT NULL) OR (branch_selection_kind = 'DirectoryVersion' AND branch_selected_reference_id IS NULL)) AND watch_event_cursor IS NULL) OR (caller_kind = 'Watch' AND branch_previous_branch_id IS NULL AND branch_selection_kind IS NULL AND branch_selected_reference_id IS NULL AND watch_event_cursor IS NOT NULL) OR (caller_kind = 'Connect' AND branch_previous_branch_id IS NULL AND branch_selection_kind IS NULL AND branch_selected_reference_id IS NULL AND watch_event_cursor IS NULL)));"

        executeNonQuery
            connection
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_working_directory_update_completions_pending ON working_directory_update_completions(finalization_state) WHERE finalization_state = 'Pending';"

        executeNonQuery
            connection
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_working_directory_update_completions_terminal_caller ON working_directory_update_completions(caller_kind) WHERE finalization_state = 'Terminal';"

        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_status_files_directory_path ON status_files(directory_path);"
        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_status_files_directory_version_id ON status_files(directory_version_id);"
        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_status_files_sha256 ON status_files(sha256_hash);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS object_cache_directories (directory_version_id TEXT PRIMARY KEY, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_object_cache_directories_relative_path ON object_cache_directories(relative_path);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS object_cache_directory_children (parent_directory_version_id TEXT NOT NULL, child_directory_version_id TEXT NOT NULL, ordinal INTEGER NOT NULL, PRIMARY KEY (parent_directory_version_id, child_directory_version_id), FOREIGN KEY (parent_directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE CASCADE, FOREIGN KEY (child_directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE RESTRICT);"

        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_object_cache_children_parent ON object_cache_directory_children(parent_directory_version_id);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS object_cache_directory_files (directory_version_id TEXT NOT NULL, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, PRIMARY KEY (directory_version_id, relative_path), FOREIGN KEY (directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE CASCADE);"

        executeNonQuery connection "CREATE INDEX IF NOT EXISTS ix_object_cache_files_path_hash ON object_cache_directory_files(relative_path, sha256_hash);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS watch_journal (sequence INTEGER PRIMARY KEY AUTOINCREMENT, created_at_unix_ticks INTEGER NOT NULL, repository_id TEXT, branch_id TEXT, workspace_root TEXT, watch_root TEXT, root_directory_version_id TEXT, root_directory_sha256_hash TEXT, root_directory_blake3_hash TEXT, watch_mode TEXT, difference_type TEXT NOT NULL, entry_type TEXT NOT NULL, relative_path TEXT NOT NULL, quarantined_at_unix_ticks INTEGER, quarantine_reason TEXT);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS watch_lifecycle_events (sequence INTEGER PRIMARY KEY AUTOINCREMENT, created_at_unix_ticks INTEGER NOT NULL, repository_id TEXT, branch_id TEXT, workspace_root TEXT, watch_root TEXT, root_directory_version_id TEXT, root_directory_sha256_hash TEXT, root_directory_blake3_hash TEXT, watch_mode TEXT, event_type TEXT NOT NULL, message TEXT NOT NULL, replayable INTEGER NOT NULL CHECK (replayable = 0));"

        executeNonQuery connection $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '{LocalStateDb.SchemaVersion}');"
        executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

        executeNonQuery
            connection
            $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', '{rootSha256Hash}', '{rootBlake3Hash}', {ticks}, {ticks});"

    /// Seeds an unrelated object-cache row so journal-only resets prove they do not recreate local state.
    let private seedObjectCacheDirectory (connection: SqliteConnection) directoryVersionId relativePath sha256Hash blake3Hash =
        executeNonQuery
            connection
            $"INSERT OR REPLACE INTO object_cache_directories (directory_version_id, relative_path, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ('{directoryVersionId}', '{relativePath}', '{sha256Hash}', '{blake3Hash}', 10, 11, 12);"

    /// Builds seed partial v4 without root blake3 column test data used to exercise CLI local State Db behavior.
    let private seedPartialV4WithoutRootBlake3Column (dbPath: string) (rootId: Guid) rootSha256Hash ticks =
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
        |> ignore

        use connection = openRawConnection dbPath
        executeNonQuery connection "PRAGMA journal_mode = WAL;"
        executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"

        executeNonQuery
            connection
            "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"

        executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '4');"

        executeNonQuery
            connection
            $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', '{rootSha256Hash}', {ticks}, {ticks});"

    /// Builds a deterministic test status for test scenarios fixture for the CLI local State Db assertions.
    let private createTestStatus (rootId: Guid) (rootHash: string) (ticks: int64) =
        { GraceStatus.Default with
            RootDirectoryId = rootId
            RootDirectorySha256Hash = rootHash
            LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(ticks)
            LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(ticks)
        }

    /// Requires a successful private Working Directory Update contract construction in local-state tests.
    let private requiredWorkingDirectoryUpdate value =
        match value with
        | Ok result -> result
        | Error error -> failwith error

    /// Builds a status snapshot whose root exactly matches the Working Directory Update target.
    let private completionStatus (configuration: GraceConfiguration) rootId sha256Hash blake3Hash ticks =
        let lastWrite = DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)

        let rootDirectory =
            LocalDirectoryVersion.CreateWithHashes
                rootId
                configuration.OwnerId
                configuration.OrganizationId
                configuration.RepositoryId
                Constants.RootDirectoryPath
                sha256Hash
                blake3Hash
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>())
                0L
                lastWrite

        let index = GraceIndex()
        index.TryAdd(rootId, rootDirectory) |> ignore

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = rootId
            RootDirectorySha256Hash = sha256Hash
            RootDirectoryBlake3Hash = blake3Hash
            LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(ticks)
            LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(ticks)
        },
        rootDirectory

    /// Builds a complete Branch operation, exact target, and bounded finalization details for local completion tests.
    let private completionTargetAndOperation (configuration: GraceConfiguration) rootId sha256Hash blake3Hash =
        let target =
            WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
            |> requiredWorkingDirectoryUpdate

        let previousBranchId = Guid.NewGuid()
        let selectedReferenceId = Guid.NewGuid()

        let operation =
            WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId target
            |> requiredWorkingDirectoryUpdate

        let completionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, selectedReferenceId)

        target, operation, completionDetails

    /// Exercises private behavior.
    type private WorkerCommand = { FileName: string; ArgumentsPrefix: string }

    /// Attempts get worker command for test assertions.
    let private tryGetWorkerCommand () =
        try
            let baseDir = AppContext.BaseDirectory
            let tfm = DirectoryInfo(baseDir).Name
            let config = DirectoryInfo(baseDir).Parent.Name

            /// Tracks current changes so this scenario can assert the resulting side effect explicitly.
            let mutable current = DirectoryInfo(baseDir)
            /// Tracks src Dir changes so this scenario can assert the resulting side effect explicitly.
            let mutable srcDir = Unchecked.defaultof<DirectoryInfo>
            /// Tracks found changes so this scenario can assert the resulting side effect explicitly.
            let mutable found = false

            while (not found) && (not <| isNull current) do
                if current.Name.Equals("src", StringComparison.OrdinalIgnoreCase) then
                    srcDir <- current
                    found <- true
                else
                    current <- current.Parent

            if not found then
                None
            else
                let workerBinDir = Path.Combine(srcDir.FullName, "Grace.CLI.LocalStateDb.Worker", "bin", config, tfm)

                let exePath = Path.Combine(workerBinDir, "Grace.CLI.LocalStateDb.Worker.exe")
                let dllPath = Path.Combine(workerBinDir, "Grace.CLI.LocalStateDb.Worker.dll")

                if File.Exists(exePath) then
                    Some { FileName = exePath; ArgumentsPrefix = String.Empty }
                elif File.Exists(dllPath) then
                    Some { FileName = "dotnet"; ArgumentsPrefix = $"\"{dllPath}\"" }
                else
                    None
        with
        | _ -> None

    /// Verifies that initializes schema and status meta.
    [<Test>]
    let ``initializes schema and status meta`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = new SqliteConnection($"Data Source={configuration.GraceStatusFile}")
                connection.Open()

                use cmd = connection.CreateCommand()
                cmd.CommandText <- "SELECT value FROM meta WHERE key = 'schema_version';"
                let schemaVersion = cmd.ExecuteScalar() :?> string

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                cmd.CommandText <- "SELECT COUNT(*) FROM status_meta;"
                let statusMetaCount = Convert.ToInt32(cmd.ExecuteScalar())
                statusMetaCount |> should equal 1
            })

    /// Successful Connect persistence commits status and its matching branch cursor together.
    [<Test>]
    let ``status snapshot and remote reference boundary commit atomically`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.Parse("11111111-8020-4000-8000-111111111111")
                let rootHash = Sha256Hash "root-sha"
                let rootBlake3 = Blake3Hash "root-sha-blake3"
                let root = createDirectoryVersion configuration rootId Constants.RootDirectoryPath rootHash Array.empty Array.empty 0L DateTime.UtcNow
                let index = GraceIndex()
                index[rootId] <- root

                let status = { createTestStatus rootId rootHash 123L with RootDirectoryBlake3Hash = rootBlake3; Index = index }

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = rootId
                        Sha256Hash = rootHash
                        Blake3Hash = rootBlake3
                        EventCursor = "branch-event-v1:7"
                    }

                let! _ = LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundary configuration.GraceStatusFile status boundary CancellationToken.None
                let! stored = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId
                let! storedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                Assert.That(stored, Is.EqualTo(Some boundary))
                Assert.That(storedStatus.RootDirectoryId, Is.EqualTo(rootId))
                Assert.That(storedStatus.RootDirectorySha256Hash, Is.EqualTo(rootHash))
                Assert.That(storedStatus.RootDirectoryBlake3Hash, Is.EqualTo(rootBlake3))
            })

    /// Missing-cursor recovery inserts a boundary only while the complete materialized status root remains current.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``missing remote reference boundary is established by exact local root CAS`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.Parse("aaaaaaaa-8040-4000-8000-aaaaaaaaaaaa")
                let rootHash = Sha256Hash "local-root-sha"
                let root = createDirectoryVersion configuration rootId Constants.RootDirectoryPath rootHash Array.empty Array.empty 0L DateTime.UtcNow
                let rootBlake3 = root.Blake3Hash
                let index = GraceIndex()
                index[rootId] <- root

                let status = { createTestStatus rootId rootHash 123L with RootDirectoryBlake3Hash = rootBlake3; Index = index }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = rootId
                        Sha256Hash = rootHash
                        Blake3Hash = rootBlake3
                        EventCursor = "branch-event-v1:4"
                    }

                let! stored = LocalStateDb.establishRemoteReferenceBoundaryIfAbsent configuration.GraceStatusFile status boundary CancellationToken.None

                let! durable = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                Assert.That(stored, Is.EqualTo(boundary))
                Assert.That(durable, Is.EqualTo(Some boundary))
            })

    /// Cancellation at the missing-boundary commit seam rolls back the inserted cursor without changing local status.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``missing remote reference boundary cancellation rolls back the insert`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.Parse("dddddddd-8040-4000-8000-dddddddddddd")
                let rootHash = Sha256Hash "cancelled-root-sha"
                let root = createDirectoryVersion configuration rootId Constants.RootDirectoryPath rootHash Array.empty Array.empty 0L DateTime.UtcNow
                let index = GraceIndex()
                index[rootId] <- root

                let status = { createTestStatus rootId rootHash 123L with RootDirectoryBlake3Hash = root.Blake3Hash; Index = index }

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = rootId
                        Sha256Hash = rootHash
                        Blake3Hash = root.Blake3Hash
                        EventCursor = "branch-event-v1:5"
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status
                use cancellation = new CancellationTokenSource()

                Assert.ThrowsAsync<OperationCanceledException>(
                    Func<Task> (fun () ->
                        LocalStateDb.establishRemoteReferenceBoundaryIfAbsentWithBeforeCommit
                            configuration.GraceStatusFile
                            status
                            boundary
                            cancellation.Token
                            cancellation.Cancel
                        :> Task)
                )
                |> ignore

                let! durable = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId
                let! durableStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                Assert.That(durable, Is.EqualTo(None))
                Assert.That(durableStatus.RootDirectoryId, Is.EqualTo(rootId))
                Assert.That(durableStatus.RootDirectorySha256Hash, Is.EqualTo(rootHash))
                Assert.That(durableStatus.RootDirectoryBlake3Hash, Is.EqualTo(root.Blake3Hash))
            })

    /// A changed, recreated, or already-bounded database cannot accept a stale missing-cursor decision.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``missing remote reference boundary rejects stale local database state`` () =
        withTempDir (fun _ configuration ->
            task {
                let expectedRootId = Guid.Parse("bbbbbbbb-8040-4000-8000-bbbbbbbbbbbb")
                let expectedHash = Sha256Hash "expected-root-sha"

                let expectedRoot =
                    createDirectoryVersion configuration expectedRootId Constants.RootDirectoryPath expectedHash Array.empty Array.empty 0L DateTime.UtcNow

                let expectedBlake3 = expectedRoot.Blake3Hash

                let expectedIndex = GraceIndex()
                expectedIndex[expectedRootId] <- expectedRoot

                let expectedStatus = { createTestStatus expectedRootId expectedHash 123L with RootDirectoryBlake3Hash = expectedBlake3; Index = expectedIndex }

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = expectedRootId
                        Sha256Hash = expectedHash
                        Blake3Hash = expectedBlake3
                        EventCursor = "branch-event-v1:5"
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile expectedStatus
                SqliteConnection.ClearAllPools()
                File.Delete(configuration.GraceStatusFile)
                File.Delete(configuration.GraceStatusFile + "-wal")
                File.Delete(configuration.GraceStatusFile + "-shm")

                let mutable staleDecisionRejected = false

                try
                    let! _ = LocalStateDb.establishRemoteReferenceBoundaryIfAbsent configuration.GraceStatusFile expectedStatus boundary CancellationToken.None

                    ()
                with
                | _ -> staleDecisionRejected <- true

                Assert.That(staleDecisionRejected, Is.True)

                use recreatedConnection = openRawConnection configuration.GraceStatusFile

                let boundaryTableCount =
                    executeScalarInt recreatedConnection "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'remote_reference_boundaries';"

                let boundaryRowCount = executeScalarInt recreatedConnection "SELECT COUNT(*) FROM remote_reference_boundaries;"

                Assert.That(boundaryTableCount, Is.EqualTo(1))
                Assert.That(boundaryRowCount, Is.EqualTo(0))
            })

    /// A concurrent or preexisting boundary prevents missing-cursor recovery from overwriting cursor authority.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``missing remote reference boundary rejects an existing boundary`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.Parse("cccccccc-8040-4000-8000-cccccccccccc")
                let rootHash = Sha256Hash "existing-root-sha"
                let root = createDirectoryVersion configuration rootId Constants.RootDirectoryPath rootHash Array.empty Array.empty 0L DateTime.UtcNow
                let index = GraceIndex()
                index[rootId] <- root

                let status = { createTestStatus rootId rootHash 123L with RootDirectoryBlake3Hash = root.Blake3Hash; Index = index }

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = rootId
                        Sha256Hash = rootHash
                        Blake3Hash = root.Blake3Hash
                        EventCursor = "branch-event-v1:5"
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                let! _ = LocalStateDb.establishRemoteReferenceBoundaryIfAbsent configuration.GraceStatusFile status boundary CancellationToken.None

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        LocalStateDb.establishRemoteReferenceBoundaryIfAbsent
                            configuration.GraceStatusFile
                            status
                            { boundary with EventCursor = "branch-event-v1:6" }
                            CancellationToken.None
                        :> Task)
                )
                |> ignore
            })

    /// A mismatched cursor is rejected before it can replace the previously committed status snapshot.
    [<Test>]
    let ``mismatched remote reference boundary leaves status and cursor unchanged`` () =
        withTempDir (fun _ configuration ->
            task {
                let originalRoot = Guid.Parse("22222222-8020-4000-8000-222222222222")
                let originalStatus = createTestStatus originalRoot (Sha256Hash "original") 10L
                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile originalStatus

                let replacementRoot = Guid.Parse("33333333-8020-4000-8000-333333333333")
                let replacementStatus = createTestStatus replacementRoot (Sha256Hash "replacement") 20L

                let mismatched =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = originalRoot
                        Sha256Hash = Sha256Hash "original"
                        Blake3Hash = Blake3Hash String.Empty
                        EventCursor = "branch-event-v1:1"
                    }

                Assert.ThrowsAsync<ArgumentException>(
                    Func<Task> (fun () ->
                        LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundary
                            configuration.GraceStatusFile
                            replacementStatus
                            mismatched
                            CancellationToken.None
                        :> Task)
                )
                |> ignore

                let! storedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                let! storedBoundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId
                Assert.That(storedStatus.RootDirectoryId, Is.EqualTo(originalRoot))
                Assert.That(storedBoundary, Is.EqualTo(None))
            })

    /// A cursor persistence failure rolls back the matching status replacement instead of acknowledging a false boundary.
    [<Test>]
    let ``remote reference boundary write failure rolls back status replacement`` () =
        withTempDir (fun _ configuration ->
            task {
                let originalRoot = Guid.Parse("44444444-8020-4000-8000-444444444444")
                let originalStatus = createTestStatus originalRoot (Sha256Hash "original") 10L
                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile originalStatus

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery
                        connection
                        "CREATE TRIGGER reject_remote_reference_boundary BEFORE INSERT ON remote_reference_boundaries BEGIN SELECT RAISE(ABORT, 'forced boundary failure'); END;"

                let replacementRoot = Guid.Parse("55555555-8020-4000-8000-555555555555")
                let replacementBlake3 = Blake3Hash "replacement-blake3"

                let replacementStatus = { createTestStatus replacementRoot (Sha256Hash "replacement") 20L with RootDirectoryBlake3Hash = replacementBlake3 }

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = replacementRoot
                        Sha256Hash = replacementStatus.RootDirectorySha256Hash
                        Blake3Hash = replacementBlake3
                        EventCursor = "branch-event-v1:2"
                    }

                Assert.ThrowsAsync<SqliteException>(
                    Func<Task> (fun () ->
                        LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundary
                            configuration.GraceStatusFile
                            replacementStatus
                            boundary
                            CancellationToken.None
                        :> Task)
                )
                |> ignore

                let! storedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                let! storedBoundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId
                Assert.That(storedStatus.RootDirectoryId, Is.EqualTo(originalRoot))
                Assert.That(storedBoundary, Is.EqualTo(None))
            })

    /// Cancellation after status and boundary staging rolls the entire SQLite transaction back before durable acceptance.
    [<Test>]
    let ``remote reference boundary cancellation before commit rolls back status replacement`` () =
        withTempDir (fun _ configuration ->
            task {
                let originalRoot = Guid.Parse("66666666-8020-4000-8000-666666666666")
                let originalStatus = createTestStatus originalRoot (Sha256Hash "original") 10L
                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile originalStatus

                let replacementRoot = Guid.Parse("77777777-8020-4000-8000-777777777777")
                let replacementBlake3 = Blake3Hash "replacement-blake3"

                let replacementStatus = { createTestStatus replacementRoot (Sha256Hash "replacement") 20L with RootDirectoryBlake3Hash = replacementBlake3 }

                let boundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = replacementRoot
                        Sha256Hash = replacementStatus.RootDirectorySha256Hash
                        Blake3Hash = replacementBlake3
                        EventCursor = "branch-event-v1:3"
                    }

                use cancellation = new CancellationTokenSource()
                let mutable cancelled = false

                try
                    let! _ =
                        LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundaryWithBeforeCommit
                            configuration.GraceStatusFile
                            replacementStatus
                            boundary
                            cancellation.Token
                            cancellation.Cancel

                    ()
                with
                | :? OperationCanceledException -> cancelled <- true

                let! storedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                let! storedBoundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                Assert.That(cancelled, Is.True)
                Assert.That(storedStatus.RootDirectoryId, Is.EqualTo(originalRoot))
                Assert.That(storedBoundary, Is.EqualTo(None))
            })

    /// A writer that claims SQLite after Doctor's final validation remains authoritative and makes repair fail closed.
    [<Test; Category("LocalStateRepairWriterExclusion")>]
    let ``local state repair refuses writer claiming database before replacement`` () =
        withTempDir (fun _ configuration ->
            task {
                let writerRoot = Guid.Parse("67676767-8020-4000-8000-676767676767")
                let writerStatus = { createTestStatus writerRoot (Sha256Hash "writer") 11L with RootDirectoryBlake3Hash = Blake3Hash "writer-blake3" }

                let writerBoundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = writerRoot
                        Sha256Hash = writerStatus.RootDirectorySha256Hash
                        Blake3Hash = writerStatus.RootDirectoryBlake3Hash
                        EventCursor = "writer-boundary"
                    }

                let doctorRoot = Guid.Parse("68686868-8020-4000-8000-686868686868")
                let doctorStatus = { createTestStatus doctorRoot (Sha256Hash "doctor") 12L with RootDirectoryBlake3Hash = Blake3Hash "doctor-blake3" }

                let doctorBoundary =
                    { writerBoundary with
                        DirectoryId = doctorRoot
                        Sha256Hash = doctorStatus.RootDirectorySha256Hash
                        Blake3Hash = doctorStatus.RootDirectoryBlake3Hash
                        EventCursor = "doctor-boundary"
                    }

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                let! baseline = LocalStateDb.captureLocalStateRepairBaseline configuration.GraceStatusFile
                use writer = openRawConnection configuration.GraceStatusFile

                let beginAndStageWriter () =
                    executeNonQuery writer "BEGIN IMMEDIATE;"
                    executeNonQuery writer "DELETE FROM status_directories;"
                    executeNonQuery writer "DELETE FROM status_files;"

                    executeNonQuery
                        writer
                        $"UPDATE status_meta SET root_directory_version_id = '{writerRoot}', root_directory_sha256_hash = 'writer', root_directory_blake3_hash = 'writer-blake3' WHERE id = 1;"

                    executeNonQuery
                        writer
                        $"INSERT INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ('.', '', '{writerRoot}', 'writer', 'writer-blake3', 0, 0, 0);"

                    executeNonQuery
                        writer
                        $"INSERT OR REPLACE INTO remote_reference_boundaries (repository_id, branch_id, root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, event_cursor) VALUES ('{configuration.RepositoryId}', '{configuration.BranchId}', '{writerRoot}', 'writer', 'writer-blake3', 'writer-boundary');"

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundaryForLocalStateRepairWithBeforeWriteClaim
                            configuration.GraceStatusFile
                            baseline
                            doctorStatus
                            doctorBoundary
                            CancellationToken.None
                            beginAndStageWriter
                        :> Task)
                )
                |> ignore

                executeNonQuery writer "COMMIT;"

                let! storedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                let! storedBoundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId
                Assert.That(storedStatus.RootDirectoryId, Is.EqualTo(writerStatus.RootDirectoryId))
                Assert.That(storedBoundary, Is.EqualTo(Some writerBoundary))
            })

    /// A terminal replay acknowledgement advances the exact cursor and accepted root without rewriting status tables.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``remote reference cursor acknowledgement advances exact boundary`` () =
        withTempDir (fun _ configuration ->
            task {
                let originalRoot = Guid.Parse("88888888-8030-4000-8000-888888888888")
                let originalStatus = { createTestStatus originalRoot (Sha256Hash "original") 10L with RootDirectoryBlake3Hash = Blake3Hash "original-blake3" }

                let originalBoundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = originalRoot
                        Sha256Hash = originalStatus.RootDirectorySha256Hash
                        Blake3Hash = originalStatus.RootDirectoryBlake3Hash
                        EventCursor = "opaque-original"
                    }

                let! _ =
                    LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundary
                        configuration.GraceStatusFile
                        originalStatus
                        originalBoundary
                        CancellationToken.None

                let acceptedBoundary =
                    { originalBoundary with
                        DirectoryId = Guid.Parse("99999999-8030-4000-8000-999999999999")
                        Sha256Hash = Sha256Hash "accepted"
                        Blake3Hash = Blake3Hash "accepted-blake3"
                        EventCursor = "opaque-accepted"
                    }

                let! advanced =
                    LocalStateDb.advanceRemoteReferenceBoundaryCursor configuration.GraceStatusFile originalBoundary acceptedBoundary CancellationToken.None

                let! stored = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                let! storedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                Assert.That(advanced, Is.EqualTo(acceptedBoundary))
                Assert.That(stored, Is.EqualTo(Some acceptedBoundary))
                Assert.That(storedStatus.RootDirectoryId, Is.EqualTo(originalRoot))
            })

    /// A stale response interval cannot overwrite a cursor already acknowledged by another completion.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``stale remote reference cursor acknowledgement leaves newer boundary unchanged`` () =
        withTempDir (fun _ configuration ->
            task {
                let root = Guid.Parse("aaaaaaaa-8030-4000-8000-aaaaaaaaaaaa")
                let status = { createTestStatus root (Sha256Hash "root") 10L with RootDirectoryBlake3Hash = Blake3Hash "root-blake3" }

                let originalBoundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = root
                        Sha256Hash = status.RootDirectorySha256Hash
                        Blake3Hash = status.RootDirectoryBlake3Hash
                        EventCursor = "opaque-1"
                    }

                let! _ =
                    LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundary configuration.GraceStatusFile status originalBoundary CancellationToken.None

                let newerBoundary = { originalBoundary with EventCursor = "opaque-2" }

                let! _ = LocalStateDb.advanceRemoteReferenceBoundaryCursor configuration.GraceStatusFile originalBoundary newerBoundary CancellationToken.None

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        LocalStateDb.advanceRemoteReferenceBoundaryCursor
                            configuration.GraceStatusFile
                            originalBoundary
                            { originalBoundary with EventCursor = "opaque-stale" }
                            CancellationToken.None
                        :> Task)
                )
                |> ignore

                let! stored = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                Assert.That(stored, Is.EqualTo(Some newerBoundary))
            })

    /// Cancellation or a SQLite failure before commit leaves the prior opaque cursor replayable.
    [<Test; Category("CurrentBranchCursorReplay")>]
    let ``remote reference cursor failure leaves prior boundary replayable`` () =
        withTempDir (fun _ configuration ->
            task {
                let root = Guid.Parse("bbbbbbbb-8030-4000-8000-bbbbbbbbbbbb")
                let status = { createTestStatus root (Sha256Hash "root") 10L with RootDirectoryBlake3Hash = Blake3Hash "root-blake3" }

                let originalBoundary =
                    { Grace.Types.Reference.ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = root
                        Sha256Hash = status.RootDirectorySha256Hash
                        Blake3Hash = status.RootDirectoryBlake3Hash
                        EventCursor = "opaque-before"
                    }

                let! _ =
                    LocalStateDb.replaceStatusSnapshotWithRemoteReferenceBoundary configuration.GraceStatusFile status originalBoundary CancellationToken.None

                use cancellation = new CancellationTokenSource()

                Assert.ThrowsAsync<OperationCanceledException>(
                    Func<Task> (fun () ->
                        LocalStateDb.advanceRemoteReferenceBoundaryCursorWithBeforeCommit
                            configuration.GraceStatusFile
                            originalBoundary
                            { originalBoundary with EventCursor = "opaque-cancelled" }
                            cancellation.Token
                            cancellation.Cancel
                        :> Task)
                )
                |> ignore

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery
                        connection
                        "CREATE TRIGGER reject_remote_reference_cursor BEFORE UPDATE ON remote_reference_boundaries BEGIN SELECT RAISE(ABORT, 'forced cursor failure'); END;"

                Assert.ThrowsAsync<SqliteException>(
                    Func<Task> (fun () ->
                        LocalStateDb.advanceRemoteReferenceBoundaryCursor
                            configuration.GraceStatusFile
                            originalBoundary
                            { originalBoundary with EventCursor = "opaque-failed" }
                            CancellationToken.None
                        :> Task)
                )
                |> ignore

                let! stored = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                Assert.That(stored, Is.EqualTo(Some originalBoundary))
            })

    /// Verifies that initializes watch journal schema and applied through metadata.
    [<Test>]
    let ``initializes watch journal schema and applied through metadata`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                let journalTableCount = executeScalarInt connection "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal';"

                journalTableCount |> should equal 1

                let sequencePk = executeScalarInt connection "SELECT pk FROM pragma_table_info('watch_journal') WHERE name = 'sequence';"

                let sequenceType = executeScalarString connection "SELECT UPPER(type) FROM pragma_table_info('watch_journal') WHERE name = 'sequence';"

                sequencePk |> should equal 1
                sequenceType |> should equal "INTEGER"

                let tableSql = executeScalarString connection "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal';"

                tableSql.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                |> should be (greaterThanOrEqualTo 0)

                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"

                appliedThrough |> should equal "0"

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 0L
            })

    /// Verifies that v5 initialization recreates meta tables that ignore the default Watch watermark insert.
    [<Test>]
    let ``initialization recreates constrained meta table when default watch watermark insert is ignored`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = getCurrentInstant().ToUnixTimeTicks()
                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile (Guid.NewGuid()) "root-sha" "root-blake3" now

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "ALTER TABLE meta RENAME TO meta_valid;"

                    executeNonQuery
                        connection
                        "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL, required_marker TEXT NOT NULL CHECK (required_marker = 'seeded'));"

                    executeNonQuery
                        connection
                        "INSERT INTO meta (key, value, required_marker) SELECT key, value, 'seeded' FROM meta_valid WHERE key <> 'AppliedThroughSequence';"

                    executeNonQuery connection "DROP TABLE meta_valid;"

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                do! LocalStateDb.setWatchJournalAppliedThroughSequence configuration.GraceStatusFile 0L

                use connection = openRawConnection configuration.GraceStatusFile

                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                appliedThrough |> should equal "0"

                let requiredMarkerColumns = executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('meta') WHERE name = 'required_marker';"
                requiredMarkerColumns |> should equal 0

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 0L

                getCorruptBackups configuration.GraceStatusFile
                |> should haveLength 1
            })

    /// Verifies that watch journal retention keeps unapplied rows and a bounded applied tail.
    [<Test>]
    let ``watch journal retention keeps unapplied rows and bounded applied tail`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                insertWatchJournalRows connection 1030L

                do! LocalStateDb.setWatchJournalAppliedThroughSequence configuration.GraceStatusFile 1026L
                do! LocalStateDb.pruneWatchJournalRetention configuration.GraceStatusFile

                let minSequence = executeScalarInt64 connection "SELECT MIN(sequence) FROM watch_journal;"
                let maxSequence = executeScalarInt64 connection "SELECT MAX(sequence) FROM watch_journal;"
                let rowCount = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                minSequence |> should equal 3L
                maxSequence |> should equal 1030L
                rowCount |> should equal 1028

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 1026L
            })

    /// Verifies that Watch recovery metadata cannot be moved behind the current applied sequence.
    [<Test>]
    let ``watch journal applied through sequence cannot rewind`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 5L

                do! LocalStateDb.setWatchJournalAppliedThroughSequence configuration.GraceStatusFile 5L

                let operation = Func<Task>(fun () -> LocalStateDb.setWatchJournalAppliedThroughSequence configuration.GraceStatusFile 4L :> Task)

                let ex = Assert.ThrowsAsync<InvalidOperationException>(operation)

                ex.Message
                |> should contain "cannot move backward"

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 5L
            })

    /// Verifies that Watch recovery metadata cannot outrun SQLite's allocated journal sequence.
    [<Test>]
    let ``watch journal applied through sequence cannot exceed allocated sequence`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let operation = Func<Task>(fun () -> LocalStateDb.setWatchJournalAppliedThroughSequence configuration.GraceStatusFile 1L :> Task)

                let ex = Assert.ThrowsAsync<InvalidOperationException>(operation)

                ex.Message
                |> should contain "only allocated through 0"

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 0L
            })

    /// Verifies that the explicit maintenance reset clears only Watch journal state.
    [<Test>]
    let ``clear watch journal resets only journal rows allocation and watermark`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let rootId = Guid.NewGuid()
                let rootHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                let status = createTestStatus rootId rootHash 123L
                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 3L
                    executeNonQuery connection "UPDATE meta SET value = '2' WHERE key = 'AppliedThroughSequence';"

                let! result = LocalStateDb.clearWatchJournal configuration.GraceStatusFile

                result.RowsDeleted |> should equal 3L

                result.AppliedThroughSequenceBefore
                |> should equal 2L

                result.AppliedThroughSequenceAfter
                |> should equal 0L

                result.AllocatedSequenceBefore |> should equal 3L
                result.AllocatedSequenceAfter |> should equal 0L

                use connection = openRawConnection configuration.GraceStatusFile
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"
                let allocationRows = executeScalarInt connection "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'watch_journal';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let statusMetaRows = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"

                journalRows |> should equal 0
                allocationRows |> should equal 0
                appliedThrough |> should equal "0"
                statusMetaRows |> should equal 1

                let! statusAfter = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                statusAfter.RootDirectoryId |> should equal rootId

                statusAfter.RootDirectorySha256Hash
                |> should equal rootHash
            })

    /// Verifies that clear-journal treats malformed SQLite allocation metadata as resettable journal-only state.
    [<Test>]
    let ``clear watch journal clears malformed allocation metadata without recreating local state`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let objectCacheId = Guid.NewGuid()
                let rootHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                let rootBlake3Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                let objectHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                let objectBlake3Hash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash rootBlake3Hash 123L

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 2L
                    seedObjectCacheDirectory connection objectCacheId "cache" objectHash objectBlake3Hash
                    executeNonQuery connection "UPDATE meta SET value = '1' WHERE key = 'AppliedThroughSequence';"
                    executeNonQuery connection "UPDATE sqlite_sequence SET seq = 'not-a-number' WHERE name = 'watch_journal';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                let! result = LocalStateDb.clearWatchJournal configuration.GraceStatusFile

                result.RowsDeleted |> should equal 2L

                result.AppliedThroughSequenceBefore
                |> should equal 1L

                result.AllocatedSequenceBefore |> should equal 0L
                result.AllocatedSequenceAfter |> should equal 0L

                use connection = openRawConnection configuration.GraceStatusFile
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"
                let allocationRows = executeScalarInt connection "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'watch_journal';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let statusRootId = executeScalarString connection "SELECT root_directory_version_id FROM status_meta WHERE id = 1;"
                let objectCacheRows = executeScalarInt connection "SELECT COUNT(*) FROM object_cache_directories;"

                journalRows |> should equal 0
                allocationRows |> should equal 0
                appliedThrough |> should equal "0"
                statusRootId |> should equal (string rootId)
                objectCacheRows |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal corruptBefore
            })

    /// Verifies that empty clear-journal does not create a partial local-state database.
    [<Test>]
    let ``clear watch journal no-ops without creating a missing local state database`` () =
        withTempDir (fun _ configuration ->
            task {
                File.Exists(configuration.GraceStatusFile)
                |> should equal false

                let! result = LocalStateDb.clearWatchJournal configuration.GraceStatusFile

                result.RowsDeleted |> should equal 0L

                result.AppliedThroughSequenceBefore
                |> should equal 0L

                result.AppliedThroughSequenceAfter
                |> should equal 0L

                result.AllocatedSequenceBefore |> should equal 0L
                result.AllocatedSequenceAfter |> should equal 0L

                File.Exists(configuration.GraceStatusFile)
                |> should equal false
            })

    /// Verifies that journal-only clear repairs untrusted Watch metadata without recreating unrelated local state.
    [<TestCase("missing")>]
    [<TestCase("malformed")>]
    let ``clear watch journal repairs journal metadata without recreating status or object cache`` metadataCase =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let objectCacheId = Guid.NewGuid()
                let rootHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                let rootBlake3Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                let objectHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                let objectBlake3Hash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash rootBlake3Hash 123L

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 2L
                    seedObjectCacheDirectory connection objectCacheId "cache" objectHash objectBlake3Hash

                    match metadataCase with
                    | "missing" -> executeNonQuery connection "DELETE FROM meta WHERE key = 'AppliedThroughSequence';"
                    | "malformed" -> executeNonQuery connection "UPDATE meta SET value = 'not-a-sequence' WHERE key = 'AppliedThroughSequence';"
                    | value -> failwith $"Unsupported metadata case: {value}"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                let! result = LocalStateDb.clearWatchJournal configuration.GraceStatusFile

                result.RowsDeleted |> should equal 2L

                result.AppliedThroughSequenceAfter
                |> should equal 0L

                result.AllocatedSequenceAfter |> should equal 0L

                use connection = openRawConnection configuration.GraceStatusFile
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"
                let allocationRows = executeScalarInt connection "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'watch_journal';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let statusRootId = executeScalarString connection "SELECT root_directory_version_id FROM status_meta WHERE id = 1;"
                let objectCacheRows = executeScalarInt connection "SELECT COUNT(*) FROM object_cache_directories;"

                journalRows |> should equal 0
                allocationRows |> should equal 0
                appliedThrough |> should equal "0"
                statusRootId |> should equal (string rootId)
                objectCacheRows |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal corruptBefore
            })

    /// Verifies that concurrent Watch recovery watermark advances cannot let a lower stale write win.
    [<Test>]
    let ``watch journal applied through sequence is atomic under interleaved advances`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                do
                    use seedConnection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows seedConnection 4L

                use lockConnection = openRawConnection configuration.GraceStatusFile
                executeNonQuery lockConnection "BEGIN IMMEDIATE;"

                let runAdvance sequence =
                    task {
                        try
                            do! LocalStateDb.setWatchJournalAppliedThroughSequence configuration.GraceStatusFile sequence
                            return Some sequence
                        with
                        | :? InvalidOperationException as ex when ex.Message.Contains("cannot move backward", StringComparison.OrdinalIgnoreCase) -> return None
                    }

                let startAdvance sequence =
                    Task
                        .Factory
                        .StartNew(Func<Task<int64 option>>(fun () -> runAdvance sequence))
                        .Unwrap()

                let highTask = startAdvance 4L
                do! Task.Delay(50)

                let lowerTasks = [| 1L .. 3L |] |> Array.map startAdvance

                do! Task.Delay(100)
                executeNonQuery lockConnection "ROLLBACK;"

                let! highResult = highTask
                let! lowerResults = Task.WhenAll lowerTasks

                highResult |> should equal (Some 4L)

                lowerResults
                |> Array.choose id
                |> Array.iter (fun sequence -> sequence |> should be (lessThanOrEqualTo 4L))

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 4L
            })

    /// Verifies that applied boundary advancement cannot skip lower pending journal rows.
    [<Test>]
    let ``watch journal contiguous advance does not skip pending lower sequence`` () =
        withTempDir (fun _ configuration ->
            task {
                let firstObservation = watchJournalObservation DifferenceType.Delete FileSystemEntryType.File "first.txt"
                let secondObservation = watchJournalObservation DifferenceType.Change FileSystemEntryType.File "second.txt"

                let! firstSequences = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile [ firstObservation ]
                let! secondSequences = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile [ secondObservation ]

                firstSequences |> should equal [| 1L |]
                secondSequences |> should equal [| 2L |]

                let! skippedAdvance = LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences configuration.GraceStatusFile secondSequences
                skippedAdvance |> should equal 0L

                let! afterSkippedAdvance = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                afterSkippedAdvance |> should equal 0L

                let! firstAdvance = LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences configuration.GraceStatusFile firstSequences
                firstAdvance |> should equal 1L

                let! secondAdvance = LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences configuration.GraceStatusFile secondSequences
                secondAdvance |> should equal 2L

                let! snapshot = LocalStateDb.readWatchJournalSnapshot configuration.GraceStatusFile "applied" None 10

                snapshot.Rows
                |> Array.map (fun row -> row.Sequence, row.DifferenceType, row.EntryType, row.RelativePath)
                |> should
                    equal
                    [|
                        2L, "Change", "File", Some "second.txt"
                        1L, "Delete", "File", Some "first.txt"
                    |]
            })

    /// Verifies that pending-work summaries report only unresolved non-quarantined durable journal rows.
    [<Test>]
    let ``watch journal pending work summary ignores applied and quarantined rows`` () =
        withTempDir (fun _ configuration ->
            task {
                let appliedObservation = watchJournalObservation DifferenceType.Change FileSystemEntryType.File "applied.txt"
                let pendingObservation = watchJournalObservation DifferenceType.Change FileSystemEntryType.File "pending.txt"
                let quarantinedObservation = watchJournalObservation DifferenceType.Delete FileSystemEntryType.File "quarantined.txt"

                let! appliedSequences = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile [ appliedObservation ]
                let! pendingSequences = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile [ pendingObservation ]
                let! quarantinedSequences = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile [ quarantinedObservation ]

                let! appliedThrough = LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences configuration.GraceStatusFile appliedSequences
                appliedThrough |> should equal 1L

                let! quarantineBoundary =
                    LocalStateDb.quarantineWatchJournalSequences configuration.GraceStatusFile quarantinedSequences "test quarantined stale identity"

                quarantineBoundary |> should equal 1L

                let! summary = LocalStateDb.readWatchJournalPendingWorkSummary configuration.GraceStatusFile

                summary.AppliedThroughSequence |> should equal 1L
                summary.PendingRowCount |> should equal 1L
                summary.HasPendingRows |> should equal true

                let! pendingAdvance = LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences configuration.GraceStatusFile pendingSequences
                pendingAdvance |> should equal 3L

                let! cleanSummary = LocalStateDb.readWatchJournalPendingWorkSummary configuration.GraceStatusFile

                cleanSummary.AppliedThroughSequence
                |> should equal 3L

                cleanSummary.PendingRowCount |> should equal 0L
                cleanSummary.HasPendingRows |> should equal false
            })

    /// Verifies that transition checks fail closed instead of trusting a missing local-state database as clean.
    [<Test>]
    let ``watch journal transition summary treats missing local-state database as uninspectable`` () =
        withTempDir (fun _ configuration ->
            task {
                let operation = Func<Task>(fun () -> LocalStateDb.readWatchJournalPendingWorkSummaryForTransitionCheck configuration.GraceStatusFile :> Task)

                let ex = Assert.ThrowsAsync<InvalidDataException>(operation)

                ex.Message
                |> should contain "local-state database is missing"
            })

    /// Verifies the pending-work summary avoids full diagnostic schema/index inspection in Watch hot paths.
    [<Test>]
    let ``watch journal pending work summary ignores unrelated diagnostic index drift`` () =
        withTempDir (fun _ configuration ->
            task {
                let pendingObservation = watchJournalObservation DifferenceType.Change FileSystemEntryType.File "pending.txt"
                let! _ = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile [ pendingObservation ]

                use connection = openRawConnection configuration.GraceStatusFile
                executeNonQuery connection "DROP INDEX IF EXISTS ix_status_files_sha256;"

                let! summary = LocalStateDb.readWatchJournalPendingWorkSummary configuration.GraceStatusFile

                summary.AppliedThroughSequence |> should equal 0L
                summary.PendingRowCount |> should equal 1L
                summary.HasPendingRows |> should equal true
            })

    /// Verifies that startup recovery returns compatible pending rows without mutating them before Watch replays them.
    [<Test>]
    let ``watch journal startup recovery returns compatible pending rows`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration

                let! sequences =
                    LocalStateDb.appendWatchJournalObservations
                        configuration.GraceStatusFile
                        [
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "src/app.fs"
                        ]

                let! recovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences |> should equal [| 1L |]
                recovery.AppliedThroughSequence |> should equal 0L
                recovery.QuarantinedRows |> should haveLength 0

                recovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.DifferenceType, row.EntryType, row.RelativePath)
                |> should
                    equal
                    [|
                        1L, DifferenceType.Change, FileSystemEntryType.File, RelativePath "src/app.fs"
                    |]

                let! snapshot = LocalStateDb.readWatchJournalSnapshot configuration.GraceStatusFile "pending" None 10
                snapshot.Rows |> should haveLength 1

                snapshot.Rows[0].State
                |> should equal LocalStateDb.WatchJournalRowState.Pending
            })

    /// Verifies that startup recovery quarantines persisted relative paths that would escape the watch root.
    [<Test>]
    let ``watch journal startup recovery quarantines relative paths that escape watch root`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration

                let! sequences =
                    LocalStateDb.appendWatchJournalObservations
                        configuration.GraceStatusFile
                        [
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "../outside.txt"
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "dir/../../outside.txt"
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "src/app.fs"
                        ]

                let! recovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences |> should equal [| 1L; 2L; 3L |]

                recovery.QuarantinedRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "../outside.txt", Some "relative path escapes watch root"
                        2L, Some "dir/../../outside.txt", Some "relative path escapes watch root"
                    |]

                recovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath)
                |> should equal [| 3L, RelativePath "src/app.fs" |]

                let! appliedThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                appliedThrough |> should equal 2L
            })

    /// Verifies that startup recovery uses the repository path comparison when matching durable replay roots.
    [<Test>]
    let ``watch journal startup recovery matches roots with repository path comparison`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = { watchJournalScope configuration with PathComparison = StringComparison.OrdinalIgnoreCase }

                let differentlyCasedScope =
                    { scope with WorkspaceRoot = configuration.RootDirectory.ToUpperInvariant(); WatchRoot = configuration.RootDirectory.ToUpperInvariant() }

                let caseSensitiveScope = { watchJournalScope configuration with PathComparison = StringComparison.Ordinal }

                let! sequences =
                    LocalStateDb.appendWatchJournalObservations
                        configuration.GraceStatusFile
                        [
                            scopedWatchJournalObservation differentlyCasedScope DifferenceType.Change FileSystemEntryType.File "compatible.txt"
                        ]

                let! insensitiveRecovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences |> should equal [| 1L |]

                insensitiveRecovery.QuarantinedRows
                |> should haveLength 0

                insensitiveRecovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath)
                |> should equal [| 1L, RelativePath "compatible.txt" |]

                let! sensitiveRecovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile caseSensitiveScope

                sensitiveRecovery.QuarantinedRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "compatible.txt", Some "wrong workspace root"
                    |]
            })

    /// Verifies that startup recovery rejects non-canonical durable replay paths before they can become status keys.
    [<Test>]
    let ``watch journal startup recovery quarantines non canonical relative paths`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration

                let! sequences =
                    LocalStateDb.appendWatchJournalObservations
                        configuration.GraceStatusFile
                        [
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "./file.txt"
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "sub/../file.txt"
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "src/app.fs"
                        ]

                let! recovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences |> should equal [| 1L; 2L; 3L |]

                recovery.QuarantinedRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "./file.txt", Some "relative path is not canonical"
                        2L, Some "sub/../file.txt", Some "relative path is not canonical"
                    |]

                recovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath)
                |> should equal [| 3L, RelativePath "src/app.fs" |]
            })

    /// Verifies that durable replay shape classification rejects rows Watch cannot emit before status mutation.
    [<Test>]
    let ``watch journal startup recovery classifies durable replay shapes before replay`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration

                let! sequences =
                    LocalStateDb.appendWatchJournalObservations
                        configuration.GraceStatusFile
                        [
                            scopedWatchJournalObservation scope DifferenceType.Delete FileSystemEntryType.File "."
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "dir/.."
                            scopedWatchJournalObservation scope DifferenceType.Add FileSystemEntryType.File "child/../."
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.Directory "changed-directory"
                            scopedWatchJournalObservation scope DifferenceType.Delete FileSystemEntryType.File "deleted-directory/"
                            scopedWatchJournalObservation scope DifferenceType.Add FileSystemEntryType.File "file-add.txt"
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "file-change.txt"
                            scopedWatchJournalObservation scope DifferenceType.Delete FileSystemEntryType.File "file-delete.txt"
                            scopedWatchJournalObservation scope DifferenceType.Add FileSystemEntryType.Directory "directory-add"
                            scopedWatchJournalObservation scope DifferenceType.Delete FileSystemEntryType.Directory "directory-delete"
                        ]

                let! recovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences
                |> should
                    equal
                    [|
                        1L
                        2L
                        3L
                        4L
                        5L
                        6L
                        7L
                        8L
                        9L
                        10L
                    |]

                recovery.QuarantinedRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some ".", Some "watch root path cannot be replayed as a status difference"
                        2L, Some "dir/..", Some "watch root path cannot be replayed as a status difference"
                        3L, Some "child/../.", Some "watch root path cannot be replayed as a status difference"
                        4L, Some "changed-directory", Some "directory change rows are not emitted by Watch startup scan"
                        5L, Some "deleted-directory/", Some "file replay row targets a directory-shaped path"
                    |]

                recovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.DifferenceType, row.EntryType, row.RelativePath)
                |> should
                    equal
                    [|
                        6L, DifferenceType.Add, FileSystemEntryType.File, RelativePath "file-add.txt"
                        7L, DifferenceType.Change, FileSystemEntryType.File, RelativePath "file-change.txt"
                        8L, DifferenceType.Delete, FileSystemEntryType.File, RelativePath "file-delete.txt"
                        9L, DifferenceType.Add, FileSystemEntryType.Directory, RelativePath "directory-add"
                        10L, DifferenceType.Delete, FileSystemEntryType.Directory, RelativePath "directory-delete"
                    |]

                let! appliedThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                appliedThrough |> should equal 5L
            })

    /// Verifies that startup recovery quarantines malformed durable roots instead of throwing during classification.
    [<Test>]
    let ``watch journal startup recovery quarantines malformed durable roots`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration
                let malformedRoot = "malformed" + string (char 0)
                let malformedWorkspace = { scope with WorkspaceRoot = malformedRoot }
                let malformedWatchRoot = { scope with WatchRoot = malformedRoot }

                let! sequences =
                    LocalStateDb.appendWatchJournalObservations
                        configuration.GraceStatusFile
                        [
                            scopedWatchJournalObservation malformedWorkspace DifferenceType.Change FileSystemEntryType.File "workspace.txt"
                            scopedWatchJournalObservation malformedWatchRoot DifferenceType.Change FileSystemEntryType.File "watch-root.txt"
                            scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "compatible.txt"
                        ]

                let! recovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences |> should equal [| 1L; 2L; 3L |]

                recovery.QuarantinedRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "workspace.txt", Some "invalid workspace root"
                        2L, Some "watch-root.txt", Some "invalid watch root"
                    |]

                recovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath)
                |> should equal [| 3L, RelativePath "compatible.txt" |]
            })

    /// Verifies that startup recovery quarantines rows from stale identity scopes and retires only contiguous failures.
    [<Test>]
    let ``watch journal startup recovery quarantines incompatible identity rows`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration

                let wrongRepository = { scope with RepositoryId = Guid.NewGuid() }

                let wrongBranch = { scope with BranchId = Guid.NewGuid() }

                let wrongWorkspace = { scope with WorkspaceRoot = Path.Combine(configuration.RootDirectory, "other-workspace") }

                let wrongRoot = { scope with WatchRoot = Path.Combine(configuration.RootDirectory, "other-root") }

                let failedContinuity = { scope with RootDirectoryBlake3Hash = Blake3Hash "different-root-blake3" }

                let failedSha256Continuity = { scope with RootDirectorySha256Hash = Sha256Hash "different-root-sha256" }

                let observations =
                    [|
                        scopedWatchJournalObservation wrongRepository DifferenceType.Change FileSystemEntryType.File "wrong-repo.txt"
                        scopedWatchJournalObservation wrongBranch DifferenceType.Change FileSystemEntryType.File "wrong-branch.txt"
                        scopedWatchJournalObservation wrongWorkspace DifferenceType.Change FileSystemEntryType.File "wrong-workspace.txt"
                        scopedWatchJournalObservation wrongRoot DifferenceType.Change FileSystemEntryType.File "wrong-root.txt"
                        scopedWatchJournalObservation failedContinuity DifferenceType.Change FileSystemEntryType.File "failed-continuity.txt"
                        scopedWatchJournalObservation failedSha256Continuity DifferenceType.Change FileSystemEntryType.File "failed-sha256-continuity.txt"
                        scopedWatchJournalObservation scope DifferenceType.Change FileSystemEntryType.File "compatible.txt"
                    |]

                let! sequences = LocalStateDb.appendWatchJournalObservations configuration.GraceStatusFile observations
                let! recovery = LocalStateDb.recoverWatchJournalForStartup configuration.GraceStatusFile scope

                sequences
                |> should equal [| 1L; 2L; 3L; 4L; 5L; 6L; 7L |]

                recovery.QuarantinedRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "wrong-repo.txt", Some "wrong repository"
                        2L, Some "wrong-branch.txt", Some "wrong branch"
                        3L, Some "wrong-workspace.txt", Some "wrong workspace root"
                        4L, Some "wrong-root.txt", Some "wrong watch root"
                        5L, Some "failed-continuity.txt", Some "failed root hash continuity"
                        6L, Some "failed-sha256-continuity.txt", Some "failed root SHA-256 continuity"
                    |]

                recovery.CompatibleReplayRows
                |> Array.map (fun row -> row.Sequence, row.RelativePath)
                |> should equal [| 7L, RelativePath "compatible.txt" |]

                let! appliedThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                appliedThrough |> should equal 6L

                let! quarantinedSnapshot = LocalStateDb.readWatchJournalSnapshot configuration.GraceStatusFile "quarantined" None 10
                quarantinedSnapshot.Rows |> should haveLength 6

                let! pendingSnapshot = LocalStateDb.readWatchJournalSnapshot configuration.GraceStatusFile "pending" None 10

                pendingSnapshot.Rows
                |> Array.map (fun row -> row.Sequence)
                |> should equal [| 7L |]
            })

    /// Verifies that lifecycle diagnostics are durable and explicitly non-replayable.
    [<Test>]
    let ``watch lifecycle events are recorded as non replayable diagnostics`` () =
        withTempDir (fun _ configuration ->
            task {
                let scope = watchJournalScope configuration

                do!
                    LocalStateDb.recordWatchLifecycleEvent
                        configuration.GraceStatusFile
                        { Scope = scope; EventType = "startup-replay-complete"; Message = "Startup replay completed." }

                use connection = new SqliteConnection($"Data Source={configuration.GraceStatusFile}")
                connection.Open()
                let eventCount = executeScalarInt connection "SELECT COUNT(*) FROM watch_lifecycle_events WHERE event_type = 'startup-replay-complete';"
                let replayableTotal = executeScalarInt connection "SELECT SUM(replayable) FROM watch_lifecycle_events;"

                eventCount |> should equal 1
                replayableTotal |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"
                |> should equal 0
            })

    /// Verifies that current-schema initialization recreates malformed lifecycle tables before inserts trust them.
    [<Test>]
    let ``ensureDbInitialized recreates current schema database with malformed watch lifecycle table`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L
                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE watch_lifecycle_events;"
                    executeNonQuery connection "CREATE TABLE watch_lifecycle_events (sequence INTEGER PRIMARY KEY AUTOINCREMENT);"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                let lifecycleColumns = executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('watch_lifecycle_events');"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                lifecycleColumns |> should equal 13

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)

                do!
                    LocalStateDb.recordWatchLifecycleEvent
                        configuration.GraceStatusFile
                        { Scope = watchJournalScope configuration; EventType = "startup-replay-complete"; Message = "Lifecycle table was repaired." }

                let lifecycleRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_lifecycle_events;"
                lifecycleRows |> should equal 1
            })

    /// Verifies that current-schema validation rejects lifecycle tables that allow replayable diagnostics.
    [<Test>]
    let ``ensureDbInitialized recreates current schema database with replayable lifecycle constraint`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L
                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE watch_lifecycle_events;"

                    executeNonQuery
                        connection
                        "CREATE TABLE watch_lifecycle_events (sequence INTEGER PRIMARY KEY AUTOINCREMENT, created_at_unix_ticks INTEGER NOT NULL, repository_id TEXT, branch_id TEXT, workspace_root TEXT, watch_root TEXT, root_directory_version_id TEXT, root_directory_blake3_hash TEXT, watch_mode TEXT, event_type TEXT NOT NULL, message TEXT NOT NULL, replayable INTEGER NOT NULL CHECK (replayable = 1));"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)

                do!
                    LocalStateDb.recordWatchLifecycleEvent
                        configuration.GraceStatusFile
                        { Scope = watchJournalScope configuration; EventType = "startup-replay-complete"; Message = "Lifecycle table was repaired." }

                let lifecycleRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_lifecycle_events WHERE replayable = 0;"
                lifecycleRows |> should equal 1

                (fun () ->
                    executeNonQuery
                        connection
                        "INSERT INTO watch_lifecycle_events (created_at_unix_ticks, event_type, message, replayable) VALUES (1, 'malformed', 'must fail', 1);")
                |> should throw typeof<SqliteException>
            })

    /// Verifies that round trips status snapshot.
    [<Test>]
    let ``round trips status snapshot`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = getCurrentInstant ()
                let lastWrite = DateTime.UtcNow
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let rootFile = createFileVersion "root.txt" "root-hash" false 12L now lastWrite
                let srcFile = createFileVersionWithHashes "src/file.txt" "src-hash" "src-blake3" false 34L now lastWrite

                let srcDirectory = createDirectoryVersion configuration srcId "src" "src-dir-hash" [||] [| srcFile |] srcFile.Size lastWrite

                let rootDirectory =
                    createDirectoryVersion
                        configuration
                        rootId
                        Constants.RootDirectoryPath
                        "root-dir-hash"
                        [| srcId |]
                        [| rootFile |]
                        (rootFile.Size + srcDirectory.Size)
                        lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDirectory) |> ignore
                index.TryAdd(srcId, srcDirectory) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDirectory.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status
                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.RootDirectoryId |> should equal rootId

                readBack.RootDirectorySha256Hash
                |> should equal rootDirectory.Sha256Hash

                use connection = openRawConnection configuration.GraceStatusFile
                let persistedRootBlake3 = executeScalarString connection "SELECT root_directory_blake3_hash FROM status_meta WHERE id = 1;"

                persistedRootBlake3
                |> should equal (string rootDirectory.Blake3Hash)

                readBack.Index.Count |> should equal 2

                let files =
                    readBack.Index.Values
                    |> Seq.collect (fun dv -> dv.Files)
                    |> Seq.toList

                files.Length |> should equal 2

                let srcRead =
                    files
                    |> Seq.find (fun file -> file.RelativePath = "src/file.txt")

                srcRead.Sha256Hash |> should equal "src-hash"
                srcRead.Blake3Hash |> should equal "src-blake3"
                srcRead.Size |> should equal 34L
            })

    /// Verifies that applies incremental updates.
    [<Test>]
    let ``applies incremental updates`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = getCurrentInstant ()
                let lastWrite = DateTime.UtcNow
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()
                let oldId = Guid.NewGuid()

                let rootFile = createFileVersion "root.txt" "root-hash" false 10L now lastWrite
                let srcFile = createFileVersion "src/file.txt" "src-hash" false 20L now lastWrite

                let oldDirectory = createDirectoryVersion configuration oldId "old" "old-dir-hash" [||] [||] 0L lastWrite

                let srcDirectory = createDirectoryVersion configuration srcId "src" "src-dir-hash" [||] [| srcFile |] srcFile.Size lastWrite

                let rootDirectory =
                    createDirectoryVersion
                        configuration
                        rootId
                        Constants.RootDirectoryPath
                        "root-dir-hash"
                        [| srcId; oldId |]
                        [| rootFile |]
                        (rootFile.Size + srcDirectory.Size)
                        lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDirectory) |> ignore
                index.TryAdd(srcId, srcDirectory) |> ignore
                index.TryAdd(oldId, oldDirectory) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDirectory.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                let newRootId = Guid.NewGuid()
                let newSrcId = Guid.NewGuid()
                let changedFile = createFileVersionWithHashes "src/file.txt" "src-hash-2" "src-blake3-2" false 25L now lastWrite
                let newFile = createFileVersion "src/new.txt" "new-hash" false 5L now lastWrite

                let newSrcDirectory =
                    createDirectoryVersion
                        configuration
                        newSrcId
                        "src"
                        "src-dir-hash-2"
                        [||]
                        [| changedFile; newFile |]
                        (changedFile.Size + newFile.Size)
                        lastWrite

                let newRootDirectory =
                    createDirectoryVersion
                        configuration
                        newRootId
                        Constants.RootDirectoryPath
                        "root-dir-hash-2"
                        [| newSrcId |]
                        [||]
                        (changedFile.Size + newFile.Size)
                        lastWrite

                let newIndex = GraceIndex()

                newIndex.TryAdd(newRootId, newRootDirectory)
                |> ignore

                newIndex.TryAdd(newSrcId, newSrcDirectory)
                |> ignore

                let updatedStatus = { status with Index = newIndex; RootDirectoryId = newRootId; RootDirectorySha256Hash = newRootDirectory.Sha256Hash }

                let differences =
                    [
                        FileSystemDifference.Create Change FileSystemEntryType.File "src/file.txt"
                        FileSystemDifference.Create Add FileSystemEntryType.File "src/new.txt"
                        FileSystemDifference.Create Delete FileSystemEntryType.File "root.txt"
                        FileSystemDifference.Create Delete FileSystemEntryType.Directory "old"
                    ]

                do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile updatedStatus [ newSrcDirectory; newRootDirectory ] differences

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                readBack.RootDirectoryId |> should equal newRootId

                readBack.Index.ContainsKey(oldId)
                |> should equal false

                let srcRead =
                    readBack.Index.Values
                    |> Seq.find (fun dv -> dv.RelativePath = "src")

                srcRead.Files.Count |> should equal 2

                srcRead.Files
                |> Seq.exists (fun file -> file.RelativePath = "src/new.txt")
                |> should equal true

                let changedRead =
                    srcRead.Files
                    |> Seq.find (fun file -> file.RelativePath = "src/file.txt")

                changedRead.Blake3Hash
                |> should equal "src-blake3-2"

                readBack.Index.Values
                |> Seq.collect (fun dv -> dv.Files)
                |> Seq.exists (fun file -> file.RelativePath = "root.txt")
                |> should equal false
            })

    /// Verifies that upserts object cache entries.
    [<Test>]
    let ``upserts object cache entries`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = getCurrentInstant ()
                let lastWrite = DateTime.UtcNow
                let directoryId = Guid.NewGuid()
                let fileVersion = createFileVersionWithHashes "src/cache.txt" "cache-hash" "cache-blake3" false 12L now lastWrite

                let directory = createDirectoryVersion configuration directoryId "src" "cache-dir-hash" [||] [| fileVersion |] fileVersion.Size lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ directory ]

                let! directoryExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile directoryId

                directoryExists |> should equal true

                let! fileExists = LocalStateDb.isFileVersionInObjectCache configuration.GraceStatusFile fileVersion

                fileExists |> should equal true

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarString connection "SELECT blake3_hash FROM object_cache_directory_files WHERE relative_path = 'src/cache.txt';"
                |> should equal "cache-blake3"
            })

    /// Verifies that concurrent writers do not corrupt database.
    [<Test>]
    let ``concurrent writers do not corrupt database`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "root-hash"
                let baseTicks = getCurrentInstant().ToUnixTimeTicks()

                let tasks =
                    Array.init 8 (fun index ->
                        task {
                            let status =
                                { GraceStatus.Default with
                                    RootDirectoryId = rootId
                                    RootDirectorySha256Hash = rootHash
                                    LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(baseTicks + int64 index)
                                }

                            do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile status Seq.empty Seq.empty
                        })

                do! Task.WhenAll(tasks |> Array.map (fun task -> task :> Task))

                let! meta = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                meta.RootDirectoryId |> should equal rootId

                meta.RootDirectorySha256Hash
                |> should equal rootHash
            })

    /// Verifies that ensure db initialized recreates db when schema version mismatches.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema_version mismatches`` () =
        withTempDir (fun _ configuration ->
            task {
                seedSchemaVersionOnly configuration.GraceStatusFile "0"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Proves a complete prior v10 database is replaced instead of being read through as the v11 typed-selector shape.
    [<Test>]
    let ``ensureDbInitialized cleanly recreates v10 local state for typed Branch selectors`` () =
        withTempDir (fun _ configuration ->
            task {
                seedSchemaVersionOnly configuration.GraceStatusFile "10"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                |> should equal "11"

                let selectorColumn =
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "SELECT COUNT(*) FROM pragma_table_info('working_directory_update_completions') WHERE name = 'branch_selection_kind';"

                    command.ExecuteScalar() |> Convert.ToInt32

                selectorColumn |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates db when schema v6 has a malformed Watch journal table.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 watch journal shape is malformed`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()

                let ticks =
                    Instant
                        .FromUtc(2026, 1, 2, 3, 4)
                        .ToUnixTimeTicks()

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "UPDATE meta SET value = '6' WHERE key = 'schema_version';"
                    executeNonQuery connection "DROP TABLE watch_journal;"
                    executeNonQuery connection "CREATE TABLE watch_journal (sequence TEXT PRIMARY KEY);"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let sequencePk = executeScalarInt connection "SELECT pk FROM pragma_table_info('watch_journal') WHERE name = 'sequence';"
                sequencePk |> should equal 1

                let sequenceType = executeScalarString connection "SELECT UPPER(type) FROM pragma_table_info('watch_journal') WHERE name = 'sequence';"
                sequenceType |> should equal "INTEGER"

                let tableSql = executeScalarString connection "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal';"

                tableSql.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                |> should be (greaterThanOrEqualTo 0)

                let createdAtNotNull =
                    executeScalarInt connection "SELECT [notnull] FROM pragma_table_info('watch_journal') WHERE name = 'created_at_unix_ticks';"

                createdAtNotNull |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates db when schema v6 reuses rowids for Watch journal sequences.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 watch journal lacks autoincrement`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()

                let ticks =
                    Instant
                        .FromUtc(2026, 1, 2, 3, 4)
                        .ToUnixTimeTicks()

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE watch_journal;"
                    executeNonQuery connection "CREATE TABLE watch_journal (sequence INTEGER PRIMARY KEY, created_at_unix_ticks INTEGER NOT NULL);"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let tableSql = executeScalarString connection "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal';"

                tableSql.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                |> should be (greaterThanOrEqualTo 0)

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that AUTOINCREMENT acceptance belongs to the sequence column declaration, not nearby SQL text.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 autoincrement text is outside sequence declaration`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE watch_journal;"

                    executeNonQuery
                        connection
                        "CREATE TABLE watch_journal (sequence INTEGER PRIMARY KEY, created_at_unix_ticks INTEGER NOT NULL CHECK ('sequence INTEGER PRIMARY KEY AUTOINCREMENT' IS NOT NULL));"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let tableSql = executeScalarString connection "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal';"

                tableSql.IndexOf("sequence INTEGER PRIMARY KEY AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                |> should be (greaterThanOrEqualTo 0)

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates db when Watch recovery metadata is malformed.
    [<TestCase("not-a-number")>]
    [<TestCase("-1")>]
    let ``ensureDbInitialized recreates DB when schema v6 applied through metadata is invalid`` (appliedThroughValue: string) =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery connection $"UPDATE meta SET value = '{appliedThroughValue}' WHERE key = 'AppliedThroughSequence';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 0L

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects duplicated Watch recovery metadata in malformed schema v6 tables.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 applied through metadata is duplicated`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '1');"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let duplicateRows = executeScalarInt connection "SELECT COUNT(*) FROM meta WHERE key = 'AppliedThroughSequence';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                duplicateRows |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects meta tables that cannot preserve one row per key.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 meta key is not unique`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"

                let duplicateInsertSucceeded =
                    try
                        executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '1');"
                        true
                    with
                    | :? SqliteException -> false

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                duplicateInsertSucceeded |> should equal false

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects malformed SQLite journal allocation metadata.
    [<TestCase("not-a-number")>]
    [<TestCase("-1")>]
    let ``ensureDbInitialized recreates DB when schema v6 allocated journal sequence is invalid`` (allocatedSequence: string) =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 2L
                    executeNonQuery connection "UPDATE meta SET value = '1' WHERE key = 'AppliedThroughSequence';"
                    executeNonQuery connection $"UPDATE sqlite_sequence SET seq = '{allocatedSequence}' WHERE name = 'watch_journal';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that empty schema v6 databases still reject malformed SQLite journal allocation metadata.
    [<TestCase("not-a-number")>]
    [<TestCase("-1")>]
    let ``ensureDbInitialized recreates DB when schema v6 empty journal has invalid allocation row`` (allocatedSequence: string) =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DELETE FROM meta WHERE key = 'AppliedThroughSequence';"
                    executeNonQuery connection $"INSERT INTO sqlite_sequence (name, seq) VALUES ('watch_journal', '{allocatedSequence}');"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"
                let allocationRows = executeScalarInt connection "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'watch_journal';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0
                allocationRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects missing SQLite journal allocation metadata when rows exist.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 journal rows have no allocation row`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 2L
                    executeNonQuery connection "DELETE FROM sqlite_sequence WHERE name = 'watch_journal';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects stale SQLite journal allocation below the highest persisted row.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 allocated journal sequence is below journal max`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 2L
                    executeNonQuery connection "UPDATE sqlite_sequence SET seq = 1 WHERE name = 'watch_journal';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects Watch recovery metadata beyond SQLite's allocated journal sequence.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 applied through metadata exceeds allocated sequence`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    insertWatchJournalRows connection 2L
                    executeNonQuery connection "UPDATE meta SET value = '3' WHERE key = 'AppliedThroughSequence';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects NULL Watch recovery metadata without throwing.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 applied through metadata is null`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', NULL);"

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true

                inspection.WatchJournalAppliedThroughMetadataValid
                |> should equal (Some false)

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance skips expression indexes when proving metadata key uniqueness.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 meta uniqueness is expression index only`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "CREATE UNIQUE INDEX ux_meta_lower_key ON meta(lower(key));"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true

                inspection.WatchJournalAppliedThroughMetadataValid
                |> should equal (Some false)

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects composite primary keys before trusting metadata key uniqueness.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 meta key is part of composite primary key`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (key, value));"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true

                inspection.WatchJournalAppliedThroughMetadataValid
                |> should equal (Some false)

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects journal rows whose SQLite sequence was not positively allocated.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 journal rows have non-positive sequence`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery
                        connection
                        "INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path) VALUES (0, 1, 'Change', 'File', 'zero.txt');"

                    executeNonQuery connection "UPDATE sqlite_sequence SET seq = 0 WHERE name = 'watch_journal';"

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true

                inspection.WatchJournalAppliedThroughMetadataValid
                |> should equal (Some false)

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema v6 databases with journal rows must carry trustworthy Watch recovery metadata.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v6 has journal rows without applied through metadata`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery
                        connection
                        "INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path) VALUES (1, 1, 'Change', 'File', 'one.txt');"

                    executeNonQuery connection "DELETE FROM meta WHERE key = 'AppliedThroughSequence';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recovers from a corrupt non sqlite file.
    [<Test>]
    let ``ensureDbInitialized recovers from a corrupt non-sqlite file`` () =
        withTempDir (fun _ configuration ->
            task {
                Directory.CreateDirectory(Path.GetDirectoryName(configuration.GraceStatusFile))
                |> ignore

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                let bytes = Encoding.UTF8.GetBytes("this is not a sqlite database")
                File.WriteAllBytes(configuration.GraceStatusFile, bytes)

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreation refreshes sidecar files.
    [<Test>]
    let ``ensureDbInitialized recreation refreshes sidecar files`` () =
        withTempDir (fun _ configuration ->
            task {
                seedSchemaVersionOnly configuration.GraceStatusFile "0"
                let oldTime = DateTime.UtcNow.AddDays(-1)

                let journalPath = configuration.GraceStatusFile + "-journal"
                let walPath = configuration.GraceStatusFile + "-wal"
                let shmPath = configuration.GraceStatusFile + "-shm"

                File.WriteAllText(journalPath, "sentinel")
                File.WriteAllText(walPath, "sentinel")
                File.WriteAllText(shmPath, "sentinel")

                File.SetLastWriteTimeUtc(journalPath, oldTime)
                File.SetLastWriteTimeUtc(walPath, oldTime)
                File.SetLastWriteTimeUtc(shmPath, oldTime)

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                File.Exists(journalPath) |> should equal false

                if File.Exists(walPath) then
                    File.GetLastWriteTimeUtc(walPath)
                    |> should be (greaterThan oldTime)

                if File.Exists(shmPath) then
                    File.GetLastWriteTimeUtc(shmPath)
                    |> should be (greaterThan oldTime)
            })

    /// Verifies that read only inspection reports valid database metadata.
    [<Test>]
    let ``read-only inspection reports valid database metadata`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true
                inspection.OpenError |> should equal None

                inspection.SchemaVersion
                |> should equal (Some LocalStateDb.SchemaVersion)

                inspection.MissingRequiredTables
                |> should equal Array.empty<string>

                inspection.MissingRequiredIndexes
                |> should equal Array.empty<string>

                inspection.IntegrityCheckRows
                |> should equal [| "ok" |]

                inspection.ForeignKeyViolations
                |> should equal Array.empty<string>

                inspection.ObjectCacheReadable
                |> should equal (Some true)

                inspection.ObjectCacheError |> should equal None
            })

    /// Verifies that read only inspection opens checkpointed wal database without creating missing sidecars.
    [<Test>]
    let ``read-only inspection opens checkpointed wal database without creating missing sidecars`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                SqliteConnection.ClearAllPools()

                let walPath = configuration.GraceStatusFile + "-wal"
                let shmPath = configuration.GraceStatusFile + "-shm"

                [| walPath; shmPath |]
                |> Array.iter (fun sidecar -> if File.Exists(sidecar) then File.Delete(sidecar))

                File.Exists(walPath) |> should equal false
                File.Exists(shmPath) |> should equal false

                let dbBefore = snapshotFile configuration.GraceStatusFile

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true
                inspection.OpenError |> should equal None

                inspection.SchemaVersion
                |> should equal (Some LocalStateDb.SchemaVersion)

                inspection.IntegrityCheckRows
                |> should equal [| "ok" |]

                inspection.ObjectCacheReadable
                |> should equal (Some true)

                snapshotFile configuration.GraceStatusFile
                |> should equal dbBefore

                File.Exists(walPath) |> should equal false
                File.Exists(shmPath) |> should equal false
            })

    /// Verifies that read only inspection does not create missing parent or database.
    [<Test>]
    let ``read-only inspection does not create missing parent or database`` () =
        withTempDir (fun root _ ->
            task {
                let missingDbPath = Path.Combine(root, "missing-grace", Constants.GraceLocalStateDbFileName)

                let inspection = LocalStateDb.inspectReadOnly missingDbPath

                inspection.ParentDirectoryExists
                |> should equal false

                inspection.DbFileExists |> should equal false
                inspection.OpenedReadOnly |> should equal false

                Directory.Exists(Path.GetDirectoryName(missingDbPath))
                |> should equal false

                File.Exists(missingDbPath) |> should equal false
            })

    /// Verifies that read only inspection preserves corrupt bytes sidecars and backups.
    [<Test>]
    let ``read-only inspection preserves corrupt bytes sidecars and backups`` () =
        withTempDir (fun _ configuration ->
            task {
                Directory.CreateDirectory(Path.GetDirectoryName(configuration.GraceStatusFile))
                |> ignore

                let corruptBytes = Encoding.UTF8.GetBytes("this is not a sqlite database")
                File.WriteAllBytes(configuration.GraceStatusFile, corruptBytes)

                let oldTime = DateTime.UtcNow.AddDays(-2.0)

                let sidecars =
                    [| "-journal"; "-wal"; "-shm" |]
                    |> Array.map (fun suffix -> configuration.GraceStatusFile + suffix)

                sidecars
                |> Array.iter (fun sidecar ->
                    File.WriteAllText(sidecar, $"sentinel-{Path.GetFileName(sidecar)}")
                    File.SetLastWriteTimeUtc(sidecar, oldTime))

                let dbBefore = snapshotFile configuration.GraceStatusFile
                let sidecarsBefore = sidecars |> Array.map snapshotFile

                let backupsBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal false
                inspection.OpenError.IsSome |> should equal true

                snapshotFile configuration.GraceStatusFile
                |> should equal dbBefore

                sidecars
                |> Array.map snapshotFile
                |> should equal sidecarsBefore

                let backupsAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                backupsAfter |> should equal backupsBefore
            })

    /// Verifies that read only inspection reports schema mismatch without corrupt backup.
    [<Test>]
    let ``read-only inspection reports schema mismatch without corrupt backup`` () =
        withTempDir (fun _ configuration ->
            task {
                seedSchemaVersionOnly configuration.GraceStatusFile "0"
                let dbBefore = snapshotFile configuration.GraceStatusFile

                let backupsBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true

                inspection.SchemaVersion
                |> should equal (Some "0")

                inspection.MissingRequiredTables
                |> should contain "status_meta"

                inspection.MissingRequiredIndexes
                |> should contain "ix_object_cache_files_path_hash"

                snapshotFile configuration.GraceStatusFile
                |> should equal dbBefore

                let backupsAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                backupsAfter |> should equal backupsBefore
            })

    /// Verifies that Watch journal diagnostics report stale schema without repairing or rotating the DB.
    [<Test>]
    let ``watch journal snapshot reports stale schema without mutating local state`` () =
        withTempDir (fun _ configuration ->
            task {
                seedSchemaVersionOnly configuration.GraceStatusFile "0"
                let dbBefore = snapshotFile configuration.GraceStatusFile

                let backupsBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                let operation = Func<Task>(fun () -> LocalStateDb.readWatchJournalSnapshot configuration.GraceStatusFile "all" None 10 :> Task)

                let ex = Assert.ThrowsAsync<InvalidDataException>(operation)

                ex.Message
                |> should contain "healthy local state database"

                snapshotFile configuration.GraceStatusFile
                |> should equal dbBefore

                let backupsAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                backupsAfter |> should equal backupsBefore
            })

    /// Verifies that read only inspection reports foreign key inconsistency without repair.
    [<Test>]
    let ``read-only inspection reports foreign-key inconsistency without repair`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                executeNonQuery connection "PRAGMA foreign_keys = OFF;"

                executeNonQuery
                    connection
                    "INSERT INTO object_cache_directory_files (directory_version_id, relative_path, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ('00000000-0000-0000-0000-000000000111', 'orphan.txt', 'hash', '', 0, 1, 0, 0, 0);"

                let inspection = LocalStateDb.inspectReadOnly configuration.GraceStatusFile

                inspection.OpenedReadOnly |> should equal true

                inspection.ForeignKeyViolations.Length
                |> should be (greaterThan 0)

                let orphanCount = executeScalarInt connection "SELECT COUNT(*) FROM object_cache_directory_files WHERE relative_path = 'orphan.txt';"
                orphanCount |> should equal 1
            })

    /// Verifies that ensure db initialized creates expected tables and indexes.
    [<Test>]
    let ``ensureDbInitialized creates expected tables and indexes`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                use cmd = connection.CreateCommand()
                cmd.CommandText <- "SELECT type, name FROM sqlite_master WHERE type IN ('table', 'index');"
                use reader = cmd.ExecuteReader()

                let objects = HashSet<string>(StringComparer.OrdinalIgnoreCase)

                while reader.Read() do
                    let objectType = reader.GetString(0)
                    let name = reader.GetString(1)
                    objects.Add($"{objectType}:{name}") |> ignore

                let expected =
                    [|
                        "table:meta"
                        "table:status_meta"
                        "table:status_directories"
                        "index:ix_status_directories_directory_version_id"
                        "table:status_files"
                        "index:ix_status_files_directory_path"
                        "index:ix_status_files_directory_version_id"
                        "index:ix_status_files_sha256"
                        "table:object_cache_directories"
                        "index:ix_object_cache_directories_relative_path"
                        "table:object_cache_directory_children"
                        "index:ix_object_cache_children_parent"
                        "table:object_cache_directory_files"
                        "index:ix_object_cache_files_path_hash"
                        "table:watch_journal"
                    |]

                expected
                |> Array.iter (fun name -> objects.Contains(name) |> should equal true)
            })

    /// Verifies that ensure db initialized is idempotent and preserves created at.
    [<Test>]
    let ``ensureDbInitialized is idempotent and preserves created_at`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection1 = openRawConnection configuration.GraceStatusFile
                let createdAt1 = executeScalarInt64 connection1 "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'created_at_unix_ticks';"
                let statusMetaCount1 = executeScalarInt connection1 "SELECT COUNT(*) FROM status_meta;"
                let appliedThrough1 = executeScalarString connection1 "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                statusMetaCount1 |> should equal 1
                appliedThrough1 |> should equal "0"

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection2 = openRawConnection configuration.GraceStatusFile
                let createdAt2 = executeScalarInt64 connection2 "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'created_at_unix_ticks';"
                createdAt2 |> should equal createdAt1

                let statusMetaCount2 = executeScalarInt connection2 "SELECT COUNT(*) FROM status_meta;"
                let appliedThrough2 = executeScalarString connection2 "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                statusMetaCount2 |> should equal 1
                appliedThrough2 |> should equal "0"
            })

    /// Verifies that process-local initialization cannot hide a genuinely deleted SQLite database.
    [<Test>]
    let ``ensureDbInitialized recreates a database deleted after initialization`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

                [|
                    configuration.GraceStatusFile
                    configuration.GraceStatusFile + "-wal"
                    configuration.GraceStatusFile + "-shm"
                    configuration.GraceStatusFile + "-journal"
                |]
                |> Array.iter (fun path -> if File.Exists(path) then File.Delete(path))

                Assert.That(File.Exists(configuration.GraceStatusFile), Is.False)
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                |> should equal 1

                executeScalarInt connection "SELECT COUNT(*) FROM remote_reference_boundaries;"
                |> should equal 0
            })

    /// Verifies that ensure db initialized recreates legacy schema v2 database without blake3 columns.
    [<Test>]
    let ``ensureDbInitialized recreates legacy schema v2 database without blake3 columns`` () =
        withTempDir (fun _ configuration ->
            task {
                Directory.CreateDirectory(Path.GetDirectoryName(configuration.GraceStatusFile))
                |> ignore

                let rootId = Guid.NewGuid()
                let rootHash = "custom-root-hash"
                let ticks = 1234567890L

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"

                    executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '2');"

                    executeNonQuery
                        connection
                        $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', '{rootHash}', {ticks}, {ticks});"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection2 = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection2 "SELECT value FROM meta WHERE key = 'schema_version';"
                let readRootId = executeScalarString connection2 "SELECT root_directory_version_id FROM status_meta WHERE id = 1;"
                let readRootHash = executeScalarString connection2 "SELECT root_directory_sha256_hash FROM status_meta WHERE id = 1;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                readRootId
                |> should not' (equal (rootId.ToString()))

                readRootHash |> should not' (equal rootHash)

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized preserves existing current schema status meta row.
    [<Test>]
    let ``ensureDbInitialized preserves existing current schema status_meta row`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "custom-root-hash"
                let ticks = 1234567890L

                let rootBlake3Hash = "custom-root-blake3"
                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash rootBlake3Hash ticks

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let readRootId = executeScalarString connection "SELECT root_directory_version_id FROM status_meta WHERE id = 1;"
                let readRootHash = executeScalarString connection "SELECT root_directory_sha256_hash FROM status_meta WHERE id = 1;"
                let readRootBlake3Hash = executeScalarString connection "SELECT root_directory_blake3_hash FROM status_meta WHERE id = 1;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                readRootId |> should equal (rootId.ToString())
                readRootHash |> should equal rootHash
                readRootBlake3Hash |> should equal rootBlake3Hash

                let blake3Columns = executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('status_files') WHERE name = 'blake3_hash';"

                let rootBlake3Columns =
                    executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('status_meta') WHERE name = 'root_directory_blake3_hash';"

                blake3Columns |> should equal 1
                rootBlake3Columns |> should equal 1

                let replacementStatus = createTestStatus (Guid.NewGuid()) "replacement-root-hash" (ticks + 1L)

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile replacementStatus

                let updatedRootHash = executeScalarString connection "SELECT root_directory_sha256_hash FROM status_meta WHERE id = 1;"

                updatedRootHash
                |> should equal "replacement-root-hash"

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal corruptBefore
            })

    /// Verifies that ensure db initialized rejects schema v6 databases without watch journal.
    [<Test>]
    let ``ensureDbInitialized recreates schema v6 database missing watch journal`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "missing-journal-root-hash"
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash "root-blake3" ticks

                use seedConnection = openRawConnection configuration.GraceStatusFile
                executeNonQuery seedConnection "UPDATE meta SET value = '6' WHERE key = 'schema_version';"
                executeNonQuery seedConnection "DROP TABLE watch_journal;"
                seedConnection.Dispose()

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let watchJournalCount = executeScalarInt connection "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                watchJournalCount |> should equal 1
                appliedThrough |> should equal "0"

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates current schema databases with append-incompatible journal columns.
    [<Test>]
    let ``ensureDbInitialized recreates schema v6 database with hidden required watch journal column`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "required-journal-column-root-hash"
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash "root-blake3" ticks

                do
                    use seedConnection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery seedConnection "ALTER TABLE watch_journal RENAME TO watch_journal_valid;"

                    executeNonQuery
                        seedConnection
                        "CREATE TABLE watch_journal (sequence INTEGER PRIMARY KEY AUTOINCREMENT, created_at_unix_ticks INTEGER NOT NULL, difference_type TEXT NOT NULL, entry_type TEXT NOT NULL, relative_path TEXT NOT NULL, hidden_required TEXT NOT NULL);"

                    executeNonQuery
                        seedConnection
                        "INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path, hidden_required) SELECT sequence, created_at_unix_ticks, difference_type, entry_type, relative_path, 'seeded' FROM watch_journal_valid;"

                    executeNonQuery seedConnection "DROP TABLE watch_journal_valid;"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                let hiddenRequiredColumns =
                    executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('watch_journal') WHERE name = 'hidden_required';"

                let journalColumns = executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('watch_journal');"

                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                hiddenRequiredColumns |> should equal 0
                journalColumns |> should equal 15
                appliedThrough |> should equal "0"

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates partial schema v4 database missing root blake3 column.
    [<Test>]
    let ``ensureDbInitialized recreates partial schema v4 database missing root blake3 column`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "partial-v4-root-hash"
                let ticks = 1234567890L

                seedPartialV4WithoutRootBlake3Column configuration.GraceStatusFile rootId rootHash ticks

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                let rootBlake3Columns =
                    executeScalarInt connection "SELECT COUNT(*) FROM pragma_table_info('status_meta') WHERE name = 'root_directory_blake3_hash';"

                let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                rootBlake3Columns |> should equal 1
                statusMetaCount |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates current schema database with empty status blake3 rows.
    [<Test>]
    let ``ensureDbInitialized recreates current schema database with empty status blake3 rows`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "partial-empty-blake3-root-hash"
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash "root-blake3" ticks

                use seedConnection = openRawConnection configuration.GraceStatusFile

                executeNonQuery
                    seedConnection
                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ('.', '', '00000000-0000-0000-0000-000000000001', 'root-sha', '', 0, 0, 0);"

                seedConnection.Dispose()

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let statusDirectoryCount = executeScalarInt connection "SELECT COUNT(*) FROM status_directories;"
                let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                statusDirectoryCount |> should equal 0
                statusMetaCount |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that journal mode is wal after initialization.
    [<Test>]
    let ``journal mode is WAL after initialization`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let journalMode = executeScalarString connection "PRAGMA journal_mode;"

                journalMode.ToLowerInvariant()
                |> should equal "wal"
            })

    /// Verifies that non busy sqlite failures are not retried.
    [<Test>]
    let ``non-busy sqlite failures are not retried`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeNonQuery connection "CREATE TRIGGER abort_status_meta BEFORE INSERT ON status_meta BEGIN SELECT RAISE(ABORT,'boom'); END;"

                let stopwatch = Stopwatch.StartNew()

                let rootId = Guid.NewGuid()
                let rootHash = "root-hash"
                let ticks = getCurrentInstant().ToUnixTimeTicks()
                let status = createTestStatus rootId rootHash ticks

                let operation =
                    Func<Task>(fun () -> task { do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile status Seq.empty Seq.empty } :> Task)

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                stopwatch.Stop()

                stopwatch.ElapsedMilliseconds
                |> should be (lessThan 1500L)
            })

    /// Verifies that each committed status transaction returns and persists one monotonic local-status revision.
    [<Test>]
    let ``status transactions advance and return committed local-status revision`` () =
        withTempDir (fun _ configuration ->
            task {
                let! initialRevision = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile
                initialRevision |> should equal 0L

                let status = createTestStatus (Guid.NewGuid()) "revision-root" 100L

                let! replacementRevision = LocalStateDb.replaceStatusSnapshotWithRevision configuration.GraceStatusFile status
                replacementRevision |> should equal 1L

                let! incrementalRevision = LocalStateDb.applyStatusIncrementalWithRevision configuration.GraceStatusFile status Seq.empty Seq.empty

                incrementalRevision |> should equal 2L

                let! durableRevision = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile

                durableRevision
                |> should equal incrementalRevision
            })

    /// Verifies malformed local-status revision metadata is rejected instead of becoming an incremental baseline.
    [<Test>]
    let ``local-status revision reader rejects malformed durable evidence`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeNonQuery connection $"UPDATE meta SET value = 'invalid' WHERE key = '{LocalStateDb.LocalStatusRevisionMetaKey}';"

                let operation = Func<Task>(fun () -> LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile :> Task)

                Assert.ThrowsAsync<InvalidDataException>(operation)
                |> ignore
            })

    /// Verifies that replace status snapshot is atomic (rollback on failure).
    [<Test>]
    let ``replaceStatusSnapshot is atomic (rollback on failure)`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(111L)
                let lastWrite = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()

                let rootDirectory = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-dir-hash" [||] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDirectory) |> ignore

                let statusA =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDirectory.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusA
                let! revisionBeforeFailure = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeNonQuery connection "CREATE TRIGGER abort_status_files BEFORE INSERT ON status_files BEGIN SELECT RAISE(ABORT,'boom'); END;"

                let rootFile = createFileVersion "root.txt" "root-file-hash-NEW" false 1L now lastWrite

                let rootDirectoryB =
                    createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-dir-hash-NEW" [||] [| rootFile |] rootFile.Size lastWrite

                let indexB = GraceIndex()
                indexB.TryAdd(rootId, rootDirectoryB) |> ignore

                let statusB =
                    { statusA with
                        Index = indexB
                        RootDirectorySha256Hash = rootDirectoryB.Sha256Hash
                        LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(999L)
                    }

                let operation = Func<Task>(fun () -> task { do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusB } :> Task)

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.RootDirectorySha256Hash
                |> should equal statusA.RootDirectorySha256Hash

                readBack.LastSuccessfulFileUpload
                |> should equal statusA.LastSuccessfulFileUpload

                readBack.Index.Count |> should equal 1

                let! revisionAfterFailure = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile

                revisionAfterFailure
                |> should equal revisionBeforeFailure
            })

    /// Verifies a pending Working Directory Update completion durably joins the exact status and object-cache facts.
    [<Test>]
    let ``working directory update completion atomically persists matching local facts across sqlite restart`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "a")
                let blake3Hash = Blake3Hash(String.replicate 64 "b")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 123L
                let target, operation, completionDetails = completionTargetAndOperation configuration rootId sha256Hash blake3Hash

                let! revision =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        completionDetails
                        target
                        operation

                revision |> should equal 1L

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                |> should equal 1

                executeScalarIntWithTextParameter
                    connection
                    "SELECT COUNT(*) FROM status_directories WHERE directory_version_id = $root_id;"
                    "$root_id"
                    (rootId.ToString())
                |> should equal 1

                executeScalarIntWithTextParameter
                    connection
                    "SELECT COUNT(*) FROM object_cache_directories WHERE directory_version_id = $root_id;"
                    "$root_id"
                    (rootId.ToString())
                |> should equal 1

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions WHERE finalization_state = 'Pending';"
                |> should equal 1

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation

                completion
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending)
            })

    /// Verifies restart reconstructs exact Branch and Watch finalizers and rejects altered persisted facts.
    [<Test>]
    let ``working directory update pending finalization reconstructs typed Branch and Watch facts after restart`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "c")
                let blake3Hash = Blake3Hash(String.replicate 64 "d")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 223L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let previousBranchId = configuration.BranchId
                let selectedReferenceId = Guid.NewGuid()

                let branchOperation =
                    WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId target
                    |> requiredWorkingDirectoryUpdate

                let branchCompletionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, selectedReferenceId)

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        branchCompletionDetails
                        target
                        branchOperation

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let! branchPending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile

                match branchPending with
                | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (persistedTarget,
                                                                                                          persistedOperation,
                                                                                                          persistedPreviousBranchId,
                                                                                                          persistedSelection)) ->
                    WorkingDirectoryUpdate.Target.canonical persistedTarget
                    |> should equal (WorkingDirectoryUpdate.Target.canonical target)

                    WorkingDirectoryUpdate.Operation.value persistedOperation
                    |> should equal (WorkingDirectoryUpdate.Operation.value branchOperation)

                    persistedPreviousBranchId
                    |> should equal previousBranchId

                    persistedSelection
                    |> should equal (WorkingDirectoryUpdate.BranchSelection.Reference selectedReferenceId)
                | _ -> failwith "Expected the persisted Branch finalizer after restart."

                do! LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target branchOperation

                let watchCursor = "typed-watch-cursor-001"

                let watchOperation =
                    WorkingDirectoryUpdate.Operation.watchReplay configuration.RepositoryId configuration.BranchId watchCursor
                    |> requiredWorkingDirectoryUpdate

                let watchCompletionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.WatchFinalization watchCursor

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        watchCompletionDetails
                        target
                        watchOperation

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let! watchPending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile

                match watchPending with
                | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingWatchFinalization (persistedTarget, persistedOperation, persistedCursor)) ->
                    WorkingDirectoryUpdate.Target.canonical persistedTarget
                    |> should equal (WorkingDirectoryUpdate.Target.canonical target)

                    WorkingDirectoryUpdate.Operation.value persistedOperation
                    |> should equal (WorkingDirectoryUpdate.Operation.value watchOperation)

                    persistedCursor |> should equal watchCursor
                | _ -> failwith "Expected the persisted Watch finalizer after restart."

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery
                        connection
                        "UPDATE working_directory_update_completions SET watch_event_cursor = 'altered-watch-cursor' WHERE finalization_state = 'Pending';"

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile

                let alteredRead = Func<Task>(fun () -> LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile :> Task)

                Assert.ThrowsAsync<InvalidOperationException>(alteredRead)
                |> ignore
            })

    /// Proves reopening a hash-selected Branch finalization retains its typed no-Reference selector.
    [<Test>]
    let ``working directory update pending finalization reconstructs DirectoryVersion Branch selection after restart`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "c")
                let blake3Hash = Blake3Hash(String.replicate 64 "d")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 224L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let previousBranchId = configuration.BranchId

                let operation =
                    WorkingDirectoryUpdate.Operation.branchSwitchWithSelection previousBranchId WorkingDirectoryUpdate.BranchSelection.DirectoryVersion target
                    |> requiredWorkingDirectoryUpdate

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization previousBranchId)
                        target
                        operation

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let! pending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile

                match pending with
                | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (persistedTarget,
                                                                                                          persistedOperation,
                                                                                                          persistedPreviousBranchId,
                                                                                                          WorkingDirectoryUpdate.BranchSelection.DirectoryVersion)) ->
                    persistedPreviousBranchId
                    |> should equal previousBranchId

                    WorkingDirectoryUpdate.Target.canonical persistedTarget
                    |> should equal (WorkingDirectoryUpdate.Target.canonical target)

                    WorkingDirectoryUpdate.Operation.value persistedOperation
                    |> should equal (WorkingDirectoryUpdate.Operation.value operation)
                | _ -> failwith "Expected the persisted hash-selected Branch finalizer after restart."
            })

    /// Proves persisted DirectoryVersion selections reject when their previous Branch no longer matches the exact target Branch.
    [<Test>]
    let ``working directory update pending DirectoryVersion Branch corruption rejects on reopen`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "e")
                let blake3Hash = Blake3Hash(String.replicate 64 "f")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 225L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let operation =
                    WorkingDirectoryUpdate.Operation.branchSwitchWithSelection
                        configuration.BranchId
                        WorkingDirectoryUpdate.BranchSelection.DirectoryVersion
                        target
                    |> requiredWorkingDirectoryUpdate

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization configuration.BranchId)
                        target
                        operation

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeNonQuery
                        connection
                        $"UPDATE working_directory_update_completions SET branch_previous_branch_id = '{Guid.NewGuid()}' WHERE finalization_state = 'Pending';"

                assertPendingFinalizationReopenRejects
                    configuration
                    "Pending Branch finalization is invalid: DirectoryVersion Branch selection must retain the current Branch."
            })

    /// Proves impossible persisted selector and Reference combinations reject during strict pending-row reconstruction.
    [<Test>]
    let ``working directory update pending Branch selector corruption rejects on reopen`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "e")
                let blake3Hash = Blake3Hash(String.replicate 64 "f")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 225L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let previousBranchId = configuration.BranchId
                let selectedReferenceId = Guid.NewGuid()

                let operation =
                    WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId target
                    |> requiredWorkingDirectoryUpdate

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, selectedReferenceId))
                        target
                        operation

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "PRAGMA ignore_check_constraints = ON;"

                    executeNonQuery
                        connection
                        "UPDATE working_directory_update_completions SET branch_selection_kind = 'DirectoryVersion' WHERE finalization_state = 'Pending';"

                    executeNonQuery connection "PRAGMA ignore_check_constraints = OFF;"

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile

                assertPendingFinalizationReopenRejects configuration "DirectoryVersion Branch finalization must not persist a Reference id."
            })

    /// Proves restart validation rejects independently mutated target, operation, and caller facts from an otherwise valid pending row.
    [<TestCase("target")>]
    [<TestCase("operation")>]
    [<TestCase("caller")>]
    let ``working directory update pending finalization rejects each corrupted persisted identity fact`` (corruptedFact: string) =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "a")
                let blake3Hash = Blake3Hash(String.replicate 64 "b")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 226L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let operation =
                    WorkingDirectoryUpdate.Operation.branchSwitchWithSelection
                        configuration.BranchId
                        WorkingDirectoryUpdate.BranchSelection.DirectoryVersion
                        target
                    |> requiredWorkingDirectoryUpdate

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization configuration.BranchId)
                        target
                        operation

                let expectedMessage =
                    use connection = openRawConnection configuration.GraceStatusFile

                    match corruptedFact with
                    | "target" ->
                        executeNonQuery
                            connection
                            "UPDATE working_directory_update_completions SET target_root_directory_sha256_hash = 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc' WHERE finalization_state = 'Pending';"

                        "Pending Working Directory Update finalization target facts do not match their canonical target."
                    | "operation" ->
                        executeNonQuery
                            connection
                            "UPDATE working_directory_update_completions SET operation_value = 'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc' WHERE finalization_state = 'Pending';"

                        "Pending Working Directory Update finalization facts do not match their operation identity."
                    | "caller" ->
                        executeNonQuery connection "PRAGMA ignore_check_constraints = ON;"

                        executeNonQuery
                            connection
                            "UPDATE working_directory_update_completions SET caller_kind = 'Connect' WHERE finalization_state = 'Pending';"

                        executeNonQuery connection "PRAGMA ignore_check_constraints = OFF;"
                        "Connect completion must be terminal and cannot be a pending finalization."
                    | value -> failwith $"Unexpected persisted identity fact '{value}'."

                assertPendingFinalizationReopenRejects configuration expectedMessage
            })

    /// Verifies Connect cursor progress and its terminal completion cannot commit separately from matching local facts.
    [<Test>]
    let ``working directory update completion atomically persists connect cursor`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "c")
                let blake3Hash = Blake3Hash(String.replicate 64 "d")
                let cursor = "connect-cursor-001"
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 234L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let localRootScope =
                    WorkingDirectoryUpdate.LocalRootScope.create configuration.RootDirectory
                    |> requiredWorkingDirectoryUpdate

                let operation =
                    WorkingDirectoryUpdate.Operation.connectBootstrap target cursor localRootScope
                    |> requiredWorkingDirectoryUpdate

                let completionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.ConnectCompletion(cursor, localRootScope)

                let mismatchedIdentityCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.ConnectCompletion("different-connect-cursor", localRootScope))
                            target
                            operation
                        :> Task)

                Assert.ThrowsAsync<ArgumentException>(mismatchedIdentityCommit)
                |> ignore

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        completionDetails
                        target
                        operation

                let nextCursor = "connect-cursor-002"

                let nextOperation =
                    WorkingDirectoryUpdate.Operation.connectBootstrap target nextCursor localRootScope
                    |> requiredWorkingDirectoryUpdate

                let nextCompletionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.ConnectCompletion(nextCursor, localRootScope)

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        nextCompletionDetails
                        target
                        nextOperation

                SqliteConnection.ClearAllPools()
                LocalStateDb.invalidateInitializationCacheForLocalStateRepair configuration.GraceStatusFile
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                let! persistedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                let! persistedBoundary =
                    LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                persistedStatus.RootDirectoryId
                |> should equal rootId

                persistedStatus.RootDirectorySha256Hash
                |> should equal sha256Hash

                persistedStatus.RootDirectoryBlake3Hash
                |> should equal blake3Hash

                match persistedBoundary with
                | Some boundary ->
                    boundary.RepositoryId
                    |> should equal configuration.RepositoryId

                    boundary.BranchId
                    |> should equal configuration.BranchId

                    boundary.DirectoryId |> should equal rootId
                    boundary.Sha256Hash |> should equal sha256Hash
                    boundary.Blake3Hash |> should equal blake3Hash
                    boundary.EventCursor |> should equal nextCursor
                | None -> failwith "Expected Connect cursor boundary after reopening the local state database."

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    executeScalarIntWithTextParameter
                        connection
                        "SELECT COUNT(*) FROM object_cache_directories WHERE directory_version_id = $root_id;"
                        "$root_id"
                        (rootId.ToString())
                    |> should equal 1

                let! supersededCompletion = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target nextOperation

                supersededCompletion |> should equal None

                completion
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)
            })

    /// Proves completion writes reject selector disagreement and the impossible DirectoryVersion target/previous-Branch tuple.
    [<Test>]
    let ``working directory update completion rejects mismatched Branch selector and DirectoryVersion Branch retention on write`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "7")
                let blake3Hash = Blake3Hash(String.replicate 64 "8")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 344L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let previousBranchId = configuration.BranchId
                let selectedReferenceId = Guid.NewGuid()

                let directoryVersionOperation =
                    WorkingDirectoryUpdate.Operation.branchSwitchWithSelection previousBranchId WorkingDirectoryUpdate.BranchSelection.DirectoryVersion target
                    |> requiredWorkingDirectoryUpdate

                let referenceOperation =
                    WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId target
                    |> requiredWorkingDirectoryUpdate

                let referenceDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, selectedReferenceId)

                let directoryVersionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization previousBranchId

                let mismatchedReferenceWrite =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            referenceDetails
                            target
                            directoryVersionOperation
                        :> Task)

                let mismatchedDirectoryVersionWrite =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            directoryVersionDetails
                            target
                            referenceOperation
                        :> Task)

                Assert.ThrowsAsync<ArgumentException>(mismatchedReferenceWrite)
                |> ignore

                Assert.ThrowsAsync<ArgumentException>(mismatchedDirectoryVersionWrite)
                |> ignore

                let otherBranchTarget =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId (Guid.NewGuid()) rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let referenceOperationForOtherBranch =
                    WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId otherBranchTarget
                    |> requiredWorkingDirectoryUpdate

                let invalidDirectoryVersionWrite =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization previousBranchId)
                            otherBranchTarget
                            referenceOperationForOtherBranch
                        :> Task)

                let thrownException = Assert.ThrowsAsync<ArgumentException>(invalidDirectoryVersionWrite)

                thrownException.Message
                |> should equal "DirectoryVersion Branch completion must retain the current Branch. (Parameter 'completionDetails')"
            })

    /// Verifies an injected pre-commit failure rolls back every local fact in the update completion transaction.
    [<Test>]
    let ``working directory update pre-commit failure leaves no completion facts`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                let! revisionBefore = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "e")
                let blake3Hash = Blake3Hash(String.replicate 64 "f")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 345L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let cursor = "rollback-connect-cursor"

                let localRootScope =
                    WorkingDirectoryUpdate.LocalRootScope.create configuration.RootDirectory
                    |> requiredWorkingDirectoryUpdate

                let operation =
                    WorkingDirectoryUpdate.Operation.connectBootstrap target cursor localRootScope
                    |> requiredWorkingDirectoryUpdate

                let completionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.ConnectCompletion(cursor, localRootScope)

                let failingCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletionWithBeforeCommit
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            completionDetails
                            target
                            operation
                            (fun () -> raise (InvalidOperationException("injected before commit")))
                        :> Task)

                Assert.ThrowsAsync<InvalidOperationException>(failingCommit)
                |> ignore

                let! persistedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                let! revisionAfter = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile

                persistedStatus.RootDirectoryId
                |> should equal DirectoryVersionId.Empty

                revisionAfter |> should equal revisionBefore

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM status_directories;"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM object_cache_directories;"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM remote_reference_boundaries;"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions;"
                |> should equal 0
            })

    /// Verifies one mismatched target hash rejects the whole completion before any durable row appears.
    [<Test>]
    let ``working directory update completion rejects one-hash status mismatch`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "1")
                let targetBlake3Hash = Blake3Hash(String.replicate 64 "2")
                let statusBlake3Hash = Blake3Hash(String.replicate 64 "3")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash statusBlake3Hash 456L
                let target, operation, completionDetails = completionTargetAndOperation configuration rootId sha256Hash targetBlake3Hash

                let mismatchedCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            completionDetails
                            target
                            operation
                        :> Task)

                Assert.ThrowsAsync<ArgumentException>(mismatchedCommit)
                |> ignore

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions;"
                |> should equal 0
            })

    /// Verifies object-cache root metadata cannot diverge by one hash from matching status and target facts.
    [<Test>]
    let ``working directory update completion rejects one-hash object metadata mismatch`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "6")
                let blake3Hash = Blake3Hash(String.replicate 64 "7")
                let mismatchedBlake3Hash = Blake3Hash(String.replicate 64 "8")
                let status, _ = completionStatus configuration rootId sha256Hash blake3Hash 457L
                let target, operation, completionDetails = completionTargetAndOperation configuration rootId sha256Hash blake3Hash

                let mismatchedRoot =
                    LocalDirectoryVersion.CreateWithHashes
                        rootId
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId
                        Constants.RootDirectoryPath
                        sha256Hash
                        mismatchedBlake3Hash
                        (List<DirectoryVersionId>())
                        (List<LocalFileVersion>())
                        0L
                        (DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc))

                let mismatchedCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ mismatchedRoot ]
                            completionDetails
                            target
                            operation
                        :> Task)

                Assert.ThrowsAsync<ArgumentException>(mismatchedCommit)
                |> ignore

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM object_cache_directories;"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions;"
                |> should equal 0
            })

    /// Verifies repository and root identity mismatches reject completion before any durable local fact appears.
    [<Test>]
    let ``working directory update completion rejects direct repository and root mismatches`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "9")
                let blake3Hash = Blake3Hash(String.replicate 64 "a")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 478L

                let branchCompletion target =
                    let previousBranchId = Guid.NewGuid()
                    let selectedReferenceId = Guid.NewGuid()

                    let operation =
                        WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId target
                        |> requiredWorkingDirectoryUpdate

                    let completionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, selectedReferenceId)

                    operation, completionDetails

                let repositoryMismatchTarget =
                    WorkingDirectoryUpdate.Target.create (Guid.NewGuid()) configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let repositoryMismatchOperation, repositoryMismatchCompletionDetails = branchCompletion repositoryMismatchTarget

                let repositoryMismatchCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            repositoryMismatchCompletionDetails
                            repositoryMismatchTarget
                            repositoryMismatchOperation
                        :> Task)

                Assert.ThrowsAsync<ArgumentException>(repositoryMismatchCommit)
                |> ignore

                let rootMismatchTarget =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId (Guid.NewGuid()) sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let rootMismatchOperation, rootMismatchCompletionDetails = branchCompletion rootMismatchTarget

                let rootMismatchCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            rootMismatchCompletionDetails
                            rootMismatchTarget
                            rootMismatchOperation
                        :> Task)

                Assert.ThrowsAsync<ArgumentException>(rootMismatchCommit)
                |> ignore

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM status_directories;"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM object_cache_directories;"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions;"
                |> should equal 0
            })

    /// Verifies pending completion is never displaced and terminal supersession stays scoped to one caller kind.
    [<Test>]
    let ``working directory update completion retention preserves pending and other callers`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let sha256Hash = Sha256Hash(String.replicate 64 "4")
                let blake3Hash = Blake3Hash(String.replicate 64 "5")
                let status, rootDirectory = completionStatus configuration rootId sha256Hash blake3Hash 567L

                let target =
                    WorkingDirectoryUpdate.Target.create configuration.RepositoryId configuration.BranchId rootId sha256Hash blake3Hash
                    |> requiredWorkingDirectoryUpdate

                let branchOperation () =
                    let previousBranchId = Guid.NewGuid()
                    let selectedReferenceId = Guid.NewGuid()

                    let operation =
                        WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId target
                        |> requiredWorkingDirectoryUpdate

                    let completionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, selectedReferenceId)

                    operation, completionDetails

                let firstBranch, firstBranchCompletionDetails = branchOperation ()

                let watch =
                    WorkingDirectoryUpdate.Operation.watchReplay configuration.RepositoryId configuration.BranchId "watch-cursor-001"
                    |> requiredWorkingDirectoryUpdate

                let watchCompletionDetails = LocalStateDb.WorkingDirectoryUpdateCompletionDetails.WatchFinalization "watch-cursor-001"

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        firstBranchCompletionDetails
                        target
                        firstBranch

                do! LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target firstBranch

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        watchCompletionDetails
                        target
                        watch

                do! LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target watch

                let secondBranch, secondBranchCompletionDetails = branchOperation ()

                let! _ =
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        configuration.GraceStatusFile
                        status
                        [ rootDirectory ]
                        secondBranchCompletionDetails
                        target
                        secondBranch

                let displacedPending, displacedPendingCompletionDetails = branchOperation ()

                let conflictingCommit =
                    Func<Task> (fun () ->
                        LocalStateDb.commitWorkingDirectoryUpdateCompletion
                            configuration.GraceStatusFile
                            status
                            [ rootDirectory ]
                            displacedPendingCompletionDetails
                            target
                            displacedPending
                        :> Task)

                Assert.ThrowsAsync<InvalidOperationException>(conflictingCommit)
                |> ignore

                let! pending = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target secondBranch
                let! firstBranchBeforeFinalization = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target firstBranch
                let! watchBeforeFinalization = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target watch

                pending
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending)

                firstBranchBeforeFinalization
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

                watchBeforeFinalization
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

                do! LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target secondBranch

                let! firstBranchAfterFinalization = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target firstBranch
                let! secondBranchAfterFinalization = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target secondBranch
                let! watchAfterFinalization = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target watch

                firstBranchAfterFinalization |> should equal None

                secondBranchAfterFinalization
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

                watchAfterFinalization
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

                use connection = openRawConnection configuration.GraceStatusFile

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions WHERE finalization_state = 'Pending';"
                |> should equal 0

                executeScalarInt connection "SELECT COUNT(*) FROM working_directory_update_completions WHERE finalization_state = 'Terminal';"
                |> should equal 2
            })

    /// Verifies that apply status incremental is atomic (rollback on failure).
    [<Test>]
    let ``applyStatusIncremental is atomic (rollback on failure)`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(1000L)
                let lastWrite = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let rootDirectory = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-dir-hash" [| srcId |] [||] 0L lastWrite

                let srcDirectory = createDirectoryVersion configuration srcId "src" "src-dir-hash" [||] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDirectory) |> ignore
                index.TryAdd(srcId, srcDirectory) |> ignore

                let statusA =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDirectory.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusA
                let! revisionBeforeFailure = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile

                executeNonQuery connection "CREATE TRIGGER abort_status_files BEFORE INSERT ON status_files BEGIN SELECT RAISE(ABORT,'boom'); END;"

                let newFile = createFileVersion "src/file.txt" "hash-1" false 11L now lastWrite

                let updatedSrc = createDirectoryVersion configuration srcId "src" "src-dir-hash" [||] [| newFile |] newFile.Size lastWrite

                let updatedStatus = { statusA with RootDirectorySha256Hash = "root-dir-hash-NEW"; LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(2222L) }

                let differences =
                    [
                        FileSystemDifference.Create Add FileSystemEntryType.File "src/file.txt"
                    ]

                let operation =
                    Func<Task> (fun () ->
                        task { do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile updatedStatus [ updatedSrc ] differences } :> Task)

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                let! meta = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                meta.RootDirectorySha256Hash
                |> should equal statusA.RootDirectorySha256Hash

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.Index.Values
                |> Seq.collect (fun dv -> dv.Files)
                |> Seq.exists (fun file -> file.RelativePath = "src/file.txt")
                |> should equal false

                let! revisionAfterFailure = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile

                revisionAfterFailure
                |> should equal revisionBeforeFailure
            })

    /// Verifies that concurrent ensure db initialized calls do not deadlock or corrupt.
    [<Test>]
    let ``concurrent ensureDbInitialized calls do not deadlock or corrupt`` () =
        withTempDir (fun _ configuration ->
            task {
                let tasks = Array.init 16 (fun _ -> LocalStateDb.ensureDbInitialized configuration.GraceStatusFile)

                do!
                    Task
                        .WhenAll(tasks |> Array.map (fun t -> t :> Task))
                        .WaitAsync(TimeSpan.FromSeconds(15.0))

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion

                let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                statusMetaCount |> should equal 1
            })

    /// Verifies that ensure db initialized treats paths case insensitively on windows.
    [<Test; Category("LocalStatePathComparison")>]
    let ``ensureDbInitialized treats paths case-insensitively on Windows`` () =
        withTempDir (fun _ configuration ->
            task {
                if not (OperatingSystem.IsWindows()) then
                    Assert.Ignore("Windows path aliases are case-insensitive only on Windows.")

                let pathA = configuration.GraceStatusFile.ToLowerInvariant()
                let pathB = configuration.GraceStatusFile.ToUpperInvariant()

                let tasks =
                    [|
                        LocalStateDb.ensureDbInitialized pathA
                        LocalStateDb.ensureDbInitialized pathB
                    |]

                do!
                    Task
                        .WhenAll(tasks |> Array.map (fun t -> t :> Task))
                        .WaitAsync(TimeSpan.FromSeconds(15.0))

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                schemaVersion
                |> should equal LocalStateDb.SchemaVersion
            })

    /// Verifies non-Windows filesystems can initialize case-distinct local-state paths without rewriting the temp root.
    [<Test; Category("LocalStatePathComparison")>]
    let ``ensureDbInitialized preserves case-distinct paths on non-Windows`` () =
        withTempDir (fun root configuration ->
            task {
                if OperatingSystem.IsWindows() then
                    Assert.Ignore("Case-distinct local-state paths are a non-Windows contract.")

                let alternateGraceDirectory = Path.Combine(root, Constants.GraceConfigDirectory.ToUpperInvariant())

                Directory.CreateDirectory(alternateGraceDirectory)
                |> ignore

                let alternatePath = Path.Combine(alternateGraceDirectory, Constants.GraceLocalStateDbFileName)

                do!
                    Task
                        .WhenAll(
                            [|
                                LocalStateDb.ensureDbInitialized configuration.GraceStatusFile :> Task
                                LocalStateDb.ensureDbInitialized alternatePath :> Task
                            |]
                        )
                        .WaitAsync(TimeSpan.FromSeconds(15.0))

                File.Exists(configuration.GraceStatusFile)
                |> should equal true

                File.Exists(alternatePath) |> should equal true

                Path.GetFullPath(configuration.GraceStatusFile)
                |> should not' (equal (Path.GetFullPath(alternatePath)))
            })

    /// Verifies that replace status snapshot fully clears old snapshot rows.
    [<Test>]
    let ``replaceStatusSnapshot fully clears old snapshot rows`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(123L)
                let lastWrite = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)

                let rootId1 = Guid.NewGuid()
                let srcId1 = Guid.NewGuid()
                let utilId1 = Guid.NewGuid()

                let file1 = createFileVersion "src/a.txt" "a" false 1L now lastWrite
                let file2 = createFileVersion "src/b.txt" "b" false 2L now lastWrite
                let file3 = createFileVersion "src/utils/c.txt" "c" false 3L now lastWrite

                let utilDir1 = createDirectoryVersion configuration utilId1 "src/utils" "util-hash" [||] [| file3 |] file3.Size lastWrite

                let srcDir1 =
                    createDirectoryVersion
                        configuration
                        srcId1
                        "src"
                        "src-hash"
                        [| utilId1 |]
                        [| file1; file2 |]
                        (file1.Size + file2.Size + utilDir1.Size)
                        lastWrite

                let rootDir1 = createDirectoryVersion configuration rootId1 Constants.RootDirectoryPath "root-hash" [| srcId1 |] [||] srcDir1.Size lastWrite

                let index1 = GraceIndex()
                index1.TryAdd(rootId1, rootDir1) |> ignore
                index1.TryAdd(srcId1, srcDir1) |> ignore
                index1.TryAdd(utilId1, utilDir1) |> ignore

                let status1 =
                    { GraceStatus.Default with
                        Index = index1
                        RootDirectoryId = rootId1
                        RootDirectorySha256Hash = rootDir1.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status1

                let rootId2 = Guid.NewGuid()

                let rootDir2 = createDirectoryVersion configuration rootId2 Constants.RootDirectoryPath "root-hash-2" [||] [||] 0L lastWrite

                let index2 = GraceIndex()
                index2.TryAdd(rootId2, rootDir2) |> ignore

                let status2 = { status1 with Index = index2; RootDirectoryId = rootId2; RootDirectorySha256Hash = rootDir2.Sha256Hash }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status2

                use connection = openRawConnection configuration.GraceStatusFile
                let directoryCount = executeScalarInt connection "SELECT COUNT(*) FROM status_directories;"
                let fileCount = executeScalarInt connection "SELECT COUNT(*) FROM status_files;"
                directoryCount |> should equal 1
                fileCount |> should equal 0
            })

    /// Verifies that replace status snapshot writes correct parent path values.
    [<Test>]
    let ``replaceStatusSnapshot writes correct parent_path values`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(456L)
                let lastWrite = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)

                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()
                let utilId = Guid.NewGuid()

                let utilDir = createDirectoryVersion configuration utilId "src/utils" "util-hash" [||] [||] 0L lastWrite

                let srcDir = createDirectoryVersion configuration srcId "src" "src-hash" [| utilId |] [||] 0L lastWrite

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [| srcId |] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore
                index.TryAdd(srcId, srcDir) |> ignore
                index.TryAdd(utilId, utilDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                use connection = openRawConnection configuration.GraceStatusFile
                let rootParent = executeScalarString connection "SELECT parent_path FROM status_directories WHERE relative_path = '.';"
                let srcParent = executeScalarString connection "SELECT parent_path FROM status_directories WHERE relative_path = 'src';"
                let utilParent = executeScalarString connection "SELECT parent_path FROM status_directories WHERE relative_path = 'src/utils';"

                rootParent |> should equal String.Empty

                srcParent
                |> should equal Constants.RootDirectoryPath

                utilParent |> should equal "src"
            })

    /// Verifies that read status snapshot reconstructs child relationships.
    [<Test>]
    let ``readStatusSnapshot reconstructs child relationships`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(789L)
                let lastWrite = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)

                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()
                let utilId = Guid.NewGuid()

                let utilDir = createDirectoryVersion configuration utilId "src/utils" "util-hash" [||] [||] 0L lastWrite

                let srcDir = createDirectoryVersion configuration srcId "src" "src-hash" [| utilId |] [||] 0L lastWrite

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [| srcId |] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore
                index.TryAdd(srcId, srcDir) |> ignore
                index.TryAdd(utilId, utilDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status
                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                let rootRead = readBack.Index[rootId]

                rootRead.Directories.Contains(srcId)
                |> should equal true

                let srcRead = readBack.Index[srcId]

                srcRead.Directories.Contains(utilId)
                |> should equal true
            })

    /// Verifies that read status snapshot round trips last write ticks as utc.
    [<Test>]
    let ``readStatusSnapshot round-trips last write ticks as UTC`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(999L)
                let lastWrite = DateTime(2021, 10, 11, 12, 13, 14, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()

                let file = createFileVersion "root.txt" "hash" false 10L now lastWrite

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [||] [| file |] file.Size lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status
                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                let rootRead = readBack.Index[rootId]

                rootRead.LastWriteTimeUtc.Ticks
                |> should equal lastWrite.Ticks

                rootRead.LastWriteTimeUtc.Kind
                |> should equal DateTimeKind.Utc

                let fileRead =
                    rootRead.Files
                    |> Seq.find (fun f -> f.RelativePath = "root.txt")

                fileRead.LastWriteTimeUtc.Ticks
                |> should equal lastWrite.Ticks

                fileRead.LastWriteTimeUtc.Kind
                |> should equal DateTimeKind.Utc
            })

    /// Verifies that read status snapshot read only preserves persisted file blake3 hashes.
    [<Test>]
    let ``readStatusSnapshotReadOnly preserves persisted file blake3 hashes`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(999L)
                let lastWrite = DateTime(2021, 10, 11, 12, 13, 14, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let blake3Hash = "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
                let file = createFileVersionWithHashes "root.txt" "sha256-hash" blake3Hash false 10L now lastWrite
                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [||] [| file |] file.Size lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                let! readOnlyResult =
                    LocalStateDb.readStatusSnapshotReadOnly
                        configuration.GraceStatusFile
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId

                match readOnlyResult with
                | Ok readBack ->
                    let rootRead = readBack.Index[rootId]

                    let fileRead =
                        rootRead.Files
                        |> Seq.find (fun f -> f.RelativePath = "root.txt")

                    fileRead.Blake3Hash
                    |> should equal (Blake3Hash blake3Hash)

                    rootRead.Blake3Hash
                    |> should equal rootDir.Blake3Hash
                | Error error -> Assert.Fail($"Expected read-only snapshot to load, but got: {error}")
            })

    /// Verifies that read status snapshot read only rejects legacy sha only schema with reset guidance.
    [<Test>]
    let ``readStatusSnapshotReadOnly rejects legacy sha-only schema with reset guidance`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '3');"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"

                    executeNonQuery
                        connection
                        $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', 'root-sha', {ticks}, {ticks});"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_directories (relative_path TEXT PRIMARY KEY, parent_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_files (relative_path TEXT PRIMARY KEY, directory_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

                let! readOnlyResult =
                    LocalStateDb.readStatusSnapshotReadOnly
                        configuration.GraceStatusFile
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId

                match readOnlyResult with
                | Ok _ -> Assert.Fail("Expected legacy SHA-only local state to be rejected.")
                | Error error ->
                    error
                    |> should contain "schema version is incompatible"

                    error
                    |> should contain "reset the local state database"
            })

    /// Verifies that read status snapshot read only rejects partial v4 object cache schema missing blake3 columns.
    [<Test>]
    let ``readStatusSnapshotReadOnly rejects partial v4 object-cache schema missing blake3 columns`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
                    executeNonQuery connection $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '{LocalStateDb.SchemaVersion}');"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, root_directory_blake3_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"

                    executeNonQuery
                        connection
                        $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', 'root-sha', 'root-blake3', {ticks}, {ticks});"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_directories (relative_path TEXT PRIMARY KEY, parent_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS status_files (relative_path TEXT PRIMARY KEY, directory_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS object_cache_directories (directory_version_id TEXT PRIMARY KEY, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"

                    executeNonQuery
                        connection
                        "CREATE TABLE IF NOT EXISTS object_cache_directory_files (directory_version_id TEXT NOT NULL, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, PRIMARY KEY (directory_version_id, relative_path));"

                let! readOnlyResult =
                    LocalStateDb.readStatusSnapshotReadOnly
                        configuration.GraceStatusFile
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId

                match readOnlyResult with
                | Ok _ -> Assert.Fail("Expected partial object-cache BLAKE3 schema to be rejected.")
                | Error error ->
                    error
                    |> should contain "object_cache_directories.blake3_hash"

                    error
                    |> should contain "object_cache_directory_files.blake3_hash"

                    error
                    |> should contain "reset the local state database"
            })

    /// Verifies that read status snapshot read only rejects empty persisted blake3 values with reset guidance.
    [<Test>]
    let ``readStatusSnapshotReadOnly rejects empty persisted blake3 values with reset guidance`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(1001L)
                let lastWrite = DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let file = createFileVersionWithHashes "root.txt" "sha256-hash" "file-blake3" false 10L now lastWrite
                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [||] [| file |] file.Size lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                use connection = openRawConnection configuration.GraceStatusFile
                executeNonQuery connection "UPDATE status_directories SET blake3_hash = '' WHERE relative_path = '.';"

                let! readOnlyResult =
                    LocalStateDb.readStatusSnapshotReadOnly
                        configuration.GraceStatusFile
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId

                match readOnlyResult with
                | Ok _ -> Assert.Fail("Expected empty BLAKE3 local state to be rejected.")
                | Error error ->
                    error |> should contain "empty BLAKE3 values"

                    error
                    |> should contain "reset the local state database"
            })

    /// Verifies Watch's shared local-state reader accepts a complete nested graph and rejects truncation or malformed relationships.
    [<TestCase("valid")>]
    [<TestCase("truncated")>]
    [<TestCase("disconnected")>]
    [<TestCase("duplicate-path")>]
    [<TestCase("malformed-file-parent")>]
    [<Category("CompleteLocalStatusTree")>]
    let ``complete status reader validates the full rooted graph`` shape =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(2001L)
                let lastWrite = DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let childId = Guid.NewGuid()
                let file = createFileVersionWithHashes "src/nested.txt" "file-sha" "file-blake3" false 12L now lastWrite

                let childEntries =
                    [|
                        Grace.Shared.Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash
                    |]

                let childSha = Grace.Shared.Services.computeSha256ForDirectoryEntries "src" childEntries
                let childBlake3 = Grace.Shared.Services.computeBlake3ForDirectory "src" childEntries

                let child =
                    LocalDirectoryVersion.CreateWithHashes
                        childId
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId
                        "src"
                        childSha
                        childBlake3
                        (List<DirectoryVersionId>())
                        (List<LocalFileVersion>([| file |]))
                        file.Size
                        lastWrite

                let rootEntries =
                    [|
                        Grace.Shared.Services.DirectoryVersionPreimageEntry.Directory child.RelativePath child.Size child.Blake3Hash child.Sha256Hash
                    |]

                let rootSha = Grace.Shared.Services.computeSha256ForDirectoryEntries Constants.RootDirectoryPath rootEntries
                let rootBlake3 = Grace.Shared.Services.computeBlake3ForDirectory Constants.RootDirectoryPath rootEntries

                let root =
                    LocalDirectoryVersion.CreateWithHashes
                        rootId
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId
                        Constants.RootDirectoryPath
                        rootSha
                        rootBlake3
                        (List<DirectoryVersionId>([| childId |]))
                        (List<LocalFileVersion>())
                        child.Size
                        lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, root) |> ignore
                index.TryAdd(childId, child) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootSha
                        RootDirectoryBlake3Hash = rootBlake3
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                do
                    use connection = openRawConnection configuration.GraceStatusFile

                    match shape with
                    | "valid" -> ()
                    | "truncated" ->
                        executeNonQuery connection "DELETE FROM status_files WHERE relative_path = 'src/nested.txt';"
                        executeNonQuery connection "DELETE FROM status_directories WHERE relative_path = 'src';"
                    | "disconnected" -> executeNonQuery connection "UPDATE status_directories SET parent_path = 'missing' WHERE relative_path = 'src';"
                    | "duplicate-path" ->
                        executeNonQuery
                            connection
                            $"UPDATE status_files SET relative_path = 'src', directory_path = '.', directory_version_id = '{rootId}' WHERE relative_path = 'src/nested.txt';"
                    | "malformed-file-parent" ->
                        executeNonQuery connection "UPDATE status_files SET directory_path = '.' WHERE relative_path = 'src/nested.txt';"
                    | _ -> invalidOp $"Unexpected complete-tree test shape: {shape}"

                let! result =
                    LocalStateDb.readCompleteStatusSnapshotReadOnly
                        configuration.GraceStatusFile
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId

                match shape, result with
                | "valid", Ok readBack -> readBack.Index.Count |> should equal 2
                | "valid", Error error -> Assert.Fail($"Expected a valid complete status tree, got: {error}")
                | _, Error error -> error |> should contain "status tree"
                | _, Ok _ -> Assert.Fail($"Expected malformed status shape '{shape}' to fail closed.")
            })

    /// Verifies that read status snapshot tolerates missing status meta row.
    [<Test>]
    let ``readStatusSnapshot tolerates missing status_meta row`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(1234L)
                let lastWrite = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [||] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                use connection = openRawConnection configuration.GraceStatusFile
                executeNonQuery connection "DELETE FROM status_meta;"

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.RootDirectoryId
                |> should equal DirectoryVersionId.Empty

                readBack.RootDirectorySha256Hash
                |> should equal (Sha256Hash String.Empty)

                readBack.Index.Count |> should equal 1
            })

    /// Verifies that status files enforces directory version id.
    [<Test>]
    let ``status_files enforces directory_version_id`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                executeNonQuery connection "PRAGMA foreign_keys = ON;"

                executeNonQuery
                    connection
                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ('.', '', '00000000-0000-0000-0000-000000000001', 'root', '', 0, 0, 0);"

                let orphanId = Guid.NewGuid()

                Assert.Throws<SqliteException>(
                    Action (fun () ->
                        executeNonQuery
                            connection
                            $"INSERT OR REPLACE INTO status_files (relative_path, directory_path, directory_version_id, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ('orphan.txt', 'missing', '{orphanId}', 'hash', '', 0, 1, 0, 0, 0);")
                )
                |> ignore
            })

    /// Verifies that apply status incremental upserts add and change file values.
    [<Test>]
    let ``applyStatusIncremental upserts add and change file values`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(4000L)
                let lastWrite1 = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let lastWrite2 = DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [| srcId |] [||] 0L lastWrite1

                let file1 = LocalFileVersion.CreateWithHashes "src/file.txt" "hash-1" "blake3-1" true 10L now false lastWrite1

                let srcDir1 = createDirectoryVersion configuration srcId "src" "src-hash" [||] [| file1 |] file1.Size lastWrite1

                let status1 =
                    { GraceStatus.Default with
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do!
                    LocalStateDb.applyStatusIncremental
                        configuration.GraceStatusFile
                        status1
                        [ rootDir; srcDir1 ]
                        [
                            FileSystemDifference.Create Add FileSystemEntryType.File "src/file.txt"
                        ]

                let! readBack1 = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                let srcRead1 =
                    readBack1.Index.Values
                    |> Seq.find (fun dv -> dv.RelativePath = "src")

                let fileRead1 =
                    srcRead1.Files
                    |> Seq.find (fun f -> f.RelativePath = "src/file.txt")

                fileRead1.Sha256Hash |> should equal "hash-1"
                fileRead1.IsBinary |> should equal true

                fileRead1.UploadedToObjectStorage
                |> should equal false

                fileRead1.Size |> should equal 10L

                fileRead1.LastWriteTimeUtc.Ticks
                |> should equal lastWrite1.Ticks

                let file2 = LocalFileVersion.CreateWithHashes "src/file.txt" "hash-2" "blake3-2" false 25L now true lastWrite2

                let srcDir2 = createDirectoryVersion configuration srcId "src" "src-hash-2" [||] [| file2 |] file2.Size lastWrite2

                let status2 = { status1 with LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(5000L) }

                do!
                    LocalStateDb.applyStatusIncremental
                        configuration.GraceStatusFile
                        status2
                        [ srcDir2 ]
                        [
                            FileSystemDifference.Create Change FileSystemEntryType.File "src/file.txt"
                        ]

                let! readBack2 = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                let srcRead2 =
                    readBack2.Index.Values
                    |> Seq.find (fun dv -> dv.RelativePath = "src")

                let fileRead2 =
                    srcRead2.Files
                    |> Seq.find (fun f -> f.RelativePath = "src/file.txt")

                fileRead2.Sha256Hash |> should equal "hash-2"
                fileRead2.IsBinary |> should equal false

                fileRead2.UploadedToObjectStorage
                |> should equal true

                fileRead2.Size |> should equal 25L

                fileRead2.LastWriteTimeUtc.Ticks
                |> should equal lastWrite2.Ticks
            })

    /// Verifies that apply status incremental preserves root blake3 metadata for meta only status updates.
    [<Test>]
    let ``applyStatusIncremental preserves root blake3 metadata for meta-only status updates`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(5300L)
                let lastWrite = DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [||] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore

                let statusWithRoot =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusWithRoot

                let metaOnlyStatus =
                    { statusWithRoot with
                        Index = GraceIndex()
                        LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(5400L)
                        LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(5400L)
                    }

                do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile metaOnlyStatus [] []

                let! meta = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                meta.RootDirectoryBlake3Hash
                |> should equal rootDir.Blake3Hash

                meta.LastSuccessfulFileUpload
                |> should equal metaOnlyStatus.LastSuccessfulFileUpload
            })

    /// Verifies that apply status incremental does not preserve root blake3 metadata for different root identity.
    [<Test>]
    let ``applyStatusIncremental does not preserve root blake3 metadata for different root identity`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(5350L)
                let lastWrite = DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                let originalRootId = Guid.NewGuid()
                let replacementRootId = Guid.NewGuid()

                let originalRoot = createDirectoryVersion configuration originalRootId Constants.RootDirectoryPath "original-root-hash" [||] [||] 0L lastWrite

                let index = GraceIndex()

                index.TryAdd(originalRootId, originalRoot)
                |> ignore

                let statusWithRoot =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = originalRootId
                        RootDirectorySha256Hash = originalRoot.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusWithRoot

                let metaOnlyDifferentRootStatus =
                    { statusWithRoot with
                        Index = GraceIndex()
                        RootDirectoryId = replacementRootId
                        RootDirectorySha256Hash = Sha256Hash "replacement-root-hash"
                        LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(5360L)
                        LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(5360L)
                    }

                do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile metaOnlyDifferentRootStatus [] []

                let! meta = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                meta.RootDirectoryId
                |> should equal replacementRootId

                meta.RootDirectorySha256Hash
                |> should equal (Sha256Hash "replacement-root-hash")

                meta.RootDirectoryBlake3Hash
                |> should equal (Blake3Hash String.Empty)
            })

    /// Verifies that replace status snapshot writes empty root blake3 for default status.
    [<Test>]
    let ``replaceStatusSnapshot writes empty root blake3 for default status`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(5450L)
                let lastWrite = DateTime(2022, 2, 4, 5, 6, 7, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [||] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore

                let statusWithRoot =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusWithRoot
                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile GraceStatus.Default

                let! meta = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                meta.RootDirectoryId
                |> should equal DirectoryVersionId.Empty

                meta.RootDirectoryBlake3Hash
                |> should equal (Blake3Hash String.Empty)
            })

    /// Verifies that apply status incremental keeps unchanged files when directory version id changes.
    [<Test>]
    let ``applyStatusIncremental keeps unchanged files when directory version id changes`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(5500L)
                let lastWrite1 = DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc)
                let lastWrite2 = DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc)
                let rootId1 = Guid.NewGuid()
                let rootId2 = Guid.NewGuid()

                let originalLicense = createFileVersion "LICENSE.md" "license-hash-1" false 10L now lastWrite1
                let originalReadme = createFileVersion "README.md" "readme-hash-1" false 20L now lastWrite1

                let rootDir1 =
                    createDirectoryVersion
                        configuration
                        rootId1
                        Constants.RootDirectoryPath
                        "root-hash-1"
                        [||]
                        [| originalLicense; originalReadme |]
                        (originalLicense.Size + originalReadme.Size)
                        lastWrite1

                let index1 = GraceIndex()
                index1.TryAdd(rootId1, rootDir1) |> ignore

                let status1 =
                    { GraceStatus.Default with
                        Index = index1
                        RootDirectoryId = rootId1
                        RootDirectorySha256Hash = rootDir1.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status1

                let changedLicense = createFileVersion "LICENSE.md" "license-hash-2" false 15L now lastWrite2
                let unchangedReadme = createFileVersion "README.md" "readme-hash-1" false 20L now lastWrite1

                let rootDir2 =
                    createDirectoryVersion
                        configuration
                        rootId2
                        Constants.RootDirectoryPath
                        "root-hash-2"
                        [||]
                        [| changedLicense; unchangedReadme |]
                        (changedLicense.Size + unchangedReadme.Size)
                        lastWrite2

                let status2 =
                    { GraceStatus.Default with
                        RootDirectoryId = rootId2
                        RootDirectorySha256Hash = rootDir2.Sha256Hash
                        LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(5600L)
                        LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(5600L)
                    }

                do!
                    LocalStateDb.applyStatusIncremental
                        configuration.GraceStatusFile
                        status2
                        [ rootDir2 ]
                        [
                            FileSystemDifference.Create Change FileSystemEntryType.File "LICENSE.md"
                        ]

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                readBack.RootDirectoryId |> should equal rootId2

                let rootRead =
                    readBack.Index.Values
                    |> Seq.find (fun dv -> dv.RelativePath = Constants.RootDirectoryPath)

                rootRead.Files.Count |> should equal 2

                rootRead.Files
                |> Seq.exists (fun file ->
                    file.RelativePath = "LICENSE.md"
                    && file.Sha256Hash = "license-hash-2")
                |> should equal true

                rootRead.Files
                |> Seq.exists (fun file ->
                    file.RelativePath = "README.md"
                    && file.Sha256Hash = "readme-hash-1")
                |> should equal true
            })

    /// Verifies that apply status incremental delete file removes only the matching status file row.
    [<Test>]
    let ``applyStatusIncremental delete file removes only the matching status file row`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(6000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let deletedFile = createFileVersion "src/delete-me.txt" "hash-delete" false 1L now lastWrite
                let siblingFile = createFileVersion "src/delete-me-too.txt" "hash-keep" false 2L now lastWrite

                let srcDir =
                    createDirectoryVersion
                        configuration
                        srcId
                        "src"
                        "src-hash"
                        [||]
                        [| deletedFile; siblingFile |]
                        (deletedFile.Size + siblingFile.Size)
                        lastWrite

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [| srcId |] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore
                index.TryAdd(srcId, srcDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                do!
                    LocalStateDb.applyStatusIncremental
                        configuration.GraceStatusFile
                        status
                        Seq.empty
                        [
                            FileSystemDifference.Create Delete FileSystemEntryType.File "src/delete-me.txt"
                            FileSystemDifference.Create Delete FileSystemEntryType.File "src/delete-me.txt"
                            FileSystemDifference.Create Delete FileSystemEntryType.File "src/unknown.txt"
                        ]

                use connection = openRawConnection configuration.GraceStatusFile

                countStatusFileRows connection "src/delete-me.txt"
                |> should equal 0

                countStatusFileRows connection "src/delete-me-too.txt"
                |> should equal 1

                countStatusDirectoryRows connection "src"
                |> should equal 1
            })

    /// Verifies that apply status incremental delete directory removes only the matching status directory row.
    [<Test>]
    let ``applyStatusIncremental delete directory removes only the matching status directory row`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(7000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()
                let srcOldId = Guid.NewGuid()

                let srcDir = createDirectoryVersion configuration srcId "src" "src-hash" [||] [||] 0L lastWrite
                let srcOldDir = createDirectoryVersion configuration srcOldId "src-old" "src-old-hash" [||] [||] 0L lastWrite

                let rootDir = createDirectoryVersion configuration rootId Constants.RootDirectoryPath "root-hash" [| srcId; srcOldId |] [||] 0L lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore
                index.TryAdd(srcId, srcDir) |> ignore
                index.TryAdd(srcOldId, srcOldDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                do!
                    LocalStateDb.applyStatusIncremental
                        configuration.GraceStatusFile
                        status
                        Seq.empty
                        [
                            FileSystemDifference.Create Delete FileSystemEntryType.Directory "src"
                            FileSystemDifference.Create Delete FileSystemEntryType.Directory "src"
                            FileSystemDifference.Create Delete FileSystemEntryType.Directory "src-missing"
                        ]

                use connection = openRawConnection configuration.GraceStatusFile

                countStatusDirectoryRows connection "src"
                |> should equal 0

                countStatusDirectoryRows connection "src-old"
                |> should equal 1

                countStatusDirectoryRows connection Constants.RootDirectoryPath
                |> should equal 1
            })

    /// Verifies that apply status incremental subtree delete differences remove descendant status rows and preserve prefix siblings.
    [<Test>]
    let ``applyStatusIncremental subtree delete differences remove descendant status rows and preserve prefix siblings`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(8000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()
                let nestedId = Guid.NewGuid()
                let srcOldId = Guid.NewGuid()

                let srcFile = createFileVersion "src/delete-me.txt" "src-delete-hash" false 1L now lastWrite
                let nestedFile = createFileVersion "src/nested/delete-me.txt" "nested-delete-hash" false 2L now lastWrite
                let prefixSiblingFile = createFileVersion "src-old/keep-me.txt" "src-old-keep-hash" false 3L now lastWrite

                let nestedDir = createDirectoryVersion configuration nestedId "src/nested" "nested-hash" [||] [| nestedFile |] nestedFile.Size lastWrite

                let srcDir = createDirectoryVersion configuration srcId "src" "src-hash" [| nestedId |] [| srcFile |] (srcFile.Size + nestedDir.Size) lastWrite

                let srcOldDir =
                    createDirectoryVersion configuration srcOldId "src-old" "src-old-hash" [||] [| prefixSiblingFile |] prefixSiblingFile.Size lastWrite

                let rootDir =
                    createDirectoryVersion
                        configuration
                        rootId
                        Constants.RootDirectoryPath
                        "root-hash"
                        [| srcId; srcOldId |]
                        [||]
                        (srcDir.Size + srcOldDir.Size)
                        lastWrite

                let index = GraceIndex()
                index.TryAdd(rootId, rootDir) |> ignore
                index.TryAdd(srcId, srcDir) |> ignore
                index.TryAdd(nestedId, nestedDir) |> ignore
                index.TryAdd(srcOldId, srcOldDir) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = index
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootDir.Sha256Hash
                        LastSuccessfulFileUpload = now
                        LastSuccessfulDirectoryVersionUpload = now
                    }

                do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile status

                do!
                    LocalStateDb.applyStatusIncremental
                        configuration.GraceStatusFile
                        status
                        Seq.empty
                        [
                            FileSystemDifference.Create Delete FileSystemEntryType.File "src/nested/delete-me.txt"
                            FileSystemDifference.Create Delete FileSystemEntryType.Directory "src"
                            FileSystemDifference.Create Delete FileSystemEntryType.File "src/delete-me.txt"
                            FileSystemDifference.Create Delete FileSystemEntryType.Directory "src/nested"
                        ]

                use connection = openRawConnection configuration.GraceStatusFile

                countStatusDirectoryRows connection "src"
                |> should equal 0

                countStatusDirectoryRows connection "src/nested"
                |> should equal 0

                countStatusFileRows connection "src/delete-me.txt"
                |> should equal 0

                countStatusFileRows connection "src/nested/delete-me.txt"
                |> should equal 0

                countStatusDirectoryRows connection "src-old"
                |> should equal 1

                countStatusFileRows connection "src-old/keep-me.txt"
                |> should equal 1
            })

    /// Verifies that upsert object cache enforces foreign keys.
    [<Test>]
    let ``upsertObjectCache enforces foreign keys`` () =
        withTempDir (fun _ configuration ->
            task {
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let parentId = Guid.NewGuid()
                let missingChildId = Guid.NewGuid()

                let parentDir = createDirectoryVersion configuration parentId "src" "parent-hash" [| missingChildId |] [||] 0L lastWrite

                let operation = Func<Task>(fun () -> task { do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ parentDir ] } :> Task)

                Assert.ThrowsAsync<InvalidOperationException>(operation)
                |> ignore

                let! exists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile parentId
                exists |> should equal false
            })

    /// Verifies that upsert object cache supports parent before child order.
    [<Test>]
    let ``upsertObjectCache supports parent before child order`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(9000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let parentId = Guid.NewGuid()
                let childId = Guid.NewGuid()

                let file = createFileVersion "src/parent.txt" "hash-parent" false 1L now lastWrite
                let childDir = createDirectoryVersion configuration childId "src/child" "child-hash" [||] [||] 0L lastWrite
                let parentDir = createDirectoryVersion configuration parentId "src" "parent-hash" [| childId |] [| file |] file.Size lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ parentDir; childDir ]

                let! parentExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile parentId
                parentExists |> should equal true

                let! childExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile childId
                childExists |> should equal true

                use connection = openRawConnection configuration.GraceStatusFile

                let childLinkCount =
                    executeScalarInt
                        connection
                        $"SELECT COUNT(*) FROM object_cache_directory_children WHERE parent_directory_version_id = '{parentId}' AND child_directory_version_id = '{childId}';"

                childLinkCount |> should equal 1

                let fileCount =
                    executeScalarInt
                        connection
                        $"SELECT COUNT(*) FROM object_cache_directory_files WHERE directory_version_id = '{parentId}' AND relative_path = 'src/parent.txt';"

                fileCount |> should equal 1
            })

    /// Verifies that upsert object cache updates referenced child without fk violation.
    [<Test>]
    let ``upsertObjectCache updates referenced child without FK violation`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(9100L)
                let lastWrite = DateTime(2022, 1, 3, 4, 5, 6, DateTimeKind.Utc)
                let parentId = Guid.NewGuid()
                let childId = Guid.NewGuid()

                let childDirV1 = createDirectoryVersion configuration childId "src/child" "child-hash-v1" [||] [||] 0L lastWrite
                let parentDir = createDirectoryVersion configuration parentId "src" "parent-hash" [| childId |] [||] 0L lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ parentDir; childDirV1 ]

                let childFile = createFileVersion "src/child/file.txt" "child-file-hash-v2" false 2L now lastWrite

                let childDirV2 = createDirectoryVersion configuration childId "src/child" "child-hash-v2" [||] [| childFile |] childFile.Size lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ childDirV2 ]

                use connection = openRawConnection configuration.GraceStatusFile

                let childLinkCount =
                    executeScalarInt
                        connection
                        $"SELECT COUNT(*) FROM object_cache_directory_children WHERE parent_directory_version_id = '{parentId}' AND child_directory_version_id = '{childId}';"

                childLinkCount |> should equal 1

                let childHash = executeScalarString connection $"SELECT sha256_hash FROM object_cache_directories WHERE directory_version_id = '{childId}';"

                childHash |> should equal "child-hash-v2"

                let childFileCount =
                    executeScalarInt
                        connection
                        $"SELECT COUNT(*) FROM object_cache_directory_files WHERE directory_version_id = '{childId}' AND relative_path = 'src/child/file.txt';"

                childFileCount |> should equal 1
            })

    /// Verifies that remove object cache directory cascades to children and files.
    [<Test>]
    let ``removeObjectCacheDirectory cascades to children and files`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(9000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)

                let parentId = Guid.NewGuid()
                let childId = Guid.NewGuid()

                let file = createFileVersion "src/cache.txt" "hash" false 1L now lastWrite

                let childDir = createDirectoryVersion configuration childId "src/child" "child-hash" [||] [||] 0L lastWrite

                let parentDir = createDirectoryVersion configuration parentId "src" "parent-hash" [| childId |] [| file |] file.Size lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ childDir; parentDir ]

                do! LocalStateDb.removeObjectCacheDirectory configuration.GraceStatusFile parentId

                use connection = openRawConnection configuration.GraceStatusFile

                use childrenCmd = connection.CreateCommand()
                childrenCmd.CommandText <- "SELECT COUNT(*) FROM object_cache_directory_children WHERE parent_directory_version_id = $id;"

                childrenCmd.Parameters.AddWithValue("$id", parentId.ToString())
                |> ignore

                let childrenCount = childrenCmd.ExecuteScalar() |> Convert.ToInt32
                childrenCount |> should equal 0

                use filesCmd = connection.CreateCommand()
                filesCmd.CommandText <- "SELECT COUNT(*) FROM object_cache_directory_files WHERE directory_version_id = $id;"

                filesCmd.Parameters.AddWithValue("$id", parentId.ToString())
                |> ignore

                let filesCount = filesCmd.ExecuteScalar() |> Convert.ToInt32
                filesCount |> should equal 0

                let! parentExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile parentId
                parentExists |> should equal false

                let! childExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile childId
                childExists |> should equal true
            })

    /// Verifies that remove object cache directory respects restrict when child is referenced.
    [<Test>]
    let ``removeObjectCacheDirectory respects RESTRICT when child is referenced`` () =
        withTempDir (fun _ configuration ->
            task {
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)

                let parentId = Guid.NewGuid()
                let childId = Guid.NewGuid()

                let childDir = createDirectoryVersion configuration childId "src/child" "child-hash" [||] [||] 0L lastWrite

                let parentDir = createDirectoryVersion configuration parentId "src" "parent-hash" [| childId |] [||] 0L lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ childDir; parentDir ]

                let operation = Func<Task>(fun () -> task { do! LocalStateDb.removeObjectCacheDirectory configuration.GraceStatusFile childId } :> Task)

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                let! stillExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile childId
                stillExists |> should equal true
            })

    /// Verifies that multi process writers do not crash or corrupt database.
    [<Test>]
    let ``multi-process writers do not crash or corrupt database`` () =
        withTempDir (fun _ configuration ->
            Task.Run<unit> (fun () ->
                match tryGetWorkerCommand () with
                | None -> Assert.Ignore("Worker binary was not found; build the solution before running this test.")
                | Some worker ->
                    let dbPath = configuration.GraceStatusFile
                    let rootId = Guid.NewGuid()
                    let rootHash = "root-hash"
                    let processCount = 4
                    let iterationsPerProcess = 25

                    let processes =
                        Array.init processCount (fun _ ->
                            let startInfo = ProcessStartInfo()
                            startInfo.FileName <- worker.FileName

                            let rootBlake3Hash = "root-blake3"

                            startInfo.Arguments <- $"{worker.ArgumentsPrefix} \"{dbPath}\" {rootId} {rootHash} {rootBlake3Hash} {iterationsPerProcess}"

                            startInfo.RedirectStandardOutput <- true
                            startInfo.RedirectStandardError <- true
                            startInfo.UseShellExecute <- false
                            startInfo.CreateNoWindow <- true

                            let proc = new Process()
                            proc.StartInfo <- startInfo

                            if not (proc.Start()) then failwith "Failed to start worker process."

                            proc)

                    /// Tracks failed changes so this scenario can assert the resulting side effect explicitly.
                    let mutable failed = false
                    let failures = List<string>()

                    processes
                    |> Array.iter (fun proc ->
                        if not failed then
                            if not (proc.WaitForExit(30000)) then
                                failed <- true

                                try
                                    proc.Kill(true)
                                with
                                | _ -> ()

                                failures.Add("Worker process timed out.")
                            elif proc.ExitCode <> 0 then
                                failed <- true
                                let stdout = proc.StandardOutput.ReadToEnd()
                                let stderr = proc.StandardError.ReadToEnd()
                                failures.Add($"Worker exit code {proc.ExitCode}. stdout={stdout} stderr={stderr}")

                        proc.Dispose())

                    if failed then Assert.Fail(String.Join(Environment.NewLine, failures))

                    use connection = openRawConnection dbPath
                    let integrity = executeScalarString connection "PRAGMA integrity_check;"
                    integrity.ToLowerInvariant() |> should equal "ok"

                    let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"

                    schemaVersion
                    |> should equal LocalStateDb.SchemaVersion

                    let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                    statusMetaCount |> should equal 1

                    let meta =
                        LocalStateDb.readStatusMeta dbPath
                        |> fun task -> task.GetAwaiter().GetResult()

                    meta.RootDirectoryId |> should equal rootId

                    meta.RootDirectorySha256Hash
                    |> should equal rootHash

                ()))
