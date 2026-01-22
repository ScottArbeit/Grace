namespace Grace.CLI

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Types
open Microsoft.Data.Sqlite
open NodaTime
open SQLitePCL

module LocalStateDb =
    [<Literal>]
    let private SchemaVersion = "2"

    [<Literal>]
    let private BusyTimeoutMs = 30000

    let private retryDelaysMs = [| 50; 100; 200; 400; 800; 1600 |]

    let mutable private verboseEnabled = false

    let setVerbose enabled = verboseEnabled <- enabled
    let private traceFilePath = Environment.GetEnvironmentVariable("GRACE_LOCALSTATE_DB_TRACE_PATH")
    let private traceOpenConnections = not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRACE_LOCALSTATE_DB_TRACE_OPEN")))
    let private initLocks = ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase)
    let private initializedDbs = ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

    let private sqliteInitialized =
        lazy
            (Batteries_V2.Init()
             true)

    let private logVerbose message = if verboseEnabled then Log.LogVerbose message

    let private logTrace message =
        if not (String.IsNullOrWhiteSpace(traceFilePath)) then
            try
                File.AppendAllText(traceFilePath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}")
            with
            | _ -> ()

    let private logTraceStatement label (statement: string) =
        let trimmed =
            if statement.Length > 240 then
                statement.Substring(0, 240) + "..."
            else
                statement

        logTrace $"{label}: {trimmed}"

    let private isBusyOrLocked (ex: SqliteException) = ex.SqliteErrorCode = 5 || ex.SqliteErrorCode = 6

    let private executeWithRetry (operation: unit -> Task<unit>) =
        let rec run attempt =
            task {
                try
                    do! operation ()
                with
                | :? SqliteException as ex when isBusyOrLocked ex ->
                    if attempt >= retryDelaysMs.Length then return raise ex
                    let jitter = Random.Shared.Next(0, 50)
                    let delayMs = retryDelaysMs[attempt] + jitter
                    do! Task.Delay(delayMs)
                    return! run (attempt + 1)
                | ex -> return raise ex
            }

        run 0

    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    let private executePragma (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    let private executeNonQueryWithParams (connection: SqliteConnection) (sql: string) (configureParameters: SqliteParameterCollection -> unit) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        configureParameters cmd.Parameters
        cmd.ExecuteNonQuery() |> ignore

    let private applyConnectionPragmas (connection: SqliteConnection) =
        executePragma connection $"PRAGMA busy_timeout = {BusyTimeoutMs};"
        executePragma connection "PRAGMA foreign_keys = ON;"
        executePragma connection "PRAGMA synchronous = NORMAL;"
        executePragma connection "PRAGMA temp_store = MEMORY;"

    let private ensureJournalMode (connection: SqliteConnection) = executePragma connection "PRAGMA journal_mode = WAL;"

    let private openConnection (dbPath: string) =
        sqliteInitialized.Value |> ignore
        let directoryPath = Path.GetDirectoryName(dbPath)
        logVerbose $"LocalStateDb.openConnection starting. dbPath={dbPath} dir={directoryPath}"

        if traceOpenConnections then
            logTrace $"openConnection starting. dbPath={dbPath} dir={directoryPath}"

        let stopwatch = Stopwatch.StartNew()
        Directory.CreateDirectory(directoryPath) |> ignore
        logVerbose $"LocalStateDb.openConnection directory ensured in {stopwatch.ElapsedMilliseconds}ms"

        if traceOpenConnections then
            logTrace $"openConnection directory ensured in {stopwatch.ElapsedMilliseconds}ms"

        let connectionString =
            let builder = SqliteConnectionStringBuilder()
            builder.DataSource <- dbPath
            builder.Mode <- SqliteOpenMode.ReadWriteCreate
            builder.Pooling <- true
            builder.DefaultTimeout <- BusyTimeoutMs / 1000
            builder.ToString()

        let connection = new SqliteConnection(connectionString)

        try
            connection.Open()
            applyConnectionPragmas connection
            logVerbose $"LocalStateDb.openConnection opened connection in {stopwatch.ElapsedMilliseconds}ms"

            if traceOpenConnections then
                logTrace $"openConnection opened connection in {stopwatch.ElapsedMilliseconds}ms"

            connection
        with
        | ex ->
            try
                connection.Dispose()
            with
            | _ -> ()

            raise ex

    let private schemaStatements =
        [|
            "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
            "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"
            "CREATE TABLE IF NOT EXISTS status_directories (relative_path TEXT PRIMARY KEY, parent_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"
            "CREATE INDEX IF NOT EXISTS ix_status_directories_parent ON status_directories(parent_path);"
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_status_directories_directory_version_id ON status_directories(directory_version_id);"
            "CREATE TABLE IF NOT EXISTS status_files (relative_path TEXT PRIMARY KEY, directory_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, FOREIGN KEY (directory_version_id) REFERENCES status_directories(directory_version_id) ON DELETE CASCADE);"
            "CREATE INDEX IF NOT EXISTS ix_status_files_directory_path ON status_files(directory_path);"
            "CREATE INDEX IF NOT EXISTS ix_status_files_directory_version_id ON status_files(directory_version_id);"
            "CREATE INDEX IF NOT EXISTS ix_status_files_sha256 ON status_files(sha256_hash);"
            "CREATE TABLE IF NOT EXISTS object_cache_directories (directory_version_id TEXT PRIMARY KEY, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"
            "CREATE INDEX IF NOT EXISTS ix_object_cache_directories_relative_path ON object_cache_directories(relative_path);"
            "CREATE TABLE IF NOT EXISTS object_cache_directory_children (parent_directory_version_id TEXT NOT NULL, child_directory_version_id TEXT NOT NULL, ordinal INTEGER NOT NULL, PRIMARY KEY (parent_directory_version_id, child_directory_version_id), FOREIGN KEY (parent_directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE CASCADE, FOREIGN KEY (child_directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE RESTRICT);"
            "CREATE INDEX IF NOT EXISTS ix_object_cache_children_parent ON object_cache_directory_children(parent_directory_version_id);"
            "CREATE TABLE IF NOT EXISTS object_cache_directory_files (directory_version_id TEXT NOT NULL, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, PRIMARY KEY (directory_version_id, relative_path), FOREIGN KEY (directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE CASCADE);"
            "CREATE INDEX IF NOT EXISTS ix_object_cache_files_path_hash ON object_cache_directory_files(relative_path, sha256_hash);"
        |]

    let private tryGetMetaValue (connection: SqliteConnection) (key: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "SELECT value FROM meta WHERE key = $key LIMIT 1;"
        cmd.Parameters.AddWithValue("$key", key) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(reader.GetString(0)) else None

    let private setMetaValue (connection: SqliteConnection) (key: string) (value: string) =
        executeNonQueryWithParams connection "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);" (fun parameters ->
            parameters.AddWithValue("$key", key) |> ignore
            parameters.AddWithValue("$value", value) |> ignore)

    let private insertStatusMetaIfMissing (connection: SqliteConnection) =
        let defaultStatus = GraceStatus.Default

        executeNonQueryWithParams
            connection
            "INSERT OR IGNORE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, $root_id, $root_hash, $last_file, $last_dir);"
            (fun parameters ->
                parameters.AddWithValue("$root_id", defaultStatus.RootDirectoryId.ToString())
                |> ignore

                parameters.AddWithValue("$root_hash", defaultStatus.RootDirectorySha256Hash)
                |> ignore

                parameters.AddWithValue("$last_file", defaultStatus.LastSuccessfulFileUpload.ToUnixTimeTicks())
                |> ignore

                parameters.AddWithValue("$last_dir", defaultStatus.LastSuccessfulDirectoryVersionUpload.ToUnixTimeTicks())
                |> ignore)

    let private recreateDatabase (dbPath: string) =
        try
            SqliteConnection.ClearAllPools()
        with
        | _ -> ()

        if File.Exists(dbPath) then
            let timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss")
            let directoryPath = Path.GetDirectoryName(dbPath)
            let corruptPath = Path.Combine(directoryPath, $"grace-local.corrupt.{timestamp}.db")
            File.Move(dbPath, corruptPath, true)

        let sidecars = [| "-wal"; "-shm"; "-journal" |]

        sidecars
        |> Array.iter (fun suffix ->
            let sidecarPath = dbPath + suffix
            if File.Exists(sidecarPath) then File.Delete(sidecarPath))

    let ensureDbInitialized (dbPath: string) =
        task {
            let normalizedPath = Path.GetFullPath(dbPath)
            let mutable loopCount = 0

            match initializedDbs.TryGetValue(normalizedPath) with
            | true, _ -> ()
            | _ ->
                let semaphore = initLocks.GetOrAdd(normalizedPath, (fun _ -> new SemaphoreSlim(1, 1)))

                do! semaphore.WaitAsync()

                try
                    match initializedDbs.TryGetValue(normalizedPath) with
                    | true, _ -> ()
                    | _ ->
                        do!
                            executeWithRetry (fun () ->
                                task {
                                    let runSchema (connection: SqliteConnection) =
                                        ensureJournalMode connection

                                        schemaStatements
                                        |> Array.iteri (fun index statement ->
                                            logTraceStatement $"schema[{index}] start" statement
                                            executeNonQuery connection statement
                                            logTrace $"schema[{index}] done")

                                    let schemaExists (connection: SqliteConnection) =
                                        use cmd = connection.CreateCommand()
                                        cmd.CommandText <- "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta' LIMIT 1;"
                                        use reader = cmd.ExecuteReader()
                                        reader.Read()

                                    let mutable recreate = false

                                    do
                                        do
                                            try
                                                use schemaConnection = openConnection normalizedPath
                                                if not (schemaExists schemaConnection) then runSchema schemaConnection
                                            with
                                            | :? SqliteException as ex when ex.SqliteErrorCode = 26 -> recreate <- true

                                        loopCount <- loopCount + 1
                                        logTrace $"Local state DB schema check attempt {loopCount} for {normalizedPath}"

                                        try
                                            use connection = openConnection normalizedPath

                                            try
                                                ensureJournalMode connection

                                                match tryGetMetaValue connection "schema_version" with
                                                | Some version when version = SchemaVersion -> ()
                                                | Some _ -> recreate <- true
                                                | None ->
                                                    logTrace "meta schema_version missing; writing defaults"
                                                    let createdAtTicks = getCurrentInstant().ToUnixTimeTicks()
                                                    setMetaValue connection "schema_version" SchemaVersion
                                                    setMetaValue connection "created_at_unix_ticks" $"{createdAtTicks}"

                                                if not recreate then
                                                    logTrace "status_meta ensuring default row"
                                                    insertStatusMetaIfMissing connection
                                            with
                                            | :? SqliteException as ex when ex.SqliteErrorCode = 26 -> recreate <- true
                                        with
                                        | :? SqliteException as ex when ex.SqliteErrorCode = 26 -> recreate <- true

                                    if recreate then
                                        logVerbose $"Local state DB schema mismatch or corruption detected. Recreating {normalizedPath}."
                                        logTrace "recreateDatabase triggered"
                                        recreateDatabase normalizedPath

                                        do
                                            use schemaConnection = openConnection normalizedPath
                                            runSchema schemaConnection

                                        use connection = openConnection normalizedPath
                                        ensureJournalMode connection
                                        setMetaValue connection "schema_version" SchemaVersion
                                        setMetaValue connection "created_at_unix_ticks" $"{getCurrentInstant().ToUnixTimeTicks()}"
                                        logTrace "status_meta ensuring default row"
                                        insertStatusMetaIfMissing connection
                                })

                        initializedDbs[normalizedPath] <- true
                finally
                    semaphore.Release() |> ignore
        }

    type StatusMeta =
        {
            RootDirectoryId: DirectoryVersionId
            RootDirectorySha256Hash: Sha256Hash
            LastSuccessfulFileUpload: Instant
            LastSuccessfulDirectoryVersionUpload: Instant
        }

    let private readStatusMetaInternal (connection: SqliteConnection) =
        use cmd = connection.CreateCommand()

        cmd.CommandText <-
            "SELECT root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks FROM status_meta WHERE id = 1;"

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let rootId = Guid.Parse(reader.GetString(0))
            let rootHash = reader.GetString(1)
            let lastFile = Instant.FromUnixTimeTicks(reader.GetInt64(2))
            let lastDir = Instant.FromUnixTimeTicks(reader.GetInt64(3))

            {
                RootDirectoryId = rootId
                RootDirectorySha256Hash = rootHash
                LastSuccessfulFileUpload = lastFile
                LastSuccessfulDirectoryVersionUpload = lastDir
            }
            |> Some
        else
            None

    let readStatusMeta (dbPath: string) =
        task {
            do! ensureDbInitialized dbPath
            let connection = openConnection dbPath

            try
                match readStatusMetaInternal connection with
                | Some meta -> return meta
                | None ->
                    let defaultStatus = GraceStatus.Default

                    return
                        {
                            RootDirectoryId = defaultStatus.RootDirectoryId
                            RootDirectorySha256Hash = defaultStatus.RootDirectorySha256Hash
                            LastSuccessfulFileUpload = defaultStatus.LastSuccessfulFileUpload
                            LastSuccessfulDirectoryVersionUpload = defaultStatus.LastSuccessfulDirectoryVersionUpload
                        }
            finally
                connection.Dispose()
        }

    let private setStatusMeta (connection: SqliteConnection) (graceStatus: GraceStatus) =
        executeNonQueryWithParams
            connection
            "INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, $root_id, $root_hash, $last_file, $last_dir);"
            (fun parameters ->
                parameters.AddWithValue("$root_id", graceStatus.RootDirectoryId.ToString())
                |> ignore

                parameters.AddWithValue("$root_hash", graceStatus.RootDirectorySha256Hash)
                |> ignore

                parameters.AddWithValue("$last_file", graceStatus.LastSuccessfulFileUpload.ToUnixTimeTicks())
                |> ignore

                parameters.AddWithValue("$last_dir", graceStatus.LastSuccessfulDirectoryVersionUpload.ToUnixTimeTicks())
                |> ignore)

    let replaceStatusSnapshot (dbPath: string) (graceStatus: GraceStatus) =
        task {
            do! ensureDbInitialized dbPath

            return!
                executeWithRetry (fun () ->
                    task {
                        let connection = openConnection dbPath

                        try
                            executeNonQuery connection "BEGIN IMMEDIATE;"

                            try
                                executeNonQuery connection "DELETE FROM status_directories;"
                                executeNonQuery connection "DELETE FROM status_files;"
                                setStatusMeta connection graceStatus

                                use directoryCommand = connection.CreateCommand()

                                directoryCommand.CommandText <-
                                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ($relative_path, $parent_path, $directory_version_id, $sha256_hash, $size_bytes, $created_at, $last_write);"

                                directoryCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$parent_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                use fileCommand = connection.CreateCommand()

                                fileCommand.CommandText <-
                                    "INSERT OR REPLACE INTO status_files (relative_path, directory_path, directory_version_id, sha256_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ($relative_path, $directory_path, $directory_version_id, $sha256_hash, $is_binary, $size_bytes, $created_at, $uploaded, $last_write);"

                                fileCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$directory_path", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$is_binary", SqliteType.Integer)
                                |> ignore

                                fileCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                fileCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                fileCommand.Parameters.Add("$uploaded", SqliteType.Integer)
                                |> ignore

                                fileCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                graceStatus.Index.Values
                                |> Seq.iter (fun directory ->
                                    let parentPath =
                                        match getParentPath directory.RelativePath with
                                        | Some path -> path
                                        | None -> String.Empty

                                    directoryCommand.Parameters["$relative_path"].Value <- directory.RelativePath
                                    directoryCommand.Parameters["$parent_path"].Value <- parentPath
                                    directoryCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    directoryCommand.Parameters["$sha256_hash"].Value <- directory.Sha256Hash
                                    directoryCommand.Parameters["$size_bytes"].Value <- directory.Size
                                    directoryCommand.Parameters["$created_at"].Value <- directory.CreatedAt.ToUnixTimeTicks()
                                    directoryCommand.Parameters["$last_write"].Value <- directory.LastWriteTimeUtc.Ticks
                                    directoryCommand.ExecuteNonQuery() |> ignore

                                    directory.Files
                                    |> Seq.iter (fun file ->
                                        fileCommand.Parameters["$relative_path"].Value <- file.RelativePath
                                        fileCommand.Parameters["$directory_path"].Value <- directory.RelativePath
                                        fileCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                        fileCommand.Parameters["$sha256_hash"].Value <- file.Sha256Hash
                                        fileCommand.Parameters["$is_binary"].Value <- if file.IsBinary then 1 else 0
                                        fileCommand.Parameters["$size_bytes"].Value <- file.Size
                                        fileCommand.Parameters["$created_at"].Value <- file.CreatedAt.ToUnixTimeTicks()
                                        fileCommand.Parameters["$uploaded"].Value <- if file.UploadedToObjectStorage then 1 else 0
                                        fileCommand.Parameters["$last_write"].Value <- file.LastWriteTimeUtc.Ticks
                                        fileCommand.ExecuteNonQuery() |> ignore))

                                executeNonQuery connection "COMMIT;"
                            with
                            | ex ->
                                executeNonQuery connection "ROLLBACK;"
                                return raise ex
                        finally
                            connection.Dispose()
                    })
        }

    let upsertObjectCache (dbPath: string) (newDirectoryVersions: IEnumerable<LocalDirectoryVersion>) =
        task {
            do! ensureDbInitialized dbPath

            return!
                executeWithRetry (fun () ->
                    task {
                        let connection = openConnection dbPath

                        try
                            executeNonQuery connection "BEGIN IMMEDIATE;"

                            try
                                use directoryCommand = connection.CreateCommand()

                                directoryCommand.CommandText <-
                                    "INSERT OR REPLACE INTO object_cache_directories (directory_version_id, relative_path, sha256_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ($directory_version_id, $relative_path, $sha256_hash, $size_bytes, $created_at, $last_write);"

                                directoryCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                use deleteChildrenCommand = connection.CreateCommand()

                                deleteChildrenCommand.CommandText <-
                                    "DELETE FROM object_cache_directory_children WHERE parent_directory_version_id = $parent_directory_version_id;"

                                deleteChildrenCommand.Parameters.Add("$parent_directory_version_id", SqliteType.Text)
                                |> ignore

                                use insertChildCommand = connection.CreateCommand()

                                insertChildCommand.CommandText <-
                                    "INSERT OR REPLACE INTO object_cache_directory_children (parent_directory_version_id, child_directory_version_id, ordinal) VALUES ($parent_directory_version_id, $child_directory_version_id, $ordinal);"

                                insertChildCommand.Parameters.Add("$parent_directory_version_id", SqliteType.Text)
                                |> ignore

                                insertChildCommand.Parameters.Add("$child_directory_version_id", SqliteType.Text)
                                |> ignore

                                insertChildCommand.Parameters.Add("$ordinal", SqliteType.Integer)
                                |> ignore

                                use deleteFilesCommand = connection.CreateCommand()
                                deleteFilesCommand.CommandText <- "DELETE FROM object_cache_directory_files WHERE directory_version_id = $directory_version_id;"

                                deleteFilesCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                use insertFileCommand = connection.CreateCommand()

                                insertFileCommand.CommandText <-
                                    "INSERT OR REPLACE INTO object_cache_directory_files (directory_version_id, relative_path, sha256_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ($directory_version_id, $relative_path, $sha256_hash, $is_binary, $size_bytes, $created_at, $uploaded, $last_write);"

                                insertFileCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                insertFileCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                insertFileCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                insertFileCommand.Parameters.Add("$is_binary", SqliteType.Integer)
                                |> ignore

                                insertFileCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                insertFileCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                insertFileCommand.Parameters.Add("$uploaded", SqliteType.Integer)
                                |> ignore

                                insertFileCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                newDirectoryVersions
                                |> Seq.iter (fun directory ->
                                    directoryCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    directoryCommand.Parameters["$relative_path"].Value <- directory.RelativePath
                                    directoryCommand.Parameters["$sha256_hash"].Value <- directory.Sha256Hash
                                    directoryCommand.Parameters["$size_bytes"].Value <- directory.Size
                                    directoryCommand.Parameters["$created_at"].Value <- directory.CreatedAt.ToUnixTimeTicks()
                                    directoryCommand.Parameters["$last_write"].Value <- directory.LastWriteTimeUtc.Ticks
                                    directoryCommand.ExecuteNonQuery() |> ignore

                                    deleteChildrenCommand.Parameters["$parent_directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    deleteChildrenCommand.ExecuteNonQuery() |> ignore

                                    deleteFilesCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    deleteFilesCommand.ExecuteNonQuery() |> ignore

                                    directory.Directories
                                    |> Seq.iteri (fun index childId ->
                                        insertChildCommand.Parameters["$parent_directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                        insertChildCommand.Parameters["$child_directory_version_id"].Value <- childId.ToString()
                                        insertChildCommand.Parameters["$ordinal"].Value <- index
                                        insertChildCommand.ExecuteNonQuery() |> ignore)

                                    directory.Files
                                    |> Seq.iter (fun file ->
                                        insertFileCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                        insertFileCommand.Parameters["$relative_path"].Value <- file.RelativePath
                                        insertFileCommand.Parameters["$sha256_hash"].Value <- file.Sha256Hash
                                        insertFileCommand.Parameters["$is_binary"].Value <- if file.IsBinary then 1 else 0
                                        insertFileCommand.Parameters["$size_bytes"].Value <- file.Size
                                        insertFileCommand.Parameters["$created_at"].Value <- file.CreatedAt.ToUnixTimeTicks()
                                        insertFileCommand.Parameters["$uploaded"].Value <- if file.UploadedToObjectStorage then 1 else 0
                                        insertFileCommand.Parameters["$last_write"].Value <- file.LastWriteTimeUtc.Ticks
                                        insertFileCommand.ExecuteNonQuery() |> ignore))

                                executeNonQuery connection "COMMIT;"
                            with
                            | ex ->
                                executeNonQuery connection "ROLLBACK;"
                                return raise ex
                        finally
                            connection.Dispose()
                    })
        }

    let isFileVersionInObjectCache (dbPath: string) (fileVersion: LocalFileVersion) =
        task {
            do! ensureDbInitialized dbPath
            let connection = openConnection dbPath

            try
                use cmd = connection.CreateCommand()
                cmd.CommandText <- "SELECT 1 FROM object_cache_directory_files WHERE relative_path = $relative_path AND sha256_hash = $sha256_hash LIMIT 1;"

                cmd.Parameters.AddWithValue("$relative_path", fileVersion.RelativePath)
                |> ignore

                cmd.Parameters.AddWithValue("$sha256_hash", fileVersion.Sha256Hash)
                |> ignore

                use reader = cmd.ExecuteReader()
                return reader.Read()
            finally
                connection.Dispose()
        }

    let isDirectoryVersionInObjectCache (dbPath: string) (directoryVersionId: DirectoryVersionId) =
        task {
            do! ensureDbInitialized dbPath
            let connection = openConnection dbPath

            try
                use cmd = connection.CreateCommand()
                cmd.CommandText <- "SELECT 1 FROM object_cache_directories WHERE directory_version_id = $id LIMIT 1;"

                cmd.Parameters.AddWithValue("$id", directoryVersionId.ToString())
                |> ignore

                use reader = cmd.ExecuteReader()
                return reader.Read()
            finally
                connection.Dispose()
        }

    let removeObjectCacheDirectory (dbPath: string) (directoryVersionId: DirectoryVersionId) =
        task {
            do! ensureDbInitialized dbPath

            return!
                executeWithRetry (fun () ->
                    task {
                        let connection = openConnection dbPath

                        try
                            executeNonQuery connection "BEGIN IMMEDIATE;"

                            try
                                use cmd = connection.CreateCommand()
                                cmd.CommandText <- "DELETE FROM object_cache_directories WHERE directory_version_id = $id;"

                                cmd.Parameters.AddWithValue("$id", directoryVersionId.ToString())
                                |> ignore

                                cmd.ExecuteNonQuery() |> ignore
                                executeNonQuery connection "COMMIT;"
                            with
                            | ex ->
                                executeNonQuery connection "ROLLBACK;"
                                return raise ex
                        finally
                            connection.Dispose()
                    })
        }

    let applyStatusIncremental
        (dbPath: string)
        (newGraceStatus: GraceStatus)
        (newDirectoryVersions: IEnumerable<LocalDirectoryVersion>)
        (differences: IEnumerable<FileSystemDifference>)
        =
        task {
            do! ensureDbInitialized dbPath

            return!
                executeWithRetry (fun () ->
                    task {
                        let connection = openConnection dbPath

                        try
                            executeNonQuery connection "BEGIN IMMEDIATE;"

                            try
                                setStatusMeta connection newGraceStatus

                                use directoryCommand = connection.CreateCommand()

                                directoryCommand.CommandText <-
                                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ($relative_path, $parent_path, $directory_version_id, $sha256_hash, $size_bytes, $created_at, $last_write);"

                                directoryCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$parent_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                newDirectoryVersions
                                |> Seq.iter (fun directory ->
                                    let parentPath =
                                        match getParentPath directory.RelativePath with
                                        | Some path -> path
                                        | None -> String.Empty

                                    directoryCommand.Parameters["$relative_path"].Value <- directory.RelativePath
                                    directoryCommand.Parameters["$parent_path"].Value <- parentPath
                                    directoryCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    directoryCommand.Parameters["$sha256_hash"].Value <- directory.Sha256Hash
                                    directoryCommand.Parameters["$size_bytes"].Value <- directory.Size
                                    directoryCommand.Parameters["$created_at"].Value <- directory.CreatedAt.ToUnixTimeTicks()
                                    directoryCommand.Parameters["$last_write"].Value <- directory.LastWriteTimeUtc.Ticks
                                    directoryCommand.ExecuteNonQuery() |> ignore)

                                use fileUpsertCommand = connection.CreateCommand()

                                fileUpsertCommand.CommandText <-
                                    "INSERT OR REPLACE INTO status_files (relative_path, directory_path, directory_version_id, sha256_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ($relative_path, $directory_path, $directory_version_id, $sha256_hash, $is_binary, $size_bytes, $created_at, $uploaded, $last_write);"

                                fileUpsertCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$directory_path", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$is_binary", SqliteType.Integer)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$uploaded", SqliteType.Integer)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                use fileDeleteCommand = connection.CreateCommand()
                                fileDeleteCommand.CommandText <- "DELETE FROM status_files WHERE relative_path = $relative_path;"

                                fileDeleteCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                use directoryDeleteCommand = connection.CreateCommand()
                                directoryDeleteCommand.CommandText <- "DELETE FROM status_directories WHERE relative_path = $relative_path;"

                                directoryDeleteCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                let fileLookup =
                                    newDirectoryVersions
                                    |> Seq.collect (fun dv ->
                                        dv.Files
                                        |> Seq.map (fun file -> (file.RelativePath, (file, dv.DirectoryVersionId, dv.RelativePath))))
                                    |> dict

                                differences
                                |> Seq.iter (fun difference ->
                                    match difference.DifferenceType with
                                    | Add
                                    | Change ->
                                        if difference.FileSystemEntryType.IsFile then
                                            let mutable payload = Unchecked.defaultof<LocalFileVersion * DirectoryVersionId * string>

                                            if fileLookup.TryGetValue(difference.RelativePath, &payload) then
                                                let file, directoryVersionId, directoryPath = payload
                                                fileUpsertCommand.Parameters["$relative_path"].Value <- file.RelativePath
                                                fileUpsertCommand.Parameters["$directory_path"].Value <- directoryPath
                                                fileUpsertCommand.Parameters["$directory_version_id"].Value <- directoryVersionId.ToString()
                                                fileUpsertCommand.Parameters["$sha256_hash"].Value <- file.Sha256Hash
                                                fileUpsertCommand.Parameters["$is_binary"].Value <- if file.IsBinary then 1 else 0
                                                fileUpsertCommand.Parameters["$size_bytes"].Value <- file.Size
                                                fileUpsertCommand.Parameters["$created_at"].Value <- file.CreatedAt.ToUnixTimeTicks()
                                                fileUpsertCommand.Parameters["$uploaded"].Value <- if file.UploadedToObjectStorage then 1 else 0
                                                fileUpsertCommand.Parameters["$last_write"].Value <- file.LastWriteTimeUtc.Ticks
                                                fileUpsertCommand.ExecuteNonQuery() |> ignore
                                    | Delete ->
                                        if difference.FileSystemEntryType.IsFile then
                                            fileDeleteCommand.Parameters["$relative_path"].Value <- difference.RelativePath
                                            fileDeleteCommand.ExecuteNonQuery() |> ignore
                                        else
                                            directoryDeleteCommand.Parameters["$relative_path"].Value <- difference.RelativePath
                                            directoryDeleteCommand.ExecuteNonQuery() |> ignore)

                                executeNonQuery connection "COMMIT;"
                            with
                            | ex ->
                                executeNonQuery connection "ROLLBACK;"
                                return raise ex
                        finally
                            connection.Dispose()
                    })
        }

    type private StatusDirectoryRow =
        {
            RelativePath: string
            ParentPath: string
            DirectoryVersionId: DirectoryVersionId
            Sha256Hash: Sha256Hash
            SizeBytes: int64
            CreatedAt: Instant
            LastWriteTimeUtc: DateTime
        }

    type private StatusFileRow =
        {
            RelativePath: string
            DirectoryVersionId: DirectoryVersionId
            Sha256Hash: Sha256Hash
            IsBinary: bool
            SizeBytes: int64
            CreatedAt: Instant
            UploadedToObjectStorage: bool
            LastWriteTimeUtc: DateTime
        }

    let readStatusSnapshot (dbPath: string) =
        task {
            do! ensureDbInitialized dbPath
            let connection = openConnection dbPath

            try
                let meta: StatusMeta =
                    match readStatusMetaInternal connection with
                    | Some value -> value
                    | None ->
                        let defaultStatus = GraceStatus.Default

                        {
                            RootDirectoryId = defaultStatus.RootDirectoryId
                            RootDirectorySha256Hash = defaultStatus.RootDirectorySha256Hash
                            LastSuccessfulFileUpload = defaultStatus.LastSuccessfulFileUpload
                            LastSuccessfulDirectoryVersionUpload = defaultStatus.LastSuccessfulDirectoryVersionUpload
                        }

                let directories = List<StatusDirectoryRow>()
                let files = List<StatusFileRow>()

                use directoryCommand = connection.CreateCommand()

                directoryCommand.CommandText <-
                    "SELECT relative_path, parent_path, directory_version_id, sha256_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks FROM status_directories;"

                use directoryReader = directoryCommand.ExecuteReader()

                while directoryReader.Read() do
                    let relativePath = directoryReader.GetString(0)
                    let parentPath = directoryReader.GetString(1)
                    let directoryVersionId = Guid.Parse(directoryReader.GetString(2))
                    let sha256Hash = directoryReader.GetString(3)
                    let sizeBytes = directoryReader.GetInt64(4)
                    let createdAt = Instant.FromUnixTimeTicks(directoryReader.GetInt64(5))
                    let lastWriteTimeUtc = DateTime(directoryReader.GetInt64(6), DateTimeKind.Utc)

                    directories.Add(
                        {
                            RelativePath = relativePath
                            ParentPath = parentPath
                            DirectoryVersionId = directoryVersionId
                            Sha256Hash = sha256Hash
                            SizeBytes = sizeBytes
                            CreatedAt = createdAt
                            LastWriteTimeUtc = lastWriteTimeUtc
                        }
                    )

                use fileCommand = connection.CreateCommand()

                fileCommand.CommandText <-
                    "SELECT relative_path, directory_version_id, sha256_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks FROM status_files;"

                use fileReader = fileCommand.ExecuteReader()

                while fileReader.Read() do
                    let relativePath = fileReader.GetString(0)
                    let directoryVersionId = Guid.Parse(fileReader.GetString(1))
                    let sha256Hash = fileReader.GetString(2)
                    let isBinary = fileReader.GetInt64(3) = 1L
                    let sizeBytes = fileReader.GetInt64(4)
                    let createdAt = Instant.FromUnixTimeTicks(fileReader.GetInt64(5))
                    let uploaded = fileReader.GetInt64(6) = 1L
                    let lastWriteTimeUtc = DateTime(fileReader.GetInt64(7), DateTimeKind.Utc)

                    files.Add(
                        {
                            RelativePath = relativePath
                            DirectoryVersionId = directoryVersionId
                            Sha256Hash = sha256Hash
                            IsBinary = isBinary
                            SizeBytes = sizeBytes
                            CreatedAt = createdAt
                            UploadedToObjectStorage = uploaded
                            LastWriteTimeUtc = lastWriteTimeUtc
                        }
                    )

                let directoriesByParent = Dictionary<string, List<DirectoryVersionId>>()
                let filesByDirectory = Dictionary<DirectoryVersionId, List<LocalFileVersion>>()

                directories
                |> Seq.iter (fun directory ->
                    let parentPath = directory.ParentPath
                    let mutable existing = Unchecked.defaultof<List<DirectoryVersionId>>

                    if directoriesByParent.TryGetValue(parentPath, &existing) then
                        existing.Add(directory.DirectoryVersionId)
                    else
                        directoriesByParent.Add(parentPath, List<DirectoryVersionId>([ directory.DirectoryVersionId ])))

                files
                |> Seq.iter (fun file ->
                    let localFile =
                        LocalFileVersion.Create
                            file.RelativePath
                            file.Sha256Hash
                            file.IsBinary
                            file.SizeBytes
                            file.CreatedAt
                            file.UploadedToObjectStorage
                            file.LastWriteTimeUtc

                    let mutable existing = Unchecked.defaultof<List<LocalFileVersion>>

                    if filesByDirectory.TryGetValue(file.DirectoryVersionId, &existing) then
                        existing.Add(localFile)
                    else
                        filesByDirectory.Add(file.DirectoryVersionId, List<LocalFileVersion>([ localFile ])))

                let index = GraceIndex()

                directories
                |> Seq.iter (fun directory ->
                    let directoriesForPath =
                        let mutable list = Unchecked.defaultof<List<DirectoryVersionId>>

                        if directoriesByParent.TryGetValue(directory.RelativePath, &list) then
                            list
                        else
                            List<DirectoryVersionId>()

                    let filesForPath =
                        let mutable list = Unchecked.defaultof<List<LocalFileVersion>>

                        if filesByDirectory.TryGetValue(directory.DirectoryVersionId, &list) then
                            list
                        else
                            List<LocalFileVersion>()

                    let localDirectory =
                        LocalDirectoryVersion.Create
                            directory.DirectoryVersionId
                            (Current().OwnerId)
                            (Current().OrganizationId)
                            (Current().RepositoryId)
                            directory.RelativePath
                            directory.Sha256Hash
                            directoriesForPath
                            filesForPath
                            directory.SizeBytes
                            directory.LastWriteTimeUtc

                    index.TryAdd(directory.DirectoryVersionId, localDirectory)
                    |> ignore)

                return
                    {
                        Index = index
                        RootDirectoryId = meta.RootDirectoryId
                        RootDirectorySha256Hash = meta.RootDirectorySha256Hash
                        LastSuccessfulFileUpload = meta.LastSuccessfulFileUpload
                        LastSuccessfulDirectoryVersionUpload = meta.LastSuccessfulDirectoryVersionUpload
                    }
            finally
                connection.Dispose()
        }
