/// Exercises proposed pause/resume gates without adding production commands or persistence behavior.
module internal LibraryPauseExperiment

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.LibraryOperation
open Grace.CLI.LibraryLocalState
open Grace.Shared.Utilities
open Grace.Types.Library
open Microsoft.Data.Sqlite

/// Stops one modeled command at a named effect boundary; restart reads only files and fresh SQLite connections.
exception Interrupted of string
/// Distinguishes an expected product stop from a failed experiment assertion.
exception Blocked of string

/// Names the actual working tree and its persisted repository identity, with no cached progress or operation.
type Copy =
    { Root: string
      Db: string
      RepositoryId: Guid }

/// Keeps test outcomes separate from the application rows used for restart.
type CaseResult =
    { Name: string
      Passed: bool
      Detail: string }

/// Models only remote catalog/feed availability; it is not a new client persistence responsibility.
type RemotePolicy =
    { Catalog: Guid
      Epoch: string
      RequiresBaseline: bool }

let results = ResizeArray<CaseResult>()
let runRoot = Path.GetFullPath(Environment.GetCommandLineArgs()[1])
let sourceBytes = Text.Encoding.UTF8.GetBytes("original nonempty X")
let savedBytes = Text.Encoding.UTF8.GetBytes("frozen saved nonempty Z")

let parent: LibraryParentDto =
    { Kind = "root"
      LibraryPath = Some "Library"
      ItemId = None }

/// Rejects a false conclusion rather than treating an assertion failure as an expected product error.
let require condition message = if not condition then failwith message

/// Opens the actual production Library connection, including WAL and FULL synchronization.
let connect copy =
    LibraryLocalState.openConnection copy.Db

/// Reads the unchanged production repository record through its real serializer/adapter.
let state copy =
    LibraryLocalState.readRepository copy.Db copy.RepositoryId
    |> Option.get

/// Reads real typed operation JSON and checks its derived SQLite indexes.
let operations copy =
    LibraryLocalState.readOperations copy.Db copy.RepositoryId

/// Resolves only a path within the disposable working tree.
let path copy name =
    Path.Combine(copy.Root, "Library", name)

/// Acquires the production cooperative Windows working-root lease.
let lease copy token =
    let scope =
        WorkingDirectoryUpdateCoordination.Scope.create copy.RepositoryId copy.Root
        |> Result.defaultWith failwith

    WorkingDirectoryUpdateCoordination.Lease.acquire scope token

/// Executes fixture or proposed participation SQL without changing production source.
let execute (connection: SqliteConnection) transaction commandText parameters =
    use command = connection.CreateCommand()

    transaction
    |> Option.iter (fun value -> command.Transaction <- value)

    command.CommandText <- commandText

    parameters
    |> List.iter (fun (key, value: obj) ->
        command.Parameters.AddWithValue(key, value)
        |> ignore)

    command.ExecuteNonQuery()

/// Reads the proposed independent pause column on the existing repository row.
let paused copy =
    use connection = connect copy
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT paused FROM library_repository_state WHERE repository_id=$id"

    command.Parameters.AddWithValue("$id", copy.RepositoryId.ToString("D"))
    |> ignore

    Convert.ToInt32(command.ExecuteScalar()) = 1

/// Captures rows and physical residue for inspection; execution never reads this diagnostic file.
let snapshot copy label =
    let files =
        Directory.GetFiles(copy.Root, "*", SearchOption.AllDirectories)
        |> Array.filter (fun file ->
            file.Contains("Library")
            || file.Contains("objects"))
        |> Array.map (fun file ->
            {| Path = Path.GetRelativePath(copy.Root, file)
               Sha256 = Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes file))
               Ticks = File.GetLastWriteTimeUtc(file).Ticks |})

    let evidence =
        {| Paused = paused copy
           Repository = state copy
           Operations = operations copy
           Files = files |}

    File.WriteAllText(Path.Combine(copy.Root, label + ".json"), serialize evidence)

/// Injects a requested failure only at its exact named boundary.
let hit copy selected point =
    if selected = point then
        snapshot copy ("interruption-" + point)
        raise (Interrupted point)

/// Builds a real descriptor with the production dual-hash helper.
let content bytes : LibraryContentVersionDto =
    let identity = LibraryFilesystem.content bytes

    { ContentVersionId = Guid.NewGuid()
      Blake3Hash = identity.Blake3Hash
      Sha256Hash = identity.Sha256Hash
      Size = identity.Size
      CreatedAt = getCurrentInstant () }

/// Creates a real nonempty item for the disposable parent graph.
let item name bytes =
    { ItemId = Guid.NewGuid()
      ItemKind = ItemKind.File
      LastChangeCursor = "cursor-1"
      Namespace =
        Some
            { Parent = parent
              Name = name
              NamespaceVersion = Guid.NewGuid() }
      Content = Some(content bytes)
      ContentRevision = Some "cursor-1"
      Tombstone = None }

