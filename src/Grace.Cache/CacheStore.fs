namespace Grace.Cache

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.Runtime.CompilerServices
open System.Threading
open Microsoft.Data.Sqlite
open SQLitePCL

[<assembly: InternalsVisibleTo("Grace.Cache.Tests")>]
do ()

/// Reports the SQLite settings verified whenever a cache store gains process ownership.
type CacheStoreDiagnostics = { SchemaVersion: int; JournalMode: string; ForeignKeysEnabled: bool; BusyTimeoutMilliseconds: int }

/// Carries the test-host descriptor used only to hold the retained #623 process-lock seam.
type internal CacheArtifactDescriptor = { ArtifactId: string; Digest: string; UncompressedSize: int64; RootDirectoryVersionId: string; FillToken: string }

/// Represents the test-host's retained process-lock setup result without exposing a second production lifecycle.
type internal CacheIngestBeginResult =
    | Pending
    | NotSupported

/// Holds one process-owned database lock and shared lifetime state for cache-store leases.
type private CacheStoreShared =
    {
        DatabasePath: string
        Diagnostics: CacheStoreDiagnostics
        OwnershipLock: FileStream
        LifecycleGate: obj
        OperationGate: obj
        mutable ReferenceCount: int
        mutable ActiveOperationCount: int
    }

/// Represents a private lease over the one cache process database owner.
type CacheStore = private { Shared: CacheStoreShared; mutable Released: int }

/// Returns an opened private cache store or the stable result when another process owns its database.
type CacheStoreOpenResult =
    | Opened of store: CacheStore * recoveredIncomplete: obj array
    | CacheDatabaseInUse

    /// Returns the opened lease or fails when another process currently owns the database.
    member this.Store =
        match this with
        | Opened (store, _) -> store
        | CacheDatabaseInUse -> invalidOp "Cache database is already owned by another process."

