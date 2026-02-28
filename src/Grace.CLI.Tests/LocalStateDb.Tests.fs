namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared.Client.Configuration
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
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

[<NonParallelizable>]
module LocalStateDbTests =
    let private configureVerboseLogging () =
        let value = Environment.GetEnvironmentVariable("GRACE_LOCALSTATE_DB_VERBOSE")

        if not (String.IsNullOrWhiteSpace(value)) then
            let enabled =
                value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

            LocalStateDb.setVerbose enabled

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

    let private ensureGraceConfig (root: string) =
        let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)
        let configPath = Path.Combine(graceDir, Constants.GraceConfigFileName)

        if not (Directory.Exists(graceDir)) then
            Directory.CreateDirectory(graceDir) |> ignore

        if not (File.Exists(configPath)) then
            File.WriteAllText(configPath, "{}")

    let private withTempDir (action: string -> GraceConfiguration -> Task<'T>) =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"grace-tests-{Guid.NewGuid()}")
            Directory.CreateDirectory(root) |> ignore
            let previousDirectory = Environment.CurrentDirectory
            let previousConfiguration =
                if configurationFileExists () then
                    Some(Current())
                else
                    None

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

    let private createFileVersion relativePath sha256Hash isBinary size createdAt lastWriteTime =
        LocalFileVersion.Create relativePath sha256Hash isBinary size createdAt true lastWriteTime

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
        LocalDirectoryVersion.Create
            directoryVersionId
            configuration.OwnerId
            configuration.OrganizationId
            configuration.RepositoryId
            relativePath
            sha256Hash
            (List<DirectoryVersionId>(directoryIds))
            (List<LocalFileVersion>(files))
            sizeBytes
            lastWriteTimeUtc

    let private openRawConnection (dbPath: string) =
        let connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        connection

    let private executeScalarString (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar() :?> string

    let private executeScalarInt (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar() |> Convert.ToInt32

    let private executeScalarInt64 (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar() |> Convert.ToInt64

    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    let private getCorruptBackups (dbPath: string) =
        let directoryPath = Path.GetDirectoryName(dbPath)

        if String.IsNullOrWhiteSpace(directoryPath) then
            Array.Empty<string>()
        else
            Directory.GetFiles(directoryPath, "grace-local.corrupt.*.db")

    let private seedSchemaVersionOnly (dbPath: string) (schemaVersion: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
        |> ignore

        use connection = openRawConnection dbPath
        executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
        executeNonQuery connection $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '{schemaVersion}');"

    let private createTestStatus (rootId: Guid) (rootHash: string) (ticks: int64) =
        { GraceStatus.Default with
            RootDirectoryId = rootId
            RootDirectorySha256Hash = rootHash
            LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(ticks)
            LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(ticks)
        }

    type private WorkerCommand = { FileName: string; ArgumentsPrefix: string }

    let private tryGetWorkerCommand () =
        try
            let baseDir = AppContext.BaseDirectory
            let tfm = DirectoryInfo(baseDir).Name
            let config = DirectoryInfo(baseDir).Parent.Name

            let mutable current = DirectoryInfo(baseDir)
            let mutable srcDir = Unchecked.defaultof<DirectoryInfo>
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
                schemaVersion |> should equal "2"

                cmd.CommandText <- "SELECT COUNT(*) FROM status_meta;"
                let statusMetaCount = Convert.ToInt32(cmd.ExecuteScalar())
                statusMetaCount |> should equal 1
            })

    [<Test>]
    let ``round trips status snapshot`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = getCurrentInstant ()
                let lastWrite = DateTime.UtcNow
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let rootFile = createFileVersion "root.txt" "root-hash" false 12L now lastWrite
                let srcFile = createFileVersion "src/file.txt" "src-hash" false 34L now lastWrite

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
                srcRead.Size |> should equal 34L
            })

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
                let changedFile = createFileVersion "src/file.txt" "src-hash-2" false 25L now lastWrite
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

                readBack.Index.Values
                |> Seq.collect (fun dv -> dv.Files)
                |> Seq.exists (fun file -> file.RelativePath = "root.txt")
                |> should equal false
            })

    [<Test>]
    let ``upserts object cache entries`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = getCurrentInstant ()
                let lastWrite = DateTime.UtcNow
                let directoryId = Guid.NewGuid()
                let fileVersion = createFileVersion "src/cache.txt" "cache-hash" false 12L now lastWrite

                let directory = createDirectoryVersion configuration directoryId "src" "cache-dir-hash" [||] [| fileVersion |] fileVersion.Size lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ directory ]

                let! directoryExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile directoryId

                directoryExists |> should equal true

                let! fileExists = LocalStateDb.isFileVersionInObjectCache configuration.GraceStatusFile fileVersion

                fileExists |> should equal true
            })

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
                schemaVersion |> should equal "2"

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

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
                schemaVersion |> should equal "2"

                let corruptAfter =
                    getCorruptBackups configuration.GraceStatusFile
                    |> Array.length

                corruptAfter |> should equal (corruptBefore + 1)
            })

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
                    |]

                expected
                |> Array.iter (fun name -> objects.Contains(name) |> should equal true)
            })

    [<Test>]
    let ``ensureDbInitialized is idempotent and preserves created_at`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection1 = openRawConnection configuration.GraceStatusFile
                let createdAt1 = executeScalarInt64 connection1 "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'created_at_unix_ticks';"
                let statusMetaCount1 = executeScalarInt connection1 "SELECT COUNT(*) FROM status_meta;"
                statusMetaCount1 |> should equal 1

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection2 = openRawConnection configuration.GraceStatusFile
                let createdAt2 = executeScalarInt64 connection2 "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'created_at_unix_ticks';"
                createdAt2 |> should equal createdAt1

                let statusMetaCount2 = executeScalarInt connection2 "SELECT COUNT(*) FROM status_meta;"
                statusMetaCount2 |> should equal 1
            })

    [<Test>]
    let ``ensureDbInitialized does not overwrite existing status_meta row`` () =
        withTempDir (fun _ configuration ->
            task {
                Directory.CreateDirectory(Path.GetDirectoryName(configuration.GraceStatusFile))
                |> ignore

                let rootId = Guid.NewGuid()
                let rootHash = "custom-root-hash"
                let ticks = 1234567890L

                use connection = openRawConnection configuration.GraceStatusFile

                executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"

                executeNonQuery
                    connection
                    "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"

                executeNonQuery connection "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '2');"

                executeNonQuery
                    connection
                    $"INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, '{rootId}', '{rootHash}', {ticks}, {ticks});"

                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection2 = openRawConnection configuration.GraceStatusFile
                let readRootId = executeScalarString connection2 "SELECT root_directory_version_id FROM status_meta WHERE id = 1;"
                let readRootHash = executeScalarString connection2 "SELECT root_directory_sha256_hash FROM status_meta WHERE id = 1;"
                readRootId |> should equal (rootId.ToString())
                readRootHash |> should equal rootHash
            })

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

    [<Test>]
    let ``busy writer retries and eventually succeeds`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use lockConnection = openRawConnection configuration.GraceStatusFile
                executeNonQuery lockConnection "BEGIN IMMEDIATE;"

                let rootId = Guid.NewGuid()
                let rootHash = "root-hash"
                let ticks = getCurrentInstant().ToUnixTimeTicks()
                let status = createTestStatus rootId rootHash ticks

                let writerTask = task { do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile status Seq.empty Seq.empty }

                do! Task.Delay(450)
                executeNonQuery lockConnection "COMMIT;"

                do! writerTask

                let! meta = LocalStateDb.readStatusMeta configuration.GraceStatusFile
                meta.RootDirectoryId |> should equal rootId

                meta.RootDirectorySha256Hash
                |> should equal rootHash
            })

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

                let operation = fun () -> task { do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile status Seq.empty Seq.empty } :> Task

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                stopwatch.Stop()

                stopwatch.ElapsedMilliseconds
                |> should be (lessThan 1500L)
            })

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

                let operation = fun () -> task { do! LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile statusB } :> Task

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.RootDirectorySha256Hash
                |> should equal statusA.RootDirectorySha256Hash

                readBack.LastSuccessfulFileUpload
                |> should equal statusA.LastSuccessfulFileUpload

                readBack.Index.Count |> should equal 1
            })

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
                    fun () -> task { do! LocalStateDb.applyStatusIncremental configuration.GraceStatusFile updatedStatus [ updatedSrc ] differences } :> Task

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
                schemaVersion |> should equal "2"

                let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                statusMetaCount |> should equal 1
            })

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
                schemaVersion |> should equal "2"
            })

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

    [<Test>]
    let ``status_files enforces directory_version_id`` () =
        withTempDir (fun _ configuration ->
            task {
                do! LocalStateDb.ensureDbInitialized configuration.GraceStatusFile

                use connection = openRawConnection configuration.GraceStatusFile
                executeNonQuery connection "PRAGMA foreign_keys = ON;"

                executeNonQuery
                    connection
                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ('.', '', '00000000-0000-0000-0000-000000000001', 'root', 0, 0, 0);"

                let orphanId = Guid.NewGuid()

                Assert.Throws<SqliteException>(
                    TestDelegate(fun () ->
                        executeNonQuery
                            connection
                            $"INSERT OR REPLACE INTO status_files (relative_path, directory_path, directory_version_id, sha256_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ('orphan.txt', 'missing', '{orphanId}', 'hash', 0, 1, 0, 0, 0);")
                )
                |> ignore
            })

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
                |> Seq.exists (fun file -> file.RelativePath = "LICENSE.md" && file.Sha256Hash = "license-hash-2")
                |> should equal true

                rootRead.Files
                |> Seq.exists (fun file -> file.RelativePath = "README.md" && file.Sha256Hash = "readme-hash-1")
                |> should equal true
            })

    [<Test>]
    let ``applyStatusIncremental delete file removes the row`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(6000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let file = createFileVersion "src/delete-me.txt" "hash" false 1L now lastWrite

                let srcDir = createDirectoryVersion configuration srcId "src" "src-hash" [||] [| file |] file.Size lastWrite

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
                        ]

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.Index.Values
                |> Seq.collect (fun dv -> dv.Files)
                |> Seq.exists (fun f -> f.RelativePath = "src/delete-me.txt")
                |> should equal false
            })

    [<Test>]
    let ``applyStatusIncremental delete directory removes the directory row`` () =
        withTempDir (fun _ configuration ->
            task {
                let now = Instant.FromUnixTimeTicks(7000L)
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let rootId = Guid.NewGuid()
                let srcId = Guid.NewGuid()

                let srcDir = createDirectoryVersion configuration srcId "src" "src-hash" [||] [||] 0L lastWrite

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
                            FileSystemDifference.Create Delete FileSystemEntryType.Directory "src"
                        ]

                let! readBack = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                readBack.Index.Values
                |> Seq.exists (fun dv -> dv.RelativePath = "src")
                |> should equal false
            })

    [<Test>]
    let ``upsertObjectCache enforces foreign keys`` () =
        withTempDir (fun _ configuration ->
            task {
                let lastWrite = DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                let parentId = Guid.NewGuid()
                let missingChildId = Guid.NewGuid()

                let parentDir = createDirectoryVersion configuration parentId "src" "parent-hash" [| missingChildId |] [||] 0L lastWrite

                let operation = fun () -> task { do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ parentDir ] } :> Task

                Assert.ThrowsAsync<InvalidOperationException>(operation)
                |> ignore

                let! exists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile parentId
                exists |> should equal false
            })

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

                let childDirV2 =
                    createDirectoryVersion
                        configuration
                        childId
                        "src/child"
                        "child-hash-v2"
                        [||]
                        [| childFile |]
                        childFile.Size
                        lastWrite

                do! LocalStateDb.upsertObjectCache configuration.GraceStatusFile [ childDirV2 ]

                use connection = openRawConnection configuration.GraceStatusFile

                let childLinkCount =
                    executeScalarInt
                        connection
                        $"SELECT COUNT(*) FROM object_cache_directory_children WHERE parent_directory_version_id = '{parentId}' AND child_directory_version_id = '{childId}';"

                childLinkCount |> should equal 1

                let childHash =
                    executeScalarString
                        connection
                        $"SELECT sha256_hash FROM object_cache_directories WHERE directory_version_id = '{childId}';"

                childHash |> should equal "child-hash-v2"

                let childFileCount =
                    executeScalarInt
                        connection
                        $"SELECT COUNT(*) FROM object_cache_directory_files WHERE directory_version_id = '{childId}' AND relative_path = 'src/child/file.txt';"

                childFileCount |> should equal 1
            })

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

                let operation = fun () -> task { do! LocalStateDb.removeObjectCacheDirectory configuration.GraceStatusFile childId } :> Task

                Assert.ThrowsAsync<SqliteException>(operation)
                |> ignore

                let! stillExists = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile childId
                stillExists |> should equal true
            })

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

                            startInfo.Arguments <- $"{worker.ArgumentsPrefix} \"{dbPath}\" {rootId} {rootHash} {iterationsPerProcess}"

                            startInfo.RedirectStandardOutput <- true
                            startInfo.RedirectStandardError <- true
                            startInfo.UseShellExecute <- false
                            startInfo.CreateNoWindow <- true

                            let proc = new Process()
                            proc.StartInfo <- startInfo

                            if not (proc.Start()) then failwith "Failed to start worker process."

                            proc)

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
                    schemaVersion |> should equal "2"

                    let statusMetaCount = executeScalarInt connection "SELECT COUNT(*) FROM status_meta;"
                    statusMetaCount |> should equal 1

                    let meta =
                        LocalStateDb.readStatusMeta dbPath
                        |> fun task -> task.GetAwaiter().GetResult()

                    meta.RootDirectoryId |> should equal rootId

                    meta.RootDirectorySha256Hash
                    |> should equal rootHash

                ()))