/// Seeds an existing copy using production schema and serializers, with only the proposed pause column added.
let setup name =
    let root = Path.Combine(runRoot, name)

    Directory.CreateDirectory(Path.Combine(root, "Library"))
    |> ignore

    Directory.CreateDirectory(Path.Combine(root, ".grace", "objects"))
    |> ignore

    let copy =
        { Root = root
          Db = Path.Combine(root, ".grace", "grace-local.db")
          RepositoryId = Guid.NewGuid() }

    LibraryLocalState.initialize copy.Db
    |> fun work -> work.GetAwaiter().GetResult()

    let catalog =
        { RepositoryId = copy.RepositoryId
          Version = Guid.NewGuid()
          Libraries = [| "Library" |]
          CreatedAt = getCurrentInstant ()
          CreatedBy = "pause-experiment"
          PreviousVersion = None }

    LibraryLocalState.enable
        copy.Db
        { RepositoryId = copy.RepositoryId
          WorkingCopyId = Guid.NewGuid()
          Catalog = catalog
          CursorEpoch = "epoch-1"
          AppliedCursor = "cursor-1"
          NextPageToken = None
          State = "current"
          Baseline = None }

    use connection = connect copy

    execute
        connection
        None
        "ALTER TABLE library_repository_state ADD COLUMN paused INTEGER NOT NULL DEFAULT 0 CHECK(paused IN (0,1))"
        []
    |> ignore

    let original = item "source.txt" sourceBytes

    execute
        connection
        None
        "INSERT INTO library_items VALUES($repository,$item,$json)"
        [ "$repository", box (copy.RepositoryId.ToString("D"))
          "$item", box (original.ItemId.ToString("D"))
          "$json", box (serialize original) ]
    |> ignore

    File.WriteAllBytes(path copy "source.txt", sourceBytes)

    File.WriteAllText(
        Path.Combine(root, "modeled-remote-policy.json"),
        serialize
            { Catalog = catalog.Version
              Epoch = "epoch-1"
              RequiresBaseline = false }
    )

    copy

/// Loads the original materialized item through the real adapter.
let original copy =
    LibraryLocalState.readItems copy.Db copy.RepositoryId
    |> Array.head

/// Commits only pause under the root lease, with cancellation before mutation and a real transaction rollback case.
let toggle copy value boundary token =
    task {
        use! held = lease copy token
        token.ThrowIfCancellationRequested()

        if (state copy).Baseline.IsSome then
            raise (Blocked "onboarding")

        let name = if value then "pause" else "resume"
        hit copy boundary ("before-" + name + "-commit")

        do
            use connection = connect copy
            use transaction = connection.BeginTransaction()

            let count =
                execute
                    connection
                    (Some transaction)
                    "UPDATE library_repository_state SET paused=$value WHERE repository_id=$id"
                    [ "$value", box (if value then 1 else 0)
                      "$id", box (copy.RepositoryId.ToString("D")) ]

            require (count = 1) "Participation update lost its repository."

            if boundary = "inside-" + name + "-commit" then
                raise (Interrupted boundary)

            transaction.Commit()

        hit copy boundary ("after-" + name + "-commit")
    }

/// Runs one modeled caller only after a fresh under-lease pause check, regardless of its earlier status observation.
let guarded copy action =
    task {
        use! held = lease copy CancellationToken.None

        if paused copy then
            return false
        else
            action ()
            return true
    }

/// Retains physical immutable bytes and their actual typed reference before operation insertion.
let saveObject copy bytes =
    let descriptor = content bytes
    let locator = descriptor.Sha256Hash + ".object"
    let target = Path.Combine(copy.Root, ".grace", "objects", locator)

    if not (File.Exists target) then
        use stream =
            new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)

        stream.Write bytes
        stream.Flush(true)

    { ObjectPath = locator
      Content =
        { Size = descriptor.Size
          Sha256Hash = descriptor.Sha256Hash
          Blake3Hash = descriptor.Blake3Hash } }

/// Rechecks a saved reference without substituting newer working bytes.
let verifyObject copy reference =
    let file = Path.Combine(copy.Root, ".grace", "objects", reference.ObjectPath)

    if not (File.Exists file) then
        raise (Blocked "missing-object")

    let bytes = File.ReadAllBytes file
    let descriptor = LibraryFilesystem.content bytes

    if descriptor.Size <> reference.Content.Size
       || descriptor.Sha256Hash
          <> reference.Content.Sha256Hash
       || descriptor.Blake3Hash
          <> reference.Content.Blake3Hash then
        raise (Blocked "corrupt-object")

    bytes