/// Owns the development-only SQLite database and retained #623 process ownership behavior.
module CacheStore =

    [<Literal>]
    let private SchemaVersion = 2

    [<Literal>]
    let private BusyTimeoutMilliseconds = 5000

    let private sqliteInitialized =
        lazy
            (Batteries_V2.Init()
             true)

    let private pathComparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    let private initializationLocks = ConcurrentDictionary<string, obj>(pathComparer)
    let private sharedStores = ConcurrentDictionary<string, CacheStoreShared>(pathComparer)

    /// Executes a command and disposes it before the surrounding SQLite operation advances.
    let private executeNonQuery (connection: SqliteConnection) (transaction: SqliteTransaction option) sql configure =
        use command = connection.CreateCommand()
        command.CommandText <- sql

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        configure command.Parameters
        command.ExecuteNonQuery() |> ignore

    /// Reads a single integer setting from SQLite without inheriting a provider default.
    let private executeScalarInt (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        let value = command.ExecuteScalar()

        if isNull value || value = Convert.DBNull then
            None
        else
            Some(Convert.ToInt32(value, CultureInfo.InvariantCulture))

    /// Resolves existing path components while retaining a non-existing suffix for one physical database key.
    let private canonicalizeDatabasePath databasePath =
        if String.IsNullOrWhiteSpace databasePath then
            invalidArg (nameof databasePath) "Cache database path is required."

        let fullPath = Path.GetFullPath(databasePath)
        let root = Path.GetPathRoot(fullPath)

        if String.IsNullOrWhiteSpace root then
            invalidOp "Cache database path did not resolve to a rooted path."

        let components =
            fullPath[root.Length ..]
                .Split(
                    [|
                        Path.DirectorySeparatorChar
                        Path.AltDirectorySeparatorChar
                    |],
                    StringSplitOptions.RemoveEmptyEntries
                )

        let mutable current = root
        let mutable retainsSuffix = false

        for index in 0 .. components.Length - 1 do
            let candidate = Path.Combine(current, components[index])

            if retainsSuffix then
                current <- candidate
            else
                let info: FileSystemInfo =
                    if index = components.Length - 1 then
                        FileInfo(candidate) :> FileSystemInfo
                    else
                        DirectoryInfo(candidate) :> FileSystemInfo

                if info.Exists then
                    match info.ResolveLinkTarget(true) with
                    | null -> current <- info.FullName
                    | target -> current <- target.FullName
                else
                    current <- candidate
                    retainsSuffix <- true

        current

    /// Acquires the defensive sidecar byte-range lock before any schema or recovery operation runs.
    let private tryAcquireOwnershipLock (databasePath: string) =
        let directory = Path.GetDirectoryName(databasePath)

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory(directory) |> ignore

        let lockPath = databasePath + ".owner.lock"

        try
            let stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite ||| FileShare.Delete)

            try
                stream.Lock(0L, 1L)
                Some stream
            with
            | :? IOException ->
                stream.Dispose()
                None
        with
        | :? IOException -> None

    /// Opens a connection and establishes the retained WAL, foreign-key, and bounded-busy guarantees.
    let internal openConnection (databasePath: string) =
        sqliteInitialized.Value |> ignore
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
            executeNonQuery connection None "PRAGMA journal_mode = WAL;" ignore

            if executeScalarInt connection "PRAGMA foreign_keys;"
               <> Some 1 then
                invalidOp "Cache SQLite connection did not enable foreign keys."

            if executeScalarInt connection "PRAGMA busy_timeout;"
               <> Some BusyTimeoutMilliseconds then
                invalidOp "Cache SQLite connection did not establish the declared busy timeout."

            connection
        with
        | error ->
            connection.Dispose()
            raise error

    /// Creates only the reset schema marker and rejects any older development schema rather than migrating it.
    let private ensureSchema (connection: SqliteConnection) =
        executeNonQuery
            connection
            None
            "CREATE TABLE IF NOT EXISTS cache_schema (singleton INTEGER PRIMARY KEY CHECK (singleton = 1), schema_version INTEGER NOT NULL);"
            ignore

        use query = connection.CreateCommand()
        query.CommandText <- "SELECT schema_version FROM cache_schema WHERE singleton = 1;"
        let existing = query.ExecuteScalar()

        if isNull existing || existing = Convert.DBNull then
            use insert = connection.CreateCommand()
            insert.CommandText <- "INSERT INTO cache_schema (singleton, schema_version) VALUES (1, @version);"

            insert.Parameters.AddWithValue("@version", SchemaVersion)
            |> ignore

            insert.ExecuteNonQuery() |> ignore
        elif
            Convert.ToInt32(existing, CultureInfo.InvariantCulture)
            <> SchemaVersion
        then
            invalidOp "Cache SQLite development schema requires an explicit local reset; no compatibility migration is provided."

    /// Releases a shared owner only after every lease and active operation have ended.
    let private releaseIfUnused sharedStore =
        if sharedStore.ReferenceCount = 0
           && sharedStore.ActiveOperationCount = 0 then
            let mutable removed = Unchecked.defaultof<CacheStoreShared>

            if sharedStores.TryRemove(sharedStore.DatabasePath, &removed) then
                if not (Object.ReferenceEquals(removed, sharedStore)) then
                    invalidOp "Cache store registry removed a different owner."

                sharedStore.OwnershipLock.Dispose()

    /// Runs one serialized artifact operation while retaining database process ownership through cleanup.
    let internal withStoreOperation (store: CacheStore) operation =
        let sharedStore = store.Shared

        lock sharedStore.LifecycleGate (fun () ->
            if Volatile.Read(&store.Released) <> 0 then
                invalidOp "Cache store lease has already been released."

            sharedStore.ActiveOperationCount <- sharedStore.ActiveOperationCount + 1)

        try
            lock sharedStore.OperationGate (fun () -> operation sharedStore.DatabasePath)
        finally
            let initializationLock = initializationLocks.GetOrAdd(sharedStore.DatabasePath, (fun _ -> obj ()))

            lock initializationLock (fun () ->
                lock sharedStore.LifecycleGate (fun () ->
                    sharedStore.ActiveOperationCount <- sharedStore.ActiveOperationCount - 1

                    if sharedStore.ActiveOperationCount < 0 then
                        invalidOp "Cache store operation count became negative."

                    releaseIfUnused sharedStore))

    /// Retries only SQLite busy/locked operations within the retained bounded busy window.
    let internal withBusyRetry action =
        let deadline = DateTime.UtcNow.AddMilliseconds(float BusyTimeoutMilliseconds)
        let mutable retry = true
        let mutable result = Unchecked.defaultof<_>

        while retry do
            try
                result <- action ()
                retry <- false
            with
            | :? SqliteException as error when
                (error.SqliteErrorCode = 5
                 || error.SqliteErrorCode = 6)
                && DateTime.UtcNow < deadline
                ->
                Thread.Sleep(25)

        result

    /// Opens the reset cache schema only after this process has acquired exclusive ownership of its database.
    let openStore databasePath =
        let canonicalPath = canonicalizeDatabasePath databasePath
        let initializationLock = initializationLocks.GetOrAdd(canonicalPath, (fun _ -> obj ()))

        lock initializationLock (fun () ->
            match sharedStores.TryGetValue(canonicalPath) with
            | true, sharedStore ->
                lock sharedStore.LifecycleGate (fun () -> sharedStore.ReferenceCount <- sharedStore.ReferenceCount + 1)
                Opened({ Shared = sharedStore; Released = 0 }, [||])
            | false, _ ->
                match tryAcquireOwnershipLock canonicalPath with
                | None -> CacheDatabaseInUse
                | Some ownershipLock ->
                    try
                        use connection = openConnection canonicalPath
                        ensureSchema connection

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
                                OperationGate = obj ()
                                ReferenceCount = 1
                                ActiveOperationCount = 0
                            }

                        if not (sharedStores.TryAdd(canonicalPath, sharedStore)) then
                            ownershipLock.Dispose()
                            invalidOp "Cache store registry changed during initialization."

                        Opened({ Shared = sharedStore; Released = 0 }, [||])
                    with
                    | error ->
                        ownershipLock.Dispose()
                        raise error)

    /// Releases one cache-store lease and unlocks the database only after every operation exits.
    let disposeStore (store: CacheStore) =
        if Interlocked.Exchange(&store.Released, 1) = 0 then
            let sharedStore = store.Shared
            let initializationLock = initializationLocks.GetOrAdd(sharedStore.DatabasePath, (fun _ -> obj ()))

            lock initializationLock (fun () ->
                lock sharedStore.LifecycleGate (fun () ->
                    sharedStore.ReferenceCount <- sharedStore.ReferenceCount - 1

                    if sharedStore.ReferenceCount < 0 then
                        invalidOp "Cache store ownership reference count became negative."

                    releaseIfUnused sharedStore))

    /// Returns the settings that were verified while the current process acquired the store.
    let getDiagnostics store = withStoreOperation store (fun _ -> store.Shared.Diagnostics)

    /// Retains the #623 child-process lock probe without persisting any old recursive-ingest state.
    let internal beginIngest store (_: CacheArtifactDescriptor) = withStoreOperation store (fun _ -> CacheIngestBeginResult.Pending)
