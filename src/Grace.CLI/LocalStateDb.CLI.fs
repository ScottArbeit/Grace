namespace Grace.CLI

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open Microsoft.Data.Sqlite
open NodaTime
open SQLitePCL

/// Groups the local state db command parser, handlers, and output helpers.
module LocalStateDb =
    [<Literal>]
    let private SchemaVersion = "5"

    /// Identifies the single local Watch journal metadata row that records applied-through progress.
    [<Literal>]
    let WatchJournalAppliedThroughSequenceMetaKey = "AppliedThroughSequence"

    /// Keeps a bounded diagnostic tail of already-applied Watch journal rows.
    [<Literal>]
    let WatchJournalRetainedAppliedRows = 1024L

    [<Literal>]
    let private BusyTimeoutMs = 30000

    let private retryDelaysMs = [| 50; 100; 200; 400; 800; 1600 |]

    let mutable private verboseEnabled = false

    /// Coordinates local SQLite state for set verbose, including Grace status, object cache, or watch metadata.
    let setVerbose enabled = verboseEnabled <- enabled
    /// Reads trace file path from ParseResult, local configuration, or Grace ids.
    let private getTraceFilePath () = Environment.GetEnvironmentVariable("GRACE_LOCALSTATE_DB_TRACE_PATH")
    /// Resolves the local-state database should trace open connections value used to open .grace/grace-local.db.
    let private shouldTraceOpenConnections () = not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRACE_LOCALSTATE_DB_TRACE_OPEN")))
    let private initLocks = ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase)
    let private initializedDbs = ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

    let private sqliteInitialized =
        lazy
            (Batteries_V2.Init()
             true)

    /// Coordinates local SQLite state for log verbose, including Grace status, object cache, or watch metadata.
    let private logVerbose message = if verboseEnabled then Log.LogVerbose message

    /// Coordinates local SQLite state for log trace, including Grace status, object cache, or watch metadata.
    let private logTrace message =
        let traceFilePath = getTraceFilePath ()

        if not (String.IsNullOrWhiteSpace(traceFilePath)) then
            try
                File.AppendAllText(traceFilePath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}")
            with
            | _ -> ()

    /// Coordinates local SQLite state for log trace statement, including Grace status, object cache, or watch metadata.
    let private logTraceStatement label (statement: string) =
        let trimmed =
            if statement.Length > 240 then
                statement.Substring(0, 240) + "..."
            else
                statement

        logTrace $"{label}: {trimmed}"

    /// Evaluates is busy or locked against parsed options and command state.
    let private isBusyOrLocked (ex: SqliteException) = ex.SqliteErrorCode = 5 || ex.SqliteErrorCode = 6

    /// Executes a reusable command workflow.
    let private executeWithRetry (operation: unit -> Task<unit>) =
        /// Runs the command workflow with the supplied inputs.
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

    /// Executes a reusable command workflow.
    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    /// Executes a reusable command workflow.
    let private executePragma (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    /// Executes a reusable command workflow.
    let private executeNonQueryWithParams (connection: SqliteConnection) (sql: string) (configureParameters: SqliteParameterCollection -> unit) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        configureParameters cmd.Parameters
        cmd.ExecuteNonQuery() |> ignore

    /// Resolves the local-state database apply connection pragmas value used to open .grace/grace-local.db.
    let private applyConnectionPragmas (connection: SqliteConnection) =
        executePragma connection $"PRAGMA busy_timeout = {BusyTimeoutMs};"
        executePragma connection "PRAGMA foreign_keys = ON;"
        executePragma connection "PRAGMA synchronous = NORMAL;"
        executePragma connection "PRAGMA temp_store = MEMORY;"

    /// Ensures required command context is present.
    let private ensureJournalMode (connection: SqliteConnection) = executePragma connection "PRAGMA journal_mode = WAL;"

    /// Resolves the local-state database open connection value used to open .grace/grace-local.db.
    let private openConnection (dbPath: string) =
        sqliteInitialized.Value |> ignore
        let directoryPath = Path.GetDirectoryName(dbPath)
        let traceOpenConnections = shouldTraceOpenConnections ()
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
            "CREATE TABLE IF NOT EXISTS status_meta (id INTEGER PRIMARY KEY CHECK (id = 1), root_directory_version_id TEXT NOT NULL, root_directory_sha256_hash TEXT NOT NULL, root_directory_blake3_hash TEXT NOT NULL, last_successful_file_upload_unix_ticks INTEGER NOT NULL, last_successful_directory_version_upload_unix_ticks INTEGER NOT NULL);"
            "CREATE TABLE IF NOT EXISTS status_directories (relative_path TEXT PRIMARY KEY, parent_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"
            "CREATE INDEX IF NOT EXISTS ix_status_directories_parent ON status_directories(parent_path);"
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_status_directories_directory_version_id ON status_directories(directory_version_id);"
            "CREATE TABLE IF NOT EXISTS status_files (relative_path TEXT PRIMARY KEY, directory_path TEXT NOT NULL, directory_version_id TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, FOREIGN KEY (directory_version_id) REFERENCES status_directories(directory_version_id) ON DELETE CASCADE);"
            "CREATE INDEX IF NOT EXISTS ix_status_files_directory_path ON status_files(directory_path);"
            "CREATE INDEX IF NOT EXISTS ix_status_files_directory_version_id ON status_files(directory_version_id);"
            "CREATE INDEX IF NOT EXISTS ix_status_files_sha256 ON status_files(sha256_hash);"
            "CREATE TABLE IF NOT EXISTS object_cache_directories (directory_version_id TEXT PRIMARY KEY, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL);"
            "CREATE INDEX IF NOT EXISTS ix_object_cache_directories_relative_path ON object_cache_directories(relative_path);"
            "CREATE TABLE IF NOT EXISTS object_cache_directory_children (parent_directory_version_id TEXT NOT NULL, child_directory_version_id TEXT NOT NULL, ordinal INTEGER NOT NULL, PRIMARY KEY (parent_directory_version_id, child_directory_version_id), FOREIGN KEY (parent_directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE CASCADE, FOREIGN KEY (child_directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE RESTRICT);"
            "CREATE INDEX IF NOT EXISTS ix_object_cache_children_parent ON object_cache_directory_children(parent_directory_version_id);"
            "CREATE TABLE IF NOT EXISTS object_cache_directory_files (directory_version_id TEXT NOT NULL, relative_path TEXT NOT NULL, sha256_hash TEXT NOT NULL, blake3_hash TEXT NOT NULL, is_binary INTEGER NOT NULL, size_bytes INTEGER NOT NULL, created_at_unix_ticks INTEGER NOT NULL, uploaded_to_object_storage INTEGER NOT NULL, last_write_time_utc_ticks INTEGER NOT NULL, PRIMARY KEY (directory_version_id, relative_path), FOREIGN KEY (directory_version_id) REFERENCES object_cache_directories(directory_version_id) ON DELETE CASCADE);"
            "CREATE INDEX IF NOT EXISTS ix_object_cache_files_path_hash ON object_cache_directory_files(relative_path, sha256_hash);"
            "CREATE TABLE IF NOT EXISTS watch_journal (sequence INTEGER PRIMARY KEY AUTOINCREMENT, created_at_unix_ticks INTEGER NOT NULL);"
        |]

    let private requiredTableNames =
        [|
            "meta"
            "status_meta"
            "status_directories"
            "status_files"
            "object_cache_directories"
            "object_cache_directory_children"
            "object_cache_directory_files"
            "watch_journal"
        |]

    let private requiredIndexNames =
        [|
            "ix_status_directories_parent"
            "ix_status_directories_directory_version_id"
            "ix_status_files_directory_path"
            "ix_status_files_directory_version_id"
            "ix_status_files_sha256"
            "ix_object_cache_directories_relative_path"
            "ix_object_cache_children_parent"
            "ix_object_cache_files_path_hash"
        |]

    /// Models read only local state inspection values passed between the parser and local state db handlers.
    type ReadOnlyLocalStateInspection =
        {
            DbPath: string
            ParentDirectoryExists: bool
            DbFileExists: bool
            DbPathIsDirectory: bool
            OpenedReadOnly: bool
            OpenError: string option
            SchemaVersion: string option
            MissingRequiredTables: string array
            MissingRequiredIndexes: string array
            IntegrityCheckRows: string array
            ForeignKeyViolations: string array
            WatchJournalShapeValid: bool option
            WatchJournalAppliedThroughMetadataValid: bool option
            ObjectCacheReadable: bool option
            ObjectCacheError: string option
        }

    /// Reads empty read only inspection data from the local SQLite state database.
    let private emptyReadOnlyInspection dbPath parentDirectoryExists dbFileExists dbPathIsDirectory openedReadOnly openError =
        {
            DbPath = dbPath
            ParentDirectoryExists = parentDirectoryExists
            DbFileExists = dbFileExists
            DbPathIsDirectory = dbPathIsDirectory
            OpenedReadOnly = openedReadOnly
            OpenError = openError
            SchemaVersion = None
            MissingRequiredTables = requiredTableNames
            MissingRequiredIndexes = requiredIndexNames
            IntegrityCheckRows = Array.empty
            ForeignKeyViolations = Array.empty
            WatchJournalShapeValid = None
            WatchJournalAppliedThroughMetadataValid = None
            ObjectCacheReadable = None
            ObjectCacheError = None
        }

    let private sqliteHeaderMagic = Encoding.ASCII.GetBytes("SQLite format 3" + string (char 0))

    /// Evaluates is wal mode header against parsed options and command state.
    let private isWalModeHeader (dbPath: string) =
        try
            use stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

            if stream.Length < 20L then
                false
            else
                let header = Array.zeroCreate<byte> 20
                let bytesRead = stream.Read(header, 0, header.Length)

                bytesRead = header.Length
                && header[0..15] = sqliteHeaderMagic
                && header[18] = 2uy
                && header[19] = 2uy
        with
        | _ -> false

    /// Resolves the local-state database wal sidecar paths value used to open .grace/grace-local.db.
    let private walSidecarPaths (dbPath: string) = dbPath + "-wal", dbPath + "-shm"

    /// Coordinates local SQLite state for missing partial wal sidecars, including Grace status, object cache, or watch metadata.
    let private missingPartialWalSidecars (dbPath: string) =
        let walPath, shmPath = walSidecarPaths dbPath
        let walExists = File.Exists(walPath)
        let shmExists = File.Exists(shmPath)

        if walExists = shmExists then
            Array.empty
        else
            [|
                if not walExists then Path.GetFileName(walPath)

                if not shmExists then Path.GetFileName(shmPath)
            |]

    /// Reads should use immutable read only snapshot data from the local SQLite state database.
    let private shouldUseImmutableReadOnlySnapshot (dbPath: string) =
        let walPath, shmPath = walSidecarPaths dbPath

        isWalModeHeader dbPath
        && not (File.Exists(walPath))
        && not (File.Exists(shmPath))

    /// Resolves the local-state database open read only connection value used to open .grace/grace-local.db.
    let private openReadOnlyConnection (dbPath: string) immutableSnapshot =
        sqliteInitialized.Value |> ignore
        let traceOpenConnections = shouldTraceOpenConnections ()

        if traceOpenConnections then
            logTrace $"openReadOnlyConnection starting. dbPath={dbPath} immutableSnapshot={immutableSnapshot}"

        let connectionString =
            let builder = SqliteConnectionStringBuilder()

            builder.DataSource <- if immutableSnapshot then $"{Uri(dbPath).AbsoluteUri}?immutable=1" else dbPath

            builder.Mode <- SqliteOpenMode.ReadOnly
            builder.Pooling <- false
            builder.DefaultTimeout <- BusyTimeoutMs / 1000
            builder.ToString()

        let connection = new SqliteConnection(connectionString)

        try
            connection.Open()
            executePragma connection $"PRAGMA busy_timeout = {BusyTimeoutMs};"
            executePragma connection "PRAGMA query_only = ON;"

            if traceOpenConnections then
                logTrace $"openReadOnlyConnection opened connection. dbPath={dbPath} immutableSnapshot={immutableSnapshot}"

            connection
        with
        | ex ->
            try
                connection.Dispose()
            with
            | _ -> ()

            raise ex

    /// Reads text rows data needed by the CLI workflow.
    let private readTextRows (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        use reader = cmd.ExecuteReader()
        let rows = ResizeArray<string>()

        while reader.Read() do
            rows.Add(reader.GetString(0))

        rows |> Seq.toArray

    /// Reads object names data needed by the CLI workflow.
    let private readObjectNames (connection: SqliteConnection) objectType =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "SELECT name FROM sqlite_master WHERE type = $type;"

        cmd.Parameters.AddWithValue("$type", objectType)
        |> ignore

        use reader = cmd.ExecuteReader()
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        while reader.Read() do
            names.Add(reader.GetString(0)) |> ignore

        names

    /// Reads schema version read only data needed by the CLI workflow.
    let private readSchemaVersionReadOnly (connection: SqliteConnection) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "SELECT value FROM meta WHERE key = 'schema_version' LIMIT 1;"
        let value = cmd.ExecuteScalar()

        if isNull value || value = DBNull.Value then
            None
        else
            Some(Convert.ToString(value))

    /// Reads foreign key violations data needed by the CLI workflow.
    let private readForeignKeyViolations (connection: SqliteConnection) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "PRAGMA foreign_key_check;"
        use reader = cmd.ExecuteReader()
        let violations = ResizeArray<string>()

        while reader.Read() do
            let tableName = reader.GetString(0)
            let rowId = reader.GetInt64(1)
            let parent = reader.GetString(2)
            let foreignKeyId = reader.GetInt32(3)
            violations.Add($"{tableName}:{rowId}->{parent}#{foreignKeyId}")

        violations |> Seq.toArray

    /// Coordinates local SQLite state for column exists, including Grace status, object cache, or watch metadata.
    let private columnExists (connection: SqliteConnection) tableName columnName =
        use command = connection.CreateCommand()
        command.CommandText <- $"PRAGMA table_info({tableName});"
        use reader = command.ExecuteReader()
        let mutable found = false

        while reader.Read() do
            if StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(1), columnName) then
                found <- true

        found

    /// Reports whether a LocalStateDb table exists before writable operations trust the schema version.
    let private tableExists (connection: SqliteConnection) tableName =
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table_name LIMIT 1;"

        command.Parameters.AddWithValue("$table_name", tableName)
        |> ignore

        use reader = command.ExecuteReader()
        reader.Read()

    /// Captures the SQLite column shape that writable schema checks must trust before using a table.
    type private TableColumnShape = { Name: string; TypeName: string; NotNull: bool; PrimaryKeyOrdinal: int }

    /// Reads SQLite table column metadata so schema-version checks can reject partially created local databases.
    let private readTableColumnShapes (connection: SqliteConnection) tableName =
        use command = connection.CreateCommand()
        command.CommandText <- $"PRAGMA table_info({tableName});"
        use reader = command.ExecuteReader()
        let columns = ResizeArray<TableColumnShape>()

        while reader.Read() do
            columns.Add(
                { Name = reader.GetString(1); TypeName = reader.GetString(2); NotNull = reader.GetInt32(3) <> 0; PrimaryKeyOrdinal = reader.GetInt32(5) }
            )

        columns |> Seq.toArray

    /// Reports whether SQLite declared a column with INTEGER affinity for a trusted local-state sequence.
    let private isIntegerColumnType (typeName: string) = StringComparer.OrdinalIgnoreCase.Equals(typeName.Trim(), "INTEGER")

    /// Locates the top-level column list inside SQLite's stored CREATE TABLE statement.
    let private tryGetCreateTableColumnList (sql: string) =
        let mutable startIndex = -1
        let mutable endIndex = -1
        let mutable depth = 0
        let mutable quote = '\000'
        let mutable index = 0

        while index < sql.Length && endIndex < 0 do
            let ch = sql[index]

            if quote <> '\000' then
                if quote = ']' then
                    if ch = ']' then quote <- '\000'
                elif ch = quote then
                    if index + 1 < sql.Length && sql[index + 1] = quote then
                        index <- index + 1
                    else
                        quote <- '\000'
            else
                match ch with
                | '\''
                | '"'
                | '`' -> quote <- ch
                | '[' -> quote <- ']'
                | '(' ->
                    if depth = 0 then startIndex <- index + 1
                    depth <- depth + 1
                | ')' ->
                    if depth > 0 then
                        depth <- depth - 1

                        if depth = 0 then endIndex <- index
                | _ -> ()

            index <- index + 1

        if startIndex >= 0 && endIndex > startIndex then
            Some(sql.Substring(startIndex, endIndex - startIndex))
        else
            None

    /// Splits SQLite column and constraint declarations without trusting text inside defaults or CHECK expressions.
    let private splitTopLevelSqlDeclarations (declarations: string) =
        let parts = ResizeArray<string>()
        let mutable startIndex = 0
        let mutable depth = 0
        let mutable quote = '\000'
        let mutable index = 0

        while index < declarations.Length do
            let ch = declarations[index]

            if quote <> '\000' then
                if quote = ']' then
                    if ch = ']' then quote <- '\000'
                elif ch = quote then
                    if index + 1 < declarations.Length
                       && declarations[index + 1] = quote then
                        index <- index + 1
                    else
                        quote <- '\000'
            else
                match ch with
                | '\''
                | '"'
                | '`' -> quote <- ch
                | '[' -> quote <- ']'
                | '(' -> depth <- depth + 1
                | ')' when depth > 0 -> depth <- depth - 1
                | ',' when depth = 0 ->
                    parts.Add(
                        declarations
                            .Substring(startIndex, index - startIndex)
                            .Trim()
                    )

                    startIndex <- index + 1
                | _ -> ()

            index <- index + 1

        let last = declarations.Substring(startIndex).Trim()

        if not (String.IsNullOrWhiteSpace(last)) then parts.Add(last)

        parts |> Seq.toArray

    /// Parses the leading SQLite identifier from a column declaration.
    let private tryReadLeadingSqlIdentifier (declaration: string) =
        let mutable index = 0

        while index < declaration.Length
              && Char.IsWhiteSpace(declaration[index]) do
            index <- index + 1

        if index >= declaration.Length then
            None
        else
            let ch = declaration[index]

            if ch = '"' || ch = '`' || ch = '[' then
                let terminator = if ch = '[' then ']' else ch
                let startIndex = index + 1
                index <- startIndex
                let mutable identifier = StringBuilder()
                let mutable closed = false

                while index < declaration.Length && not closed do
                    if declaration[index] = terminator then
                        if terminator <> ']'
                           && index + 1 < declaration.Length
                           && declaration[index + 1] = terminator then
                            identifier.Append(terminator) |> ignore
                            index <- index + 2
                        else
                            closed <- true
                            index <- index + 1
                    else
                        identifier.Append(declaration[index]) |> ignore
                        index <- index + 1

                if closed then
                    Some(identifier.ToString(), declaration.Substring(index))
                else
                    None
            else
                let startIndex = index

                while index < declaration.Length
                      && not (Char.IsWhiteSpace(declaration[index]))
                      && declaration[index] <> ',' do
                    index <- index + 1

                if index > startIndex then
                    Some(declaration.Substring(startIndex, index - startIndex), declaration.Substring(index))
                else
                    None

    /// Matches the actual Watch journal sequence column declaration that prevents rowid reuse after pruning.
    let private watchJournalSequenceAutoincrementDeclarationPattern =
        Regex(
            @"^\s+INTEGER\s+PRIMARY\s+KEY\s+AUTOINCREMENT\b",
            RegexOptions.IgnoreCase
            ||| RegexOptions.CultureInvariant
        )

    /// Reports whether SQLite's stored CREATE TABLE statement gives the sequence column AUTOINCREMENT semantics.
    let private createSqlDeclaresSequenceAutoincrement (sql: string) =
        match tryGetCreateTableColumnList sql with
        | Some columnList ->
            splitTopLevelSqlDeclarations columnList
            |> Array.exists (fun declaration ->
                match tryReadLeadingSqlIdentifier declaration with
                | Some (identifier, remainder) when StringComparer.OrdinalIgnoreCase.Equals(identifier, "sequence") ->
                    watchJournalSequenceAutoincrementDeclarationPattern.IsMatch(remainder)
                | _ -> false)
        | None -> false

    /// Reports whether SQLite stored the Watch journal table as an AUTOINCREMENT sequence table.
    let private watchJournalUsesAutoincrement (connection: SqliteConnection) =
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'watch_journal' LIMIT 1;"
        let value = command.ExecuteScalar()

        match value with
        | :? string as sql -> createSqlDeclaresSequenceAutoincrement sql
        | _ -> false

    /// Verifies that the Watch journal table can support ordered local recovery and retention operations.
    let private hasRequiredWatchJournalShape (connection: SqliteConnection) =
        let columns = readTableColumnShapes connection "watch_journal"

        let tryFindColumn columnName =
            columns
            |> Array.tryFind (fun column -> StringComparer.OrdinalIgnoreCase.Equals(column.Name, columnName))

        let hasSequenceColumn =
            match tryFindColumn "sequence" with
            | Some column ->
                isIntegerColumnType column.TypeName
                && column.PrimaryKeyOrdinal = 1
            | None -> false

        let hasCreatedAtColumn =
            match tryFindColumn "created_at_unix_ticks" with
            | Some column ->
                isIntegerColumnType column.TypeName
                && column.NotNull
                && column.PrimaryKeyOrdinal = 0
            | None -> false

        hasSequenceColumn
        && hasCreatedAtColumn
        && watchJournalUsesAutoincrement connection

    /// Reports whether the Watch journal contains rows that require trustworthy recovery metadata.
    let private hasWatchJournalRows (connection: SqliteConnection) =
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT EXISTS(SELECT 1 FROM watch_journal LIMIT 1);"
        Convert.ToInt32(command.ExecuteScalar()) <> 0

    /// Tries to read SQLite's allocated Watch journal sequence without trusting malformed persisted values.
    let private tryReadAllocatedWatchJournalSequence (connection: SqliteConnection) =
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT seq FROM sqlite_sequence WHERE name = 'watch_journal' LIMIT 1;"
        let value = command.ExecuteScalar()

        match value with
        | null
        | :? DBNull -> Some 0L
        | :? int64 as sequence when sequence >= 0L -> Some sequence
        | :? int as sequence when sequence >= 0 -> Some(int64 sequence)
        | :? string as value ->
            match Int64.TryParse(value) with
            | true, sequence when sequence >= 0L -> Some sequence
            | _ -> None
        | _ -> None

    /// Reads SQLite's allocated Watch journal sequence so recovery metadata cannot outrun future row ids.
    let private readAllocatedWatchJournalSequence (connection: SqliteConnection) =
        match tryReadAllocatedWatchJournalSequence connection with
        | Some sequence -> sequence
        | None -> raise (InvalidDataException("sqlite_sequence.seq for watch_journal must be a non-negative 64-bit integer."))

    /// Reads local-state metadata for read-only inspection checks without writing default rows.
    let private tryGetMetaValueReadOnly (connection: SqliteConnection) (key: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "SELECT value FROM meta WHERE key = $key LIMIT 1;"
        cmd.Parameters.AddWithValue("$key", key) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(reader.GetString(0)) else None

    /// Parses read-only Watch recovery metadata using the same nonnegative sequence contract as writable acceptance.
    let private tryParseWatchJournalAppliedThroughSequenceReadOnly (value: string) =
        match Int64.TryParse(value) with
        | true, sequence when sequence >= 0L -> Some sequence
        | _ -> None

    /// Reads inspect object cache read only data from the local SQLite state database.
    let private inspectObjectCacheReadOnly (connection: SqliteConnection) =
        try
            for tableName in
                [|
                    "object_cache_directories"
                    "object_cache_directory_children"
                    "object_cache_directory_files"
                |] do
                use cmd = connection.CreateCommand()
                cmd.CommandText <- $"SELECT COUNT(*) FROM {tableName};"
                cmd.ExecuteScalar() |> ignore

            Some true, None
        with
        | ex -> Some false, Some ex.Message

    /// Reads Watch journal schema and recovery metadata health without mutating local state.
    let private inspectWatchJournalReadOnly (connection: SqliteConnection) =
        if not (tableExists connection "watch_journal") then
            Some false, None
        else
            let shapeValid =
                try
                    hasRequiredWatchJournalShape connection
                with
                | _ -> false

            let metadataValid =
                if shapeValid then
                    try
                        match tryGetMetaValueReadOnly connection WatchJournalAppliedThroughSequenceMetaKey with
                        | Some value ->
                            match tryParseWatchJournalAppliedThroughSequenceReadOnly value with
                            | Some sequence ->
                                match tryReadAllocatedWatchJournalSequence connection with
                                | Some allocatedSequence -> sequence <= allocatedSequence
                                | None -> false
                            | None -> false
                        | None -> not (hasWatchJournalRows connection)
                    with
                    | _ -> false
                else
                    false

            Some shapeValid, Some metadataValid

    /// Reads inspect read only data from the local SQLite state database.
    let inspectReadOnly (dbPath: string) =
        let normalizedPath = Path.GetFullPath(dbPath)
        let directoryPath = Path.GetDirectoryName(normalizedPath)

        let parentDirectoryExists =
            String.IsNullOrWhiteSpace(directoryPath)
            || Directory.Exists(directoryPath)

        let dbFileExists = File.Exists(normalizedPath)
        let dbPathIsDirectory = Directory.Exists(normalizedPath)

        if not parentDirectoryExists
           || (not dbFileExists && not dbPathIsDirectory)
           || dbPathIsDirectory then
            emptyReadOnlyInspection normalizedPath parentDirectoryExists dbFileExists dbPathIsDirectory false None
        else
            let missingPartialWalSidecars = missingPartialWalSidecars normalizedPath

            if missingPartialWalSidecars.Length > 0 then
                let missingNames = String.concat ", " missingPartialWalSidecars

                emptyReadOnlyInspection
                    normalizedPath
                    parentDirectoryExists
                    dbFileExists
                    dbPathIsDirectory
                    false
                    (Some
                        $"Database has an incomplete WAL sidecar set; missing: {missingNames}. Doctor did not open the database to avoid creating sidecar files or ignoring live WAL content.")
            else
                try
                    let immutableSnapshot = shouldUseImmutableReadOnlySnapshot normalizedPath
                    use connection = openReadOnlyConnection normalizedPath immutableSnapshot
                    let tableNames = readObjectNames connection "table"
                    let indexNames = readObjectNames connection "index"

                    let missingRequiredTables =
                        requiredTableNames
                        |> Array.filter (fun tableName -> not (tableNames.Contains(tableName)))

                    let missingRequiredIndexes =
                        requiredIndexNames
                        |> Array.filter (fun indexName -> not (indexNames.Contains(indexName)))

                    let schemaVersion =
                        try
                            readSchemaVersionReadOnly connection
                        with
                        | _ -> None

                    let integrityRows =
                        try
                            readTextRows connection "PRAGMA integrity_check;"
                        with
                        | ex -> [| ex.Message |]

                    let foreignKeyViolations =
                        try
                            readForeignKeyViolations connection
                        with
                        | ex -> [| ex.Message |]

                    let objectCacheReadable, objectCacheError = inspectObjectCacheReadOnly connection
                    let watchJournalShapeValid, watchJournalAppliedThroughMetadataValid = inspectWatchJournalReadOnly connection

                    {
                        DbPath = normalizedPath
                        ParentDirectoryExists = parentDirectoryExists
                        DbFileExists = dbFileExists
                        DbPathIsDirectory = dbPathIsDirectory
                        OpenedReadOnly = true
                        OpenError = None
                        SchemaVersion = schemaVersion
                        MissingRequiredTables = missingRequiredTables
                        MissingRequiredIndexes = missingRequiredIndexes
                        IntegrityCheckRows = integrityRows
                        ForeignKeyViolations = foreignKeyViolations
                        WatchJournalShapeValid = watchJournalShapeValid
                        WatchJournalAppliedThroughMetadataValid = watchJournalAppliedThroughMetadataValid
                        ObjectCacheReadable = objectCacheReadable
                        ObjectCacheError = objectCacheError
                    }
                with
                | ex -> emptyReadOnlyInspection normalizedPath parentDirectoryExists dbFileExists dbPathIsDirectory false (Some ex.Message)

    /// Tries to map get meta value and returns a GraceError instead of throwing on unsupported input.
    let private tryGetMetaValue (connection: SqliteConnection) (key: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "SELECT value FROM meta WHERE key = $key LIMIT 1;"
        cmd.Parameters.AddWithValue("$key", key) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(reader.GetString(0)) else None

    /// Coordinates local SQLite state for set meta value, including Grace status, object cache, or watch metadata.
    let private setMetaValue (connection: SqliteConnection) (key: string) (value: string) =
        executeNonQueryWithParams connection "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);" (fun parameters ->
            parameters.AddWithValue("$key", key) |> ignore
            parameters.AddWithValue("$value", value) |> ignore)

    /// Persists the watch journal applied-through metadata default without advancing recovery state.
    let private insertWatchJournalAppliedThroughIfMissing (connection: SqliteConnection) =
        executeNonQueryWithParams connection "INSERT OR IGNORE INTO meta (key, value) VALUES ($key, '0');" (fun parameters ->
            parameters.AddWithValue("$key", WatchJournalAppliedThroughSequenceMetaKey)
            |> ignore)

    /// Parses Watch journal recovery metadata only when it preserves the nonnegative sequence invariant.
    let private tryParseWatchJournalAppliedThroughSequence (value: string) =
        match Int64.TryParse(value) with
        | true, sequence when sequence >= 0L -> Some sequence
        | _ -> None

    /// Verifies that existing Watch recovery metadata can be trusted before accepting schema version 5.
    let private hasValidWatchJournalAppliedThroughSequenceMeta (connection: SqliteConnection) =
        match tryGetMetaValue connection WatchJournalAppliedThroughSequenceMetaKey with
        | Some value ->
            match tryParseWatchJournalAppliedThroughSequence value with
            | Some sequence ->
                match tryReadAllocatedWatchJournalSequence connection with
                | Some allocatedSequence -> sequence <= allocatedSequence
                | None -> false
            | None -> false
        | None -> not (hasWatchJournalRows connection)

    /// Reads the applied-through journal sequence used by future Watch recovery work.
    let private readWatchJournalAppliedThroughSequenceInternal (connection: SqliteConnection) =
        match tryGetMetaValue connection WatchJournalAppliedThroughSequenceMetaKey with
        | Some value ->
            match tryParseWatchJournalAppliedThroughSequence value with
            | Some sequence -> sequence
            | None -> raise (InvalidDataException($"{WatchJournalAppliedThroughSequenceMetaKey} must be a non-negative 64-bit integer."))
        | None -> 0L

    /// Persists insert status meta if missing changes in the local SQLite state database.
    let private insertStatusMetaIfMissing (connection: SqliteConnection) =
        let defaultStatus = GraceStatus.Default

        executeNonQueryWithParams
            connection
            "INSERT OR IGNORE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, $root_id, $root_sha256_hash, $root_blake3_hash, $last_file, $last_dir);"
            (fun parameters ->
                parameters.AddWithValue("$root_id", defaultStatus.RootDirectoryId.ToString())
                |> ignore

                parameters.AddWithValue("$root_sha256_hash", defaultStatus.RootDirectorySha256Hash)
                |> ignore

                parameters.AddWithValue("$root_blake3_hash", Blake3Hash String.Empty)
                |> ignore

                parameters.AddWithValue("$last_file", defaultStatus.LastSuccessfulFileUpload.ToUnixTimeTicks())
                |> ignore

                parameters.AddWithValue("$last_dir", defaultStatus.LastSuccessfulDirectoryVersionUpload.ToUnixTimeTicks())
                |> ignore)

    /// Evaluates has required writable schema against parsed options and command state.
    let private hasRequiredWritableSchema (connection: SqliteConnection) =
        columnExists connection "status_meta" "root_directory_blake3_hash"
        && columnExists connection "status_directories" "blake3_hash"
        && columnExists connection "status_files" "blake3_hash"
        && columnExists connection "object_cache_directories" "blake3_hash"
        && columnExists connection "object_cache_directory_files" "blake3_hash"
        && tableExists connection "watch_journal"
        && hasRequiredWatchJournalShape connection
        && hasValidWatchJournalAppliedThroughSequenceMeta connection

    /// Evaluates has empty writable status blake3 rows against parsed options and command state.
    let private hasEmptyWritableStatusBlake3Rows (connection: SqliteConnection) =
        if columnExists connection "status_directories" "blake3_hash"
           && columnExists connection "status_files" "blake3_hash" then
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT EXISTS(SELECT 1 FROM status_directories WHERE TRIM(blake3_hash) = '' LIMIT 1) OR EXISTS(SELECT 1 FROM status_files WHERE TRIM(blake3_hash) = '' LIMIT 1);"

            Convert.ToInt32(command.ExecuteScalar()) <> 0
        else
            false

    /// Resolves the local-state database recreate database value used to open .grace/grace-local.db.
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

    /// Ensures required command context is present.
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
                                    /// Coordinates local SQLite state for run schema, including Grace status, object cache, or watch metadata.
                                    let runSchema (connection: SqliteConnection) =
                                        ensureJournalMode connection

                                        schemaStatements
                                        |> Array.iteri (fun index statement ->
                                            logTraceStatement $"schema[{index}] start" statement
                                            executeNonQuery connection statement
                                            logTrace $"schema[{index}] done")

                                    /// Coordinates local SQLite state for schema exists, including Grace status, object cache, or watch metadata.
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

                                                if
                                                    not recreate
                                                    && not (hasRequiredWritableSchema connection)
                                                then
                                                    recreate <- true

                                                if not recreate
                                                   && hasEmptyWritableStatusBlake3Rows connection then
                                                    recreate <- true

                                                if not recreate then
                                                    logTrace "status_meta ensuring default row"
                                                    insertStatusMetaIfMissing connection
                                                    insertWatchJournalAppliedThroughIfMissing connection
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
                                        insertWatchJournalAppliedThroughIfMissing connection
                                        logTrace "status_meta ensuring default row"
                                        insertStatusMetaIfMissing connection
                                })

                        initializedDbs[normalizedPath] <- true
                finally
                    semaphore.Release() |> ignore
        }

    /// Reads the local Watch journal recovery watermark from LocalStateDb metadata.
    let readWatchJournalAppliedThroughSequence (dbPath: string) =
        task {
            do! ensureDbInitialized dbPath
            use connection = openConnection dbPath
            return readWatchJournalAppliedThroughSequenceInternal connection
        }

    /// Persists the local Watch journal recovery watermark without changing journal rows.
    let setWatchJournalAppliedThroughSequence (dbPath: string) (sequence: int64) =
        task {
            if sequence < 0L then
                invalidArg (nameof sequence) "Applied-through sequence must be greater than or equal to zero."

            do! ensureDbInitialized dbPath

            return!
                executeWithRetry (fun () ->
                    task {
                        use connection = openConnection dbPath
                        executeNonQuery connection "BEGIN IMMEDIATE;"
                        let mutable committed = false

                        try
                            let currentSequence = readWatchJournalAppliedThroughSequenceInternal connection

                            if sequence < currentSequence then
                                invalidOp $"Applied-through sequence cannot move backward from {currentSequence} to {sequence}."

                            let allocatedSequence = readAllocatedWatchJournalSequence connection

                            if sequence > allocatedSequence then
                                invalidOp
                                    $"Applied-through sequence cannot advance to {sequence} because the Watch journal has only allocated through {allocatedSequence}."

                            setMetaValue connection WatchJournalAppliedThroughSequenceMetaKey $"{sequence}"
                            executeNonQuery connection "COMMIT;"
                            committed <- true
                        finally
                            if not committed then
                                try
                                    executeNonQuery connection "ROLLBACK;"
                                with
                                | _ -> ()
                    })
        }

    /// Prunes applied Watch journal rows while keeping a small diagnostic tail behind the watermark.
    let pruneWatchJournalRetention (dbPath: string) =
        task {
            do! ensureDbInitialized dbPath

            return!
                executeWithRetry (fun () ->
                    task {
                        use connection = openConnection dbPath
                        let appliedThroughSequence = readWatchJournalAppliedThroughSequenceInternal connection

                        let pruneThroughSequence =
                            max
                                0L
                                (appliedThroughSequence
                                 - WatchJournalRetainedAppliedRows)

                        if pruneThroughSequence > 0L then
                            executeNonQueryWithParams connection "DELETE FROM watch_journal WHERE sequence <= $sequence;" (fun parameters ->
                                parameters.AddWithValue("$sequence", pruneThroughSequence)
                                |> ignore)
                    })
        }

    /// Models status meta values passed between the parser and local state db handlers.
    type StatusMeta =
        {
            RootDirectoryId: DirectoryVersionId
            RootDirectorySha256Hash: Sha256Hash
            RootDirectoryBlake3Hash: Blake3Hash
            LastSuccessfulFileUpload: Instant
            LastSuccessfulDirectoryVersionUpload: Instant
        }

    /// Reads status meta internal data needed by the CLI workflow.
    let private readStatusMetaInternal (connection: SqliteConnection) =
        use cmd = connection.CreateCommand()

        cmd.CommandText <-
            "SELECT root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks FROM status_meta WHERE id = 1;"

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let rootId = Guid.Parse(reader.GetString(0))
            let rootSha256Hash = reader.GetString(1)
            let rootBlake3Hash = reader.GetString(2)
            let lastFile = Instant.FromUnixTimeTicks(reader.GetInt64(3))
            let lastDir = Instant.FromUnixTimeTicks(reader.GetInt64(4))

            {
                RootDirectoryId = rootId
                RootDirectorySha256Hash = rootSha256Hash
                RootDirectoryBlake3Hash = rootBlake3Hash
                LastSuccessfulFileUpload = lastFile
                LastSuccessfulDirectoryVersionUpload = lastDir
            }
            |> Some
        else
            None

    /// Reads status meta data needed by the CLI workflow.
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
                            RootDirectoryBlake3Hash = Blake3Hash String.Empty
                            LastSuccessfulFileUpload = defaultStatus.LastSuccessfulFileUpload
                            LastSuccessfulDirectoryVersionUpload = defaultStatus.LastSuccessfulDirectoryVersionUpload
                        }
            finally
                connection.Dispose()
        }

    /// Reads root directory blake3 hash from ParseResult, local configuration, or Grace ids.
    let private getRootDirectoryBlake3Hash (graceStatus: GraceStatus) =
        let mutable rootDirectory = Unchecked.defaultof<LocalDirectoryVersion>

        if
            not (isNull graceStatus.Index)
            && graceStatus.Index.TryGetValue(graceStatus.RootDirectoryId, &rootDirectory)
        then
            rootDirectory.Blake3Hash
        elif not (String.IsNullOrWhiteSpace(string graceStatus.RootDirectoryBlake3Hash)) then
            graceStatus.RootDirectoryBlake3Hash
        else
            Blake3Hash String.Empty

    /// Coordinates local SQLite state for set status meta, including Grace status, object cache, or watch metadata.
    let private setStatusMeta (connection: SqliteConnection) (graceStatus: GraceStatus) =
        let incomingRootDirectoryBlake3Hash = getRootDirectoryBlake3Hash graceStatus

        let statusHasRootIdentity =
            graceStatus.RootDirectoryId
            <> DirectoryVersionId.Empty
            || not (String.IsNullOrWhiteSpace(string graceStatus.RootDirectorySha256Hash))

        let rootDirectoryBlake3Hash =
            if not (String.IsNullOrWhiteSpace(string incomingRootDirectoryBlake3Hash)) then
                incomingRootDirectoryBlake3Hash
            elif not statusHasRootIdentity then
                incomingRootDirectoryBlake3Hash
            else
                match readStatusMetaInternal connection with
                | Some meta when
                    meta.RootDirectoryId = graceStatus.RootDirectoryId
                    && meta.RootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
                    && not (String.IsNullOrWhiteSpace(string meta.RootDirectoryBlake3Hash))
                    ->
                    meta.RootDirectoryBlake3Hash
                | _ -> incomingRootDirectoryBlake3Hash

        executeNonQueryWithParams
            connection
            "INSERT OR REPLACE INTO status_meta (id, root_directory_version_id, root_directory_sha256_hash, root_directory_blake3_hash, last_successful_file_upload_unix_ticks, last_successful_directory_version_upload_unix_ticks) VALUES (1, $root_id, $root_sha256_hash, $root_blake3_hash, $last_file, $last_dir);"
            (fun parameters ->
                parameters.AddWithValue("$root_id", graceStatus.RootDirectoryId.ToString())
                |> ignore

                parameters.AddWithValue("$root_sha256_hash", graceStatus.RootDirectorySha256Hash)
                |> ignore

                parameters.AddWithValue("$root_blake3_hash", rootDirectoryBlake3Hash)
                |> ignore

                parameters.AddWithValue("$last_file", graceStatus.LastSuccessfulFileUpload.ToUnixTimeTicks())
                |> ignore

                parameters.AddWithValue("$last_dir", graceStatus.LastSuccessfulDirectoryVersionUpload.ToUnixTimeTicks())
                |> ignore)

    /// Coordinates local SQLite state for replace status snapshot, including Grace status, object cache, or watch metadata.
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
                                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ($relative_path, $parent_path, $directory_version_id, $sha256_hash, $blake3_hash, $size_bytes, $created_at, $last_write);"

                                directoryCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$parent_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$blake3_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$size_bytes", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$created_at", SqliteType.Integer)
                                |> ignore

                                directoryCommand.Parameters.Add("$last_write", SqliteType.Integer)
                                |> ignore

                                use fileCommand = connection.CreateCommand()

                                fileCommand.CommandText <-
                                    "INSERT OR REPLACE INTO status_files (relative_path, directory_path, directory_version_id, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ($relative_path, $directory_path, $directory_version_id, $sha256_hash, $blake3_hash, $is_binary, $size_bytes, $created_at, $uploaded, $last_write);"

                                fileCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$directory_path", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                fileCommand.Parameters.Add("$blake3_hash", SqliteType.Text)
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
                                    directoryCommand.Parameters["$blake3_hash"].Value <- directory.Blake3Hash
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
                                        fileCommand.Parameters["$blake3_hash"].Value <- file.Blake3Hash
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

    /// Persists upsert object cache changes in the local SQLite state database.
    let upsertObjectCache (dbPath: string) (newDirectoryVersions: IEnumerable<LocalDirectoryVersion>) =
        task {
            do! ensureDbInitialized dbPath
            let directoriesToUpsert = newDirectoryVersions |> Seq.toArray

            return!
                executeWithRetry (fun () ->
                    task {
                        let connection = openConnection dbPath

                        try
                            executeNonQuery connection "BEGIN IMMEDIATE;"

                            try
                                let knownDirectoryIds = HashSet<string>(StringComparer.OrdinalIgnoreCase)

                                use knownDirectoryIdsCommand = connection.CreateCommand()
                                knownDirectoryIdsCommand.CommandText <- "SELECT directory_version_id FROM object_cache_directories;"

                                use knownDirectoryIdsReader = knownDirectoryIdsCommand.ExecuteReader()

                                while knownDirectoryIdsReader.Read() do
                                    knownDirectoryIds.Add(knownDirectoryIdsReader.GetString(0))
                                    |> ignore

                                use directoryCommand = connection.CreateCommand()

                                directoryCommand.CommandText <-
                                    "INSERT INTO object_cache_directories (directory_version_id, relative_path, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ($directory_version_id, $relative_path, $sha256_hash, $blake3_hash, $size_bytes, $created_at, $last_write) ON CONFLICT(directory_version_id) DO UPDATE SET relative_path = excluded.relative_path, sha256_hash = excluded.sha256_hash, blake3_hash = excluded.blake3_hash, size_bytes = excluded.size_bytes, created_at_unix_ticks = excluded.created_at_unix_ticks, last_write_time_utc_ticks = excluded.last_write_time_utc_ticks;"

                                directoryCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$blake3_hash", SqliteType.Text)
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
                                    "INSERT INTO object_cache_directory_children (parent_directory_version_id, child_directory_version_id, ordinal) VALUES ($parent_directory_version_id, $child_directory_version_id, $ordinal) ON CONFLICT(parent_directory_version_id, child_directory_version_id) DO UPDATE SET ordinal = excluded.ordinal;"

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
                                    "INSERT INTO object_cache_directory_files (directory_version_id, relative_path, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ($directory_version_id, $relative_path, $sha256_hash, $blake3_hash, $is_binary, $size_bytes, $created_at, $uploaded, $last_write) ON CONFLICT(directory_version_id, relative_path) DO UPDATE SET sha256_hash = excluded.sha256_hash, blake3_hash = excluded.blake3_hash, is_binary = excluded.is_binary, size_bytes = excluded.size_bytes, created_at_unix_ticks = excluded.created_at_unix_ticks, uploaded_to_object_storage = excluded.uploaded_to_object_storage, last_write_time_utc_ticks = excluded.last_write_time_utc_ticks;"

                                insertFileCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                insertFileCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                insertFileCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                insertFileCommand.Parameters.Add("$blake3_hash", SqliteType.Text)
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

                                // Pass 1: Ensure all directory rows exist before adding any FK-dependent rows.
                                directoriesToUpsert
                                |> Seq.iter (fun directory ->
                                    let directoryVersionId = directory.DirectoryVersionId.ToString()
                                    directoryCommand.Parameters["$directory_version_id"].Value <- directoryVersionId
                                    directoryCommand.Parameters["$relative_path"].Value <- directory.RelativePath
                                    directoryCommand.Parameters["$sha256_hash"].Value <- directory.Sha256Hash
                                    directoryCommand.Parameters["$blake3_hash"].Value <- directory.Blake3Hash
                                    directoryCommand.Parameters["$size_bytes"].Value <- directory.Size
                                    directoryCommand.Parameters["$created_at"].Value <- directory.CreatedAt.ToUnixTimeTicks()
                                    directoryCommand.Parameters["$last_write"].Value <- directory.LastWriteTimeUtc.Ticks
                                    directoryCommand.ExecuteNonQuery() |> ignore

                                    knownDirectoryIds.Add(directoryVersionId)
                                    |> ignore)

                                // Pass 2: Refresh child and file links for each upserted directory.
                                directoriesToUpsert
                                |> Seq.iter (fun directory ->
                                    deleteChildrenCommand.Parameters["$parent_directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    deleteChildrenCommand.ExecuteNonQuery() |> ignore

                                    deleteFilesCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    deleteFilesCommand.ExecuteNonQuery() |> ignore

                                    directory.Directories
                                    |> Seq.iteri (fun index childId ->
                                        let childDirectoryVersionId = childId.ToString()

                                        if knownDirectoryIds.Contains(childDirectoryVersionId) then
                                            insertChildCommand.Parameters["$parent_directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                            insertChildCommand.Parameters["$child_directory_version_id"].Value <- childDirectoryVersionId
                                            insertChildCommand.Parameters["$ordinal"].Value <- index
                                            insertChildCommand.ExecuteNonQuery() |> ignore
                                        else
                                            invalidOp
                                                $"Cannot upsert object cache because child DirectoryVersionId {childDirectoryVersionId} is missing. Parent DirectoryVersionId: {directory.DirectoryVersionId}.")

                                    directory.Files
                                    |> Seq.iter (fun file ->
                                        insertFileCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                        insertFileCommand.Parameters["$relative_path"].Value <- file.RelativePath
                                        insertFileCommand.Parameters["$sha256_hash"].Value <- file.Sha256Hash
                                        insertFileCommand.Parameters["$blake3_hash"].Value <- file.Blake3Hash
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

    /// Evaluates is file version in object cache against parsed options and command state.
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

    /// Evaluates is directory version in object cache against parsed options and command state.
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

    /// Persists remove object cache directory changes in the local SQLite state database.
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
                                    "INSERT OR REPLACE INTO status_directories (relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks) VALUES ($relative_path, $parent_path, $directory_version_id, $sha256_hash, $blake3_hash, $size_bytes, $created_at, $last_write);"

                                directoryCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$parent_path", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                directoryCommand.Parameters.Add("$blake3_hash", SqliteType.Text)
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
                                    directoryCommand.Parameters["$blake3_hash"].Value <- directory.Blake3Hash
                                    directoryCommand.Parameters["$size_bytes"].Value <- directory.Size
                                    directoryCommand.Parameters["$created_at"].Value <- directory.CreatedAt.ToUnixTimeTicks()
                                    directoryCommand.Parameters["$last_write"].Value <- directory.LastWriteTimeUtc.Ticks
                                    directoryCommand.ExecuteNonQuery() |> ignore)

                                use fileUpsertCommand = connection.CreateCommand()

                                fileUpsertCommand.CommandText <-
                                    "INSERT OR REPLACE INTO status_files (relative_path, directory_path, directory_version_id, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks) VALUES ($relative_path, $directory_path, $directory_version_id, $sha256_hash, $blake3_hash, $is_binary, $size_bytes, $created_at, $uploaded, $last_write);"

                                fileUpsertCommand.Parameters.Add("$relative_path", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$directory_path", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$directory_version_id", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$sha256_hash", SqliteType.Text)
                                |> ignore

                                fileUpsertCommand.Parameters.Add("$blake3_hash", SqliteType.Text)
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

                                // Upsert every file in each changed/new directory version. This keeps unchanged sibling files
                                // attached to the new directory_version_id when a directory row is replaced.
                                newDirectoryVersions
                                |> Seq.collect (fun directory ->
                                    directory.Files
                                    |> Seq.map (fun file -> (file, directory)))
                                |> Seq.iter (fun (file, directory) ->
                                    fileUpsertCommand.Parameters["$relative_path"].Value <- file.RelativePath
                                    fileUpsertCommand.Parameters["$directory_path"].Value <- directory.RelativePath
                                    fileUpsertCommand.Parameters["$directory_version_id"].Value <- directory.DirectoryVersionId.ToString()
                                    fileUpsertCommand.Parameters["$sha256_hash"].Value <- file.Sha256Hash
                                    fileUpsertCommand.Parameters["$blake3_hash"].Value <- file.Blake3Hash
                                    fileUpsertCommand.Parameters["$is_binary"].Value <- if file.IsBinary then 1 else 0
                                    fileUpsertCommand.Parameters["$size_bytes"].Value <- file.Size
                                    fileUpsertCommand.Parameters["$created_at"].Value <- file.CreatedAt.ToUnixTimeTicks()
                                    fileUpsertCommand.Parameters["$uploaded"].Value <- if file.UploadedToObjectStorage then 1 else 0
                                    fileUpsertCommand.Parameters["$last_write"].Value <- file.LastWriteTimeUtc.Ticks
                                    fileUpsertCommand.ExecuteNonQuery() |> ignore)

                                differences
                                |> Seq.iter (fun difference ->
                                    if difference.DifferenceType = Delete then
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

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private StatusDirectoryRow =
        {
            RelativePath: string
            ParentPath: string
            DirectoryVersionId: DirectoryVersionId
            Sha256Hash: Sha256Hash
            Blake3Hash: Blake3Hash
            SizeBytes: int64
            CreatedAt: Instant
            LastWriteTimeUtc: DateTime
        }

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private StatusFileRow =
        {
            RelativePath: string
            DirectoryVersionId: DirectoryVersionId
            Sha256Hash: Sha256Hash
            Blake3Hash: Blake3Hash
            IsBinary: bool
            SizeBytes: int64
            CreatedAt: Instant
            UploadedToObjectStorage: bool
            LastWriteTimeUtc: DateTime
        }

    /// Reads status snapshot data needed by the CLI workflow.
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
                            RootDirectoryBlake3Hash = Blake3Hash String.Empty
                            LastSuccessfulFileUpload = defaultStatus.LastSuccessfulFileUpload
                            LastSuccessfulDirectoryVersionUpload = defaultStatus.LastSuccessfulDirectoryVersionUpload
                        }

                let directories = List<StatusDirectoryRow>()
                let files = List<StatusFileRow>()

                use directoryCommand = connection.CreateCommand()

                directoryCommand.CommandText <-
                    "SELECT relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks FROM status_directories;"

                use directoryReader = directoryCommand.ExecuteReader()

                while directoryReader.Read() do
                    let relativePath = directoryReader.GetString(0)
                    let parentPath = directoryReader.GetString(1)
                    let directoryVersionId = Guid.Parse(directoryReader.GetString(2))
                    let sha256Hash = directoryReader.GetString(3)
                    let blake3Hash = directoryReader.GetString(4)
                    let sizeBytes = directoryReader.GetInt64(5)
                    let createdAt = Instant.FromUnixTimeTicks(directoryReader.GetInt64(6))
                    let lastWriteTimeUtc = DateTime(directoryReader.GetInt64(7), DateTimeKind.Utc)

                    directories.Add(
                        {
                            RelativePath = relativePath
                            ParentPath = parentPath
                            DirectoryVersionId = directoryVersionId
                            Sha256Hash = sha256Hash
                            Blake3Hash = blake3Hash
                            SizeBytes = sizeBytes
                            CreatedAt = createdAt
                            LastWriteTimeUtc = lastWriteTimeUtc
                        }
                    )

                use fileCommand = connection.CreateCommand()

                fileCommand.CommandText <-
                    "SELECT relative_path, directory_version_id, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks FROM status_files;"

                use fileReader = fileCommand.ExecuteReader()

                while fileReader.Read() do
                    let relativePath = fileReader.GetString(0)
                    let directoryVersionId = Guid.Parse(fileReader.GetString(1))
                    let sha256Hash = fileReader.GetString(2)
                    let blake3Hash = fileReader.GetString(3)
                    let isBinary = fileReader.GetInt64(4) = 1L
                    let sizeBytes = fileReader.GetInt64(5)
                    let createdAt = Instant.FromUnixTimeTicks(fileReader.GetInt64(6))
                    let uploaded = fileReader.GetInt64(7) = 1L
                    let lastWriteTimeUtc = DateTime(fileReader.GetInt64(8), DateTimeKind.Utc)

                    files.Add(
                        {
                            RelativePath = relativePath
                            DirectoryVersionId = directoryVersionId
                            Sha256Hash = sha256Hash
                            Blake3Hash = blake3Hash
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
                        LocalFileVersion.CreateWithHashes
                            file.RelativePath
                            file.Sha256Hash
                            file.Blake3Hash
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
                        LocalDirectoryVersion.CreateWithHashes
                            directory.DirectoryVersionId
                            (Current().OwnerId)
                            (Current().OrganizationId)
                            (Current().RepositoryId)
                            directory.RelativePath
                            directory.Sha256Hash
                            directory.Blake3Hash
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
                        RootDirectoryBlake3Hash = meta.RootDirectoryBlake3Hash
                        LastSuccessfulFileUpload = meta.LastSuccessfulFileUpload
                        LastSuccessfulDirectoryVersionUpload = meta.LastSuccessfulDirectoryVersionUpload
                    }
            finally
                connection.Dispose()
        }

    /// Reads status snapshot read only data needed by the CLI workflow.
    let readStatusSnapshotReadOnly (dbPath: string) (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryId: RepositoryId) =
        task {
            let normalizedPath = Path.GetFullPath(dbPath)
            let directoryPath = Path.GetDirectoryName(normalizedPath)

            if
                not (String.IsNullOrWhiteSpace(directoryPath))
                && not (Directory.Exists(directoryPath))
            then
                return Error $"Local state directory was not found for {normalizedPath}."
            elif Directory.Exists(normalizedPath) then
                return Error $"Local state database path is a directory: {normalizedPath}."
            elif not (File.Exists(normalizedPath)) then
                return Error $"Local state database was not found at {normalizedPath}."
            else
                let missingPartialWalSidecars = missingPartialWalSidecars normalizedPath

                if missingPartialWalSidecars.Length > 0 then
                    let missingNames = String.concat ", " missingPartialWalSidecars

                    return
                        Error
                            $"Database has an incomplete WAL sidecar set; missing: {missingNames}. Doctor did not open the database to avoid creating sidecar files or ignoring live WAL content."
                else
                    try
                        let immutableSnapshot = shouldUseImmutableReadOnlySnapshot normalizedPath
                        use connection = openReadOnlyConnection normalizedPath immutableSnapshot
                        let schemaVersion = readSchemaVersionReadOnly connection

                        let missingBlake3Columns =
                            [|
                                if not (columnExists connection "status_meta" "root_directory_blake3_hash") then
                                    "status_meta.root_directory_blake3_hash"

                                if not (columnExists connection "status_directories" "blake3_hash") then
                                    "status_directories.blake3_hash"

                                if not (columnExists connection "status_files" "blake3_hash") then
                                    "status_files.blake3_hash"

                                if not (columnExists connection "object_cache_directories" "blake3_hash") then
                                    "object_cache_directories.blake3_hash"

                                if not (columnExists connection "object_cache_directory_files" "blake3_hash") then
                                    "object_cache_directory_files.blake3_hash"
                            |]

                        if schemaVersion <> Some SchemaVersion then
                            let foundSchemaVersion = defaultArg schemaVersion "<missing>"

                            return
                                Error
                                    $"Local state database schema version is incompatible with this Grace CLI. Expected {SchemaVersion}, found {foundSchemaVersion}. Run a normal Grace command to reset the local state database, or move the local state database aside and retry."
                        elif missingBlake3Columns.Length > 0 then
                            let missingColumns = String.concat ", " missingBlake3Columns

                            return
                                Error
                                    $"Local state database is missing required BLAKE3 columns: {missingColumns}. Run a normal Grace command to reset the local state database, or move the local state database aside and retry."
                        else
                            match readStatusMetaInternal connection with
                            | None -> return Error "Local state status_meta row is missing or unreadable."
                            | Some meta ->
                                let directories = List<StatusDirectoryRow>()
                                let files = List<StatusFileRow>()

                                use directoryCommand = connection.CreateCommand()

                                directoryCommand.CommandText <-
                                    "SELECT relative_path, parent_path, directory_version_id, sha256_hash, blake3_hash, size_bytes, created_at_unix_ticks, last_write_time_utc_ticks FROM status_directories;"

                                use directoryReader = directoryCommand.ExecuteReader()

                                while directoryReader.Read() do
                                    directories.Add(
                                        {
                                            RelativePath = directoryReader.GetString(0)
                                            ParentPath = directoryReader.GetString(1)
                                            DirectoryVersionId = Guid.Parse(directoryReader.GetString(2))
                                            Sha256Hash = directoryReader.GetString(3)
                                            Blake3Hash = directoryReader.GetString(4)
                                            SizeBytes = directoryReader.GetInt64(5)
                                            CreatedAt = Instant.FromUnixTimeTicks(directoryReader.GetInt64(6))
                                            LastWriteTimeUtc = DateTime(directoryReader.GetInt64(7), DateTimeKind.Utc)
                                        }
                                    )

                                use fileCommand = connection.CreateCommand()

                                fileCommand.CommandText <-
                                    "SELECT relative_path, directory_version_id, sha256_hash, blake3_hash, is_binary, size_bytes, created_at_unix_ticks, uploaded_to_object_storage, last_write_time_utc_ticks FROM status_files;"

                                use fileReader = fileCommand.ExecuteReader()

                                while fileReader.Read() do
                                    files.Add(
                                        {
                                            RelativePath = fileReader.GetString(0)
                                            DirectoryVersionId = Guid.Parse(fileReader.GetString(1))
                                            Sha256Hash = fileReader.GetString(2)
                                            Blake3Hash = fileReader.GetString(3)
                                            IsBinary = fileReader.GetInt64(4) = 1L
                                            SizeBytes = fileReader.GetInt64(5)
                                            CreatedAt = Instant.FromUnixTimeTicks(fileReader.GetInt64(6))
                                            UploadedToObjectStorage = fileReader.GetInt64(7) = 1L
                                            LastWriteTimeUtc = DateTime(fileReader.GetInt64(8), DateTimeKind.Utc)
                                        }
                                    )

                                let emptyBlake3Rows =
                                    seq {
                                        yield!
                                            directories
                                            |> Seq.filter (fun directory -> String.IsNullOrWhiteSpace(string directory.Blake3Hash))
                                            |> Seq.map (fun directory -> $"directory:{directory.RelativePath}")

                                        yield!
                                            files
                                            |> Seq.filter (fun file -> String.IsNullOrWhiteSpace(string file.Blake3Hash))
                                            |> Seq.map (fun file -> $"file:{file.RelativePath}")
                                    }
                                    |> Seq.toArray

                                if emptyBlake3Rows.Length > 0 then
                                    let rows = String.concat ", " emptyBlake3Rows

                                    return
                                        Error
                                            $"Local state database contains empty BLAKE3 values in status rows: {rows}. Run a normal Grace command to reset the local state database, or move the local state database aside and retry."
                                else
                                    let directoriesByParent = Dictionary<string, List<DirectoryVersionId>>()
                                    let filesByDirectory = Dictionary<DirectoryVersionId, List<LocalFileVersion>>()

                                    directories
                                    |> Seq.iter (fun directory ->
                                        let mutable existing = Unchecked.defaultof<List<DirectoryVersionId>>

                                        if directoriesByParent.TryGetValue(directory.ParentPath, &existing) then
                                            existing.Add(directory.DirectoryVersionId)
                                        else
                                            directoriesByParent.Add(directory.ParentPath, List<DirectoryVersionId>([ directory.DirectoryVersionId ])))

                                    files
                                    |> Seq.iter (fun file ->
                                        let localFile =
                                            LocalFileVersion.CreateWithHashes
                                                file.RelativePath
                                                file.Sha256Hash
                                                file.Blake3Hash
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
                                            LocalDirectoryVersion.CreateWithHashes
                                                directory.DirectoryVersionId
                                                ownerId
                                                organizationId
                                                repositoryId
                                                directory.RelativePath
                                                directory.Sha256Hash
                                                directory.Blake3Hash
                                                directoriesForPath
                                                filesForPath
                                                directory.SizeBytes
                                                directory.LastWriteTimeUtc

                                        index.TryAdd(directory.DirectoryVersionId, localDirectory)
                                        |> ignore)

                                    return
                                        Ok
                                            {
                                                Index = index
                                                RootDirectoryId = meta.RootDirectoryId
                                                RootDirectorySha256Hash = meta.RootDirectorySha256Hash
                                                RootDirectoryBlake3Hash = meta.RootDirectoryBlake3Hash
                                                LastSuccessfulFileUpload = meta.LastSuccessfulFileUpload
                                                LastSuccessfulDirectoryVersionUpload = meta.LastSuccessfulDirectoryVersionUpload
                                            }
                    with
                    | ex -> return Error ex.Message
        }