/// Uses actual operation variants and serialization while making the remote request model explicit.
let seedSaved copy progress =
    let old = original copy
    let reference = saveObject copy savedBytes

    let op =
        { OperationId = Guid.NewGuid()
          CatalogVersion = (state copy).Catalog.Version
          CreatedAtTicks = DateTime.UtcNow.Ticks
          Work =
            OperationWork.SavedFile(
                { SourcePath = "Library/source.txt"
                  Object = reference
                  Base = SavedBase.MaterializedItem old },
                progress
            ) }

    LibraryLocalState.insertOperation copy.Db copy.RepositoryId op
    File.WriteAllBytes(path copy "source.txt", savedBytes)
    op

/// Builds the modeled accepted change carried by the real production receipt/operation types.
let accepted copy id changed kind : LibraryChangeDto =
    { OperationId = id
      ChangeKind = kind
      AcceptedAt = getCurrentInstant ()
      AcceptedBy = "modeled-remote"
      LibraryCatalogVersion = (state copy).Catalog.Version
      Item = changed
      Conflict = None }

/// Updates the exact pending operation through the production persistence function.
let update copy before after =
    LibraryLocalState.updateOperation copy.Db copy.RepositoryId before after

/// Reads modeled remote policy freshly at an affected decision, without adopting a new catalog or baseline.
let checkRemote copy =
    let remote =
        deserialize<RemotePolicy> (File.ReadAllText(Path.Combine(copy.Root, "modeled-remote-policy.json")))

    let current = state copy

    if remote.Catalog <> current.Catalog.Version then
        raise (Blocked "catalog")

    if remote.Epoch <> current.CursorEpoch
       || remote.RequiresBaseline then
        raise (Blocked "rebaselineRequired")

/// Captures only the latest saved disk state under the caller's root lease and retains its original materialized base.
let captureLatest copy =
    let bytes = File.ReadAllBytes(path copy "source.txt")

    if bytes.Length > 0 then
        let descriptor = LibraryFilesystem.content bytes

        if
            descriptor.Sha256Hash
            <> (original copy).Content.Value.Sha256Hash
            && not
                (
                    operations copy
                    |> Array.exists (fun op ->
                        op.SourceObject
                        |> Option.exists (fun reference -> reference.Content.Sha256Hash = descriptor.Sha256Hash))
                )
        then
            let reference = saveObject copy bytes

            let op =
                { OperationId = Guid.NewGuid()
                  CatalogVersion = (state copy).Catalog.Version
                  CreatedAtTicks = DateTime.UtcNow.Ticks
                  Work =
                    OperationWork.SavedFile(
                        { SourcePath = "Library/source.txt"
                          Object = reference
                          Base = SavedBase.MaterializedItem(original copy) },
                        SavedFileProgress.Captured
                    ) }

            LibraryLocalState.insertOperation copy.Db copy.RepositoryId op

/// Seeds real prepared-rename JSON and physical destination-publication residue.
let seedRename copy =
    let old = original copy

    let changed =
        { old with
            Namespace =
                Some
                    { old.Namespace.Value with
                        Name = "renamed.txt"
                        NamespaceVersion = Guid.NewGuid() }
            LastChangeCursor = "cursor-2" }

    let id = Guid.NewGuid()
    let change = accepted copy id changed ChangeKind.Rename

    let checkpoint =
        { ExpectedCursor = "cursor-1"
          ExpectedAncestry = [| old |]
          ExpectedTarget = TargetObservation.Absent
          SourcePath = "Library/source.txt"
          TargetPath = "Library/renamed.txt"
          Echo = EchoState.Pending }

    let op =
        { OperationId = id
          CatalogVersion = (state copy).Catalog.Version
          CreatedAtTicks = DateTime.UtcNow.Ticks
          Work =
            OperationWork.ExplicitRename(
                { SourcePath = "Library/source.txt"
                  TargetPath = "Library/renamed.txt"
                  MaterializedItem = old },
                RenameProgress.Accepted(
                    "modeled frozen rename request",
                    { RequestHash = "modeled-rename-hash"
                      Change = change },
                    ApplicationProgress.Prepared checkpoint
                )
            ) }

    LibraryLocalState.insertOperation copy.Db copy.RepositoryId op
    File.WriteAllBytes(path copy "renamed.txt", sourceBytes)
    op

