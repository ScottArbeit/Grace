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

/// Holds one process-owned cache database lock and the reference count for its in-process store leases.
type private CacheStoreShared =
    {
        DatabasePath: string
        Diagnostics: CacheStoreDiagnostics
        OwnershipLock: FileStream
        LifecycleGate: obj
        mutable ReferenceCount: int
    }

/// Represents the private cache database handle used only by the cache runtime's narrow store API.
type CacheStore = private { Shared: CacheStoreShared; mutable Released: int }

/// Identifies a pending artifact that restart recovery removed and the filesystem owner must clean up.
type CacheRecoveredIncompleteIngest = { ArtifactId: string; RootDirectoryVersionId: string; Digest: string; UncompressedSize: int64; FillToken: string }

/// Returns the result of acquiring the cache database's exclusive process ownership.
type CacheStoreOpenResult =
    | Opened of store: CacheStore * recoveredIncomplete: CacheRecoveredIncompleteIngest array
    | CacheDatabaseInUse
    /// Exposes an opened store while making an in-use result fail loudly if a caller skips result handling.
    member this.Store =
        match this with
        | Opened (store, _) -> store
        | CacheDatabaseInUse -> invalidOp "Cache database is already owned by another process."

    /// Exposes restart recovery only for the process that acquired exclusive ownership.
    member this.RecoveredIncomplete =
        match this with
        | Opened (_, recoveredIncomplete) -> recoveredIncomplete
        | CacheDatabaseInUse -> [||]

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

    let private sharedStores = ConcurrentDictionary<string, CacheStoreShared>(StringComparer.OrdinalIgnoreCase)

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

    /// Resolves a cache database path to one registry key so relative and link aliases cannot obtain separate owners.
    let private canonicalizeDatabasePath (databasePath: string) =
        if String.IsNullOrWhiteSpace databasePath then
            invalidArg (nameof databasePath) "Cache database path is required."

        let fullPath = Path.GetFullPath(databasePath)
        let file = FileInfo(fullPath)

        if file.Exists then
            match file.ResolveLinkTarget(true) with
            | null -> file.FullName
            | target -> target.FullName
        else
            let directoryPath = Path.GetDirectoryName(fullPath)
            let directory = DirectoryInfo(directoryPath)

            let resolvedDirectory =
                if directory.Exists then
                    match directory.ResolveLinkTarget(true) with
                    | :? DirectoryInfo as target -> target.FullName
                    | _ -> directory.FullName
                else
                    directory.FullName

            Path.Combine(resolvedDirectory, file.Name)

    /// Acquires the sidecar byte-range lock that proves this process exclusively owns a canonical cache database path.
    let private tryAcquireOwnershipLock (databasePath: string) =
        let directory = Path.GetDirectoryName(databasePath)

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory(directory) |> ignore

        let lockPath = databasePath + ".owner.lock"

        try
            let ownershipLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite ||| FileShare.Delete)

            try
                ownershipLock.Lock(0L, 1L)
                Some ownershipLock
            with
            | :? IOException ->
                ownershipLock.Dispose()
                None
        with
        | :? IOException -> None

    /// Creates a caller lease over a live shared process owner without rerunning startup recovery.
    let private createStoreLease (sharedStore: CacheStoreShared) =
        lock sharedStore.LifecycleGate (fun () ->
            if sharedStore.ReferenceCount <= 0 then
                invalidOp "Cache database ownership ended before a new store lease could be created."

            sharedStore.ReferenceCount <- sharedStore.ReferenceCount + 1
            { Shared = sharedStore; Released = 0 })

    /// Rejects use of a store handle after that caller has released its shared ownership lease.
    let private requireLiveStore (store: CacheStore) =
        if Volatile.Read(&store.Released) <> 0 then
            invalidOp "Cache store lease has already been released."

        store.Shared

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

    /// Compares the immutable artifact identity that must agree across roots even when fill tokens differ.
    let private artifactIdentityMatches (expected: CacheArtifactDescriptor) (actual: CacheArtifactDescriptor) =
        expected.ArtifactId = actual.ArtifactId
        && expected.Digest = actual.Digest
        && expected.UncompressedSize = actual.UncompressedSize

    /// Detects whether another root already holds a pending row for the artifact with a divergent immutable identity.
    let private hasPendingArtifactIdentityConflict connection transaction (descriptor: CacheArtifactDescriptor) =
        use command =
            createCommand
                connection
                transaction
                "SELECT artifact_id, digest, uncompressed_size, root_directory_version_id, fill_token FROM pending_ingests WHERE artifact_id = @artifactId;"
                (fun parameters ->
                    parameters.AddWithValue("@artifactId", descriptor.ArtifactId)
                    |> ignore)

        use reader = command.ExecuteReader()
        let mutable conflict = false

        while reader.Read() do
            let pending: CacheArtifactDescriptor =
                {
                    ArtifactId = reader.GetString(0)
                    Digest = reader.GetString(1)
                    UncompressedSize = reader.GetInt64(2)
                    RootDirectoryVersionId = reader.GetString(3)
                    FillToken = reader.GetString(4)
                }

            if not (artifactIdentityMatches descriptor pending) then conflict <- true

        conflict

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
            not (isNull value)
            && value.Length = 64
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
                    let childIds = HashSet<string>(StringComparer.Ordinal)
                    let artifactIds = HashSet<string>(StringComparer.Ordinal)

                    for childId in directory.ChildDirectoryVersionIds do
                        if
                            String.IsNullOrWhiteSpace childId
                            || not (directories.ContainsKey(childId))
                        then
                            error <- Some "Every child directory membership must reference supplied recursive metadata."
                        elif not (childIds.Add(childId)) then
                            error <- Some "Recursive metadata cannot repeat a child directory membership."
                        elif childId = directory.DirectoryVersionId then
                            error <- Some "A directory cannot be its own child."

                    for artifactId in directory.ArtifactIds do
                        if artifactId <> descriptor.ArtifactId then
                            error <- Some "Recursive metadata cannot reference an artifact that was not confirmed for this ingest."
                        elif not (artifactIds.Add(artifactId)) then
                            error <- Some "Recursive metadata cannot repeat an artifact membership."
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
                        let incomingEdges = Dictionary<string, int>(StringComparer.Ordinal)

                        for directory in metadata.Directories do
                            incomingEdges.Add(directory.DirectoryVersionId, 0)

                        for directory in metadata.Directories do
                            for childId in directory.ChildDirectoryVersionIds do
                                incomingEdges[childId] <- incomingEdges[childId] + 1

                        let roots = Queue<string>()

                        for directoryId in incomingEdges.Keys do
                            if incomingEdges[directoryId] = 0 then roots.Enqueue(directoryId)

                        let mutable visited = 0

                        while roots.Count > 0 do
                            let directoryId = roots.Dequeue()
                            visited <- visited + 1

                            for childId in directories[directoryId].ChildDirectoryVersionIds do
                                incomingEdges[childId] <- incomingEdges[childId] - 1

                                if incomingEdges[childId] = 0 then roots.Enqueue(childId)

                        if visited <> directories.Count then
                            Error "Recursive metadata cannot contain cycles."
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

    /// Reads one immutable directory's direct membership ids without exposing the relational schema to callers.
    let private readDirectoryMembershipIds connection transaction tableName columnName directoryVersionId =
        use command =
            createCommand
                connection
                transaction
                $"SELECT {columnName} FROM {tableName} WHERE directory_version_id = @directoryVersionId ORDER BY {columnName};"
                (fun parameters ->
                    parameters.AddWithValue("@directoryVersionId", directoryVersionId)
                    |> ignore)

        use reader = command.ExecuteReader()
        let memberships = HashSet<string>(StringComparer.Ordinal)

        while reader.Read() do
            memberships.Add(reader.GetString(0)) |> ignore

        memberships

    /// Verifies that a reused immutable directory version has exactly the same direct recursive metadata.
    let private validateCanonicalDirectoryMetadata connection transaction (metadata: CacheRecursiveMetadata) =
        let mutable error = None

        for directory in metadata.Directories do
            let exists =
                executeScalarInt
                    connection
                    transaction
                    "SELECT COUNT(*) FROM directory_versions WHERE directory_version_id = @directoryVersionId;"
                    (fun parameters ->
                        parameters.AddWithValue("@directoryVersionId", directory.DirectoryVersionId)
                        |> ignore)
                |> Option.map (fun count -> count > 0)
                |> Option.defaultValue false

            if exists then
                let existingChildren =
                    readDirectoryMembershipIds connection transaction "directory_child_memberships" "child_directory_version_id" directory.DirectoryVersionId

                let existingArtifacts =
                    readDirectoryMembershipIds connection transaction "directory_artifact_memberships" "artifact_id" directory.DirectoryVersionId

                if
                    not (existingChildren.SetEquals(directory.ChildDirectoryVersionIds))
                    || not (existingArtifacts.SetEquals(directory.ArtifactIds))
                then
                    error <- Some "Directory version id already belongs to different immutable recursive metadata."

        match error with
        | Some reason -> Error reason
        | None -> Ok()

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

    /// Inserts the root-specific pending state only after immutable artifact identity has been revalidated.
    let private insertPendingIngest connection transaction (descriptor: CacheArtifactDescriptor) =
        executeNonQuery
            connection
            transaction
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

    /// Acquires exclusive process ownership before schema work and returns a shared lease for same-process callers.
    let openStore databasePath =
        let canonicalPath = canonicalizeDatabasePath databasePath
        let initializationLock = initializationLocks.GetOrAdd(canonicalPath, (fun _ -> obj ()))

        lock initializationLock (fun () ->
            match sharedStores.TryGetValue(canonicalPath) with
            | true, sharedStore -> Opened(createStoreLease sharedStore, [||])
            | false, _ ->
                match tryAcquireOwnershipLock canonicalPath with
                | None -> CacheDatabaseInUse
                | Some ownershipLock ->
                    try
                        use connection = openConnection canonicalPath
                        ensureSchema connection
                        verifyIntegrity connection
                        let recovered = recoverIncompleteIngests connection
                        verifyIntegrity connection

                        let sharedStore =
                            {
                                DatabasePath = canonicalPath
                                Diagnostics =
                                    {
                                        SchemaVersion = SchemaVersion
                                        JournalMode = "wal"
                                        ForeignKeysEnabled = true
                                        BusyTimeoutMilliseconds = BusyTimeoutMilliseconds
                                    }
                                OwnershipLock = ownershipLock
                                LifecycleGate = obj ()
                                ReferenceCount = 1
                            }

                        if not (sharedStores.TryAdd(canonicalPath, sharedStore)) then
                            ownershipLock.Dispose()
                            invalidOp "Cache database ownership registry changed during initialization."

                        Opened({ Shared = sharedStore; Released = 0 }, recovered)
                    with
                    | error ->
                        ownershipLock.Dispose()
                        raise error)

    /// Releases one caller lease and unlocks the database only after the last same-process caller has released it.
    let disposeStore (store: CacheStore) =
        if Interlocked.Exchange(&store.Released, 1) = 0 then
            let sharedStore = store.Shared
            let initializationLock = initializationLocks.GetOrAdd(sharedStore.DatabasePath, (fun _ -> obj ()))

            lock initializationLock (fun () ->
                lock sharedStore.LifecycleGate (fun () ->
                    sharedStore.ReferenceCount <- sharedStore.ReferenceCount - 1

                    if sharedStore.ReferenceCount < 0 then
                        invalidOp "Cache store ownership reference count became negative."

                    if sharedStore.ReferenceCount = 0 then
                        let mutable removed = Unchecked.defaultof<CacheStoreShared>

                        sharedStores.TryRemove(sharedStore.DatabasePath, &removed)
                        |> ignore

                        sharedStore.OwnershipLock.Dispose()))

    /// Returns the verified connection settings captured while the shared store owner was opened.
    let getDiagnostics (store: CacheStore) =
        let sharedStore = requireLiveStore store
        sharedStore.Diagnostics

    /// Records a valid source descriptor as pending while rejecting malformed or stale identities before persistence.
    let beginIngest (store: CacheStore) (descriptor: CacheArtifactDescriptor) : CacheIngestBeginResult =
        let sharedStore = requireLiveStore store

        match validateDescriptor descriptor with
        | Error reason -> CacheIngestBeginResult.Rejected reason
        | Ok () ->
            withBusyRetry (fun () ->
                use connection = openConnection sharedStore.DatabasePath
                use transaction = connection.BeginTransaction()

                match tryReadDescriptor connection (Some transaction) "artifacts" descriptor.ArtifactId "" with
                | Some valid when
                    descriptorMatches descriptor valid
                    && isArtifactRetainedByRoot connection (Some transaction) descriptor.ArtifactId descriptor.RootDirectoryVersionId
                    ->
                    transaction.Commit()
                    CacheIngestBeginResult.AlreadyValid
                | Some valid when descriptorMatches descriptor valid ->
                    match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                    | Some pending when descriptorMatches descriptor pending ->
                        transaction.Commit()
                        CacheIngestBeginResult.Pending
                    | Some _ ->
                        transaction.Rollback()
                        CacheIngestBeginResult.Rejected "Artifact/root pair already has a pending ingest with a different stale-sensitive identity."
                    | None ->
                        insertPendingIngest connection (Some transaction) descriptor
                        transaction.Commit()
                        CacheIngestBeginResult.Pending
                | Some _ ->
                    transaction.Rollback()
                    CacheIngestBeginResult.Rejected "Artifact id already belongs to a different valid immutable descriptor."
                | None when hasPendingArtifactIdentityConflict connection (Some transaction) descriptor ->
                    transaction.Rollback()
                    CacheIngestBeginResult.Rejected "Artifact id already has a cross-root pending ingest with a different immutable identity."
                | None ->
                    match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                    | Some pending when descriptorMatches descriptor pending ->
                        transaction.Commit()
                        CacheIngestBeginResult.Pending
                    | Some _ ->
                        transaction.Rollback()
                        CacheIngestBeginResult.Rejected "Artifact/root pair already has a pending ingest with a different stale-sensitive identity."
                    | None ->
                        insertPendingIngest connection (Some transaction) descriptor
                        transaction.Commit()
                        CacheIngestBeginResult.Pending)

    /// Atomically publishes one confirmed artifact and its complete recursive metadata only while the original pending identity remains current.
    let commitPromotedIngest (store: CacheStore) (descriptor: CacheArtifactDescriptor) (metadata: CacheRecursiveMetadata) : CacheIngestCommitResult =
        let sharedStore = requireLiveStore store

        match validateDescriptor descriptor, validateMetadata descriptor metadata with
        | Error reason, _
        | _, Error reason -> CacheIngestCommitResult.Rejected reason
        | Ok (), Ok () ->
            withBusyRetry (fun () ->
                use connection = openConnection sharedStore.DatabasePath
                use transaction = connection.BeginTransaction()

                match tryReadDescriptor connection (Some transaction) "pending_ingests" descriptor.ArtifactId descriptor.RootDirectoryVersionId with
                | None ->
                    match tryReadDescriptor connection (Some transaction) "artifacts" descriptor.ArtifactId "" with
                    | Some valid when descriptorMatches descriptor valid ->
                        match validateCanonicalDirectoryMetadata connection (Some transaction) metadata with
                        | Error reason ->
                            transaction.Rollback()
                            CacheIngestCommitResult.Rejected reason
                        | Ok () ->
                            insertMetadata connection (Some transaction) descriptor metadata

                            if isArtifactRetainedByRoot connection (Some transaction) descriptor.ArtifactId descriptor.RootDirectoryVersionId then
                                transaction.Commit()
                                CacheIngestCommitResult.AlreadyPublished
                            else
                                transaction.Rollback()
                                CacheIngestCommitResult.Rejected "Existing artifact publication could not establish the requested root membership."
                    | _ ->
                        transaction.Rollback()
                        CacheIngestCommitResult.Rejected "No current pending ingest exists for the promoted artifact."
                | Some pending when not (descriptorMatches descriptor pending) ->
                    transaction.Rollback()
                    CacheIngestCommitResult.Rejected "Pending ingest identity changed before promoted publication."
                | Some _ ->
                    match validateCanonicalDirectoryMetadata connection (Some transaction) metadata with
                    | Error reason ->
                        transaction.Rollback()
                        CacheIngestCommitResult.Rejected reason
                    | Ok () ->
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
        let sharedStore = requireLiveStore store
        use connection = openConnection sharedStore.DatabasePath

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
