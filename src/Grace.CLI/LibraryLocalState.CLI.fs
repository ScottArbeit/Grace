namespace Grace.CLI

open System
open Grace.Shared.Utilities
open Grace.Types.Library
open Microsoft.Data.Sqlite

/// Stores Library participation, materialized ancestry, and pending operations in the existing local database.
module internal LibraryLocalState =

    /// Owns this copy's participation and complete bounded catalog; its cursor describes completed local application.
    [<CLIMutable>]
    type RepositoryState =
        {
            RepositoryId: Guid
            WorkingCopyId: Guid
            Catalog: LibraryCatalogDto
            CursorEpoch: string
            AppliedCursor: string
            NextPageToken: string option
            State: string
        }

    /// Retains immutable saved input and the exact request/result needed to resume one Library operation.
    [<CLIMutable>]
    type PendingOperation =
        {
            OperationId: Guid
            Direction: string
            SourcePath: string
            SourceBytes: byte array option
            MaterializedBase: LibraryItemDto option
            OriginatingCreateId: Guid option
            Parent: LibraryParentDto
            Name: string
            ItemKind: string
            RequestJson: string option
            Uploaded: bool
            Accepted: LibraryChangeDto option
            Prepared: bool
            ExpectedCatalogVersion: Guid
            ExpectedCursor: string
            ExpectedAncestry: LibraryItemDto array
            ExpectedTarget: string option
            TargetPath: string
            Terminal: bool
            EchoPending: bool
            CreatedAtTicks: int64
        }

    /// Runs parameterized writes on a Library-owned connection or its current completion transaction.
    let private execute (connection: SqliteConnection) (transaction: SqliteTransaction option) sql parameters =
        use command = connection.CreateCommand()
        command.CommandText <- sql

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        parameters
        |> List.iter (fun (name, value: obj) ->
            command.Parameters.AddWithValue(name, value)
            |> ignore)

        command.ExecuteNonQuery()

    /// Opens a Library connection with FULL durability without changing other local-state connection policies.
    let internal openConnection dbPath =
        let connection = new SqliteConnection($"Data Source={dbPath};Pooling=False;Default Timeout=30")
        connection.Open()

        execute connection None "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=30000;" []
        |> ignore

        connection

    /// Adds exactly three Library tables to the existing database; no older unmerged Library schema is supported.
    let initialize dbPath =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            execute
                connection
                None
                "CREATE TABLE IF NOT EXISTS library_repository_state(repository_id TEXT PRIMARY KEY,working_copy_id TEXT NOT NULL,catalog_json TEXT NOT NULL,cursor_epoch TEXT NOT NULL,applied_cursor TEXT NOT NULL,next_page_token TEXT NULL,lifecycle_state TEXT NOT NULL);
                 CREATE TABLE IF NOT EXISTS library_items(repository_id TEXT NOT NULL,item_id TEXT NOT NULL,item_json TEXT NOT NULL,PRIMARY KEY(repository_id,item_id),FOREIGN KEY(repository_id) REFERENCES library_repository_state(repository_id));
                 CREATE TABLE IF NOT EXISTS library_operations(repository_id TEXT NOT NULL,operation_id TEXT NOT NULL,direction TEXT NOT NULL CHECK(direction IN ('local','remote')),terminal INTEGER NOT NULL CHECK(terminal IN (0,1)),echo_pending INTEGER NOT NULL CHECK(echo_pending IN (0,1)),created_at_ticks INTEGER NOT NULL,operation_json TEXT NOT NULL,PRIMARY KEY(repository_id,operation_id),FOREIGN KEY(repository_id) REFERENCES library_repository_state(repository_id));
                 CREATE INDEX IF NOT EXISTS ix_library_operations_pending ON library_operations(repository_id,terminal,created_at_ticks);"
                []
            |> ignore
        }

    /// Reads participation from its sole persisted source on the supplied connection.
    let private readRepositoryWith (connection: SqliteConnection) transaction (repositoryId: Guid) =
        use command = connection.CreateCommand()

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        command.CommandText <-
            "SELECT working_copy_id,catalog_json,cursor_epoch,applied_cursor,lifecycle_state,next_page_token FROM library_repository_state WHERE repository_id=$repository;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()

        if reader.Read() then
            Some
                {
                    RepositoryId = repositoryId
                    WorkingCopyId = Guid.Parse(reader.GetString(0))
                    Catalog = deserialize<LibraryCatalogDto> (reader.GetString(1))
                    CursorEpoch = reader.GetString(2)
                    AppliedCursor = reader.GetString(3)
                    State = reader.GetString(4)
                    NextPageToken = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
                }
        else
            None

    /// Reads the current local participation/catalog/cursor without consulting received server metadata.
    let readRepository dbPath repositoryId =
        use connection = openConnection dbPath
        readRepositoryWith connection None repositoryId

    /// Enables a previously disabled copy at a verified empty immutable baseline.
    let enable dbPath (state: RepositoryState) =
        use connection = openConnection dbPath

        execute
            connection
            None
            "INSERT INTO library_repository_state(repository_id,working_copy_id,catalog_json,cursor_epoch,applied_cursor,next_page_token,lifecycle_state) VALUES($repository,$copy,$catalog,$epoch,$cursor,$next,$state);"
            [
                "$repository", box (state.RepositoryId.ToString("D"))
                "$copy", box (state.WorkingCopyId.ToString("D"))
                "$catalog", box (serialize state.Catalog)
                "$epoch", box state.CursorEpoch
                "$cursor", box state.AppliedCursor
                "$next",
                (state.NextPageToken
                 |> Option.map box
                 |> Option.defaultValue (box DBNull.Value))
                "$state", box state.State
            ]
        |> ignore

    /// Loads materialized items only; incoming accepted responses do not update this table.
    let readItems dbPath (repositoryId: Guid) =
        use connection = openConnection dbPath
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT item_json FROM library_items WHERE repository_id=$repository ORDER BY item_id;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()
        let items = ResizeArray<LibraryItemDto>()

        while reader.Read() do
            items.Add(deserialize<LibraryItemDto> (reader.GetString(0)))

        items.ToArray()

    /// Loads operation facts on the caller's connection, including the active completion transaction when supplied.
    let private readOperationsWith (connection: SqliteConnection) transaction (repositoryId: Guid) =
        use command = connection.CreateCommand()

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        command.CommandText <- "SELECT operation_json FROM library_operations WHERE repository_id=$repository ORDER BY created_at_ticks,operation_id;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()
        let operations = ResizeArray<PendingOperation>()

        while reader.Read() do
            operations.Add(deserialize<PendingOperation> (reader.GetString(0)))

        operations.ToArray()

    /// Loads operations in capture order, preserving separate saved sources even when content repeats.
    let readOperations dbPath repositoryId =
        use connection = openConnection dbPath
        readOperationsWith connection None repositoryId

    /// Describes the observable result of one completed file, directory or deletion.
    let private echoFingerprint (change: LibraryChangeDto) =
        if change.Item.Tombstone.IsSome then
            None
        elif change.Item.ItemKind = ItemKind.Directory then
            Some "directory"
        else
            change.Item.Content
            |> Option.map (fun content -> $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}")

    /// Inserts immutable saved input before upload or incoming filesystem effects.
    let insertOperation dbPath (repositoryId: Guid) (operation: PendingOperation) =
        use connection = openConnection dbPath

        execute
            connection
            None
            "INSERT INTO library_operations(repository_id,operation_id,direction,terminal,echo_pending,created_at_ticks,operation_json) VALUES($repository,$operation,$direction,0,0,$created,$json);"
            [
                "$repository", box (repositoryId.ToString("D"))
                "$operation", box (operation.OperationId.ToString("D"))
                "$direction", box operation.Direction
                "$created", box operation.CreatedAtTicks
                "$json", box (serialize operation)
            ]
        |> ignore

    /// Updates a pending operation only if its exact previously read input remains current.
    let updateOperation dbPath (repositoryId: Guid) (expected: PendingOperation) (updated: PendingOperation) =
        if expected.OperationId <> updated.OperationId
           || expected.Direction <> updated.Direction
           || expected.SourcePath <> updated.SourcePath
           || expected.Parent <> updated.Parent
           || expected.Name <> updated.Name
           || expected.ItemKind <> updated.ItemKind
           || expected.CreatedAtTicks <> updated.CreatedAtTicks
           || updated.Terminal
           || (expected.Accepted.IsSome
               && expected.Accepted <> updated.Accepted)
           || (expected.Uploaded && not updated.Uploaded)
           || expected.SourceBytes <> updated.SourceBytes
           || expected.MaterializedBase
              <> updated.MaterializedBase
           || expected.OriginatingCreateId
              <> updated.OriginatingCreateId
           || (expected.RequestJson.IsSome
               && expected.RequestJson <> updated.RequestJson) then
            invalidOp "Library saved input or submitted request cannot be changed."

        use connection = openConnection dbPath

        let changed =
            execute
                connection
                None
                "UPDATE library_operations SET operation_json=$json,echo_pending=$echo WHERE repository_id=$repository AND operation_id=$operation AND operation_json=$expected AND terminal=0;"
                [
                    "$repository", box (repositoryId.ToString("D"))
                    "$operation", box (expected.OperationId.ToString("D"))
                    "$expected", box (serialize expected)
                    "$json", box (serialize updated)
                    "$echo", box (if updated.EchoPending then 1 else 0)
                ]

        if changed <> 1 then
            invalidOp "Library pending operation changed before persistence."

    /// Retains a completed page's continuation, including an empty visibility gap, without advancing applied progress.
    let recordPage dbPath (expected: RepositoryState) nextPageToken =
        use connection = openConnection dbPath

        let changed =
            execute
                connection
                None
                "UPDATE library_repository_state SET next_page_token=$next,lifecycle_state='catchingUp' WHERE repository_id=$repository AND applied_cursor=$cursor AND cursor_epoch=$epoch AND catalog_json=$catalog;"
                [
                    "$next",
                    (nextPageToken
                     |> Option.map box
                     |> Option.defaultValue (box DBNull.Value))
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$cursor", box expected.AppliedCursor
                    "$epoch", box expected.CursorEpoch
                    "$catalog", box (serialize expected.Catalog)
                ]

        if changed <> 1 then
            invalidOp "Library page authority changed before continuation persistence."

    /// Sets a derived synchronization status without advancing applied progress.
    let setState dbPath (expected: RepositoryState) state =
        use connection = openConnection dbPath

        let changed =
            execute
                connection
                None
                "UPDATE library_repository_state SET lifecycle_state=$state WHERE repository_id=$repository AND applied_cursor=$cursor AND cursor_epoch=$epoch AND catalog_json=$catalog;"
                [
                    "$state", box state
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$cursor", box expected.AppliedCursor
                    "$epoch", box expected.CursorEpoch
                    "$catalog", box (serialize expected.Catalog)
                ]

        if changed <> 1 then
            invalidOp "Library catalog or predecessor changed before status update."

    /// Commits verified item, terminal operation, and applied cursor together under exact repository/operation guards.
    let completeWith afterItem dbPath (expected: RepositoryState) (operation: PendingOperation) =
        let change =
            operation.Accepted
            |> Option.defaultWith (fun () -> invalidOp "Library completion requires an accepted result.")

        if not operation.Prepared
           || operation.Terminal
           || change.OperationId <> operation.OperationId
           || operation.ExpectedCursor <> expected.AppliedCursor
           || operation.ExpectedCatalogVersion
              <> expected.Catalog.Version
           || change.LibraryCatalogVersion
              <> expected.Catalog.Version then
            invalidOp "Library completion catalog or predecessor does not match preparation."

        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()
        let current = readRepositoryWith connection (Some transaction) expected.RepositoryId

        if current <> Some expected then
            invalidOp "Library catalog or cursor changed before completion."

        execute
            connection
            (Some transaction)
            "INSERT INTO library_items(repository_id,item_id,item_json) VALUES($repository,$item,$json) ON CONFLICT(repository_id,item_id) DO UPDATE SET item_json=excluded.item_json;"
            [
                "$repository", box (expected.RepositoryId.ToString("D"))
                "$item", box (change.Item.ItemId.ToString("D"))
                "$json", box (serialize change.Item)
            ]
        |> ignore

        afterItem connection transaction

        // This completion follows the verified filesystem effects. Older echoes for replaced placements
        // cannot become observable again merely because Watch coalesced several callbacks into one path.
        let priorEchoes =
            readOperationsWith connection (Some transaction) expected.RepositoryId
            |> Array.filter (fun prior ->
                prior.Terminal
                && prior.EchoPending
                && (String.Equals(prior.TargetPath, operation.TargetPath, StringComparison.OrdinalIgnoreCase)
                    || prior.Accepted.Value.Item.ItemId = change.Item.ItemId
                    || (change.Item.ItemKind = ItemKind.Directory
                        && not (String.Equals(operation.SourcePath, operation.TargetPath, StringComparison.OrdinalIgnoreCase))
                        && prior.TargetPath.StartsWith(operation.SourcePath + "/", StringComparison.OrdinalIgnoreCase))))

        let observablePriorEcho =
            priorEchoes
            |> Array.exists (fun prior ->
                String.Equals(prior.TargetPath, operation.TargetPath, StringComparison.OrdinalIgnoreCase)
                && echoFingerprint prior.Accepted.Value = echoFingerprint change)

        priorEchoes
        |> Array.iter (fun prior ->
            let retired = { prior with EchoPending = false }

            let changed =
                execute
                    connection
                    (Some transaction)
                    "UPDATE library_operations SET echo_pending=0,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=1 AND echo_pending=1 AND operation_json=$expected;"
                    [
                        "$repository", box (expected.RepositoryId.ToString("D"))
                        "$operation", box (prior.OperationId.ToString("D"))
                        "$json", box (serialize retired)
                        "$expected", box (serialize prior)
                    ]

            if changed <> 1 then
                invalidOp "Library publication echo changed before completion.")

        // A completion that needs no rewrite can inherit an earlier matching publication's unobserved echo.
        let terminal = { operation with Terminal = true; EchoPending = operation.EchoPending || observablePriorEcho }

        let completed =
            execute
                connection
                (Some transaction)
                "UPDATE library_operations SET terminal=1,echo_pending=$echo,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=0 AND operation_json=$expected;"
                [
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$operation", box (operation.OperationId.ToString("D"))
                    "$echo", box (if terminal.EchoPending then 1 else 0)
                    "$json", box (serialize terminal)
                    "$expected", box (serialize operation)
                ]

        if completed <> 1 then
            invalidOp "Library exact pending operation changed before completion."

        // Once any item commits, restart uses its applied cursor rather than replaying the previous page.
        // A fully applied page subsequently checkpoints its next token, including an empty HasMore page.
        execute
            connection
            (Some transaction)
            "UPDATE library_repository_state SET applied_cursor=$cursor,next_page_token=NULL,lifecycle_state='catchingUp' WHERE repository_id=$repository;"
            [
                "$cursor", box change.Item.LastChangeCursor
                "$repository", box (expected.RepositoryId.ToString("D"))
            ]
        |> ignore

        transaction.Commit()

    /// Completes one verified application without injected transaction failures.
    let complete dbPath state operation = completeWith (fun _ _ -> ()) dbPath state operation

    /// Consumes the observable completed publication once; completion has already retired superseded echoes.
    let consumeEcho dbPath repositoryId relativePath fingerprint =
        let matches =
            readOperations dbPath repositoryId
            |> Array.filter (fun operation ->
                operation.Terminal
                && operation.EchoPending
                && String.Equals(operation.TargetPath, relativePath, StringComparison.OrdinalIgnoreCase)
                && operation.Accepted
                   |> Option.exists (fun change -> echoFingerprint change = fingerprint))

        match matches with
        | [| operation |] ->
            use connection = openConnection dbPath

            let updated = { operation with EchoPending = false }

            execute
                connection
                None
                "UPDATE library_operations SET echo_pending=0,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=1 AND echo_pending=1 AND operation_json=$expected;"
                [
                    "$repository", box (repositoryId.ToString("D"))
                    "$operation", box (operation.OperationId.ToString("D"))
                    "$json", box (serialize updated)
                    "$expected", box (serialize operation)
                ] = 1
        | _ -> false

    /// Bounds classified terminal history while retaining the applied tip, unclassified echoes and every pending originating-create dependency.
    let pruneClassified dbPath repositoryId retainedCount =
        let current = readRepository dbPath repositoryId |> Option.get
        let operations = readOperations dbPath repositoryId

        let origins =
            operations
            |> Array.filter (fun operation -> not operation.Terminal)
            |> Array.choose (fun operation -> operation.OriginatingCreateId)
            |> Set.ofArray

        let terminal =
            operations
            |> Array.filter (fun operation -> operation.Terminal)
            |> Array.sortByDescending (fun operation -> operation.CreatedAtTicks)

        use connection = openConnection dbPath

        terminal
        |> Array.skip (min retainedCount terminal.Length)
        |> Array.filter (fun operation ->
            not operation.EchoPending
            && not (origins.Contains operation.OperationId)
            && operation.Accepted.Value.Item.LastChangeCursor
               <> current.AppliedCursor)
        |> Array.iter (fun operation ->
            execute
                connection
                None
                "DELETE FROM library_operations WHERE repository_id=$repository AND operation_id=$operation AND terminal=1 AND echo_pending=0 AND operation_json=$expected;"
                [
                    "$repository", box (repositoryId.ToString("D"))
                    "$operation", box (operation.OperationId.ToString("D"))
                    "$expected", box (serialize operation)
                ]
            |> ignore)