/// Restarts a pending saved submission from a disk-persisted modeled server receipt, then uses real local completion.
let applyPending copy boundary =
    let pending =
        operations copy
        |> Array.filter (fun op -> not op.Terminal)

    for initial in pending do
        checkRemote copy
        let mutable op = initial

        match op.Work with
        | OperationWork.SavedFile (intent, SavedFileProgress.Rejected _) ->
            verifyObject copy intent.Object |> ignore
            raise (Blocked "rejected-save")
        | OperationWork.SavedFile (intent, SavedFileProgress.Uploaded request) ->
            let bytes = verifyObject copy intent.Object
            hit copy boundary "before-submit"
            let receiptPath = Path.Combine(copy.Root, "modeled-server-receipt.json")

            let change =
                if File.Exists receiptPath then
                    deserialize<LibraryChangeDto> (File.ReadAllText receiptPath)
                else
                    let old = original copy

                    let changed =
                        { old with
                            Content = Some(content bytes)
                            ContentRevision = Some "cursor-2"
                            LastChangeCursor = "cursor-2" }

                    let change = accepted copy op.OperationId changed ChangeKind.UpdateContent
                    File.WriteAllText(receiptPath, serialize change)
                    File.AppendAllText(Path.Combine(copy.Root, "modeled-acceptances.log"), "accepted\n")
                    change

            require (change.OperationId = op.OperationId) "Lost response selected another operation."
            hit copy boundary "after-server-accept"

            let checkpoint =
                { ExpectedCursor = (state copy).AppliedCursor
                  ExpectedAncestry = [| original copy |]
                  ExpectedTarget = TargetObservation.FilePresent intent.Object.Content
                  SourcePath = intent.SourcePath
                  TargetPath = intent.SourcePath
                  Echo = EchoState.Pending }

            let next =
                { op with
                    Work =
                        OperationWork.SavedFile(
                            intent,
                            SavedFileProgress.Accepted(
                                request,
                                { RequestHash = "modeled-save-hash"
                                  Change = change },
                                ApplicationProgress.Prepared checkpoint
                            )
                        ) }

            update copy op next
            op <- next
            hit copy boundary "after-receipt"
        | _ -> ()

        match LibraryOperation.application op with
        | Some (ApplicationProgress.Prepared checkpoint) ->
            let change = op.Accepted.Value
            let target = Path.Combine(copy.Root, checkpoint.TargetPath)
            let source = Path.Combine(copy.Root, checkpoint.SourcePath)
            let expected = change.Item.Content.Value

            if File.Exists target && FileInfo(target).Length = 0L then
                raise (Blocked "zero-byte")

            let bytes =
                if op.SourceObject.IsSome then
                    verifyObject copy op.SourceObject.Value
                else
                    sourceBytes

            checkRemote copy
            hit copy boundary "before-publication"

            if
                not (File.Exists target)
                || File.ReadAllBytes(target) <> bytes
            then
                if File.Exists target
                   && File.ReadAllBytes(target) <> sourceBytes then
                    raise (Blocked "changed-target")

                File.WriteAllBytes(target, bytes)
                File.AppendAllText(Path.Combine(copy.Root, "publications.log"), "published\n")

            require
                ((LibraryFilesystem.content (File.ReadAllBytes target))
                    .Sha256Hash = expected.Sha256Hash)
                "Published bytes differ from acceptance."

            hit copy boundary "after-publication"

            if source <> target && File.Exists source then
                if File.ReadAllBytes(source) <> bytes then
                    raise (Blocked "changed-source")

                hit copy boundary "before-source-removal"
                File.Delete source
                hit copy boundary "after-source-removal"

            hit copy boundary "before-completion"
            checkRemote copy

            LibraryLocalState.completeWith
                (fun _ _ ->
                    if boundary = "inside-completion" then
                        raise (Interrupted boundary))
                copy.Db
                (state copy)
                op

            hit copy boundary "after-completion"
        | _ -> ()

/// Models one admitted run; error status changes do not clear or reapply deliberate pause.
let run copy boundary failure =
    guarded copy (fun () ->
        try
            if failure = "network" then
                raise (Blocked failure)

            checkRemote copy
            applyPending copy boundary
        with
        | :? OperationCanceledException -> reraise ()
        | Interrupted _ -> reraise ()
        | error ->
            use connection = connect copy

            execute
                connection
                None
                "UPDATE library_repository_state SET lifecycle_state='blocked' WHERE repository_id=$id"
                [ "$id", box (copy.RepositoryId.ToString("D")) ]
            |> ignore

            raise error)

/// Records a case and its exception without mistaking unexpected failures for injected interruptions.
let case name action =
    try
        action ()

        results.Add
            { Name = name
              Passed = true
              Detail = "Assertions passed." }
    with
    | error ->
        results.Add
            { Name = name
              Passed = false
              Detail = error.ToString() }

