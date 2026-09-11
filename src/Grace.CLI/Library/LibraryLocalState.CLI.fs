namespace Grace.CLI

open System
open Grace.Shared.Utilities
open Grace.Types.Library
open Microsoft.Data.Sqlite
open Grace.CLI.LibraryOperation

/// Stores Library participation, materialized ancestry, and pending operations in the existing local database.
module internal LibraryLocalState =

    /// Retains one immutable onboarding selection through metadata acquisition, installation and its first catch-up.
    [<CLIMutable>]
    type BaselineSelection = { BootstrapId: Guid; BoundaryCursor: string; MetadataComplete: bool; Applied: bool }

    /// Owns this copy's participation and complete bounded catalog; its cursor describes completed local application.
    [<CLIMutable>]
    type RepositoryState =
        {
            RepositoryId: Guid
            WorkingCopyId: Guid
            Catalog: LibraryCatalogDto
            CursorEpoch: LibraryCursorEpoch
            AppliedCursor: string
            NextPageToken: string option
            State: string
            Paused: bool
            Baseline: BaselineSelection option
        }

    /// Stores the typed operation model directly in the existing operation JSON column.
    type PendingOperation = LibraryOperation.Operation

    /// Derives every indexed operation column from the same typed JSON source for guarded writes.
    let private operationColumns (operation: PendingOperation) =
        [
            "$direction", box operation.Direction
            "$terminal", box (if operation.Terminal then 1 else 0)
            "$echo", box (if operation.EchoPending then 1 else 0)
            "$created", box operation.CreatedAtTicks
            "$json", box (serialize operation)
        ]

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
                "CREATE TABLE IF NOT EXISTS library_repository_state(repository_id TEXT PRIMARY KEY,working_copy_id TEXT NOT NULL,catalog_json TEXT NOT NULL,cursor_epoch TEXT NOT NULL,applied_cursor TEXT NOT NULL,next_page_token TEXT NULL,lifecycle_state TEXT NOT NULL,paused INTEGER NOT NULL CHECK(paused IN (0,1)),baseline_json TEXT NULL);
                 CREATE TABLE IF NOT EXISTS library_items(repository_id TEXT NOT NULL,item_id TEXT NOT NULL,item_json TEXT NOT NULL,PRIMARY KEY(repository_id,item_id),FOREIGN KEY(repository_id) REFERENCES library_repository_state(repository_id));
                 CREATE TABLE IF NOT EXISTS library_operations(repository_id TEXT NOT NULL,operation_id TEXT NOT NULL,direction TEXT NOT NULL CHECK(direction IN ('local','remote','baseline')),terminal INTEGER NOT NULL CHECK(terminal IN (0,1)),echo_pending INTEGER NOT NULL CHECK(echo_pending IN (0,1)),created_at_ticks INTEGER NOT NULL,operation_json TEXT NOT NULL,PRIMARY KEY(repository_id,operation_id),FOREIGN KEY(repository_id) REFERENCES library_repository_state(repository_id));
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
            "SELECT working_copy_id,catalog_json,cursor_epoch,applied_cursor,lifecycle_state,next_page_token,baseline_json,paused FROM library_repository_state WHERE repository_id=$repository;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()

        if reader.Read() then
            Some
                {
                    RepositoryId = repositoryId
                    WorkingCopyId = Guid.Parse(reader.GetString(0))
                    Catalog = deserialize<LibraryCatalogDto> (reader.GetString(1))
                    CursorEpoch = LibraryCursorEpoch.parse (reader.GetString(2))
                    AppliedCursor = reader.GetString(3)
                    State = reader.GetString(4)
                    Paused = reader.GetInt64(7) <> 0L
                    NextPageToken = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
                    Baseline =
                        if reader.IsDBNull(6) then
                            None
                        else
                            Some(deserialize<BaselineSelection> (reader.GetString(6)))
                }
        else
            None

    /// Reads the current local participation/catalog/cursor without consulting received server metadata.
    let readRepository dbPath repositoryId =
        use connection = openConnection dbPath
        readRepositoryWith connection None repositoryId

    /// Inserts participation on the caller's transaction so baseline selection and its first page commit together.
    let private enableWith connection transaction (state: RepositoryState) =
        execute
            connection
            transaction
            "INSERT INTO library_repository_state(repository_id,working_copy_id,catalog_json,cursor_epoch,applied_cursor,next_page_token,lifecycle_state,paused,baseline_json) VALUES($repository,$copy,$catalog,$epoch,$cursor,$next,$state,$paused,$baseline);"
            [
                "$repository", box (state.RepositoryId.ToString("D"))
                "$copy", box (state.WorkingCopyId.ToString("D"))
                "$paused", box (if state.Paused then 1 else 0)
                "$catalog", box (serialize state.Catalog)
                "$epoch", box (LibraryCursorEpoch.toString state.CursorEpoch)
                "$cursor", box state.AppliedCursor
                "$next",
                (state.NextPageToken
                 |> Option.map box
                 |> Option.defaultValue (box DBNull.Value))
                "$state", box state.State
                "$baseline",
                (state.Baseline
                 |> Option.map (serialize >> box)
                 |> Option.defaultValue (box DBNull.Value))
            ]
        |> ignore

    /// Inserts a participation record for an already verified starting boundary.
    let enable dbPath state =
        use connection = openConnection dbPath
        enableWith connection None state

    /// Changes only pause after the caller rereads completed participation under the root lease.
    let setPaused dbPath (expected: RepositoryState) paused =
        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected then
            invalidOp "Library participation changed before pause setting commit."

        execute
            connection
            (Some transaction)
            "UPDATE library_repository_state SET paused=$paused WHERE repository_id=$repository;"
            [
                "$repository", box (expected.RepositoryId.ToString("D"))
                "$paused", box (if paused then 1 else 0)
            ]
        |> ignore

        transaction.Commit()

    /// Loads materialized items only; incoming accepted responses do not update this table.
    let private readItemsWith (connection: SqliteConnection) transaction (repositoryId: Guid) =
        use command = connection.CreateCommand()

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        command.CommandText <- "SELECT item_json FROM library_items WHERE repository_id=$repository ORDER BY item_id;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()
        let items = ResizeArray<LibraryItemDto>()

        while reader.Read() do
            items.Add(deserialize<LibraryItemDto> (reader.GetString(0)))

        items.ToArray()

    /// Reads completed materialization without interpreting pending accepted changes as installed items.
    let readItems dbPath repositoryId =
        use connection = openConnection dbPath
        readItemsWith connection None repositoryId

    /// Loads operation facts on the caller's connection, including the active completion transaction when supplied.
    let private readOperationsWith (connection: SqliteConnection) transaction (repositoryId: Guid) =
        use command = connection.CreateCommand()

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        command.CommandText <-
            "SELECT operation_json,direction,terminal,echo_pending,created_at_ticks FROM library_operations WHERE repository_id=$repository ORDER BY created_at_ticks,operation_id;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()
        let operations = ResizeArray<PendingOperation>()

        while reader.Read() do
            let operation = deserialize<PendingOperation> (reader.GetString(0))

            if reader.GetString(1) <> operation.Direction
               || (reader.GetInt64(2) <> 0L) <> operation.Terminal
               || (reader.GetInt64(3) <> 0L)
                  <> operation.EchoPending
               || reader.GetInt64(4) <> operation.CreatedAtTicks then
                invalidOp "Library operation routing columns disagree with its typed state."

            operations.Add operation

        operations.ToArray()

    /// Loads operations in capture order, preserving separate saved sources even when content repeats.
    let readOperations dbPath repositoryId =
        use connection = openConnection dbPath
        readOperationsWith connection None repositoryId

    /// Changes only the selected catalog after comparing every local input inside the SQLite transaction.
    let selectCatalogWith afterWrite dbPath (expected: RepositoryState) items operations (selected: LibraryCatalogDto) =
        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if expected.Paused
           || expected.Baseline
              |> Option.exists (fun baseline -> not baseline.Applied)
           || selected.RepositoryId <> expected.RepositoryId
           || expected.Catalog.Libraries
              |> Array.exists (fun root -> not (selected.Libraries |> Array.contains root))
           || readRepositoryWith connection (Some transaction) expected.RepositoryId
              <> Some expected
           || readItemsWith connection (Some transaction) expected.RepositoryId
              <> items
           || readOperationsWith connection (Some transaction) expected.RepositoryId
              <> operations then
            invalidOp "Library participation, materialized items or operations changed before catalog selection."

        execute
            connection
            (Some transaction)
            "UPDATE library_repository_state SET catalog_json=$catalog WHERE repository_id=$repository;"
            [
                "$catalog", box (serialize selected)
                "$repository", box (expected.RepositoryId.ToString("D"))
            ]
        |> ignore

        afterWrite connection transaction
        transaction.Commit()

    /// Replaces onboarding progress under a transaction whose caller has checked the exact previous repository record.
    let private writeBaselineState connection transaction (state: RepositoryState) =
        execute
            connection
            (Some transaction)
            "UPDATE library_repository_state SET cursor_epoch=$epoch,applied_cursor=$cursor,next_page_token=$next,lifecycle_state=$state,baseline_json=$baseline WHERE repository_id=$repository;"
            [
                "$repository", box (state.RepositoryId.ToString("D"))
                "$epoch", box (LibraryCursorEpoch.toString state.CursorEpoch)
                "$cursor", box state.AppliedCursor
                "$next",
                (state.NextPageToken
                 |> Option.map box
                 |> Option.defaultValue (box DBNull.Value))
                "$state", box state.State
                "$baseline",
                (state.Baseline
                 |> Option.map (serialize >> box)
                 |> Option.defaultValue (box DBNull.Value))
            ]
        |> ignore

    /// Saves selected metadata and its continuation atomically; an expired incomplete selection can be replaced before effects.
    let saveBaselinePageWith afterItems dbPath (expected: RepositoryState option) (selected: RepositoryState) (page: LibraryBootstrapPageDto) restart =
        let baseline =
            selected.Baseline
            |> Option.defaultWith (fun () -> invalidOp "Baseline selection is missing.")

        if baseline.BootstrapId <> page.BootstrapId
           || baseline.BoundaryCursor <> page.BoundaryCursor
           || selected.Catalog <> page.LibraryCatalog
           || selected.CursorEpoch <> page.CursorEpoch
           || baseline.Applied
           || baseline.MetadataComplete then
            invalidOp "Baseline page does not match the selected metadata."

        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if readRepositoryWith connection (Some transaction) selected.RepositoryId
           <> expected then
            invalidOp "Baseline selection changed before page persistence."

        match expected with
        | None -> enableWith connection (Some transaction) selected
        | Some previous ->
            let prior =
                previous.Baseline
                |> Option.defaultWith (fun () -> invalidOp "Participating copies cannot acquire another baseline.")

            if prior.MetadataComplete
               || prior.Applied
               || previous.AppliedCursor <> ""
               || previous.Catalog <> selected.Catalog then
                invalidOp "Only an uninstalled incomplete baseline can continue or restart acquisition."

            if not restart && (previous <> selected) then
                invalidOp "Continuation cannot replace its selection."

            if restart then
                let operations = readOperationsWith connection (Some transaction) selected.RepositoryId

                if operations
                   |> Array.exists (fun op ->
                       op.Direction <> "baseline"
                       || op.Prepared
                       || op.Terminal) then
                    invalidOp "Baseline reset cannot discard installed or prepared work."

                execute
                    connection
                    (Some transaction)
                    "DELETE FROM library_operations WHERE repository_id=$repository;"
                    [
                        "$repository", box (selected.RepositoryId.ToString("D"))
                    ]
                |> ignore

        page.Items
        |> Array.iter (fun item ->
            let op =
                {
                    OperationId = Guid.NewGuid()
                    CatalogVersion = selected.Catalog.Version
                    Work =
                        if item.Tombstone.IsSome then
                            OperationWork.BaselineTombstone(item, TombstoneInstallation.Selected)
                        else
                            OperationWork.BaselineLive(item, BaselineInstallation.Selected)
                    CreatedAtTicks = DateTime.UtcNow.Ticks
                }

            execute
                connection
                (Some transaction)
                "INSERT INTO library_operations(repository_id,operation_id,direction,terminal,echo_pending,created_at_ticks,operation_json) VALUES($repository,$operation,$direction,$terminal,$echo,$created,$json);"
                [
                    "$repository", box (selected.RepositoryId.ToString("D"))
                    "$operation", box (op.OperationId.ToString("D"))
                    "$direction", box op.Direction
                    "$terminal", box (if op.Terminal then 1 else 0)
                    "$echo", box (if op.EchoPending then 1 else 0)
                    "$created", box op.CreatedAtTicks
                    "$json", box (serialize op)
                ]
            |> ignore)

        afterItems connection transaction

        writeBaselineState
            connection
            transaction
            { selected with
                NextPageToken = page.NextPageToken
                State = if page.NextPageToken.IsSome then "acquiringBaseline" else "installingBaseline"
                Baseline = Some { baseline with MetadataComplete = page.NextPageToken.IsNone }
            }

        transaction.Commit()

    /// Persists one immutable page without injected SQLite interruption.
    let saveBaselinePage dbPath expected selected page restart = saveBaselinePageWith (fun _ _ -> ()) dbPath expected selected page restart

    /// Commits one verified baseline effect without advancing the accepted-change cursor.
    let completeBaselineItemWith afterItem dbPath (expected: RepositoryState) (operation: PendingOperation) =
        let baseline = expected.Baseline |> Option.get
        let item = operation.BaselineItem |> Option.get

        if not baseline.MetadataComplete
           || baseline.Applied
           || expected.AppliedCursor <> ""
           || operation.Direction <> "baseline"
           || (not operation.Prepared && item.Tombstone.IsNone)
           || operation.Terminal
           || operation.Accepted.IsSome
           || operation.ExpectedCatalogVersion
              <> expected.Catalog.Version
           || operation.ExpectedCursor <> expected.AppliedCursor then
            invalidOp "Baseline item is not prepared for this selection."

        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected then
            invalidOp "Baseline changed before item completion."

        if item.Tombstone.IsNone then
            execute
                connection
                (Some transaction)
                "INSERT INTO library_items(repository_id,item_id,item_json) VALUES($repository,$item,$json);"
                [
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$item", box (item.ItemId.ToString("D"))
                    "$json", box (serialize item)
                ]
            |> ignore

        afterItem connection transaction
        let terminal = LibraryOperation.complete operation

        let changed =
            execute
                connection
                (Some transaction)
                "UPDATE library_operations SET direction=$direction,terminal=$terminal,echo_pending=$echo,created_at_ticks=$created,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=0 AND operation_json=$expected;"
                (operationColumns terminal
                 @ [
                     "$repository", box (expected.RepositoryId.ToString("D"))
                     "$operation", box (operation.OperationId.ToString("D"))
                     "$expected", box (serialize operation)
                 ])

        if changed <> 1 then
            invalidOp "Baseline exact operation changed before item completion."

        transaction.Commit()

    /// Completes a baseline item after the installer verifies its filesystem result.
    let completeBaselineItem dbPath expected operation = completeBaselineItemWith (fun _ _ -> ()) dbPath expected operation

    /// Makes the selected boundary applied only after the installer rechecks every completed item and the catalog.
    let completeBaselineWith beforeCommit dbPath (expected: RepositoryState) (operations: PendingOperation array) =
        let baseline = expected.Baseline |> Option.get

        if not baseline.MetadataComplete
           || baseline.Applied
           || expected.AppliedCursor <> ""
           || operations
              |> Array.exists (fun op -> op.Direction <> "baseline" || not op.Terminal) then
            invalidOp "Baseline still has incomplete work."

        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected
           || readOperationsWith connection (Some transaction) expected.RepositoryId
              <> operations then
            invalidOp "Baseline work changed before boundary completion."

        writeBaselineState
            connection
            transaction
            { expected with
                AppliedCursor = baseline.BoundaryCursor
                NextPageToken = None
                State = "catchingUp"
                Baseline = Some { baseline with Applied = true }
            }

        beforeCommit connection transaction
        transaction.Commit()

    /// Commits the verified baseline boundary without injected interruption.
    let completeBaseline dbPath expected operations = completeBaselineWith (fun _ _ -> ()) dbPath expected operations

    /// Releases initial capture only after a complete genuine accepted-change pull following baseline installation.
    let finishOnboarding dbPath (expected: RepositoryState) =
        if
            not
                (
                    expected.Baseline
                    |> Option.exists (fun value -> value.Applied)
                )
        then
            invalidOp "Baseline boundary has not been applied."

        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected then
            invalidOp "Onboarding progress changed."

        writeBaselineState connection transaction { expected with Baseline = None; State = "current" }
        transaction.Commit()

    /// Describes the observable result of one completed file, directory or deletion.
    let private echoFingerprint (item: LibraryItemDto) =
        if item.Tombstone.IsSome then
            None
        elif item.ItemKind = ItemKind.Directory then
            Some "directory"
        else
            item.Content
            |> Option.map (fun content -> $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}")

    /// Selects actual result metadata without representing baseline work as a server-accepted change.
    let operationItem (operation: PendingOperation) =
        operation.BaselineItem
        |> Option.orElseWith (fun () ->
            operation.Accepted
            |> Option.map (fun change -> change.Item))

    /// Inserts immutable saved input before upload or incoming filesystem effects.
    let insertOperation dbPath (repositoryId: Guid) (operation: PendingOperation) =
        use connection = openConnection dbPath

        execute
            connection
            None
            "INSERT INTO library_operations(repository_id,operation_id,direction,terminal,echo_pending,created_at_ticks,operation_json) VALUES($repository,$operation,$direction,$terminal,$echo,$created,$json);"
            [
                "$repository", box (repositoryId.ToString("D"))
                "$operation", box (operation.OperationId.ToString("D"))
                "$direction", box operation.Direction
                "$terminal", box (if operation.Terminal then 1 else 0)
                "$echo", box (if operation.EchoPending then 1 else 0)
                "$created", box operation.CreatedAtTicks
                "$json", box (serialize operation)
            ]
        |> ignore

    /// Updates a pending operation only if its exact previously read input remains current.
    let updateOperation dbPath (repositoryId: Guid) (expected: PendingOperation) (updated: PendingOperation) =
        if expected.OperationId <> updated.OperationId
           || not (LibraryOperation.sameIntent expected updated)
           || (expected.Receipt.IsSome
               && expected.Receipt <> updated.Receipt)
           || expected.CatalogVersion <> updated.CatalogVersion
           || expected.CreatedAtTicks <> updated.CreatedAtTicks
           || updated.Terminal
           || (expected.Accepted.IsSome
               && expected.Accepted <> updated.Accepted)
           || (expected.Uploaded && not updated.Uploaded)
           || (expected.Prepared && not updated.Prepared)
           || (LibraryOperation.checkpoint expected
               |> Option.exists (fun before ->
                   LibraryOperation.checkpoint updated
                   |> Option.forall (fun after ->
                       before.ExpectedCursor <> after.ExpectedCursor
                       || before.ExpectedAncestry <> after.ExpectedAncestry
                       || before.SourcePath <> after.SourcePath
                       || before.TargetPath <> after.TargetPath)))
           || (expected.RequestJson.IsSome
               && expected.RequestJson <> updated.RequestJson) then
            invalidOp "Library saved input or submitted request cannot be changed."

        use connection = openConnection dbPath

        let changed =
            execute
                connection
                None
                "UPDATE library_operations SET operation_json=$json,direction=$direction,terminal=$terminal,echo_pending=$echo,created_at_ticks=$created WHERE repository_id=$repository AND operation_id=$operation AND operation_json=$expected AND terminal=0;"
                [
                    "$repository", box (repositoryId.ToString("D"))
                    "$operation", box (expected.OperationId.ToString("D"))
                    "$expected", box (serialize expected)
                    "$json", box (serialize updated)
                    "$direction", box updated.Direction
                    "$terminal", box (if updated.Terminal then 1 else 0)
                    "$created", box updated.CreatedAtTicks
                    "$echo", box (if updated.EchoPending then 1 else 0)
                ]

        if changed <> 1 then
            invalidOp "Library pending operation changed before persistence."

    /// Retires only an unprepared namespace intent with an exact definitive rejection, preserving its receipt and all materialized state.
    let retireRejectedRename dbPath (repositoryId: Guid) (operation: PendingOperation) =
        if
            not operation.Rename
            || operation.Direction <> "local"
            || operation.Prepared
            || operation.Terminal
            || operation.SourceObject.IsSome
            || operation.Uploaded
            || operation.Accepted.IsSome
            || operation.EchoPending
            || operation.RequestJson.IsNone
            || not
                (
                    operation.Receipt
                    |> Option.exists (fun receipt ->
                        receipt.OperationId = operation.OperationId
                        && receipt.Outcome = "rejected"
                        && receipt.Change.IsNone)
                )
        then
            invalidOp "Only a definitively rejected unprepared Library rename can retire."

        use connection = openConnection dbPath
        let terminal = LibraryOperation.retireRename operation

        let changed =
            execute
                connection
                None
                "UPDATE library_operations SET direction=$direction,terminal=$terminal,echo_pending=$echo,created_at_ticks=$created,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=0 AND operation_json=$expected;"
                (operationColumns terminal
                 @ [
                     "$repository", box (repositoryId.ToString("D"))
                     "$operation", box (operation.OperationId.ToString("D"))
                     "$expected", box (serialize operation)
                 ])

        if changed <> 1 then
            invalidOp "Library rejected rename changed before retirement."

    /// Retains a completed page's continuation, including an empty visibility gap, without advancing applied progress.
    let recordPage dbPath (expected: RepositoryState) nextPageToken =
        use connection = openConnection dbPath

        let changed =
            execute
                connection
                None
                "UPDATE library_repository_state SET next_page_token=$next,lifecycle_state='catchingUp' WHERE repository_id=$repository AND applied_cursor=$cursor AND cursor_epoch=$epoch AND catalog_json=$catalog AND paused=$paused;"
                [
                    "$paused", box (if expected.Paused then 1 else 0)
                    "$next",
                    (nextPageToken
                     |> Option.map box
                     |> Option.defaultValue (box DBNull.Value))
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$cursor", box expected.AppliedCursor
                    "$epoch", box (LibraryCursorEpoch.toString expected.CursorEpoch)
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
                "UPDATE library_repository_state SET lifecycle_state=$state WHERE repository_id=$repository AND applied_cursor=$cursor AND cursor_epoch=$epoch AND catalog_json=$catalog AND paused=$paused;"
                [
                    "$state", box state
                    "$paused", box (if expected.Paused then 1 else 0)
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$cursor", box expected.AppliedCursor
                    "$epoch", box (LibraryCursorEpoch.toString expected.CursorEpoch)
                    "$catalog", box (serialize expected.Catalog)
                ]

        if changed <> 1 then
            invalidOp "Library catalog or predecessor changed before status update."

    /// Commits verified item, terminal operation, and applied cursor together under exact repository/operation guards.
    let completeWith afterItem dbPath (expected: RepositoryState) (operation: PendingOperation) =
        let change =
            operation.Accepted
            |> Option.defaultWith (fun () -> invalidOp "Library completion requires an accepted result.")

        if
            not operation.Prepared
            || operation.Terminal
            || change.OperationId <> operation.OperationId
            || operation.ExpectedCursor <> expected.AppliedCursor
            || not (Grace.Shared.Validation.Library.configurationOwnsPath expected.Catalog operation.TargetPath)
        then
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
                    || (operationItem prior
                        |> Option.exists (fun item -> item.ItemId = change.Item.ItemId))
                    || (change.Item.ItemKind = ItemKind.Directory
                        && not (String.Equals(operation.SourcePath, operation.TargetPath, StringComparison.OrdinalIgnoreCase))
                        && prior.TargetPath.StartsWith(operation.SourcePath + "/", StringComparison.OrdinalIgnoreCase))))

        let observablePriorEcho =
            priorEchoes
            |> Array.exists (fun prior ->
                String.Equals(prior.TargetPath, operation.TargetPath, StringComparison.OrdinalIgnoreCase)
                && (operationItem prior |> Option.map echoFingerprint) = Some(echoFingerprint change.Item))

        priorEchoes
        |> Array.iter (fun prior ->
            let retired = LibraryOperation.setEcho false prior

            let changed =
                execute
                    connection
                    (Some transaction)
                    "UPDATE library_operations SET direction=$direction,terminal=$terminal,echo_pending=$echo,created_at_ticks=$created,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=1 AND echo_pending=1 AND operation_json=$expected;"
                    (operationColumns retired
                     @ [
                         "$repository", box (expected.RepositoryId.ToString("D"))
                         "$operation", box (prior.OperationId.ToString("D"))
                         "$expected", box (serialize prior)
                     ])

            if changed <> 1 then
                invalidOp "Library publication echo changed before completion.")

        // A completion that needs no rewrite can inherit an earlier matching publication's unobserved echo.
        let terminal =
            operation
            |> LibraryOperation.setEcho (operation.EchoPending || observablePriorEcho)
            |> LibraryOperation.complete

        let completed =
            execute
                connection
                (Some transaction)
                "UPDATE library_operations SET direction=$direction,terminal=$terminal,echo_pending=$echo,created_at_ticks=$created,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=0 AND operation_json=$expected;"
                (operationColumns terminal
                 @ [
                     "$repository", box (expected.RepositoryId.ToString("D"))
                     "$operation", box (operation.OperationId.ToString("D"))
                     "$expected", box (serialize operation)
                 ])

        if completed <> 1 then
            invalidOp "Library exact pending operation changed before completion."

        // Once any item commits, restart uses its applied cursor rather than replaying the previous page.
        // A fully applied page subsequently checkpoints its next token, including an empty HasMore page.
        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected then
            invalidOp "Library repository changed during completion."

        let advanced =
            execute
                connection
                (Some transaction)
                "UPDATE library_repository_state SET applied_cursor=$cursor,next_page_token=NULL,lifecycle_state='catchingUp' WHERE repository_id=$repository;"
                [
                    "$cursor", box change.Item.LastChangeCursor
                    "$repository", box (expected.RepositoryId.ToString("D"))
                ]

        if advanced <> 1 then
            invalidOp "Library repository disappeared during completion."

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
                && operationItem operation
                   |> Option.exists (fun item -> echoFingerprint item = fingerprint))

        match matches with
        | [| operation |] ->
            use connection = openConnection dbPath

            let updated = LibraryOperation.setEcho false operation

            execute
                connection
                None
                "UPDATE library_operations SET direction=$direction,terminal=$terminal,echo_pending=$echo,created_at_ticks=$created,operation_json=$json WHERE repository_id=$repository AND operation_id=$operation AND terminal=1 AND echo_pending=1 AND operation_json=$expected;"
                (operationColumns updated
                 @ [
                     "$repository", box (repositoryId.ToString("D"))
                     "$operation", box (operation.OperationId.ToString("D"))
                     "$expected", box (serialize operation)
                 ]) = 1
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
            && (operation.BaselineItem.IsNone
                || current.Baseline.IsNone)
            && (operation.Accepted
                |> Option.forall (fun change ->
                    change.Item.LastChangeCursor
                    <> current.AppliedCursor)))
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
