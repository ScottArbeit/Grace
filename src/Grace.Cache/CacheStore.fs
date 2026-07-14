namespace Grace.Cache

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open SQLitePCL

/// Identifies immutable promoted content and the root whose metadata will retain it.
type CacheArtifactDescriptor = { ArtifactId: string; Digest: string; UncompressedSize: int64; RootDirectoryVersionId: string; FillToken: string }

/// Describes one canonical directory version and its recursive relationships in a cache ingest.
type CacheDirectoryMetadata = { DirectoryVersionId: string; ChildDirectoryVersionIds: string list; ArtifactIds: string list }

/// Carries the complete root-reachable directory graph required for one promoted artifact publication.
type CacheRecursiveMetadata = { Directories: CacheDirectoryMetadata list }

/// Reports the durable SQLite invariants established for a cache store connection.
type CacheStoreDiagnostics = { SchemaVersion: int; JournalMode: string; ForeignKeysEnabled: bool; BusyTimeoutMilliseconds: int }

/// Represents the private cache database handle used only by the cache runtime's narrow store API.
type CacheStore = private { DatabasePath: string; Diagnostics: CacheStoreDiagnostics }

/// Identifies a pending artifact that restart recovery removed and the filesystem owner must clean up.
type CacheRecoveredIncompleteIngest = { ArtifactId: string; RootDirectoryVersionId: string; Digest: string; UncompressedSize: int64; FillToken: string }

/// Returns a store after startup recovery has removed every incomplete ingest from its SQLite boundary.
type CacheStoreOpenResult = { Store: CacheStore; RecoveredIncomplete: CacheRecoveredIncompleteIngest array }

/// Distinguishes a new pending ingest from an idempotent valid or rejected source descriptor.
type CacheIngestBeginResult =
    | Pending
    | AlreadyValid
    | Rejected of reason: string

/// Distinguishes atomic publication from an idempotent repeat or a rejected promotion/metadata attempt.
type CacheIngestCommitResult =
    | Published
    | AlreadyPublished
    | Rejected of reason: string

/// Exposes only valid, fully committed artifacts to later cache read-through callers.
type CacheValidArtifact = { ArtifactId: string; Digest: string; UncompressedSize: int64; RootDirectoryVersionId: string }

