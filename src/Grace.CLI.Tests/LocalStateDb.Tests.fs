namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
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
open System.Threading.Tasks

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
        LocalFileVersion.Create relativePath sha256Hash isBinary size createdAt true lastWriteTime

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

    /// Allocates Watch journal sequences without adding replay semantics beyond the schema scaffold.
    let private insertWatchJournalRows (connection: SqliteConnection) throughSequence =
        [| 1L .. throughSequence |]
        |> Array.iter (fun sequence ->
            executeNonQuery connection $"INSERT INTO watch_journal (sequence, created_at_unix_ticks) VALUES ({sequence}, {sequence});")

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
            "CREATE TABLE IF NOT EXISTS watch_journal (sequence INTEGER PRIMARY KEY AUTOINCREMENT, created_at_unix_ticks INTEGER NOT NULL);"

        executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '5');"
        executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

        executeNonQuery
            connection
            $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', '{rootSha256Hash}', '{rootBlake3Hash}', {ticks}, {ticks});"

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
                schemaVersion |> should equal "5"

                cmd.CommandText <- "SELECT COUNT(*) FROM status_meta;"
                let statusMetaCount = Convert.ToInt32(cmd.ExecuteScalar())
                statusMetaCount |> should equal 1
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
                schemaVersion |> should equal "5"

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that ensure db initialized recreates db when schema v5 has a malformed Watch journal table.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 watch journal shape is malformed`` () =
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
                    executeNonQuery connection "CREATE TABLE watch_journal (sequence TEXT PRIMARY KEY);"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                schemaVersion |> should equal "5"

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

    /// Verifies that ensure db initialized recreates db when schema v5 reuses rowids for Watch journal sequences.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 watch journal lacks autoincrement`` () =
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
                schemaVersion |> should equal "5"

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
    let ``ensureDbInitialized recreates DB when schema v5 autoincrement text is outside sequence declaration`` () =
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
                schemaVersion |> should equal "5"

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
    let ``ensureDbInitialized recreates DB when schema v5 applied through metadata is invalid`` (appliedThroughValue: string) =
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

                schemaVersion |> should equal "5"
                appliedThrough |> should equal "0"

                let! readThrough = LocalStateDb.readWatchJournalAppliedThroughSequence configuration.GraceStatusFile
                readThrough |> should equal 0L

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects duplicated Watch recovery metadata in malformed schema v5 tables.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 applied through metadata is duplicated`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '5');"
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

                schemaVersion |> should equal "5"
                appliedThrough |> should equal "0"
                duplicateRows |> should equal 1

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects meta tables that cannot preserve one row per key.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 meta key is not unique`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '5');"
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

                schemaVersion |> should equal "5"
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
    let ``ensureDbInitialized recreates DB when schema v5 allocated journal sequence is invalid`` (allocatedSequence: string) =
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

                schemaVersion |> should equal "5"
                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that empty schema v5 databases still reject malformed SQLite journal allocation metadata.
    [<TestCase("not-a-number")>]
    [<TestCase("-1")>]
    let ``ensureDbInitialized recreates DB when schema v5 empty journal has invalid allocation row`` (allocatedSequence: string) =
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

                schemaVersion |> should equal "5"
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
    let ``ensureDbInitialized recreates DB when schema v5 journal rows have no allocation row`` () =
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

                schemaVersion |> should equal "5"
                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects stale SQLite journal allocation below the highest persisted row.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 allocated journal sequence is below journal max`` () =
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

                schemaVersion |> should equal "5"
                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema acceptance rejects Watch recovery metadata beyond SQLite's allocated journal sequence.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 applied through metadata exceeds allocated sequence`` () =
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

                schemaVersion |> should equal "5"
                appliedThrough |> should equal "0"
                journalRows |> should equal 0

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

    /// Verifies that schema v5 databases with journal rows must carry trustworthy Watch recovery metadata.
    [<Test>]
    let ``ensureDbInitialized recreates DB when schema v5 has journal rows without applied through metadata`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId "root-sha" "root-blake3" ticks

                do
                    use connection = openRawConnection configuration.GraceStatusFile
                    executeNonQuery connection "INSERT INTO watch_journal (sequence, created_at_unix_ticks) VALUES (1, 1);"
                    executeNonQuery connection "DELETE FROM meta WHERE key = 'AppliedThroughSequence';"

                let corruptBefore =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                let schemaVersion = executeScalarString connection "SELECT value FROM meta WHERE key = 'schema_version';"
                let appliedThrough = executeScalarString connection "SELECT value FROM meta WHERE key = 'AppliedThroughSequence';"
                let journalRows = executeScalarInt connection "SELECT COUNT(*) FROM watch_journal;"

                schemaVersion |> should equal "5"
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
                schemaVersion |> should equal "5"

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
                |> should equal (Some "5")

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
                |> should equal (Some "5")

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
                schemaVersion |> should equal "5"

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
                schemaVersion |> should equal "5"
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

    /// Verifies that ensure db initialized rejects schema v5 databases without watch journal.
    [<Test>]
    let ``ensureDbInitialized recreates schema v5 database missing watch journal`` () =
        withTempDir (fun _ configuration ->
            task {
                let rootId = Guid.NewGuid()
                let rootHash = "missing-journal-root-hash"
                let ticks = 1234567890L

                seedCurrentSchemaWithStatusMeta configuration.GraceStatusFile rootId rootHash "root-blake3" ticks

                use seedConnection = openRawConnection configuration.GraceStatusFile
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

                schemaVersion |> should equal "5"
                watchJournalCount |> should equal 1
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

                schemaVersion |> should equal "5"
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

                schemaVersion |> should equal "5"
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
                schemaVersion |> should equal "5"

                let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                statusMetaCount |> should equal 1
            })

    /// Verifies that ensure db initialized treats paths case insensitively on windows.
    [<Test>]
    let ``ensureDbInitialized treats paths case-insensitively on Windows`` () =
        withTempDir (fun _ configuration ->
            task {
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
                schemaVersion |> should equal "5"
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
                    executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '5');"

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

                let file1 = LocalFileVersion.Create "src/file.txt" "hash-1" true 10L now false lastWrite1

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

                let file2 = LocalFileVersion.Create "src/file.txt" "hash-2" false 25L now true lastWrite2

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
                    schemaVersion |> should equal "5"

                    let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                    statusMetaCount |> should equal 1

                    let meta =
                        LocalStateDb.readStatusMeta dbPath
                        |> fun task -> task.GetAwaiter().GetResult()

                    meta.RootDirectoryId |> should equal rootId

                    meta.RootDirectorySha256Hash
                    |> should equal rootHash

                ()))