/// Completes a task without keeping application state across simulated command invocations.
let wait (work: Task<'T>) = work.GetAwaiter().GetResult()

/// Expects only the selected experiment interruption.
let interrupted point action =
    let mutable observed = false

    try
        action ()
    with
    | Interrupted value when value = point -> observed <- true

    require observed ("Missing interruption: " + point)

/// Expects only the named supported product stop.
let blocked reason action =
    let mutable observed = false

    try
        action ()
    with
    | Blocked value when value = reason -> observed <- true

    require observed ("Missing block: " + reason)

/// Captures invariant-bearing local state while ignoring the one intentional pause setting.
let retained copy =
    serialize (
        state copy,
        operations copy,
        Directory.GetFiles(Path.Combine(copy.Root, ".grace", "objects"))
        |> Array.map File.ReadAllBytes
    )

[<EntryPoint>]
let main _ =
    require (OperatingSystem.IsWindows()) "Windows is required."
    require (not (Directory.Exists runRoot)) "Use a new output directory; preserved runs are immutable."
    Directory.CreateDirectory(runRoot) |> ignore

    for scenario in [ "current"; "saved"; "rename" ] do
        for value in [ true; false ] do
            let verb = if value then "pause" else "resume"

            for prefix in [ "before"; "inside"; "after" ] do
                let boundary = prefix + "-" + verb + "-commit"

                case (scenario + "-" + boundary) (fun () ->
                    let copy = setup (scenario + "-" + boundary)

                    if scenario = "saved" then
                        seedSaved copy (SavedFileProgress.Uploaded "modeled exact request")
                        |> ignore

                    if scenario = "rename" then
                        seedRename copy |> ignore

                    if not value then
                        toggle copy true "" CancellationToken.None |> wait

                    let before = retained copy

                    interrupted boundary (fun () ->
                        toggle copy value boundary CancellationToken.None
                        |> wait)

                    require
                        (paused copy = (if prefix = "after" then
                                            value
                                        else
                                            not value))
                        "Restart did not observe the committed pause bit."

                    require (retained copy = before) "Pause/resume changed retained work."
                    snapshot copy "restart"

                    toggle copy value "" CancellationToken.None
                    |> wait

                    toggle copy value "" CancellationToken.None
                    |> wait

                    require (paused copy = value && retained copy = before) "Duplicate toggle changed work.")

    for entry in
        [ "run"
          "enable"
          "rename"
          "capture"
          "timer"
          "observation"
          "wake"
          "echo-prune" ] do
        case ("stale-" + entry) (fun () ->
            let copy = setup ("stale-" + entry)
            seedRename copy |> ignore
            let cachedEnabled = not (paused copy)
            toggle copy true "" CancellationToken.None |> wait
            let before = retained copy
            let mutable effect = false

            let allowed =
                if cachedEnabled then
                    guarded copy (fun () -> effect <- true) |> wait
                else
                    false

            require
                (not allowed
                 && not effect
                 && retained copy = before)
                "Stale caller escaped pause."

            toggle copy false "" CancellationToken.None
            |> wait

            require (guarded copy (fun () -> effect <- true) |> wait) "Positive resumed entry did not run."
            require effect "Positive control had no effect.")

    case "lease-wait-cancellation" (fun () ->
        let copy = setup "lease-wait-cancellation"
        use held = lease copy CancellationToken.None |> wait
        use canceled = new CancellationTokenSource()
        let pending = toggle copy true "" canceled.Token
        require (not pending.IsCompleted) "Pause did not wait for active work."
        canceled.Cancel()
        let mutable observed = false

        try
            pending |> wait
        with
        | :? OperationCanceledException -> observed <- true

        require (observed && not (paused copy)) "Canceled waiter changed participation.")

    case "active-holder-finishes-before-pause" (fun () ->
        let copy = setup "active-holder-finishes-before-pause"
        let held = lease copy CancellationToken.None |> wait
        let pending = toggle copy true "" CancellationToken.None
        require (not pending.IsCompleted) "Pause bypassed active holder."

        seedSaved copy (SavedFileProgress.Uploaded "active request")
        |> ignore

        (held :> IDisposable).Dispose()
        require (pending.Wait(TimeSpan.FromSeconds 10.)) "Pause never acquired released lease."
        pending |> wait
        require (paused copy && (operations copy).Length = 1) "Active work was lost.")

    for scenario, boundaries in
        [ "saved",
          [ "before-submit"
            "after-server-accept"
            "after-receipt"
            "before-publication"
            "after-publication"
            "before-completion"
            "inside-completion"
            "after-completion" ]
          "rename",
          [ "before-publication"
            "after-publication"
            "before-source-removal"
            "after-source-removal"
            "before-completion"
            "inside-completion"
            "after-completion" ] ] do
        for boundary in boundaries do
            case (scenario + "-resumed-" + boundary) (fun () ->
                let copy = setup (scenario + "-resumed-" + boundary)

                if scenario = "saved" then
                    seedSaved copy (SavedFileProgress.Uploaded "modeled exact request")
                    |> ignore
                else
                    seedRename copy |> ignore

                toggle copy true "" CancellationToken.None |> wait

                toggle copy false "" CancellationToken.None
                |> wait

                interrupted boundary (fun () -> run copy boundary "" |> wait |> ignore)
                require (not (paused copy)) "Failed resume repaused the copy."

                if boundary <> "after-completion" then
                    require ((state copy).AppliedCursor = "cursor-1") "Cursor advanced before completion."

                snapshot copy "restart"
                run copy "" "" |> wait |> ignore

                require
                    ((state copy).AppliedCursor = "cursor-2"
                     && (operations copy
                         |> Array.forall (fun op -> op.Terminal)))
                    "Restart did not complete exact pending work."

                let target =
                    path
                        copy
                        (if scenario = "saved" then
                             "source.txt"
                         else
                             "renamed.txt")

                let stamp = File.GetLastWriteTimeUtc target
                run copy "" "" |> wait |> ignore
                require (File.GetLastWriteTimeUtc target = stamp) "Completed restart rewrote file."

                if scenario = "saved" then
                    require
                        (File
                            .ReadAllLines(
                                Path.Combine(copy.Root, "modeled-acceptances.log")
                            )
                            .Length = 1)
                        "Lost response duplicated server acceptance."
                else
                    require (not (File.Exists(path copy "source.txt"))) "Rename source remains.")

    for failure in
        [ "network"
          "catalog"
          "rebaselineRequired" ] do
        case ("resume-block-" + failure) (fun () ->
            let copy = setup ("resume-block-" + failure)
            let op = seedSaved copy (SavedFileProgress.Uploaded "retained request")
            toggle copy true "" CancellationToken.None |> wait

            toggle copy false "" CancellationToken.None
            |> wait

            let policy =
                { Catalog =
                    (if failure = "catalog" then
                         Guid.NewGuid()
                     else
                         (state copy).Catalog.Version)
                  Epoch = (state copy).CursorEpoch
                  RequiresBaseline = failure = "rebaselineRequired" }

            File.WriteAllText(Path.Combine(copy.Root, "modeled-remote-policy.json"), serialize policy)
            blocked failure (fun () -> run copy "" failure |> wait |> ignore)

            require
                (not (paused copy)
                 && (state copy).State = "blocked"
                 && (state copy).AppliedCursor = "cursor-1"
                 && operations copy = [| op |])
                "Blocked resume lost input or repaused."

            blocked failure (fun () -> run copy "" failure |> wait |> ignore)
            require ((state copy).AppliedCursor = "cursor-1") "Repeated retry bypassed stop.")

    case "latest-save-retains-materialized-base" (fun () ->
        let copy = setup "latest-save-retains-materialized-base"
        toggle copy true "" CancellationToken.None |> wait

        for bytes in
            [ savedBytes
              Text.Encoding.UTF8.GetBytes("intermediate B")
              Text.Encoding.UTF8.GetBytes("latest C") ] do
            File.WriteAllBytes(path copy "source.txt", bytes)

            require
                (not (
                    guarded copy (fun () -> captureLatest copy)
                    |> wait
                ))
                "Captured while paused."

        require
            ((operations copy).Length = 0
             && Directory
                 .GetFiles(
                     Path.Combine(copy.Root, ".grace", "objects")
                 )
                 .Length = 0)
            "Pause added objects or operations."
        // A newer remote content revision is deliberately not installed before retaining this local edit base.
        let newer =
            { original copy with
                Content = Some(content savedBytes)
                ContentRevision = Some "remote-2"
                LastChangeCursor = "remote-2" }

        File.WriteAllText(Path.Combine(copy.Root, "modeled-newer-remote-item.json"), serialize newer)

        toggle copy false "" CancellationToken.None
        |> wait

        guarded copy (fun () -> captureLatest copy)
        |> wait
        |> ignore

        let op = operations copy |> Array.exactlyOne

        require
            (op.MaterializedBase.Value.ContentRevision = Some "cursor-1")
            "Remote metadata replaced the saved edit base."

        require
            (verifyObject copy op.SourceObject.Value = Text.Encoding.UTF8.GetBytes("latest C"))
            "Resume selected intermediate bytes."

        guarded copy (fun () -> captureLatest copy)
        |> wait
        |> ignore

        require ((operations copy).Length = 1) "Restart captured the same save twice.")

    case "resume-then-later-pause-wins" (fun () ->
        let copy = setup "resume-then-later-pause-wins"

        seedSaved copy (SavedFileProgress.Uploaded "exact request")
        |> ignore

        toggle copy true "" CancellationToken.None |> wait

        toggle copy false "" CancellationToken.None
        |> wait

        toggle copy true "" CancellationToken.None |> wait
        require (not (run copy "" "" |> wait)) "Resume's stale run bypassed a newer pause."

        require
            (not (File.Exists(Path.Combine(copy.Root, "modeled-server-receipt.json"))))
            "Stale resumed work submitted.")

    case "concurrent-stale-reader-rechecks-under-lease" (fun () ->
        let copy = setup "concurrent-stale-reader-rechecks-under-lease"
        use read = new ManualResetEventSlim(false)
        use continueCaller = new ManualResetEventSlim(false)
        let mutable effects = 0

        let caller =
            Task.Run (fun () ->
                let enabled = not (paused copy)
                read.Set()
                require (continueCaller.Wait(TimeSpan.FromSeconds 10.)) "Caller barrier timed out."

                if enabled then
                    guarded copy (fun () -> effects <- effects + 1)
                    |> wait
                else
                    false)

        require (read.Wait(TimeSpan.FromSeconds 10.)) "Caller did not read initial status."
        toggle copy true "" CancellationToken.None |> wait
        continueCaller.Set()
        require (not (wait caller) && effects = 0) "Concurrent stale reader produced an effect.")

    case "paused-zero-byte-does-not-capture-or-apply" (fun () ->
        let copy = setup "paused-zero-byte-does-not-capture-or-apply"
        let op = seedSaved copy (SavedFileProgress.Uploaded "exact saved request")
        toggle copy true "" CancellationToken.None |> wait
        File.WriteAllBytes(path copy "source.txt", [||])

        require
            (not (
                guarded copy (fun () -> captureLatest copy)
                |> wait
            ))
            "Captured paused empty file."

        toggle copy false "" CancellationToken.None
        |> wait

        blocked "zero-byte" (fun () -> run copy "" "" |> wait |> ignore)

        require
            (FileInfo(path copy "source.txt").Length = 0L
             && (state copy).AppliedCursor = "cursor-1")
            "Empty-file obstruction was lost."

        require (verifyObject copy op.SourceObject.Value = savedBytes) "Frozen positive object was lost.")

    for damage in [ "missing"; "corrupt" ] do
        case ("resume-" + damage + "-object") (fun () ->
            let copy = setup ("resume-" + damage + "-object")
            let op = seedSaved copy (SavedFileProgress.Uploaded "exact saved request")
            toggle copy true "" CancellationToken.None |> wait

            let file =
                Path.Combine(copy.Root, ".grace", "objects", op.SourceObject.Value.ObjectPath)

            if damage = "missing" then
                File.Delete file
            else
                File.WriteAllText(file, "damaged fixture")

            File.WriteAllText(path copy "source.txt", "newer working bytes")

            toggle copy false "" CancellationToken.None
            |> wait

            blocked (damage + "-object") (fun () -> run copy "" "" |> wait |> ignore)

            require
                (operations copy = [| op |]
                 && not (File.Exists(Path.Combine(copy.Root, "modeled-server-receipt.json"))))
                "Damaged reference submitted or changed input.")

    case "rejected-save-remains-pending" (fun () ->
        let copy = setup "rejected-save-remains-pending"

        let receipt =
            { RequestHash = "rejected-hash"
              ReasonCode = RejectionCode.ItemTombstoned
              CurrentCatalog = None
              Rebaseline = None }

        let op =
            seedSaved copy (SavedFileProgress.Rejected("rejected exact request", receipt))

        toggle copy true "" CancellationToken.None |> wait

        toggle copy false "" CancellationToken.None
        |> wait

        blocked "rejected-save" (fun () -> run copy "" "" |> wait |> ignore)

        require
            (operations copy = [| op |]
             && not op.Terminal
             && verifyObject copy op.SourceObject.Value = savedBytes)
            "Pause retired rejected saved input.")

    case "all-operation-families-retained" (fun () ->
        let copy = setup "all-operation-families-retained"
        let saved = seedSaved copy SavedFileProgress.Captured
        let rename = seedRename copy
        let old = original copy
        let checkpoint = (LibraryOperation.checkpoint rename).Value
        let change = rename.Accepted.Value

        let extras =
            [ OperationWork.DirectoryCreate(
                  { SourcePath = "Library/new-dir"
                    Placement = { Parent = parent; Name = "new-dir" } },
                  DirectoryProgress.RequestFrozen "directory request"
              )
              OperationWork.Incoming(change, "Library/source.txt", Some old, ApplicationProgress.Prepared checkpoint)
              OperationWork.BaselineLive(old, BaselineInstallation.Installed checkpoint)
              OperationWork.BaselineTombstone(
                  { old with
                      Namespace = None
                      Content = None
                      ContentRevision = None
                      Tombstone =
                          Some
                              { DeletedAt = getCurrentInstant ()
                                DeletedBy = "fixture"
                                DeleteCursor = "cursor-1"
                                LastNamespace = old.Namespace.Value
                                LastContentVersionId = Some old.Content.Value.ContentVersionId } },
                  TombstoneInstallation.Installed
              ) ]

        for work in extras do
            let op =
                { saved with
                    OperationId = Guid.NewGuid()
                    Work = work }

            LibraryLocalState.insertOperation copy.Db copy.RepositoryId op

        let before = retained copy
        toggle copy true "" CancellationToken.None |> wait

        require
            (retained copy = before
             && (operations copy).Length = 6)
            "Pause changed operation family payloads or indexes."

        toggle copy false "" CancellationToken.None
        |> wait

        require (retained copy = before) "Resume changed retained operation families."
        use connection = connect copy
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'library_%'"
        require (Convert.ToInt32(command.ExecuteScalar()) = 3) "Experiment added another Library table.")

    case "incomplete-onboarding-rejects-toggle" (fun () ->
        let copy = setup "incomplete-onboarding-rejects-toggle"
        use connection = connect copy

        execute
            connection
            None
            "UPDATE library_repository_state SET baseline_json=$baseline"
            [ "$baseline",
              box (
                  serialize
                      { BootstrapId = Guid.NewGuid()
                        BoundaryCursor = "cursor-1"
                        MetadataComplete = false
                        Applied = false }
              ) ]
        |> ignore

        blocked "onboarding" (fun () -> toggle copy true "" CancellationToken.None |> wait)
        require (not (paused copy)) "Unsupported onboarding pause mutated participation.")

    case "two-copies-resume-one-modeled-accepted-rename" (fun () ->
        let a = setup "two-copies-A"
        let b = { setup "two-copies-B" with RepositoryId = a.RepositoryId }
        // Each copy is independently initialized; align the same server item/catalog before any local work.
        let aState = state a
        let aItem = original a
        use connection = connect b

        execute connection None "DELETE FROM library_items" []
        |> ignore

        execute
            connection
            None
            "UPDATE library_repository_state SET repository_id=$repo,catalog_json=$catalog"
            [ "$repo", box (a.RepositoryId.ToString("D"))
              "$catalog", box (serialize aState.Catalog) ]
        |> ignore

        execute
            connection
            None
            "INSERT INTO library_items VALUES($repo,$item,$json)"
            [ "$repo", box (b.RepositoryId.ToString("D"))
              "$item", box (aItem.ItemId.ToString("D"))
              "$json", box (serialize aItem) ]
        |> ignore

        File.WriteAllText(
            Path.Combine(b.Root, "modeled-remote-policy.json"),
            serialize
                { Catalog = aState.Catalog.Version
                  Epoch = "epoch-1"
                  RequiresBaseline = false }
        )

        let rename = seedRename a
        toggle a true "" CancellationToken.None |> wait

        let incoming =
            { rename with
                Work =
                    OperationWork.Incoming(
                        rename.Accepted.Value,
                        "Library/source.txt",
                        Some aItem,
                        ApplicationProgress.Prepared((LibraryOperation.checkpoint rename).Value)
                    ) }

        LibraryLocalState.insertOperation b.Db b.RepositoryId incoming
        run b "" "" |> wait |> ignore

        require
            ((state b).AppliedCursor = "cursor-2"
             && (state a).AppliedCursor = "cursor-1")
            "Paused A followed B prematurely."

        toggle a false "" CancellationToken.None |> wait
        run a "" "" |> wait |> ignore

        require
            ((original a).ItemId = (original b).ItemId
             && File.ReadAllBytes(path a "renamed.txt") = File.ReadAllBytes(path b "renamed.txt"))
            "Copies did not converge stable item and bytes."

        let stamps =
            File.GetLastWriteTimeUtc(path a "renamed.txt"), File.GetLastWriteTimeUtc(path b "renamed.txt")

        run a "" "" |> wait |> ignore
        run b "" "" |> wait |> ignore

        require
            (stamps = (File.GetLastWriteTimeUtc(path a "renamed.txt"), File.GetLastWriteTimeUtc(path b "renamed.txt")))
            "Restart rewrote completed files.")

    File.WriteAllText(
        Path.Combine(runRoot, "results.json"),
        serialize
            {| Question =
                "Can independent pause on the existing repository row gate every admitted caller under the existing root lease while retaining typed pending work?"
               Cases = results.ToArray()
               Passed =
                results
                |> Seq.filter (fun value -> value.Passed)
                |> Seq.length
               Total = results.Count |}
    )

    for result in results do
        printfn
            "%s %s%s"
            (if result.Passed then "PASS" else "FAIL")
            result.Name
            (if result.Passed then
                 ""
             else
                 ": " + result.Detail)

    if results |> Seq.forall (fun value -> value.Passed) then
        0
    else
        1