/// Owns the schema-versioned local SQLite store for cache metadata and immutable artifact validity.
module CacheStore =

    [<Literal>]
    let private SchemaVersion = 1

    [<Literal>]
    let private BusyTimeoutMilliseconds = 5000

    let private sqliteInitialized =
        lazy
            (Batteries_V2.Init()
             true)

    let private initializationLocks = ConcurrentDictionary<string, obj>(StringComparer.OrdinalIgnoreCase)

    let private schemaTables =
        [|
            "cache_schema"
            "directory_versions"
            "artifacts"
            "pending_ingests"
            "root_directory_memberships"
            "root_artifact_memberships"
            "directory_child_memberships"
            "directory_artifact_memberships"
        |]

    let private schemaStatements =
        [|
            "CREATE TABLE IF NOT EXISTS cache_schema (singleton INTEGER PRIMARY KEY CHECK (singleton = 1), schema_version INTEGER NOT NULL CHECK (schema_version > 0));"
            "CREATE TABLE IF NOT EXISTS directory_versions (directory_version_id TEXT PRIMARY KEY NOT NULL);"
            "CREATE TABLE IF NOT EXISTS artifacts (artifact_id TEXT PRIMARY KEY NOT NULL, digest TEXT NOT NULL, uncompressed_size INTEGER NOT NULL CHECK (uncompressed_size >= 0), lifecycle_state TEXT NOT NULL CHECK (lifecycle_state IN ('Valid')));"
            "CREATE TABLE IF NOT EXISTS pending_ingests (artifact_id TEXT NOT NULL, digest TEXT NOT NULL, uncompressed_size INTEGER NOT NULL CHECK (uncompressed_size >= 0), root_directory_version_id TEXT NOT NULL, fill_token TEXT NOT NULL, PRIMARY KEY (artifact_id, root_directory_version_id));"
            "CREATE TABLE IF NOT EXISTS root_directory_memberships (root_directory_version_id TEXT NOT NULL, directory_version_id TEXT NOT NULL, PRIMARY KEY (root_directory_version_id, directory_version_id), FOREIGN KEY (root_directory_version_id) REFERENCES directory_versions(directory_version_id) ON DELETE RESTRICT, FOREIGN KEY (directory_version_id) REFERENCES directory_versions(directory_version_id) ON DELETE RESTRICT);"
            "CREATE TABLE IF NOT EXISTS root_artifact_memberships (root_directory_version_id TEXT NOT NULL, artifact_id TEXT NOT NULL, PRIMARY KEY (root_directory_version_id, artifact_id), FOREIGN KEY (root_directory_version_id) REFERENCES directory_versions(directory_version_id) ON DELETE RESTRICT, FOREIGN KEY (artifact_id) REFERENCES artifacts(artifact_id) ON DELETE RESTRICT);"
            "CREATE TABLE IF NOT EXISTS directory_child_memberships (directory_version_id TEXT NOT NULL, child_directory_version_id TEXT NOT NULL, PRIMARY KEY (directory_version_id, child_directory_version_id), FOREIGN KEY (directory_version_id) REFERENCES directory_versions(directory_version_id) ON DELETE RESTRICT, FOREIGN KEY (child_directory_version_id) REFERENCES directory_versions(directory_version_id) ON DELETE RESTRICT);"
            "CREATE TABLE IF NOT EXISTS directory_artifact_memberships (directory_version_id TEXT NOT NULL, artifact_id TEXT NOT NULL, PRIMARY KEY (directory_version_id, artifact_id), FOREIGN KEY (directory_version_id) REFERENCES directory_versions(directory_version_id) ON DELETE RESTRICT, FOREIGN KEY (artifact_id) REFERENCES artifacts(artifact_id) ON DELETE RESTRICT);"
        |]

    /// Creates a parameterized command for private fixed-schema SQLite operations.
    let private createCommand
        (connection: SqliteConnection)
        (transaction: SqliteTransaction option)
        (sql: string)
        (configure: SqliteParameterCollection -> unit)
        =
        let command = connection.CreateCommand()
        command.CommandText <- sql

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        configure command.Parameters
        command

    /// Executes a private fixed-schema command and disposes it before the surrounding transaction advances.
    let private executeNonQuery connection transaction sql configure =
        use command = createCommand connection transaction sql configure
        command.ExecuteNonQuery() |> ignore

    /// Reads one scalar string from a private fixed-schema SQLite query.
    let private executeScalarString connection transaction sql configure =
        use command = createCommand connection transaction sql configure
        let value = command.ExecuteScalar()

        if isNull value || value = Convert.DBNull then
            None
        else
            Some(Convert.ToString(value, CultureInfo.InvariantCulture))

    /// Reads one scalar integer from a private fixed-schema SQLite query.
    let private executeScalarInt connection transaction sql configure =
        executeScalarString connection transaction sql configure
        |> Option.map (fun value -> Int32.Parse(value, CultureInfo.InvariantCulture))

    /// Opens a cache-local connection and verifies every required SQLite setting instead of inheriting process defaults.
    let private openConnection (databasePath: string) =
        sqliteInitialized.Value |> ignore
        let directory = Path.GetDirectoryName(databasePath)

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory(directory) |> ignore

        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- databasePath
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.Pooling <- true
        builder.DefaultTimeout <- BusyTimeoutMilliseconds / 1000

        let connection = new SqliteConnection(builder.ToString())

        try
            connection.Open()
            executeNonQuery connection None $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};" ignore
            executeNonQuery connection None "PRAGMA foreign_keys = ON;" ignore

            let journalMode = executeScalarString connection None "PRAGMA journal_mode = WAL;" ignore
            let foreignKeysEnabled = executeScalarInt connection None "PRAGMA foreign_keys;" ignore
            let busyTimeout = executeScalarInt connection None "PRAGMA busy_timeout;" ignore

            if journalMode <> Some "wal" then
                invalidOp "Cache SQLite connection did not establish WAL journal mode."

            if foreignKeysEnabled <> Some 1 then
                invalidOp "Cache SQLite connection did not enable foreign keys."

            if busyTimeout <> Some BusyTimeoutMilliseconds then
                invalidOp "Cache SQLite connection did not establish the declared busy timeout."

            connection
        with
        | error ->
            connection.Dispose()
            raise error

    /// Detects a named table without querying missing schema objects.
    let private tableExists connection tableName =
        let count =
            executeScalarInt connection None "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;" (fun parameters ->
                parameters.AddWithValue("@name", tableName)
                |> ignore)

        count = Some 1

    /// Counts known cache tables before schema creation so a missing version marker cannot be treated as a new database.
    let private countExistingCacheTables connection =
        schemaTables
        |> Array.sumBy (fun tableName -> if tableExists connection tableName then 1 else 0)

    /// Ensures only the deterministic cache schema version is accepted by this store release.
    let private ensureSchema connection =
        let schemaExists = tableExists connection "cache_schema"
        let existingCacheTables = countExistingCacheTables connection

        if not schemaExists && existingCacheTables <> 0 then
            invalidOp "Cache SQLite database has cache tables without the required schema version marker."

        if schemaExists
           && existingCacheTables <> schemaTables.Length then
            invalidOp "Cache SQLite database is missing a required table for its declared schema."

        use transaction = connection.BeginTransaction()

        for statement in schemaStatements do
            executeNonQuery connection (Some transaction) statement ignore

        if not schemaExists then
            executeNonQuery connection (Some transaction) "INSERT INTO cache_schema (singleton, schema_version) VALUES (1, @schemaVersion);" (fun parameters ->
                parameters.AddWithValue("@schemaVersion", SchemaVersion)
                |> ignore)

        let versions =
            use command = createCommand connection (Some transaction) "SELECT schema_version FROM cache_schema ORDER BY singleton;" ignore
            use reader = command.ExecuteReader()
            let values = List<int>()

            while reader.Read() do
                values.Add(reader.GetInt32(0))

            values

        if versions.Count <> 1
           || versions[0] <> SchemaVersion then
            invalidOp "Cache SQLite database has an unsupported schema version."

        transaction.Commit()

    /// Fails closed when SQLite detects relational or page-level corruption before recovery or valid reads proceed.
    let private verifyIntegrity connection =
        let integrity = executeScalarString connection None "PRAGMA integrity_check;" ignore

        if integrity <> Some "ok" then invalidOp "Cache SQLite integrity check failed."

        use command = createCommand connection None "PRAGMA foreign_key_check;" ignore
        use reader = command.ExecuteReader()

        if reader.Read() then
            invalidOp "Cache SQLite foreign-key integrity check failed."

    /// Reads a pending or valid descriptor without exposing SQL tables outside the store module.
    let private tryReadDescriptor connection transaction tableName artifactId rootDirectoryVersionId : CacheArtifactDescriptor option =
        let sql =
            if tableName = "pending_ingests" then
                "SELECT artifact_id, digest, uncompressed_size, root_directory_version_id, fill_token FROM pending_ingests WHERE artifact_id = @artifactId AND root_directory_version_id = @rootDirectoryVersionId;"
            else
                "SELECT artifact_id, digest, uncompressed_size, NULL, NULL FROM artifacts WHERE artifact_id = @artifactId;"

        use command =
            createCommand connection transaction sql (fun parameters ->
                parameters.AddWithValue("@artifactId", artifactId)
                |> ignore

                if tableName = "pending_ingests" then
                    parameters.AddWithValue("@rootDirectoryVersionId", rootDirectoryVersionId)
                    |> ignore)

        use reader = command.ExecuteReader()

        if reader.Read() then
            Some
                {
                    ArtifactId = reader.GetString(0)
                    Digest = reader.GetString(1)
                    UncompressedSize = reader.GetInt64(2)
                    RootDirectoryVersionId = if reader.IsDBNull(3) then "" else reader.GetString(3)
                    FillToken = if reader.IsDBNull(4) then "" else reader.GetString(4)
                }
        else
            None

    /// Compares the complete stale-sensitive identity tuple used for pending and valid publication decisions.
    let private descriptorMatches (expected: CacheArtifactDescriptor) (actual: CacheArtifactDescriptor) =
        expected.ArtifactId = actual.ArtifactId
        && expected.Digest = actual.Digest
        && expected.UncompressedSize = actual.UncompressedSize
        && (String.IsNullOrEmpty(actual.RootDirectoryVersionId)
            || expected.RootDirectoryVersionId = actual.RootDirectoryVersionId)
        && (String.IsNullOrEmpty(actual.FillToken)
            || expected.FillToken = actual.FillToken)

    /// Detects whether an already-valid canonical artifact is retained under the descriptor's requested root.
    let private isArtifactRetainedByRoot connection transaction artifactId rootDirectoryVersionId =
        let count =
            executeScalarInt
                connection
                transaction
                "SELECT COUNT(*) FROM root_artifact_memberships WHERE root_directory_version_id = @rootDirectoryVersionId AND artifact_id = @artifactId;"
                (fun parameters ->
                    parameters.AddWithValue("@rootDirectoryVersionId", rootDirectoryVersionId)
                    |> ignore

                    parameters.AddWithValue("@artifactId", artifactId)
                    |> ignore)

        count |> Option.defaultValue 0 > 0

    /// Validates source identity before it can become either pending state or a valid artifact row.
    let private validateDescriptor (descriptor: CacheArtifactDescriptor) =
        let hasSha256Shape (value: string) =
            value.Length = 64
            && value
               |> Seq.forall (fun character -> Uri.IsHexDigit(character))

        if String.IsNullOrWhiteSpace descriptor.ArtifactId then
            Error "Artifact id is required."
        elif not (hasSha256Shape descriptor.Digest) then
            Error "Artifact digest must be a 64-character hexadecimal SHA-256 value."
        elif descriptor.UncompressedSize < 0L then
            Error "Artifact uncompressed size cannot be negative."
        elif String.IsNullOrWhiteSpace descriptor.RootDirectoryVersionId then
            Error "Root directory version id is required."
        elif String.IsNullOrWhiteSpace descriptor.FillToken then
            Error "Fill token is required."
        else
            Ok()

    /// Validates that recursive metadata is non-empty, canonical, root-reachable, and tied only to the promoted artifact.
    let private validateMetadata (descriptor: CacheArtifactDescriptor) (metadata: CacheRecursiveMetadata) =
        if List.isEmpty metadata.Directories then
            Error "Recursive metadata must contain the promoted root directory."
        else
            let directories = Dictionary<string, CacheDirectoryMetadata>(StringComparer.Ordinal)
            let mutable error = None

            for directory in metadata.Directories do
                if String.IsNullOrWhiteSpace directory.DirectoryVersionId then
                    error <- Some "Directory version ids are required."
                elif directories.ContainsKey(directory.DirectoryVersionId) then
                    error <- Some "Recursive metadata cannot repeat a directory version id."
                else
                    directories.Add(directory.DirectoryVersionId, directory)

            if error.IsSome then
                Error error.Value
            elif not (directories.ContainsKey(descriptor.RootDirectoryVersionId)) then
                Error "Recursive metadata must contain the descriptor root directory version id."
            else
                let mutable hasPromotedArtifact = false

                for directory in metadata.Directories do
                    for childId in directory.ChildDirectoryVersionIds do
                        if
                            String.IsNullOrWhiteSpace childId
                            || not (directories.ContainsKey(childId))
                        then
                            error <- Some "Every child directory membership must reference supplied recursive metadata."
                        elif childId = directory.DirectoryVersionId then
                            error <- Some "A directory cannot be its own child."

                    for artifactId in directory.ArtifactIds do
                        if artifactId <> descriptor.ArtifactId then
                            error <- Some "Recursive metadata cannot reference an artifact that was not confirmed for this ingest."
                        else
                            hasPromotedArtifact <- true

                if error.IsSome then
                    Error error.Value
                elif not hasPromotedArtifact then
                    Error "Recursive metadata must retain the confirmed promoted artifact."
                else
                    let reachable = HashSet<string>(StringComparer.Ordinal)
                    let pending = Stack<string>()
                    pending.Push(descriptor.RootDirectoryVersionId)

                    while pending.Count > 0 do
                        let directoryId = pending.Pop()

                        if reachable.Add(directoryId) then
                            for childId in directories[directoryId].ChildDirectoryVersionIds do
                                pending.Push(childId)

                    if reachable.Count <> directories.Count then
                        Error "Recursive metadata cannot contain directories unreachable from the descriptor root."
                    else
                        Ok()

    /// Executes a write operation again only when SQLite reports a bounded busy or locked condition.
    let private withBusyRetry operation =
        let rec execute attempt =
            try
                operation ()
            with
            | :? SqliteException as error when
                (error.SqliteErrorCode = 5
                 || error.SqliteErrorCode = 6)
                && attempt < 6
                ->
                Thread.Sleep(25 * (attempt + 1))
                execute (attempt + 1)

        execute 0

    /// Inserts a canonical row or retains the prior identical canonical row without exposing an upsert API.
    let private insertDirectory connection transaction directoryId =
        executeNonQuery
            connection
            transaction
            "INSERT INTO directory_versions (directory_version_id) VALUES (@directoryId) ON CONFLICT(directory_version_id) DO NOTHING;"
            (fun parameters ->
                parameters.AddWithValue("@directoryId", directoryId)
                |> ignore)

    /// Persists every verified recursive relation inside the publication transaction.
    let private insertMetadata connection transaction (descriptor: CacheArtifactDescriptor) (metadata: CacheRecursiveMetadata) =
        insertDirectory connection transaction descriptor.RootDirectoryVersionId

        for directory in metadata.Directories do
            insertDirectory connection transaction directory.DirectoryVersionId

        executeNonQuery
            connection
            transaction
            "INSERT INTO artifacts (artifact_id, digest, uncompressed_size, lifecycle_state) VALUES (@artifactId, @digest, @uncompressedSize, 'Valid') ON CONFLICT(artifact_id) DO NOTHING;"
            (fun parameters ->
                parameters.AddWithValue("@artifactId", descriptor.ArtifactId)
                |> ignore

                parameters.AddWithValue("@digest", descriptor.Digest)
                |> ignore

                parameters.AddWithValue("@uncompressedSize", descriptor.UncompressedSize)
                |> ignore)

        executeNonQuery
            connection
            transaction
            "INSERT INTO root_artifact_memberships (root_directory_version_id, artifact_id) VALUES (@rootDirectoryVersionId, @artifactId) ON CONFLICT(root_directory_version_id, artifact_id) DO NOTHING;"
            (fun parameters ->
                parameters.AddWithValue("@rootDirectoryVersionId", descriptor.RootDirectoryVersionId)
                |> ignore

                parameters.AddWithValue("@artifactId", descriptor.ArtifactId)
                |> ignore)

        for directory in metadata.Directories do
            executeNonQuery
                connection
                transaction
                "INSERT INTO root_directory_memberships (root_directory_version_id, directory_version_id) VALUES (@rootDirectoryVersionId, @directoryVersionId) ON CONFLICT(root_directory_version_id, directory_version_id) DO NOTHING;"
                (fun parameters ->
                    parameters.AddWithValue("@rootDirectoryVersionId", descriptor.RootDirectoryVersionId)
                    |> ignore

                    parameters.AddWithValue("@directoryVersionId", directory.DirectoryVersionId)
                    |> ignore)

            for childId in directory.ChildDirectoryVersionIds do
                executeNonQuery
                    connection
                    transaction
                    "INSERT INTO directory_child_memberships (directory_version_id, child_directory_version_id) VALUES (@directoryVersionId, @childDirectoryVersionId) ON CONFLICT(directory_version_id, child_directory_version_id) DO NOTHING;"
                    (fun parameters ->
                        parameters.AddWithValue("@directoryVersionId", directory.DirectoryVersionId)
                        |> ignore

                        parameters.AddWithValue("@childDirectoryVersionId", childId)
                        |> ignore)

            for artifactId in directory.ArtifactIds do
                executeNonQuery
                    connection
                    transaction
                    "INSERT INTO directory_artifact_memberships (directory_version_id, artifact_id) VALUES (@directoryVersionId, @artifactId) ON CONFLICT(directory_version_id, artifact_id) DO NOTHING;"
                    (fun parameters ->
                        parameters.AddWithValue("@directoryVersionId", directory.DirectoryVersionId)
                        |> ignore

                        parameters.AddWithValue("@artifactId", artifactId)
                        |> ignore)

    /// Reads and deletes all pending rows atomically so restart cannot elevate incomplete state to valid.
    let private recoverIncompleteIngests (connection: SqliteConnection) =
        use transaction = connection.BeginTransaction()

        use command =
            createCommand
                connection
                (Some transaction)
                "SELECT artifact_id, root_directory_version_id, digest, uncompressed_size, fill_token FROM pending_ingests ORDER BY artifact_id;"
                ignore

        use reader = command.ExecuteReader()
        let recovered = List<CacheRecoveredIncompleteIngest>()

        while reader.Read() do
            recovered.Add(
                {
                    ArtifactId = reader.GetString(0)
                    RootDirectoryVersionId = reader.GetString(1)
                    Digest = reader.GetString(2)
                    UncompressedSize = reader.GetInt64(3)
                    FillToken = reader.GetString(4)
                }
            )

        reader.Close()
        executeNonQuery connection (Some transaction) "DELETE FROM pending_ingests;" ignore
        transaction.Commit()
        recovered.ToArray()

    /// Creates or verifies the cache schema, checks integrity, and recovers incomplete transactions before exposing a store.
    let openStore databasePath =
        if String.IsNullOrWhiteSpace databasePath then
            invalidArg (nameof databasePath) "Cache database path is required."

        let fullPath = Path.GetFullPath(databasePath)
        let initializationLock = initializationLocks.GetOrAdd(fullPath, (fun _ -> obj ()))

        lock initializationLock (fun () ->
            use connection = openConnection fullPath
            ensureSchema connection
            verifyIntegrity connection
            let recovered = recoverIncompleteIngests connection
            verifyIntegrity connection

            {
                Store =
                    {
                        DatabasePath = fullPath
                        Diagnostics =
                            { SchemaVersion = SchemaVersion; JournalMode = "wal"; ForeignKeysEnabled = true; BusyTimeoutMilliseconds = BusyTimeoutMilliseconds }
                    }
                RecoveredIncomplete = recovered
            })

    /// Returns the verified connection settings captured while the store was opened.
    let getDiagnostics (store: CacheStore) = store.Diagnostics

    /// Records a valid source descriptor as pending while rejecting malformed or stale identities before persistence.
    let beginIngest (store: CacheStore) (descriptor: CacheArtifactDescriptor) : CacheIngestBeginResult =
        match validateDescriptor descriptor with
        | Error reason -> CacheIngestBeginResult.Rejected reason
        | Ok () ->
            withBusyRetry (fun () ->
                use connection = openConnection store.DatabasePath
                use transaction = connection.BeginTransaction()

                match tryReadDescriptor connection (Some transaction) "artifacts" descriptor.ArtifactId "" with
                | Some valid when
                    descriptorMatches descriptor valid
                    && isArtifactRetainedByRoot connection (Some transaction) descriptor.ArtifactId descriptor.RootDirectoryVersionId
                    ->
                    transaction.Commit()
                    CacheIngestBeginResult.AlreadyValid
                | Some _ ->
                    match tryReadDescriptor connection (Some transaction) "artifacts" descriptor.ArtifactId "" with
                    | Some valid when descriptorMatches descriptor valid ->
                        match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                        | Some pending when descriptorMatches descriptor pending ->
                            transaction.Commit()
                            CacheIngestBeginResult.Pending
                        | Some _ ->
                            transaction.Rollback()
                            CacheIngestBeginResult.Rejected "Artifact/root pair already has a pending ingest with a different stale-sensitive identity."
                        | None ->
                            executeNonQuery
                                connection
                                (Some transaction)
                                "INSERT INTO pending_ingests (artifact_id, digest, uncompressed_size, root_directory_version_id, fill_token) VALUES (@artifactId, @digest, @uncompressedSize, @rootDirectoryVersionId, @fillToken);"
                                (fun parameters ->
                                    parameters.AddWithValue("@artifactId", descriptor.ArtifactId)
                                    |> ignore

                                    parameters.AddWithValue("@digest", descriptor.Digest)
                                    |> ignore

                                    parameters.AddWithValue("@uncompressedSize", descriptor.UncompressedSize)
                                    |> ignore

                                    parameters.AddWithValue("@rootDirectoryVersionId", descriptor.RootDirectoryVersionId)
                                    |> ignore

                                    parameters.AddWithValue("@fillToken", descriptor.FillToken)
                                    |> ignore)

                            transaction.Commit()
                            CacheIngestBeginResult.Pending
                    | Some _ ->
                        transaction.Rollback()
                        CacheIngestBeginResult.Rejected "Artifact id already belongs to a different valid immutable descriptor."
                    | None ->
                        transaction.Rollback()
                        CacheIngestBeginResult.Rejected "Artifact state changed before the root-specific pending ingest could be recorded."
                | None ->
                    match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                    | Some pending when descriptorMatches descriptor pending ->
                        transaction.Commit()
                        CacheIngestBeginResult.Pending
                    | Some _ ->
                        transaction.Rollback()
                        CacheIngestBeginResult.Rejected "Artifact id already has a pending ingest with a different stale-sensitive identity."
                    | None ->
                        executeNonQuery
                            connection
                            (Some transaction)
                            "INSERT INTO pending_ingests (artifact_id, digest, uncompressed_size, root_directory_version_id, fill_token) VALUES (@artifactId, @digest, @uncompressedSize, @rootDirectoryVersionId, @fillToken);"
                            (fun parameters ->
                                parameters.AddWithValue("@artifactId", descriptor.ArtifactId)
                                |> ignore

                                parameters.AddWithValue("@digest", descriptor.Digest)
                                |> ignore

                                parameters.AddWithValue("@uncompressedSize", descriptor.UncompressedSize)
                                |> ignore

                                parameters.AddWithValue("@rootDirectoryVersionId", descriptor.RootDirectoryVersionId)
                                |> ignore

                                parameters.AddWithValue("@fillToken", descriptor.FillToken)
                                |> ignore)

                        transaction.Commit()
                        CacheIngestBeginResult.Pending)

    /// Atomically publishes one confirmed artifact and its complete recursive metadata only while the original pending identity remains current.
    let commitPromotedIngest (store: CacheStore) (descriptor: CacheArtifactDescriptor) (metadata: CacheRecursiveMetadata) : CacheIngestCommitResult =
        match validateDescriptor descriptor, validateMetadata descriptor metadata with
        | Error reason, _
        | _, Error reason -> CacheIngestCommitResult.Rejected reason
        | Ok (), Ok () ->
            withBusyRetry (fun () ->
                use connection = openConnection store.DatabasePath
                use transaction = connection.BeginTransaction()

                match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                | None ->
                    match tryReadDescriptor connection (Some transaction) "artifacts" descriptor.ArtifactId "" with
                    | Some valid when descriptorMatches descriptor valid ->
                        transaction.Commit()
                        CacheIngestCommitResult.AlreadyPublished
                    | _ ->
                        transaction.Rollback()
                        CacheIngestCommitResult.Rejected "No current pending ingest exists for the promoted artifact."
                | Some pending when not (descriptorMatches descriptor pending) ->
                    transaction.Rollback()
                    CacheIngestCommitResult.Rejected "Pending ingest identity changed before promoted publication."
                | Some _ ->
                    insertMetadata connection (Some transaction) descriptor metadata

                    match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                    | Some current when descriptorMatches descriptor current ->
                        executeNonQuery
                            connection
                            (Some transaction)
                            "DELETE FROM pending_ingests WHERE artifact_id = @artifactId AND root_directory_version_id = @rootDirectoryVersionId;"
                            (fun parameters ->
                                parameters.AddWithValue("@artifactId", descriptor.ArtifactId)
                                |> ignore

                                parameters.AddWithValue("@rootDirectoryVersionId", descriptor.RootDirectoryVersionId)
                                |> ignore)

                        transaction.Commit()
                        CacheIngestCommitResult.Published
                    | _ ->
                        transaction.Rollback()
                        CacheIngestCommitResult.Rejected "Pending ingest identity changed before valid publication.")

    /// Lists only artifact rows committed as valid; pending and rejected state never crosses this query boundary.
    let getValidArtifacts (store: CacheStore) : CacheValidArtifact array =
        use connection = openConnection store.DatabasePath

        use command =
            createCommand
                connection
                None
                "SELECT artifacts.artifact_id, artifacts.digest, artifacts.uncompressed_size, roots.root_directory_version_id FROM artifacts INNER JOIN root_artifact_memberships AS roots ON roots.artifact_id = artifacts.artifact_id WHERE artifacts.lifecycle_state = 'Valid' ORDER BY artifacts.artifact_id, roots.root_directory_version_id;"
                ignore

        use reader = command.ExecuteReader()
        let artifacts = List<CacheValidArtifact>()

        while reader.Read() do
            artifacts.Add(
                {
                    ArtifactId = reader.GetString(0)
                    Digest = reader.GetString(1)
                    UncompressedSize = reader.GetInt64(2)
                    RootDirectoryVersionId = reader.GetString(3)
                }
            )

        artifacts.ToArray()
