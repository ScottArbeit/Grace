namespace Grace.CLI

open Microsoft.Data.Sqlite
open SQLitePCL
open System
open System.Globalization
open System.IO
open Grace.Types.Library

/// Owns the finite Library synchronization transactions in `.grace/grace-local.db`.
module LibraryLocalState =

    /// Names the accepted source of one local recovery operation.
    type OperationDirection =
        | Local
        | Remote
        | Bootstrap

    /// Carries the durable cursor and catalog authority for one connected working copy.
    type RepositoryState =
        {
            RepositoryId: Guid
            WorkingCopyId: Guid
            LibraryCatalogVersion: Guid
            CursorEpoch: string option
            AppliedCursor: string option
            PredecessorCursor: string option
            ParticipationEnabled: bool
            LifecycleState: string
        }

    /// Carries exact durable evidence needed to classify a restarted remote file operation.
    type PendingRemoteFile =
        {
            OperationId: Guid
            ItemId: Guid
            TargetPath: string
            OperationState: string
            ServerCursor: string
            ExpectedBlake3: string
            ExpectedSha256: string
            ExpectedSize: int64
            LibraryCatalogVersion: Guid
        }

    /// Carries one local publication that can resume from server receipt or terminal SQLite evidence.
    type RecoverableLocalOperation = { OperationId: Guid; OperationState: string; ServerCursor: string option; LibraryCatalogVersion: Guid }

    /// Carries the durable local ancestry used to reject an unobserved target edit.
    type ItemAncestry = { ItemId: Guid; NormalizedPath: string; Blake3Hash: string; NamespaceVersion: Guid; ContentVersionId: Guid }

    [<Literal>]
    let private busyTimeoutMilliseconds = 30000

    /// Formats one durable local timestamp without depending on process culture.
    let private timestamp () = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)

    /// Converts one operation direction to the accepted SQLite wire value.
    let private directionValue =
        function
        | Local -> "local"
        | Remote -> "remote"
        | Bootstrap -> "bootstrap"

    /// Opens the existing local database with the stronger durability required by Library effects.
    let private openConnection (dbPath: string) =
        Batteries_V2.Init()

        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- dbPath
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.Pooling <- true
        builder.DefaultTimeout <- busyTimeoutMilliseconds / 1000
        let connection = new SqliteConnection(builder.ToString())
        connection.Open()

        use pragma = connection.CreateCommand()
        pragma.CommandText <- $"PRAGMA busy_timeout = {busyTimeoutMilliseconds}; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;"
        pragma.ExecuteNonQuery() |> ignore
        connection

    /// Runs one bounded immediate transaction and rolls it back on every exception.
    let private inTransaction (connection: SqliteConnection) (action: unit -> 'T) =
        use beginCommand = connection.CreateCommand()
        beginCommand.CommandText <- "BEGIN IMMEDIATE;"
        beginCommand.ExecuteNonQuery() |> ignore

        try
            let result = action ()
            use commit = connection.CreateCommand()
            commit.CommandText <- "COMMIT;"
            commit.ExecuteNonQuery() |> ignore
            result
        with
        | ex ->
            use rollback = connection.CreateCommand()
            rollback.CommandText <- "ROLLBACK;"

            try
                rollback.ExecuteNonQuery() |> ignore
            with
            | _ -> ()

            raise ex

    /// Reads one nullable string from a SQLite result.
    let private optionalText (reader: SqliteDataReader) (ordinal: int) = if reader.IsDBNull(ordinal) then None else Some(reader.GetString(ordinal))

    /// Converts an optional SQLite value to its explicit database-null representation.
    let private parameterValue value =
        value
        |> Option.map box
        |> Option.defaultValue (box DBNull.Value)

    /// Creates or refreshes one enabled participant from an exact Library catalog and bootstrap boundary.
    let enable
        (dbPath: string)
        (repositoryId: Guid)
        (workingCopyId: Guid)
        (libraryCatalogVersion: Guid)
        (libraries: string array)
        (cursorEpoch: string)
        (appliedCursor: string)
        =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            inTransaction connection (fun () ->
                let now = timestamp ()
                use state = connection.CreateCommand()

                state.CommandText <-
                    "INSERT INTO library_repository_state (repository_id, working_copy_id, library_catalog_version, cursor_epoch, applied_cursor, predecessor_cursor, service_floor_cursor, participation_enabled, lifecycle_state, last_completed_at, updated_at) VALUES ($repository_id, $working_copy_id, $catalog_version, $cursor_epoch, $applied_cursor, NULL, NULL, 1, 'current', $updated_at, $updated_at) ON CONFLICT(repository_id) DO UPDATE SET working_copy_id = excluded.working_copy_id, library_catalog_version = excluded.library_catalog_version, cursor_epoch = excluded.cursor_epoch, applied_cursor = excluded.applied_cursor, predecessor_cursor = NULL, participation_enabled = 1, lifecycle_state = 'current', last_completed_at = excluded.last_completed_at, updated_at = excluded.updated_at;"

                state.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                state.Parameters.AddWithValue("$working_copy_id", workingCopyId.ToString("D"))
                |> ignore

                state.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                |> ignore

                state.Parameters.AddWithValue("$cursor_epoch", cursorEpoch)
                |> ignore

                state.Parameters.AddWithValue("$applied_cursor", appliedCursor)
                |> ignore

                state.Parameters.AddWithValue("$updated_at", now)
                |> ignore

                state.ExecuteNonQuery() |> ignore

                use deleteLibraries = connection.CreateCommand()
                deleteLibraries.CommandText <- "DELETE FROM libraries WHERE repository_id = $repository_id;"

                deleteLibraries.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                deleteLibraries.ExecuteNonQuery() |> ignore

                use insertLibrary = connection.CreateCommand()

                insertLibrary.CommandText <-
                    "INSERT INTO libraries (repository_id, normalized_path, library_catalog_version) VALUES ($repository_id, $normalized_path, $catalog_version);"

                insertLibrary.Parameters.Add("$repository_id", SqliteType.Text)
                |> ignore

                insertLibrary.Parameters.Add("$normalized_path", SqliteType.Text)
                |> ignore

                insertLibrary.Parameters.Add("$catalog_version", SqliteType.Text)
                |> ignore

                libraries
                |> Array.iter (fun library ->
                    insertLibrary.Parameters["$repository_id"].Value <- repositoryId.ToString("D")
                    insertLibrary.Parameters["$normalized_path"].Value <- library
                    insertLibrary.Parameters["$catalog_version"].Value <- libraryCatalogVersion.ToString("D")
                    insertLibrary.ExecuteNonQuery() |> ignore))
        }

    /// Reads the exact local catalog and cursor authority for one repository.
    let readRepositoryState (dbPath: string) (repositoryId: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT repository_id, working_copy_id, library_catalog_version, cursor_epoch, applied_cursor, predecessor_cursor, participation_enabled, lifecycle_state FROM library_repository_state WHERE repository_id = $repository_id;"

            command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
            |> ignore

            use reader = command.ExecuteReader()

            if reader.Read() then
                return
                    Some
                        {
                            RepositoryId = Guid.Parse(reader.GetString(0))
                            WorkingCopyId = Guid.Parse(reader.GetString(1))
                            LibraryCatalogVersion = Guid.Parse(reader.GetString(2))
                            CursorEpoch = optionalText reader 3
                            AppliedCursor = optionalText reader 4
                            PredecessorCursor = optionalText reader 5
                            ParticipationEnabled = reader.GetInt32(6) = 1
                            LifecycleState = reader.GetString(7)
                        }
            else
                return None
        }

    /// Reads one exact nonterminal remote file operation for normal execution or restart classification.
    let readPendingRemoteFile (dbPath: string) (repositoryId: Guid) (operationId: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT operation_id, item_id, target_path, operation_state, server_cursor, expected_blake3, expected_sha256, expected_size, library_catalog_version FROM library_operations WHERE repository_id = $repository_id AND operation_id = $operation_id AND direction = 'remote' AND operation_state IN ('pendingFilesystem','filesystemPublished');"

            command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
            |> ignore

            command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
            |> ignore

            use reader = command.ExecuteReader()

            if reader.Read() then
                return
                    Some
                        {
                            OperationId = Guid.Parse(reader.GetString(0))
                            ItemId = Guid.Parse(reader.GetString(1))
                            TargetPath = reader.GetString(2)
                            OperationState = reader.GetString(3)
                            ServerCursor = reader.GetString(4)
                            ExpectedBlake3 = reader.GetString(5)
                            ExpectedSha256 = reader.GetString(6)
                            ExpectedSize = reader.GetInt64(7)
                            LibraryCatalogVersion = Guid.Parse(reader.GetString(8))
                        }
            else
                return None
        }

    /// Reads retryable local publication evidence without treating a filesystem observation as server authority.
    let readRecoverableLocalOperation (dbPath: string) (repositoryId: Guid) (operationId: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT operation_id, operation_state, server_cursor, library_catalog_version FROM library_operations WHERE repository_id = $repository_id AND operation_id = $operation_id AND direction = 'local' AND operation_state IN ('pendingServer','serverAccepted','terminal');"

            command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
            |> ignore

            command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
            |> ignore

            use reader = command.ExecuteReader()

            if reader.Read() then
                return
                    Some
                        {
                            OperationId = Guid.Parse(reader.GetString(0))
                            OperationState = reader.GetString(1)
                            ServerCursor = optionalText reader 2
                            LibraryCatalogVersion = Guid.Parse(reader.GetString(3))
                        }
            else
                return None
        }

    /// Reads the current live file ancestry for one stable Library item identity.
    let readItemAncestry (dbPath: string) (repositoryId: Guid) (itemId: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT item_id, normalized_path, blake3_hash, namespace_version, content_version_id FROM library_items WHERE repository_id = $repository_id AND item_id = $item_id AND item_kind = 'file' AND item_state = 'live';"

            command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
            |> ignore

            command.Parameters.AddWithValue("$item_id", itemId.ToString("D"))
            |> ignore

            use reader = command.ExecuteReader()

            if reader.Read() then
                return
                    Some
                        {
                            ItemId = Guid.Parse(reader.GetString(0))
                            NormalizedPath = reader.GetString(1)
                            Blake3Hash = reader.GetString(2)
                            NamespaceVersion = Guid.Parse(reader.GetString(3))
                            ContentVersionId = Guid.Parse(reader.GetString(4))
                        }
            else
                return None
        }

    /// Lists live file ancestry for deterministic local scan comparison.
    let readLiveFileAncestry (dbPath: string) (repositoryId: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT item_id, normalized_path, blake3_hash, namespace_version, content_version_id FROM library_items WHERE repository_id = $repository_id AND item_kind = 'file' AND item_state = 'live' ORDER BY normalized_path;"

            command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
            |> ignore

            use reader = command.ExecuteReader()
            let items = ResizeArray<ItemAncestry>()

            while reader.Read() do
                items.Add
                    {
                        ItemId = Guid.Parse(reader.GetString(0))
                        NormalizedPath = reader.GetString(1)
                        Blake3Hash = reader.GetString(2)
                        NamespaceVersion = Guid.Parse(reader.GetString(3))
                        ContentVersionId = Guid.Parse(reader.GetString(4))
                    }

            return items.ToArray()
        }

    /// Reads the immutable Library roots copied into one enabled Watch lifetime snapshot.
    let readEnabledLibraryRoots (dbPath: string) (repositoryId: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT l.normalized_path FROM libraries l JOIN library_repository_state r ON r.repository_id = l.repository_id WHERE l.repository_id = $repository_id AND r.participation_enabled = 1 ORDER BY l.normalized_path;"

            command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
            |> ignore

            use reader = command.ExecuteReader()
            let roots = ResizeArray<string>()

            while reader.Read() do
                roots.Add(reader.GetString(0))

            return roots.ToArray()
        }

    /// Persists deterministic operation evidence before a server or filesystem effect can begin.
    let recordPending
        (dbPath: string)
        (repositoryId: Guid)
        (operationId: Guid)
        (direction: OperationDirection)
        (operationKind: string)
        (requestHash: string)
        (libraryCatalogVersion: Guid)
        (itemId: Guid option)
        (sourcePath: string option)
        (targetPath: string option)
        (preparedContentId: Guid option)
        (stagingPath: string option)
        (predecessorCursor: string option)
        (serverCursor: string option)
        (cursorEpoch: string option)
        (expectedBlake3: string option)
        (expectedSha256: string option)
        (expectedSize: int64 option)
        =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            inTransaction connection (fun () ->
                let now = timestamp ()
                use authority = connection.CreateCommand()

                authority.CommandText <-
                    "UPDATE library_repository_state SET predecessor_cursor = $predecessor, updated_at = $updated_at WHERE repository_id = $repository_id AND library_catalog_version = $catalog_version AND participation_enabled = 1 AND applied_cursor IS $predecessor;"

                authority.Parameters.AddWithValue("$predecessor", parameterValue predecessorCursor)
                |> ignore

                authority.Parameters.AddWithValue("$updated_at", now)
                |> ignore

                authority.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                authority.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                |> ignore

                if authority.ExecuteNonQuery() <> 1 then
                    invalidOp "Library pending evidence lost its exact catalog or cursor predecessor."

                use operation = connection.CreateCommand()

                operation.CommandText <-
                    "INSERT INTO library_operations (repository_id, operation_id, direction, operation_kind, operation_state, request_hash, library_catalog_version, item_id, source_path, target_path, prepared_content_id, staging_path, predecessor_cursor, server_cursor, cursor_epoch, expected_blake3, expected_sha256, expected_size, created_at, updated_at) VALUES ($repository_id, $operation_id, $direction, $operation_kind, $operation_state, $request_hash, $catalog_version, $item_id, $source_path, $target_path, $prepared_content_id, $staging_path, $predecessor_cursor, $server_cursor, $cursor_epoch, $expected_blake3, $expected_sha256, $expected_size, $created_at, $updated_at) ON CONFLICT(repository_id, operation_id) DO UPDATE SET updated_at = library_operations.updated_at WHERE library_operations.direction = excluded.direction AND library_operations.operation_kind = excluded.operation_kind AND library_operations.request_hash = excluded.request_hash AND library_operations.library_catalog_version = excluded.library_catalog_version AND library_operations.item_id IS excluded.item_id AND library_operations.source_path IS excluded.source_path AND library_operations.target_path IS excluded.target_path AND library_operations.prepared_content_id IS excluded.prepared_content_id AND library_operations.staging_path IS excluded.staging_path AND library_operations.predecessor_cursor IS excluded.predecessor_cursor AND library_operations.server_cursor IS excluded.server_cursor AND library_operations.cursor_epoch IS excluded.cursor_epoch AND library_operations.expected_blake3 IS excluded.expected_blake3 AND library_operations.expected_sha256 IS excluded.expected_sha256 AND library_operations.expected_size IS excluded.expected_size;"

                let addOptional (name: string) (value: string option) =
                    operation.Parameters.AddWithValue(name, parameterValue value)
                    |> ignore

                operation.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                operation.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                |> ignore

                operation.Parameters.AddWithValue("$direction", directionValue direction)
                |> ignore

                operation.Parameters.AddWithValue("$operation_kind", operationKind)
                |> ignore

                operation.Parameters.AddWithValue(
                    "$operation_state",
                    match direction with
                    | Remote -> "pendingFilesystem"
                    | Local -> "pendingServer"
                    | Bootstrap -> "staged"
                )
                |> ignore

                operation.Parameters.AddWithValue("$request_hash", requestHash)
                |> ignore

                operation.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                |> ignore

                addOptional
                    "$item_id"
                    (itemId
                     |> Option.map (fun value -> value.ToString("D")))

                addOptional "$source_path" sourcePath
                addOptional "$target_path" targetPath

                addOptional
                    "$prepared_content_id"
                    (preparedContentId
                     |> Option.map (fun value -> value.ToString("D")))

                addOptional "$staging_path" stagingPath
                addOptional "$predecessor_cursor" predecessorCursor
                addOptional "$server_cursor" serverCursor
                addOptional "$cursor_epoch" cursorEpoch
                addOptional "$expected_blake3" expectedBlake3
                addOptional "$expected_sha256" expectedSha256

                operation.Parameters.AddWithValue("$expected_size", parameterValue expectedSize)
                |> ignore

                operation.Parameters.AddWithValue("$created_at", now)
                |> ignore

                operation.Parameters.AddWithValue("$updated_at", now)
                |> ignore

                if operation.ExecuteNonQuery() <> 1 then
                    invalidOp "Library operation identity collided with different immutable pending evidence.")
        }

    /// Marks one exact operation terminal without item projection, used by cursor-only and focused boundary flows.
    let markTerminalWithoutProjection (dbPath: string) (repositoryId: Guid) (operationId: Guid) (libraryCatalogVersion: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            inTransaction connection (fun () ->
                use command = connection.CreateCommand()

                command.CommandText <-
                    "UPDATE library_operations SET operation_state = 'terminal', terminal_at = $terminal_at, updated_at = $terminal_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND library_catalog_version = $catalog_version AND operation_state IN ('pendingServer','serverAccepted','staged','pendingFilesystem','filesystemPublished');"

                command.Parameters.AddWithValue("$terminal_at", timestamp ())
                |> ignore

                command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                |> ignore

                command.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                |> ignore

                if command.ExecuteNonQuery() <> 1 then
                    invalidOp "Library terminal state requires one exact pending operation and catalog version.")
        }

    /// Records that the same-volume atomic move published the exact expected bytes.
    let markFilesystemPublished (dbPath: string) (repositoryId: Guid) (operationId: Guid) (libraryCatalogVersion: Guid) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            inTransaction connection (fun () ->
                use command = connection.CreateCommand()

                command.CommandText <-
                    "UPDATE library_operations SET operation_state = 'filesystemPublished', updated_at = $updated_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND library_catalog_version = $catalog_version AND operation_state = 'pendingFilesystem';"

                command.Parameters.AddWithValue("$updated_at", timestamp ())
                |> ignore

                command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                |> ignore

                command.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                |> ignore

                if command.ExecuteNonQuery() <> 1 then
                    invalidOp "Library filesystem publication requires one exact pending operation and catalog version.")
        }

    /// Records the stable accepted receipt returned for one retryable local publication.
    let markServerAccepted (dbPath: string) (repositoryId: Guid) (operationId: Guid) (libraryCatalogVersion: Guid) (serverCursor: string) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            inTransaction connection (fun () ->
                use command = connection.CreateCommand()

                command.CommandText <-
                    "UPDATE library_operations SET operation_state = 'serverAccepted', server_cursor = $server_cursor, updated_at = $updated_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND library_catalog_version = $catalog_version AND direction = 'local' AND operation_state IN ('pendingServer','serverAccepted') AND (server_cursor IS NULL OR server_cursor = $server_cursor);"

                command.Parameters.AddWithValue("$server_cursor", serverCursor)
                |> ignore

                command.Parameters.AddWithValue("$updated_at", timestamp ())
                |> ignore

                command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                |> ignore

                command.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                |> ignore

                if command.ExecuteNonQuery() <> 1 then
                    invalidOp "Library server acceptance requires the same local operation, catalog, and cursor receipt.")
        }

    /// Atomically projects one accepted live file and marks its exact operation terminal with Watch-echo evidence.
    let completeAcceptedFile (dbPath: string) (repositoryId: Guid) (change: LibraryChangeDto) =
        task {
            let namespaceValue =
                change.Namespace
                |> Option.defaultWith (fun () -> invalidOp "A live Library file requires namespace state.")

            let content =
                change.Content
                |> Option.defaultWith (fun () -> invalidOp "A live Library file requires content state.")

            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            inTransaction connection (fun () ->
                let now = timestamp ()
                use authority = connection.CreateCommand()

                authority.CommandText <-
                    "SELECT COUNT(*) FROM library_repository_state AS state JOIN library_operations AS operation ON operation.repository_id = state.repository_id WHERE state.repository_id = $repository_id AND state.library_catalog_version = $catalog_version AND state.participation_enabled = 1 AND operation.operation_id = $operation_id AND operation.operation_state IN ('serverAccepted','filesystemPublished') AND operation.library_catalog_version = state.library_catalog_version AND operation.server_cursor = $server_cursor;"

                authority.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                authority.Parameters.AddWithValue("$catalog_version", change.LibraryCatalogVersion.ToString("D"))
                |> ignore

                authority.Parameters.AddWithValue("$operation_id", change.OperationId.ToString("D"))
                |> ignore

                authority.Parameters.AddWithValue("$server_cursor", change.Cursor)
                |> ignore

                if
                    Convert.ToInt32(authority.ExecuteScalar(), CultureInfo.InvariantCulture)
                    <> 1
                then
                    invalidOp "Library terminal projection lost its exact catalog, operation, or accepted cursor authority."

                use item = connection.CreateCommand()

                item.CommandText <-
                    "INSERT INTO library_items (repository_id, item_id, item_kind, item_state, parent_kind, library_path, parent_item_id, name, normalized_path, namespace_version, slot_version, content_version_id, blake3_hash, sha256_hash, content_size, deleted_at, deleted_by, delete_cursor, last_mutation_cursor, library_catalog_version, updated_at) VALUES ($repository_id, $item_id, 'file', 'live', $parent_kind, $library_path, $parent_item_id, $name, $normalized_path, $namespace_version, $slot_version, $content_version_id, $blake3, $sha256, $content_size, NULL, NULL, NULL, $cursor, $catalog_version, $updated_at) ON CONFLICT(repository_id, item_id) DO UPDATE SET item_kind = excluded.item_kind, item_state = excluded.item_state, parent_kind = excluded.parent_kind, library_path = excluded.library_path, parent_item_id = excluded.parent_item_id, name = excluded.name, normalized_path = excluded.normalized_path, namespace_version = excluded.namespace_version, slot_version = excluded.slot_version, content_version_id = excluded.content_version_id, blake3_hash = excluded.blake3_hash, sha256_hash = excluded.sha256_hash, content_size = excluded.content_size, deleted_at = NULL, deleted_by = NULL, delete_cursor = NULL, last_mutation_cursor = excluded.last_mutation_cursor, library_catalog_version = excluded.library_catalog_version, updated_at = excluded.updated_at;"

                let parent = namespaceValue.Parent

                item.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                item.Parameters.AddWithValue("$item_id", change.ItemId.ToString("D"))
                |> ignore

                item.Parameters.AddWithValue("$parent_kind", parent.Kind)
                |> ignore

                item.Parameters.AddWithValue("$library_path", parameterValue parent.LibraryPath)
                |> ignore

                item.Parameters.AddWithValue(
                    "$parent_item_id",
                    parameterValue (
                        parent.ItemId
                        |> Option.map (fun id -> id.ToString("D"))
                    )
                )
                |> ignore

                item.Parameters.AddWithValue("$name", namespaceValue.Name)
                |> ignore

                item.Parameters.AddWithValue("$normalized_path", namespaceValue.NormalizedPath)
                |> ignore

                item.Parameters.AddWithValue("$namespace_version", namespaceValue.NamespaceVersion.ToString("D"))
                |> ignore

                item.Parameters.AddWithValue("$slot_version", namespaceValue.SlotVersion.ToString("D"))
                |> ignore

                item.Parameters.AddWithValue("$content_version_id", content.ContentVersionId.ToString("D"))
                |> ignore

                item.Parameters.AddWithValue("$blake3", content.Blake3Hash)
                |> ignore

                item.Parameters.AddWithValue("$sha256", content.Sha256Hash)
                |> ignore

                item.Parameters.AddWithValue("$content_size", content.Size)
                |> ignore

                item.Parameters.AddWithValue("$cursor", change.Cursor)
                |> ignore

                item.Parameters.AddWithValue("$catalog_version", change.LibraryCatalogVersion.ToString("D"))
                |> ignore

                item.Parameters.AddWithValue("$updated_at", now)
                |> ignore

                item.ExecuteNonQuery() |> ignore

                use slot = connection.CreateCommand()

                slot.CommandText <-
                    "INSERT INTO library_namespace_slots (repository_id, normalized_path, parent_key, normalized_name, slot_version, slot_state, occupant_item_id, last_mutation_cursor, library_catalog_version, updated_at) VALUES ($repository_id, $normalized_path, $parent_key, $normalized_name, $slot_version, 'occupied', $item_id, $cursor, $catalog_version, $updated_at) ON CONFLICT(repository_id, normalized_path) DO UPDATE SET parent_key = excluded.parent_key, normalized_name = excluded.normalized_name, slot_version = excluded.slot_version, slot_state = 'occupied', occupant_item_id = excluded.occupant_item_id, last_mutation_cursor = excluded.last_mutation_cursor, library_catalog_version = excluded.library_catalog_version, updated_at = excluded.updated_at;"

                let parentKey =
                    match parent.Kind, parent.LibraryPath, parent.ItemId with
                    | "root", Some path, _ -> $"root:{path}"
                    | "item", _, Some itemId -> $"item:{itemId:D}"
                    | _ -> invalidOp "Library namespace parent evidence is incomplete."

                slot.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                slot.Parameters.AddWithValue("$normalized_path", namespaceValue.NormalizedPath)
                |> ignore

                slot.Parameters.AddWithValue("$parent_key", parentKey)
                |> ignore

                slot.Parameters.AddWithValue("$normalized_name", namespaceValue.Name.ToUpperInvariant())
                |> ignore

                slot.Parameters.AddWithValue("$slot_version", namespaceValue.SlotVersion.ToString("D"))
                |> ignore

                slot.Parameters.AddWithValue("$item_id", change.ItemId.ToString("D"))
                |> ignore

                slot.Parameters.AddWithValue("$cursor", change.Cursor)
                |> ignore

                slot.Parameters.AddWithValue("$catalog_version", change.LibraryCatalogVersion.ToString("D"))
                |> ignore

                slot.Parameters.AddWithValue("$updated_at", now)
                |> ignore

                slot.ExecuteNonQuery() |> ignore

                use terminal = connection.CreateCommand()

                terminal.CommandText <-
                    "UPDATE library_operations SET operation_state = 'terminal', terminal_at = $updated_at, watch_echo_pending = CASE direction WHEN 'remote' THEN 1 ELSE 0 END, updated_at = $updated_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND operation_state IN ('serverAccepted','filesystemPublished');"

                terminal.Parameters.AddWithValue("$updated_at", now)
                |> ignore

                terminal.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                |> ignore

                terminal.Parameters.AddWithValue("$operation_id", change.OperationId.ToString("D"))
                |> ignore

                if terminal.ExecuteNonQuery() <> 1 then
                    invalidOp "Library terminal projection did not consume its exact filesystem publication evidence.")
        }

    /// Advances the cursor only after terminal local truth, exact predecessor, epoch, and catalog remain current.
    let tryAdvanceCursor
        (dbPath: string)
        (repositoryId: Guid)
        (operationId: Guid)
        (libraryCatalogVersion: Guid)
        (cursorEpoch: string)
        (predecessorCursor: string)
        (acceptedCursor: string)
        =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            return
                inTransaction connection (fun () ->
                    let now = timestamp ()
                    use updateState = connection.CreateCommand()

                    updateState.CommandText <-
                        "UPDATE library_repository_state SET applied_cursor = $accepted_cursor, predecessor_cursor = NULL, lifecycle_state = 'current', last_completed_at = $updated_at, updated_at = $updated_at WHERE repository_id = $repository_id AND library_catalog_version = $catalog_version AND cursor_epoch = $cursor_epoch AND applied_cursor = $predecessor_cursor AND predecessor_cursor = $predecessor_cursor AND EXISTS (SELECT 1 FROM library_operations WHERE repository_id = $repository_id AND operation_id = $operation_id AND operation_state IN ('terminal','acknowledged') AND library_catalog_version = $catalog_version AND predecessor_cursor = $predecessor_cursor AND server_cursor = $accepted_cursor);"

                    updateState.Parameters.AddWithValue("$accepted_cursor", acceptedCursor)
                    |> ignore

                    updateState.Parameters.AddWithValue("$updated_at", now)
                    |> ignore

                    updateState.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                    |> ignore

                    updateState.Parameters.AddWithValue("$catalog_version", libraryCatalogVersion.ToString("D"))
                    |> ignore

                    updateState.Parameters.AddWithValue("$cursor_epoch", cursorEpoch)
                    |> ignore

                    updateState.Parameters.AddWithValue("$predecessor_cursor", predecessorCursor)
                    |> ignore

                    updateState.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                    |> ignore

                    if updateState.ExecuteNonQuery() <> 1 then
                        false
                    else
                        use acknowledge = connection.CreateCommand()

                        acknowledge.CommandText <-
                            "UPDATE library_operations SET operation_state = 'acknowledged', updated_at = $updated_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND operation_state = 'terminal';"

                        acknowledge.Parameters.AddWithValue("$updated_at", now)
                        |> ignore

                        acknowledge.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                        |> ignore

                        acknowledge.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                        |> ignore

                        acknowledge.ExecuteNonQuery() |> ignore
                        true)
        }

    /// Consumes only one exact remote-publication echo by operation, item, normalized path, and BLAKE3 identity.
    let tryConsumeWatchEcho (dbPath: string) (repositoryId: Guid) (operationId: Guid) (itemId: Guid) (normalizedPath: string) (blake3Hash: string) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            return
                inTransaction connection (fun () ->
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "UPDATE library_operations SET watch_echo_pending = 0, watch_echo_at = $observed_at, updated_at = $observed_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND item_id = $item_id AND target_path = $target_path AND expected_blake3 = $blake3 AND direction = 'remote' AND operation_state IN ('terminal','acknowledged') AND watch_echo_pending = 1;"

                    command.Parameters.AddWithValue("$observed_at", timestamp ())
                    |> ignore

                    command.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                    |> ignore

                    command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"))
                    |> ignore

                    command.Parameters.AddWithValue("$item_id", itemId.ToString("D"))
                    |> ignore

                    command.Parameters.AddWithValue("$target_path", normalizedPath)
                    |> ignore

                    command.Parameters.AddWithValue("$blake3", blake3Hash)
                    |> ignore

                    command.ExecuteNonQuery() = 1)
        }

    /// Resolves and consumes one unambiguous exact remote Watch echo from durable operation, item, path, and BLAKE3 evidence.
    let tryConsumeWatchEchoForFile (dbPath: string) (repositoryId: Guid) (normalizedPath: string) (blake3Hash: string) =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            return
                inTransaction connection (fun () ->
                    use select = connection.CreateCommand()

                    select.CommandText <-
                        "SELECT operation_id, item_id FROM library_operations WHERE repository_id = $repository_id AND target_path = $target_path AND expected_blake3 = $blake3 AND direction = 'remote' AND operation_state IN ('terminal','acknowledged') AND watch_echo_pending = 1 ORDER BY created_at DESC LIMIT 2;"

                    select.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D"))
                    |> ignore

                    select.Parameters.AddWithValue("$target_path", normalizedPath)
                    |> ignore

                    select.Parameters.AddWithValue("$blake3", blake3Hash)
                    |> ignore

                    use reader = select.ExecuteReader()
                    let matches = ResizeArray<Guid * Guid>()

                    while reader.Read() do
                        matches.Add(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)))

                    reader.Close()

                    if matches.Count <> 1 then
                        false
                    else
                        let operationId, itemId = matches[0]
                        use consume = connection.CreateCommand()

                        consume.CommandText <-
                            "UPDATE library_operations SET watch_echo_pending = 0, watch_echo_at = $observed_at, updated_at = $observed_at WHERE repository_id = $repository_id AND operation_id = $operation_id AND item_id = $item_id AND target_path = $target_path AND expected_blake3 = $blake3 AND direction = 'remote' AND operation_state IN ('terminal','acknowledged') AND watch_echo_pending = 1;"

                        consume.Parameters.AddWithValue("$observed_at", timestamp ()) |> ignore
                        consume.Parameters.AddWithValue("$repository_id", repositoryId.ToString("D")) |> ignore
                        consume.Parameters.AddWithValue("$operation_id", operationId.ToString("D")) |> ignore
                        consume.Parameters.AddWithValue("$item_id", itemId.ToString("D")) |> ignore
                        consume.Parameters.AddWithValue("$target_path", normalizedPath) |> ignore
                        consume.Parameters.AddWithValue("$blake3", blake3Hash) |> ignore
                        consume.ExecuteNonQuery() = 1)
        }
