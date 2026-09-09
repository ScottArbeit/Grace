namespace Grace.CLI.Command

open Grace.CLI

open Grace.CLI.LibraryLocalState
open Grace.CLI.LibraryOperation
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Shared.Validation.Library
open Grace.Types.Library
open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

/// Acquires one complete immutable baseline and installs its selected revisions before accepted-change catch-up.
module internal LibraryBaseline =

    /// Uses the existing remote contracts; an absent continuation result means the server rejected its expired or invalid token.
    type Remote =
        {
            Catalog: unit -> Task<LibraryCatalogDto>
            Start: unit -> Task<LibraryBootstrapPageDto>
            Continue: Guid -> string -> Task<LibraryBootstrapPageDto option>
            Read: LibraryItemDto -> Task<byte array>
        }

    /// Resolves a validated Library path beneath this working copy without following a path outside it.
    let private fullPath (configuration: GraceConfiguration) relative =
        let normalized =
            normalizeRepositoryRelativePath relative
            |> Result.defaultWith invalidOp

        let root =
            Path
                .GetFullPath(configuration.RootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        let path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)))

        if not (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) then
            invalidOp "Baseline path escaped the working root."

        path

    /// Checks ordinary physical directories through the working root immediately before an affected filesystem decision.
    let private requireParents (configuration: GraceConfiguration) (path: string) =
        let root =
            Path
                .GetFullPath(configuration.RootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)

        let mutable parent = Path.GetDirectoryName(path)

        while parent.Length >= root.Length do
            if
                not (Directory.Exists(parent))
                || File
                    .GetAttributes(parent)
                    .HasFlag(FileAttributes.ReparsePoint)
            then
                invalidOp "Library baseline filesystem ancestry changed."

            parent <- Path.GetDirectoryName(parent)

    /// Keeps initial acquisition restricted to an empty ordinary local Library root.
    let private requireEmptyRoot (configuration: GraceConfiguration) (catalog: LibraryCatalogDto) =
        if catalog.RepositoryId <> configuration.RepositoryId
           || catalog.Libraries.Length <> 1 then
            invalidOp "Library onboarding requires one Library in this repository."

        let root = fullPath configuration catalog.Libraries[0]
        requireParents configuration root

        if
            File.Exists(root)
            || (Directory.Exists(root)
                && (File
                        .GetAttributes(root)
                        .HasFlag(FileAttributes.ReparsePoint)
                    || not (
                        Directory.EnumerateFileSystemEntries(root)
                        |> Seq.isEmpty
                    )))
        then
            invalidOp "Library onboarding requires an empty ordinary local Library root."

        root

    /// Reads exact persisted selection state between remote calls and filesystem effects.
    let private current (configuration: GraceConfiguration) =
        readRepository configuration.GraceStatusFile configuration.RepositoryId
        |> Option.get

    /// Rechecks the catalog and exact local selection after remote work and before affected publication or completion.
    let private check (remote: Remote) (configuration: GraceConfiguration) (expected: RepositoryState) =
        task {
            let! catalog = remote.Catalog()

            if catalog <> expected.Catalog
               || current configuration <> expected then
                invalidOp "Library baseline catalog or selection changed; local work is retained."
        }

    /// Creates an unapplied selection; received boundary metadata is never an applied cursor.
    let private selection (configuration: GraceConfiguration) workingCopyId (page: LibraryBootstrapPageDto) =
        {
            RepositoryId = configuration.RepositoryId
            WorkingCopyId = workingCopyId
            Catalog = page.LibraryCatalog
            CursorEpoch = page.CursorEpoch
            AppliedCursor = ""
            NextPageToken = None
            State = "acquiringBaseline"
            Baseline = Some { BootstrapId = page.BootstrapId; BoundaryCursor = page.BoundaryCursor; MetadataComplete = false; Applied = false }
        }

    /// Persists the first page and selection together after both catalog and local-empty checks.
    let startWith observe (remote: Remote) (configuration: GraceConfiguration) =
        task {
            let! page = remote.Start()
            let root = requireEmptyRoot configuration page.LibraryCatalog
            let! catalog = remote.Catalog()

            if catalog <> page.LibraryCatalog then
                invalidOp "Library catalog changed before baseline selection."

            requireEmptyRoot configuration catalog |> ignore
            Directory.CreateDirectory(root) |> ignore
            let selected = selection configuration (Guid.NewGuid()) page
            saveBaselinePage configuration.GraceStatusFile None selected page false
            observe "page"
        }

    /// Acquires all immutable metadata before installation and replaces only expired, uninstalled selections.
    let private acquire observe (remote: Remote) (configuration: GraceConfiguration) (cancellationToken: CancellationToken) =
        task {
            let mutable acquiring =
                not
                    (current configuration)
                        .Baseline
                        .Value
                        .MetadataComplete

            while acquiring do
                cancellationToken.ThrowIfCancellationRequested()
                let expected = current configuration
                do! check remote configuration expected

                requireEmptyRoot configuration expected.Catalog
                |> ignore

                let baseline = expected.Baseline.Value
                let! page = remote.Continue baseline.BootstrapId expected.NextPageToken.Value
                do! check remote configuration expected

                requireEmptyRoot configuration expected.Catalog
                |> ignore

                match page with
                | Some page -> saveBaselinePage configuration.GraceStatusFile (Some expected) expected page false
                | None ->
                    let! page = remote.Start()
                    do! check remote configuration expected

                    requireEmptyRoot configuration expected.Catalog
                    |> ignore

                    let selected = selection configuration expected.WorkingCopyId page

                    if selected.Catalog <> expected.Catalog then
                        invalidOp "Expired baseline restart cannot adopt a changed catalog."

                    saveBaselinePage configuration.GraceStatusFile (Some expected) selected page true

                observe "page"

                acquiring <-
                    not
                        (current configuration)
                            .Baseline
                            .Value
                            .MetadataComplete
        }

    /// Validates the complete live parent graph and returns parent-first work without resolving tombstone history.
    let private orderedWork (expected: RepositoryState) (operations: PendingOperation array) =
        let items = Dictionary<Guid, LibraryItemDto>()

        operations
        |> Array.iter (fun op ->
            if
                op.Direction <> "baseline"
                || op.BaselineItem.IsNone
                || not (items.TryAdd(op.BaselineItem.Value.ItemId, op.BaselineItem.Value))
            then
                invalidOp "Baseline metadata contains duplicate or unexpected work.")

        let paths = Dictionary<Guid, string>()
        let visiting = HashSet<Guid>()

        /// Resolves only live directory parent edges, rejecting cycles and paths outside the selected catalog.
        let rec resolve (item: LibraryItemDto) =
            match paths.TryGetValue item.ItemId with
            | true, path -> path
            | _ ->
                if
                    item.Tombstone.IsSome || item.Namespace.IsNone
                    || not (visiting.Add item.ItemId)
                then
                    invalidOp "Baseline live ancestry is invalid."

                let ns = item.Namespace.Value

                let parent =
                    match ns.Parent.Kind, ns.Parent.LibraryPath, ns.Parent.ItemId with
                    | "root", Some root, None when expected.Catalog.Libraries |> Array.contains root -> root
                    | "item", None, Some id ->
                        match items.TryGetValue id with
                        | true, parent when
                            parent.ItemKind = ItemKind.Directory
                            && parent.Tombstone.IsNone
                            ->
                            resolve parent
                        | _ -> invalidOp "Baseline parent is not a selected live directory."
                    | _ -> invalidOp "Baseline parent is outside the selected Library."

                let path = parent + "/" + ns.Name

                let normalized =
                    normalizeRepositoryRelativePath path
                    |> Result.defaultWith invalidOp

                if
                    normalized <> path || ns.Name.Contains('/')
                    || ns.Name.Contains('\\')
                then
                    invalidOp "Baseline item name is invalid."

                if item.ItemKind = ItemKind.File
                   && (item.Content.IsNone || item.ContentRevision.IsNone) then
                    invalidOp "Baseline file has no selected content revision."

                if item.ItemKind <> ItemKind.File
                   && item.ItemKind <> ItemKind.Directory then
                    invalidOp "Baseline item kind is invalid."

                visiting.Remove item.ItemId |> ignore
                paths.Add(item.ItemId, path)
                path

        let occupied = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        operations
        |> Array.map (fun op ->
            let item = op.BaselineItem.Value

            if item.Tombstone.IsSome then
                op, ""
            else
                let path = resolve item

                if not (occupied.Add path) then
                    invalidOp "Baseline contains colliding live paths."

                op, path)
        |> Array.sortBy (fun (_, path) -> path.Split('/').Length, path)

    /// Compares complete content identity or ordinary-directory presence for one selected live item.
    let private expectedFingerprint (item: LibraryItemDto) =
        if item.ItemKind = ItemKind.Directory then
            Some "directory"
        else
            item.Content
            |> Option.map (fun content -> $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}")

    /// Verifies a completed live effect again so changed files or parents cannot produce a false baseline boundary.
    let private verify configuration path item =
        let target = fullPath configuration path
        requireParents configuration target

        if File.Exists(target) || Directory.Exists(target) then
            if File
                .GetAttributes(target)
                   .HasFlag(FileAttributes.ReparsePoint) then
                invalidOp "Baseline target became a reparse point."

        if LibraryFilesystem.fingerprint target
           <> expectedFingerprint item then
            invalidOp "Installed baseline content changed before completion."

    /// Captures the already installed parent records used to publish one selected live item.
    let private materializedParents (configuration: GraceConfiguration) (item: LibraryItemDto) =
        let items = readItems configuration.GraceStatusFile configuration.RepositoryId

        /// Walks only the live chain required by this item, retaining each exact installed parent record.
        let rec parents (parent: LibraryParentDto) =
            match parent.ItemId with
            | None -> []
            | Some id ->
                let ancestor =
                    items
                    |> Array.find (fun value ->
                        value.ItemId = id
                        && value.ItemKind = ItemKind.Directory
                        && value.Tombstone.IsNone)

                ancestor
                :: parents ancestor.Namespace.Value.Parent

        if item.Tombstone.IsSome then
            [||]
        else
            parents item.Namespace.Value.Parent
            |> List.toArray

    /// Installs one prepared item; publication residue is reusable only when it matches the exact selected result.
    let private installItem observe (remote: Remote) (configuration: GraceConfiguration) (expected: RepositoryState) (original: PendingOperation) relative =
        task {
            let item = original.BaselineItem.Value
            do! check remote configuration expected
            let mutable operation = original

            if not operation.Prepared && item.Tombstone.IsNone then
                if item.Tombstone.IsNone then
                    let target = fullPath configuration relative
                    requireParents configuration target

                    if LibraryFilesystem.fingerprint target
                       |> Option.isSome then
                        invalidOp "Unprepared baseline target is occupied."

                let preparation =
                    {
                        ExpectedCursor = expected.AppliedCursor
                        SourcePath = relative
                        TargetPath = relative
                        Echo = EchoState.Pending
                        ExpectedTarget = TargetObservation.Absent
                        ExpectedAncestry = materializedParents configuration item
                    }

                let prepared = LibraryOperation.prepare preparation operation

                updateOperation configuration.GraceStatusFile configuration.RepositoryId operation prepared
                operation <- prepared
                observe "prepare"

            /// Rechecks exact work and ordinary ancestry at the final point before publication and local completion.
            let revalidate () =
                if current configuration <> expected
                   || materializedParents configuration item
                      <> operation.ExpectedAncestry
                   || (readOperations configuration.GraceStatusFile configuration.RepositoryId
                       |> Array.tryFind (fun op -> op.OperationId = operation.OperationId))
                      <> Some operation then
                    invalidOp "Baseline prepared work changed before publication."

                if item.Tombstone.IsNone then
                    let target = fullPath configuration relative
                    requireParents configuration target

                    if File.Exists(target) || Directory.Exists(target) then
                        if File
                            .GetAttributes(target)
                               .HasFlag(FileAttributes.ReparsePoint) then
                            invalidOp "Baseline target became a reparse point."

                    if
                        item.ItemKind = ItemKind.Directory
                        && Directory.Exists(target)
                        && not
                            (
                                Directory.EnumerateFileSystemEntries(target)
                                |> Seq.isEmpty
                            )
                    then
                        invalidOp "Uncompleted baseline directory gained a local entry."

            revalidate ()

            if item.Tombstone.IsNone then
                let target = fullPath configuration relative

                if item.ItemKind = ItemKind.Directory then
                    if
                        File.Exists(target)
                        || (Directory.Exists(target)
                            && not (
                                Directory.EnumerateFileSystemEntries(target)
                                |> Seq.isEmpty
                            ))
                    then
                        invalidOp "Uncompleted baseline directory contains a local obstruction."

                    observe "beforePublish"
                    do! check remote configuration expected
                    revalidate ()

                    if
                        File.Exists(target)
                        || (Directory.Exists(target)
                            && not (
                                Directory.EnumerateFileSystemEntries(target)
                                |> Seq.isEmpty
                            ))
                    then
                        invalidOp "Baseline directory became occupied."

                    Directory.CreateDirectory(target) |> ignore
                else
                    if item.Content.Value.Size <= 0L then
                        invalidOp "Library synchronization excludes empty baseline files."

                    let actual = LibraryFilesystem.fingerprint target

                    if actual.IsSome
                       && actual <> expectedFingerprint item then
                        invalidOp "Prepared baseline target contains a local obstruction."

                    if actual.IsNone then
                        let! bytes = remote.Read item
                        let received = LibraryFilesystem.content bytes

                        if Some $"{received.Blake3Hash}:{received.Sha256Hash}:{received.Size}"
                           <> expectedFingerprint item then
                            invalidOp "Baseline download does not match its selected revision."

                        observe "beforePublish"
                        do! check remote configuration expected
                        let staged = Path.Combine(configuration.GraceDirectory, $"library-baseline-{operation.OperationId:N}.tmp")
                        LibraryFilesystem.publishAtomic revalidate staged target None bytes

                observe "publish"
                verify configuration relative item

            do! check remote configuration expected
            revalidate ()
            if item.Tombstone.IsNone then verify configuration relative item
            completeBaselineItem configuration.GraceStatusFile expected operation
            observe "item"
        }

    /// Resumes acquisition and installation from SQLite; callers hold the existing shared working-root lease.
    let resumeWith observe (remote: Remote) (configuration: GraceConfiguration) (cancellationToken: CancellationToken) =
        task {
            do! acquire observe remote configuration cancellationToken
            let expected = current configuration

            if not expected.Baseline.Value.Applied then
                let work = orderedWork expected (readOperations configuration.GraceStatusFile configuration.RepositoryId)
                let mutable index = 0

                while index < work.Length do
                    cancellationToken.ThrowIfCancellationRequested()
                    let operation, relative = work[index]

                    if not operation.Terminal then
                        do! installItem observe remote configuration expected operation relative
                    elif operation.BaselineItem.Value.Tombstone.IsNone then
                        verify configuration relative operation.BaselineItem.Value

                    index <- index + 1

                observe "beforeBoundary"
                do! check remote configuration expected

                work
                |> Array.iter (fun (operation, relative) ->
                    if operation.BaselineItem.Value.Tombstone.IsNone then
                        verify configuration relative operation.BaselineItem.Value)

                let completed = readOperations configuration.GraceStatusFile configuration.RepositoryId

                let materialized =
                    readItems configuration.GraceStatusFile configuration.RepositoryId
                    |> Array.sortBy (fun item -> item.ItemId)

                let selected =
                    completed
                    |> Array.choose (fun op ->
                        op.BaselineItem
                        |> Option.filter (fun item -> item.Tombstone.IsNone))
                    |> Array.sortBy (fun item -> item.ItemId)

                if materialized <> selected then
                    invalidOp "Baseline materialized records changed before boundary completion."

                completeBaseline configuration.GraceStatusFile expected completed
                observe "boundary"
        }
