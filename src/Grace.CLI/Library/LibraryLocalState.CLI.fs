namespace Grace.CLI

open System
open Grace.Shared.Utilities
open Grace.Types.Library
open Microsoft.Data.Sqlite
open Grace.CLI.LibraryOperation

/// Stores Library participation, materialized ancestry, pending operations and sync health in the existing local
/// database. Library state lives only in these four tables. Sync health has its own table because it must exist on
/// copies that don't participate: Watch reads the catalog on every copy and reports failures there too. There is no
/// migration contract for old development-only Library schemas.
module internal LibraryLocalState =

    /// Retains one immutable onboarding selection through metadata acquisition, installation and its first catch-up.
    [<CLIMutable>]
    type BaselineSelection = { BootstrapId: Guid; BoundaryCursor: string; MetadataComplete: bool; Applied: bool }

    /// Owns this copy's participation and complete bounded catalog; its cursor describes completed local application.
    /// BaselinePageToken continues an incomplete baseline download. Change pages have no token: each pull starts at
    /// AppliedCursor.
    [<CLIMutable>]
    type RepositoryState =
        {
            RepositoryId: Guid
            Catalog: LibraryCatalogDto
            AppliedCursor: string
            BaselinePageToken: string option
            State: string
            Paused: bool
            Baseline: BaselineSelection option
        }

    /// Stores the typed operation model directly in the existing operation JSON column.
    type PendingOperation = LibraryOperation.Operation

    /// Names the part of Library work that failed, so a later success clears only the failures it disproves.
    [<RequireQualifiedAccess>]
    type SyncFailureSource =
        /// A `grace library sync` run, from the CLI or from Watch.
        | Sync
        /// Watch's periodic catalog read.
        | Catalog
        /// Watch's own Library steps: reading local status, registering for wake hints, classifying observations.
        | Watch

    /// Converts a failure source to its stored spelling.
    let private sourceText source =
        match source with
        | SyncFailureSource.Sync -> "sync"
        | SyncFailureSource.Catalog -> "catalog"
        | SyncFailureSource.Watch -> "watch"

    /// One recorded Library failure: what failed, when, what it affected and what the user can do next.
    type SyncFailure = { Source: SyncFailureSource; Message: string; Path: string option; OperationId: Guid option; NextStep: string; AtUnixTicks: int64 }

    /// The latest Library failure and the latest successful sync for one repository on this copy.
    type SyncHealth = { LastFailure: SyncFailure option; LastSuccessAtUnixTicks: int64 option }

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

    /// Adds the four Library tables to the existing database; no older unmerged Library schema is supported.
    let initialize dbPath =
        task {
            do! LocalStateDb.ensureDbInitialized dbPath
            use connection = openConnection dbPath

            execute
                connection
                None
                "CREATE TABLE IF NOT EXISTS library_repository_state(repository_id TEXT PRIMARY KEY,catalog_json TEXT NOT NULL,applied_cursor TEXT NOT NULL,baseline_page_token TEXT NULL,lifecycle_state TEXT NOT NULL,paused INTEGER NOT NULL CHECK(paused IN (0,1)),baseline_json TEXT NULL);
                 CREATE TABLE IF NOT EXISTS library_items(repository_id TEXT NOT NULL,item_id TEXT NOT NULL,item_json TEXT NOT NULL,PRIMARY KEY(repository_id,item_id),FOREIGN KEY(repository_id) REFERENCES library_repository_state(repository_id));
                 CREATE TABLE IF NOT EXISTS library_operations(repository_id TEXT NOT NULL,operation_id TEXT NOT NULL,direction TEXT NOT NULL CHECK(direction IN ('local','remote','baseline')),terminal INTEGER NOT NULL CHECK(terminal IN (0,1)),echo_pending INTEGER NOT NULL CHECK(echo_pending IN (0,1)),created_at_ticks INTEGER NOT NULL,operation_json TEXT NOT NULL,PRIMARY KEY(repository_id,operation_id),FOREIGN KEY(repository_id) REFERENCES library_repository_state(repository_id));
                 CREATE INDEX IF NOT EXISTS ix_library_operations_pending ON library_operations(repository_id,terminal,created_at_ticks);
                 CREATE TABLE IF NOT EXISTS library_sync_health(repository_id TEXT NOT NULL,source TEXT NOT NULL CHECK(source IN ('sync','catalog','watch')),failure_message TEXT NULL,failure_path TEXT NULL,failure_operation_id TEXT NULL,failure_next_step TEXT NULL,failure_at_unix_ticks INTEGER NULL,last_success_at_unix_ticks INTEGER NULL,PRIMARY KEY(repository_id,source),CHECK((failure_message IS NULL) = (failure_at_unix_ticks IS NULL)),CHECK(source='sync' OR last_success_at_unix_ticks IS NULL));"
                []
            |> ignore
        }

    /// Reads participation from its sole persisted source on the supplied connection.
    let private readRepositoryWith (connection: SqliteConnection) transaction (repositoryId: Guid) =
        use command = connection.CreateCommand()

        transaction
        |> Option.iter (fun value -> command.Transaction <- value)

        command.CommandText <-
            "SELECT catalog_json,applied_cursor,lifecycle_state,baseline_page_token,baseline_json,paused FROM library_repository_state WHERE repository_id=$repository;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()

        if reader.Read() then
            Some
                {
                    RepositoryId = repositoryId
                    Catalog = deserialize<LibraryCatalogDto> (reader.GetString(0))
                    AppliedCursor = reader.GetString(1)
                    State = reader.GetString(2)
                    BaselinePageToken = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
                    Baseline =
                        if reader.IsDBNull(4) then
                            None
                        else
                            Some(deserialize<BaselineSelection> (reader.GetString(4)))
                    Paused = reader.GetInt64(5) <> 0L
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
            "INSERT INTO library_repository_state(repository_id,catalog_json,applied_cursor,baseline_page_token,lifecycle_state,paused,baseline_json) VALUES($repository,$catalog,$cursor,$next,$state,$paused,$baseline);"
            [
                "$repository", box (state.RepositoryId.ToString("D"))
                "$paused", box (if state.Paused then 1 else 0)
                "$catalog", box (serialize state.Catalog)
                "$cursor", box state.AppliedCursor
                "$next",
                (state.BaselinePageToken
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

    /// Reports whether a newer catalog for the same repository only added Libraries to the one this copy selected. A
    /// copy follows an addition automatically; a removal or relocation it can't follow yet (#1081).
    let catalogOnlyAdds (selected: LibraryCatalogDto) (latest: LibraryCatalogDto) =
        latest.RepositoryId = selected.RepositoryId
        && selected.Libraries
           |> Array.forall (fun root ->
               latest.Libraries
               |> Array.exists (fun candidate -> String.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)))

    /// Switches the selected catalog to one that only added Libraries. One transaction rechecks the exact repository
    /// record and reruns the caller's folder check, so a stale read can't switch. Applied cursor, operations, items and
    /// pause are untouched.
    let selectCatalogWith (insideTransaction: unit -> unit) dbPath (expected: RepositoryState) (selected: LibraryCatalogDto) =
        if not (catalogOnlyAdds expected.Catalog selected) then
            invalidOp "A Library catalog switch can only add Libraries."

        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected then
            invalidOp "Library participation changed before the catalog switch."

        insideTransaction ()

        execute
            connection
            (Some transaction)
            "UPDATE library_repository_state SET catalog_json=$catalog WHERE repository_id=$repository;"
            [
                "$repository", box (expected.RepositoryId.ToString("D"))
                "$catalog", box (serialize selected)
            ]
        |> ignore

        transaction.Commit()

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

    /// Replaces onboarding progress under a transaction whose caller has checked the exact previous repository record.
    /// The catalog is written too, because an expired restart may select a catalog that added Libraries.
    let private writeBaselineState connection transaction (state: RepositoryState) =
        execute
            connection
            (Some transaction)
            "UPDATE library_repository_state SET catalog_json=$catalog,applied_cursor=$cursor,baseline_page_token=$next,lifecycle_state=$state,baseline_json=$baseline WHERE repository_id=$repository;"
            [
                "$repository", box (state.RepositoryId.ToString("D"))
                "$catalog", box (serialize state.Catalog)
                "$cursor", box state.AppliedCursor
                "$next",
                (state.BaselinePageToken
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

            if
                prior.MetadataComplete
                || prior.Applied
                || previous.AppliedCursor <> ""
                || (not restart
                    && previous.Catalog <> selected.Catalog)
                || not (catalogOnlyAdds previous.Catalog selected.Catalog)
            then
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
                BaselinePageToken = page.NextPageToken
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
                BaselinePageToken = None
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

    /// Sets a derived synchronization status without advancing applied progress.
    let setState dbPath (expected: RepositoryState) state =
        use connection = openConnection dbPath

        let changed =
            execute
                connection
                None
                "UPDATE library_repository_state SET lifecycle_state=$state WHERE repository_id=$repository AND applied_cursor=$cursor AND catalog_json=$catalog AND paused=$paused;"
                [
                    "$state", box state
                    "$paused", box (if expected.Paused then 1 else 0)
                    "$repository", box (expected.RepositoryId.ToString("D"))
                    "$cursor", box expected.AppliedCursor
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

        // The applied cursor advances with the item and operation, so the next pull starts right after this change.
        if readRepositoryWith connection (Some transaction) expected.RepositoryId
           <> Some expected then
            invalidOp "Library repository changed during completion."

        let advanced =
            execute
                connection
                (Some transaction)
                "UPDATE library_repository_state SET applied_cursor=$cursor,lifecycle_state='catchingUp' WHERE repository_id=$repository;"
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

    /// Reads this copy's latest Library failure and successful sync, with or without participation. Each source keeps
    /// its own failure, so a later failure of one kind never erases a still-unresolved failure of another; status shows
    /// the most recent one.
    let readHealth dbPath (repositoryId: Guid) =
        use connection = openConnection dbPath
        use command = connection.CreateCommand()

        command.CommandText <-
            "SELECT source,failure_message,failure_path,failure_operation_id,failure_next_step,failure_at_unix_ticks,last_success_at_unix_ticks FROM library_sync_health WHERE repository_id=$repository;"

        command.Parameters.AddWithValue("$repository", repositoryId.ToString("D"))
        |> ignore

        use reader = command.ExecuteReader()
        let failures = ResizeArray<SyncFailure>()
        let mutable lastSuccess = None

        while reader.Read() do
            let optionalString index = if reader.IsDBNull(index) then None else Some(reader.GetString(index))

            let source =
                match reader.GetString(0) with
                | "sync" -> SyncFailureSource.Sync
                | "catalog" -> SyncFailureSource.Catalog
                | _ -> SyncFailureSource.Watch

            if not (reader.IsDBNull(1)) then
                failures.Add
                    {
                        Source = source
                        Message = reader.GetString(1)
                        Path = optionalString 2
                        OperationId = optionalString 3 |> Option.map Guid.Parse
                        NextStep = reader.GetString(4)
                        AtUnixTicks = reader.GetInt64(5)
                    }

            if not (reader.IsDBNull(6)) then lastSuccess <- Some(reader.GetInt64(6))

        {
            LastFailure =
                failures
                |> Seq.sortByDescending (fun failure -> failure.AtUnixTicks)
                |> Seq.tryHead
            LastSuccessAtUnixTicks = lastSuccess
        }

    /// Records the latest failure of one source; failures of other sources and the last successful sync are kept.
    let recordFailure dbPath (repositoryId: Guid) (failure: SyncFailure) =
        use connection = openConnection dbPath

        execute
            connection
            None
            "INSERT INTO library_sync_health(repository_id,source,failure_message,failure_path,failure_operation_id,failure_next_step,failure_at_unix_ticks) VALUES($repository,$source,$message,$path,$operation,$next,$at)
             ON CONFLICT(repository_id,source) DO UPDATE SET failure_message=excluded.failure_message,failure_path=excluded.failure_path,failure_operation_id=excluded.failure_operation_id,failure_next_step=excluded.failure_next_step,failure_at_unix_ticks=excluded.failure_at_unix_ticks;"
            [
                "$repository", box (repositoryId.ToString("D"))
                "$source", box (sourceText failure.Source)
                "$message", box failure.Message
                "$path",
                (failure.Path
                 |> Option.map box
                 |> Option.defaultValue (box DBNull.Value))
                "$operation",
                (failure.OperationId
                 |> Option.map (fun value -> box (value.ToString("D")))
                 |> Option.defaultValue (box DBNull.Value))
                "$next", box failure.NextStep
                "$at", box failure.AtUnixTicks
            ]
        |> ignore

    /// Clears one source's failure when a later success of the same kind shows it no longer applies.
    let clearFailure dbPath (repositoryId: Guid) (source: SyncFailureSource) =
        use connection = openConnection dbPath

        execute
            connection
            None
            "UPDATE library_sync_health SET failure_message=NULL,failure_path=NULL,failure_operation_id=NULL,failure_next_step=NULL,failure_at_unix_ticks=NULL WHERE repository_id=$repository AND source=$source;"
            [
                "$repository", box (repositoryId.ToString("D"))
                "$source", box (sourceText source)
            ]
        |> ignore

    /// Records a completed sync run. A run reads the catalog and local state itself, so it clears every failure.
    let recordSyncSuccess dbPath (repositoryId: Guid) (atUnixTicks: int64) =
        use connection = openConnection dbPath
        use transaction = connection.BeginTransaction()

        execute
            connection
            (Some transaction)
            "UPDATE library_sync_health SET failure_message=NULL,failure_path=NULL,failure_operation_id=NULL,failure_next_step=NULL,failure_at_unix_ticks=NULL WHERE repository_id=$repository;"
            [
                "$repository", box (repositoryId.ToString("D"))
            ]
        |> ignore

        execute
            connection
            (Some transaction)
            "INSERT INTO library_sync_health(repository_id,source,last_success_at_unix_ticks) VALUES($repository,'sync',$at)
             ON CONFLICT(repository_id,source) DO UPDATE SET last_success_at_unix_ticks=excluded.last_success_at_unix_ticks;"
            [
                "$repository", box (repositoryId.ToString("D"))
                "$at", box atUnixTicks
            ]
        |> ignore

        transaction.Commit()
