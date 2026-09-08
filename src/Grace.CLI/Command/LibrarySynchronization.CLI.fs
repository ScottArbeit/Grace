namespace Grace.CLI.Command

open Grace.CLI
open Grace.CLI.LibraryLocalState
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Library
open Grace.Shared.Utilities
open Grace.Shared.Validation.Library
open Grace.Types.Common
open Grace.Types.Library
open System
open System.IO
open System.Threading

/// Runs the Windows Library tracer using the existing SDK, local database, and shared root exclusion.
module internal LibrarySynchronization =

    /// Exposes local participation and completed progress through the ordinary CLI result envelope.
    [<CLIMutable>]
    type Status =
        {
            Enabled: bool
            State: string
            LibraryCatalogVersion: Guid option
            CursorEpoch: string option
            AppliedCursor: string option
            PendingOperationCount: int
        }

    /// Applies the validated local repository locator to one SDK request.
    let private scoped (configuration: GraceConfiguration) correlationId (parameters: #LibraryParameters) =
        parameters.OwnerId <- configuration.OwnerId.ToString("D")
        parameters.OwnerName <- configuration.OwnerName
        parameters.OrganizationId <- configuration.OrganizationId.ToString("D")
        parameters.OrganizationName <- configuration.OrganizationName
        parameters.RepositoryId <- configuration.RepositoryId.ToString("D")
        parameters.RepositoryName <- configuration.RepositoryName
        parameters.CorrelationId <- correlationId
        parameters

    /// Keeps SDK error details visible at the command boundary.
    let private value result =
        result
        |> Result.defaultWith (fun error -> invalidOp error.Error)
        |> fun result -> result.ReturnValue

    /// Reads the sole persisted participation record after initialization.
    let private state (configuration: GraceConfiguration) =
        readRepository configuration.GraceStatusFile configuration.RepositoryId
        |> Option.defaultWith (fun () -> invalidOp "Library synchronization is not enabled for this working copy.")

    /// Maps an accepted relative path into the configured working root.
    let private fullPath (configuration: GraceConfiguration) relative =
        let normalized =
            normalizeRepositoryRelativePath relative
            |> Result.defaultWith invalidOp

        let root =
            Path
                .GetFullPath(configuration.RootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        let result = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)))

        if not (result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) then
            invalidOp "Library path escaped the working root."

        result

    /// Resolves current item paths through materialized parent/name identity rather than duplicated descendant paths.
    let rec private parentPath (items: LibraryItemDto array) (parent: LibraryParentDto) =
        match parent.Kind, parent.LibraryPath, parent.ItemId with
        | "root", Some path, None -> path
        | "item", None, Some id ->
            let item =
                items
                |> Array.find (fun item -> item.ItemId = id && item.Tombstone.IsNone)

            let ns =
                item.Namespace
                |> Option.defaultWith (fun () -> invalidOp "Materialized Library parent has no namespace.")

            parentPath items ns.Parent + "/" + ns.Name
        | _ -> invalidOp "Library parent does not identify a configured root or materialized directory."

    /// Resolves a live item's current parent/name path.
    let private itemPath items (item: LibraryItemDto) =
        let ns =
            item.Namespace
            |> Option.defaultWith (fun () -> invalidOp "Library item has no live namespace.")

        parentPath items ns.Parent + "/" + ns.Name

    /// Captures the exact materialized item and parent chain used to choose an effect.
    let private ancestry (items: LibraryItemDto array) (item: LibraryItemDto) =
        /// Retains every materialized parent whose namespace determines the target path.
        let rec parents (parent: LibraryParentDto) =
            match parent.ItemId with
            | None -> []
            | Some id ->
                let previous =
                    items
                    |> Array.find (fun value -> value.ItemId = id)

                previous
                :: parents previous.Namespace.Value.Parent

        let prior =
            items
            |> Array.tryFind (fun value -> value.ItemId = item.ItemId)
            |> Option.toList

        let ns =
            item.Namespace
            |> Option.orElseWith (fun () ->
                item.Tombstone
                |> Option.map (fun value -> value.LastNamespace))

        List.append
            prior
            (ns
             |> Option.map (fun value -> parents value.Parent)
             |> Option.defaultValue [])
        |> List.toArray

    /// Builds a complete-byte comparison key from an accepted content descriptor.
    let private contentFingerprint (content: LibraryContentVersionDto) = $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}"

    /// Reads truthful local status without treating received metadata as completed work.
    let status (configuration: GraceConfiguration) =
        task {
            do! initialize configuration.GraceStatusFile

            match readRepository configuration.GraceStatusFile configuration.RepositoryId with
            | None ->
                return
                    { Enabled = false; State = "disabled"; LibraryCatalogVersion = None; CursorEpoch = None; AppliedCursor = None; PendingOperationCount = 0 }
            | Some current ->
                let count =
                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                    |> Array.filter (fun op -> not op.Terminal)
                    |> Array.length

                return
                    {
                        Enabled = true
                        State = current.State
                        LibraryCatalogVersion = Some current.Catalog.Version
                        CursorEpoch = Some current.CursorEpoch
                        AppliedCursor = Some current.AppliedCursor
                        PendingOperationCount = count
                    }
        }

    /// Enables one Windows copy from the immutable empty baseline, keeping existing participation unchanged on retry.
    let enable (configuration: GraceConfiguration) correlationId cancellationToken =
        task {
            if not (OperatingSystem.IsWindows()) then
                invalidOp "Library synchronization requires Windows 11."

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
            do! initialize configuration.GraceStatusFile

            if (readRepository configuration.GraceStatusFile configuration.RepositoryId)
                .IsNone then
                let! result = Libraries.StartBootstrap(scoped configuration correlationId (StartLibraryBootstrapParameters()))
                let page = value result

                if page.LibraryCatalog.RepositoryId
                   <> configuration.RepositoryId
                   || page.LibraryCatalog.Libraries.Length <> 1
                   || page.Items.Length <> 0
                   || page.NextPageToken.IsSome then
                    invalidOp "This tracer requires one initially empty Library baseline."

                let root = fullPath configuration page.LibraryCatalog.Libraries[0]

                if
                    Directory.Exists(root)
                    && Directory.EnumerateFileSystemEntries(root)
                       |> Seq.isEmpty
                       |> not
                then
                    invalidOp "Enable synchronization before adding files to the initially empty Library."

                let! catalogResult = Libraries.GetCatalog(scoped configuration correlationId (GetLibraryCatalogParameters()))

                if value catalogResult <> page.LibraryCatalog then
                    invalidOp "Library catalog changed before the empty baseline was enabled."

                Directory.CreateDirectory(root) |> ignore

                LibraryLocalState.enable
                    configuration.GraceStatusFile
                    {
                        RepositoryId = configuration.RepositoryId
                        WorkingCopyId = Guid.NewGuid()
                        Catalog = page.LibraryCatalog
                        CursorEpoch = page.CursorEpoch
                        AppliedCursor = page.BoundaryCursor
                        NextPageToken = None
                        State = "current"
                    }

            return! status configuration
        }

    /// Reads the authoritative catalog for a Watch lifetime or a synchronization decision.
    let catalog configuration correlationId =
        task {
            let! result = Libraries.GetCatalog(scoped configuration correlationId (GetLibraryCatalogParameters()))
            return value result
        }

    /// Classifies actual Watch observations against terminal publication evidence under shared exclusion.
    let classifyWatchObservations (configuration: GraceConfiguration) (paths: string array) cancellationToken =
        task {
            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken

            match readRepository configuration.GraceStatusFile configuration.RepositoryId with
            | None -> ()
            | Some current ->
                for path in paths do
                    let relative =
                        Path
                            .GetRelativePath(configuration.RootDirectory, path)
                            .Replace('\\', '/')

                    if current.Catalog.Libraries
                       |> Array.exists (fun root -> relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) then
                        consumeEcho configuration.GraceStatusFile configuration.RepositoryId relative (LibraryFilesystem.fingerprint path)
                        |> ignore

                pruneClassified configuration.GraceStatusFile configuration.RepositoryId 128
        }

    /// Rereads the server catalog while the shared root lease protects local decision state.
    let private checkCatalog configuration correlationId (expected: RepositoryState) =
        task {
            let! result = Libraries.GetCatalog(scoped configuration correlationId (GetLibraryCatalogParameters()))
            let catalog = value result

            if catalog <> expected.Catalog
               || state configuration <> expected then
                invalidOp "Library catalog or applied predecessor changed; synchronization has stopped before local effects."
        }

    /// Captures all observable saved files whose parent is materialized, before incoming metadata can replace their base.
    let internal captureSaved (configuration: GraceConfiguration) =
        let current = state configuration
        let items = readItems configuration.GraceStatusFile configuration.RepositoryId
        let mutable operations = readOperations configuration.GraceStatusFile configuration.RepositoryId
        let mutable captured = false

        // A directory move can reach the filesystem before its SQLite completion. Resolve observations
        // through that exact prepared placement while continuing to capture the original materialized item as the edit base.
        let pathItems =
            items
            |> Array.map (fun item ->
                operations
                |> Array.tryPick (fun operation ->
                    if operation.Prepared
                       && not operation.Terminal
                       && operation.ExpectedCatalogVersion = current.Catalog.Version
                       && operation.ExpectedCursor = current.AppliedCursor then
                        operation.Accepted
                        |> Option.bind (fun change ->
                            if
                                change.Item.ItemId = item.ItemId
                                && item.ItemKind = ItemKind.Directory
                                && change.Item.Tombstone.IsNone
                                && ancestry items change.Item = operation.ExpectedAncestry
                                && not (Directory.Exists(fullPath configuration (itemPath items item)))
                                && Directory.Exists(fullPath configuration operation.TargetPath)
                            then
                                Some { item with Namespace = change.Item.Namespace }
                            else
                                None)
                    else
                        None)
                |> Option.defaultValue item)

        /// Resolves only filesystem observation placement, leaving the materialized edit-base record untouched.
        let observedPath (item: LibraryItemDto) =
            itemPath
                pathItems
                (pathItems
                 |> Array.find (fun current -> current.ItemId = item.ItemId))

        for libraryRoot in current.Catalog.Libraries do
            let root = fullPath configuration libraryRoot

            if not (Directory.Exists(root)) then
                invalidOp "A participating Library root is missing."

            let paths =
                Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                |> Array.sortBy (fun path -> path.Length, path)

            for path in paths do
                if File
                    .GetAttributes(path)
                       .HasFlag(FileAttributes.ReparsePoint) then
                    invalidOp "Library synchronization does not follow reparse points."

                let relative =
                    Path
                        .GetRelativePath(configuration.RootDirectory, path)
                        .Replace('\\', '/')

                let parentRelative =
                    Path
                        .GetRelativePath(configuration.RootDirectory, Path.GetDirectoryName(path))
                        .Replace('\\', '/')

                let parent =
                    if pathsEqual parentRelative libraryRoot then
                        Some { Kind = "root"; LibraryPath = Some libraryRoot; ItemId = None }
                    else
                        items
                        |> Array.tryFind (fun item ->
                            item.ItemKind = ItemKind.Directory
                            && item.Tombstone.IsNone
                            && pathsEqual (observedPath item) parentRelative)
                        |> Option.map (fun item -> { Kind = "item"; LibraryPath = None; ItemId = Some item.ItemId })

                match parent with
                | None -> ()
                | Some parent ->
                    let prior =
                        items
                        |> Array.tryFind (fun item ->
                            item.Tombstone.IsNone
                            && pathsEqual (observedPath item) relative)
                        |> Option.orElseWith (fun () ->
                            let prepared =
                                operations
                                |> Array.choose (fun operation ->
                                    if operation.Prepared
                                       && not operation.Terminal
                                       && operation.ExpectedCatalogVersion = current.Catalog.Version
                                       && operation.ExpectedCursor = current.AppliedCursor
                                       && pathsEqual operation.TargetPath relative then
                                        operation.Accepted
                                        |> Option.bind (fun change ->
                                            if change.Item.ItemKind = ItemKind.File
                                               && change.Item.Tombstone.IsNone
                                               && ancestry items change.Item = operation.ExpectedAncestry then
                                                items
                                                |> Array.tryFind (fun item ->
                                                    item.ItemId = change.Item.ItemId
                                                    && item.Tombstone.IsNone)
                                            else
                                                None)
                                    else
                                        None)

                            if prepared.Length = 1 then Some prepared[0] else None)

                    let itemKind = if Directory.Exists(path) then ItemKind.Directory else ItemKind.File

                    let bytes =
                        if itemKind = ItemKind.File then
                            Some((LibraryFilesystem.stableRead path).Bytes)
                        else
                            None

                    let unchanged =
                        match prior, bytes with
                        | Some item, None -> item.ItemKind = ItemKind.Directory
                        | Some item, Some bytes ->
                            item.Content
                            |> Option.exists (fun content ->
                                let captured = LibraryFilesystem.content bytes in

                                content.Blake3Hash = captured.Blake3Hash
                                && content.Sha256Hash = captured.Sha256Hash
                                && content.Size = captured.Size)
                        | _ -> false

                    let pendingSource =
                        let pending =
                            operations
                            |> Array.filter (fun operation ->
                                operation.Direction = "local"
                                && not operation.Terminal)

                        let samePath =
                            pending
                            |> Array.filter (fun operation -> pathsEqual operation.SourcePath relative)

                        (if samePath.Length > 0 then
                             samePath
                         else
                             pending
                             |> Array.filter (fun operation ->
                                 prior
                                 |> Option.exists (fun prior ->
                                     operation.MaterializedBase
                                     |> Option.exists (fun item -> item.ItemId = prior.ItemId))))
                        |> Array.tryLast
                        |> Option.exists (fun operation -> operation.SourceBytes = bytes)

                    let publishedPending =
                        operations
                        |> Array.filter (fun operation ->
                            operation.Prepared
                            && not operation.Terminal
                            && operation.ExpectedCatalogVersion = current.Catalog.Version
                            && operation.ExpectedCursor = current.AppliedCursor
                            && pathsEqual operation.TargetPath relative
                            && operation.Accepted
                               |> Option.exists (fun change ->
                                   ancestry items change.Item = operation.ExpectedAncestry
                                   && (if change.Item.ItemKind = ItemKind.Directory then
                                           Directory.Exists(path)
                                       else
                                           change.Item.Content
                                           |> Option.exists (fun descriptor ->
                                               bytes
                                               |> Option.exists (fun source ->
                                                   let source = LibraryFilesystem.content source

                                                   descriptor.Blake3Hash = source.Blake3Hash
                                                   && descriptor.Sha256Hash = source.Sha256Hash
                                                   && descriptor.Size = source.Size)))))

                    if not (bytes |> Option.exists Array.isEmpty)
                       && not unchanged
                       && not pendingSource
                       && publishedPending.Length <> 1 then
                        let origin =
                            if prior.IsSome then
                                None
                            else
                                operations
                                |> Array.tryFind (fun operation ->
                                    operation.Direction = "local"
                                    && not operation.Terminal
                                    && operation.MaterializedBase.IsNone
                                    && operation.OriginatingCreateId.IsNone
                                    && pathsEqual operation.SourcePath relative)
                                |> Option.map (fun operation -> operation.OperationId)

                        let operation =
                            {
                                OperationId = Guid.NewGuid()
                                Direction = "local"
                                SourcePath = relative
                                SourceBytes = bytes
                                MaterializedBase = prior
                                OriginatingCreateId = origin
                                Parent = parent
                                Name = Path.GetFileName(path)
                                ItemKind = itemKind
                                RequestJson = None
                                Uploaded = false
                                Accepted = None
                                Prepared = false
                                ExpectedCatalogVersion = current.Catalog.Version
                                ExpectedCursor = current.AppliedCursor
                                ExpectedAncestry = Array.empty
                                ExpectedTarget = None
                                TargetPath = ""
                                Terminal = false
                                EchoPending = false
                                CreatedAtTicks = DateTime.UtcNow.Ticks
                            }

                        insertOperation configuration.GraceStatusFile configuration.RepositoryId operation
                        operations <- Array.append operations [| operation |]
                        captured <- true

        captured

    /// Freezes and submits one saved request; initial-create successors resolve only from their exact terminal originating create.
    let private submitLocal (configuration: GraceConfiguration) correlationId (operation: PendingOperation) =
        task {
            let mutable operation = operation

            if operation.Accepted.IsNone then
                let prior =
                    match operation.OriginatingCreateId with
                    | None -> operation.MaterializedBase
                    | Some id ->
                        let origin =
                            readOperations configuration.GraceStatusFile configuration.RepositoryId
                            |> Array.find (fun value -> value.OperationId = id)

                        if not origin.Terminal then
                            invalidOp "The originating Library create must materialize before its saved successor can upload."

                        Some origin.Accepted.Value.Item

                let current = state configuration
                do! checkCatalog configuration correlationId current

                if current.Catalog.Version
                   <> operation.ExpectedCatalogVersion then
                    invalidOp "Saved Library operation belongs to another catalog version."

                let mutable prepared = None

                if operation.SourceBytes.IsSome
                   && not operation.Uploaded then
                    let content = LibraryFilesystem.content operation.SourceBytes.Value
                    let parameters = scoped configuration correlationId (PrepareLibraryContentParameters())
                    parameters.OperationId <- operation.OperationId
                    parameters.Blake3Hash <- content.Blake3Hash
                    parameters.Sha256Hash <- content.Sha256Hash
                    parameters.Size <- content.Size
                    let! result = Libraries.PrepareContent parameters
                    prepared <- Some(value result)

                let! request =
                    task {
                        match operation.RequestJson with
                        | Some json -> return deserialize<SubmitLibraryChangeParameters> json
                        | None ->
                            let request = scoped configuration correlationId (SubmitLibraryChangeParameters())
                            request.OperationId <- operation.OperationId
                            request.LibraryCatalogVersion <- operation.ExpectedCatalogVersion
                            request.ItemKind <- operation.ItemKind

                            match prior with
                            | Some item ->
                                request.ChangeKind <- ChangeKind.UpdateContent
                                request.ItemId <- Nullable(item.ItemId)

                                request.ContentPrecondition <-
                                    Some
                                        {
                                            ItemId = item.ItemId
                                            ExpectedContentVersionId = item.Content.Value.ContentVersionId
                                            ExpectedContentRevision = item.ContentRevision.Value
                                        }
                            | None ->
                                request.ChangeKind <-
                                    if operation.ItemKind = ItemKind.Directory then
                                        ChangeKind.CreateDirectory
                                    else
                                        ChangeKind.CreateFile

                                let slotParameters = scoped configuration correlationId (GetLibraryNamespaceSlotParameters())
                                slotParameters.Parent <- Some operation.Parent
                                slotParameters.Name <- operation.Name
                                let! result = Libraries.GetNamespaceSlot slotParameters
                                let slot = value result

                                if slot.OccupantItemId.IsSome then
                                    invalidOp "Library creation slot is occupied; saved bytes are retained."

                                request.CreationSlotExpectation <-
                                    Some { Parent = slot.Parent; Name = slot.Name; ExpectedSlotVersion = slot.SlotVersion; ExpectedState = "vacant" }

                            prepared
                            |> Option.iter (fun value -> request.UploadSessionId <- Nullable(value.UploadSessionId))

                            validateChangeShape request
                            |> Result.defaultWith invalidOp

                            let updated = { operation with RequestJson = Some(serialize request) }
                            updateOperation configuration.GraceStatusFile configuration.RepositoryId operation updated
                            operation <- updated
                            return request
                    }

                match prepared with
                | Some prepared ->
                    if request.UploadSessionId.Value
                       <> prepared.UploadSessionId then
                        invalidOp "Prepared Library session changed after the request was frozen."

                    let stagingPath = Path.Combine(configuration.GraceDirectory, $"library-upload-{operation.OperationId:N}.tmp")

                    try
                        do
                            use stream = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            stream.Write(operation.SourceBytes.Value)
                            stream.Flush(true)

                        let! uploaded =
                            LibraryManifestUpload.uploadPrepared configuration operation.OperationId prepared operation.SourcePath stagingPath correlationId

                        value uploaded |> ignore
                    finally
                        if File.Exists(stagingPath) then File.Delete(stagingPath)

                    let updated = { operation with Uploaded = true }
                    updateOperation configuration.GraceStatusFile configuration.RepositoryId operation updated
                    operation <- updated
                | None -> ()

                do! checkCatalog configuration correlationId (state configuration)
                let! result = Libraries.SubmitChange request
                let receipt = value result

                let expectedHash =
                    match toChangeCommand configuration.RepositoryId request with
                    | LibraryChangeCommand.CreateFile (_, hash, _, _, _)
                    | LibraryChangeCommand.CreateDirectory (_, hash, _, _)
                    | LibraryChangeCommand.UpdateContent (_, hash, _, _, _, _, _) -> hash
                    | _ -> invalidOp "Unexpected local Library request kind."

                if receipt.OperationId <> operation.OperationId
                   || receipt.RequestHash <> expectedHash then
                    invalidOp "Library receipt does not match the frozen request."

                let accepted =
                    receipt.Change
                    |> Option.defaultWith (fun () ->
                        invalidOp $"Library submission returned {receipt.Outcome}: {receipt.ReasonCode}. Saved bytes remain pending.")

                updateOperation configuration.GraceStatusFile configuration.RepositoryId operation { operation with Accepted = Some accepted }
        }

    /// Applies one ordered accepted result after saving any newer local bytes and rechecking exact local preconditions.
    let internal applyChangeWith afterPublication (configuration: GraceConfiguration) correlationId (expected: RepositoryState) (change: LibraryChangeDto) =
        task {
            do! checkCatalog configuration correlationId expected
            captureSaved configuration |> ignore
            let items = readItems configuration.GraceStatusFile configuration.RepositoryId

            let previous =
                items
                |> Array.tryFind (fun item -> item.ItemId = change.Item.ItemId)

            let targetRelative =
                match change.Item.Namespace with
                | Some ns -> parentPath items ns.Parent + "/" + ns.Name
                | None -> let ns = change.Item.Tombstone.Value.LastNamespace in parentPath items ns.Parent + "/" + ns.Name

            let target = fullPath configuration targetRelative

            let priorPath =
                previous
                |> Option.filter (fun item -> item.Tombstone.IsNone)
                |> Option.map (itemPath items)
                |> Option.defaultValue targetRelative

            let priorTarget = fullPath configuration priorPath
            let operations = readOperations configuration.GraceStatusFile configuration.RepositoryId

            let existing =
                operations
                |> Array.tryFind (fun operation -> operation.OperationId = change.OperationId)

            let mutable operation =
                existing
                |> Option.defaultValue
                    {
                        OperationId = change.OperationId
                        Direction = "remote"
                        SourcePath = priorPath
                        SourceBytes = None
                        MaterializedBase = previous
                        OriginatingCreateId = None
                        Parent =
                            (change.Item.Namespace
                             |> Option.orElseWith (fun () ->
                                 change.Item.Tombstone
                                 |> Option.map (fun value -> value.LastNamespace)))
                                .Value
                                .Parent
                        Name = Path.GetFileName(target)
                        ItemKind = change.Item.ItemKind
                        RequestJson = None
                        Uploaded = false
                        Accepted = Some change
                        Prepared = false
                        ExpectedCatalogVersion = expected.Catalog.Version
                        ExpectedCursor = expected.AppliedCursor
                        ExpectedAncestry = Array.empty
                        ExpectedTarget = None
                        TargetPath = targetRelative
                        Terminal = false
                        EchoPending = false
                        CreatedAtTicks = DateTime.UtcNow.Ticks
                    }

            if existing.IsNone then
                insertOperation configuration.GraceStatusFile configuration.RepositoryId operation

            if operation.Accepted.IsSome
               && operation.Accepted <> Some change then
                invalidOp "Library accepted operation identity changed."

            if operation.Terminal then
                if expected.AppliedCursor
                   <> change.Item.LastChangeCursor then
                    invalidOp "Library terminal operation is out of pull order."
            else
                let expectedContent =
                    change.Item.Content
                    |> Option.map contentFingerprint

                /// Preserves excluded empty files both before preparation and immediately before later effects.
                let requireNonemptyTarget () =
                    if change.Item.ItemKind = ItemKind.File
                       && File.Exists(target)
                       && FileInfo(target).Length = 0L then
                        invalidOp "Library synchronization preserves excluded empty files."

                requireNonemptyTarget ()
                let actual = LibraryFilesystem.fingerprint target

                let saved =
                    operations
                    |> Array.exists (fun pending ->
                        pending.Direction = "local"
                        && not pending.Terminal
                        && pathsEqual pending.SourcePath targetRelative
                        && pending.SourceBytes
                           |> Option.exists (fun bytes ->
                               let content = LibraryFilesystem.content bytes in actual = Some $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}"))

                if not operation.Prepared then
                    let materialized =
                        previous
                        |> Option.bind (fun item -> item.Content)
                        |> Option.map contentFingerprint

                    if change.Item.ItemKind = ItemKind.File
                       && actual.IsSome
                       && actual <> expectedContent
                       && actual <> materialized
                       && not saved then
                        invalidOp "Library target contains uncaptured local bytes."

                    let prepared =
                        { operation with
                            Accepted = Some change
                            Prepared = true
                            ExpectedCatalogVersion = expected.Catalog.Version
                            ExpectedCursor = expected.AppliedCursor
                            ExpectedAncestry = ancestry items change.Item
                            ExpectedTarget = actual
                            TargetPath = targetRelative
                            EchoPending =
                                if change.Item.Tombstone.IsSome then
                                    actual.IsSome
                                elif change.Item.ItemKind = ItemKind.Directory then
                                    actual.IsNone || priorPath <> targetRelative
                                else
                                    actual <> expectedContent
                                    || priorPath <> targetRelative
                        }

                    updateOperation configuration.GraceStatusFile configuration.RepositoryId operation prepared
                    operation <- prepared

                /// Rejects changed durable authority or physical ancestry immediately before filesystem effects and completion.
                let revalidate () =
                    if state configuration <> expected
                       || expected.Catalog.Version
                          <> operation.ExpectedCatalogVersion
                       || expected.AppliedCursor <> operation.ExpectedCursor then
                        invalidOp "Library catalog or predecessor changed before filesystem effects."

                    if ancestry (readItems configuration.GraceStatusFile configuration.RepositoryId) change.Item
                       <> operation.ExpectedAncestry then
                        invalidOp "Library materialized ancestry changed before filesystem effects."

                    let persisted =
                        readOperations configuration.GraceStatusFile configuration.RepositoryId
                        |> Array.find (fun value -> value.OperationId = operation.OperationId)

                    if serialize persisted <> serialize operation then
                        invalidOp "Library exact operation changed before filesystem effects."

                    requireNonemptyTarget ()

                    let mutable parent = Path.GetDirectoryName(target)

                    let root =
                        Path
                            .GetFullPath(configuration.RootDirectory)
                            .TrimEnd(Path.DirectorySeparatorChar)

                    while parent.Length >= root.Length do
                        if
                            not (Directory.Exists(parent))
                            || File
                                .GetAttributes(parent)
                                .HasFlag(FileAttributes.ReparsePoint)
                        then
                            invalidOp "Library filesystem ancestry changed before publication."

                        parent <- Path.GetDirectoryName(parent)

                revalidate ()

                /// Allows rename source removal only when changed positive bytes already have an exact accepted saved operation.
                let requireMovedSource () =
                    if
                        change.Item.Tombstone.IsNone
                        && change.Item.ItemKind = ItemKind.File
                        && priorPath <> targetRelative
                        && File.Exists(priorTarget)
                    then
                        let actual = LibraryFilesystem.fingerprint priorTarget

                        let materialized =
                            previous
                            |> Option.bind (fun item -> item.Content)
                            |> Option.map contentFingerprint

                        if FileInfo(priorTarget).Length = 0L then
                            invalidOp "Library rename preserves an excluded empty source."

                        if actual <> materialized then
                            let saved =
                                readOperations configuration.GraceStatusFile configuration.RepositoryId
                                |> Array.tryFind (fun pending ->
                                    pending.Direction = "local"
                                    && not pending.Terminal
                                    && pending.Uploaded
                                    && pending.RequestJson.IsSome
                                    && pathsEqual pending.SourcePath priorPath
                                    && (pending.MaterializedBase
                                        |> Option.exists (fun item -> item.ItemId = change.Item.ItemId))
                                    && (pending.SourceBytes
                                        |> Option.exists (fun bytes ->
                                            bytes.Length > 0
                                            && let content = LibraryFilesystem.content bytes in
                                               actual = Some $"{content.Blake3Hash}:{content.Sha256Hash}:{content.Size}"))
                                    && (pending.Accepted
                                        |> Option.exists (fun accepted ->
                                            accepted.OperationId = pending.OperationId
                                            && (accepted.Item.Content
                                                |> Option.map contentFingerprint) = actual)))

                            match saved with
                            | None -> invalidOp "Moved Library source has no exact accepted saved content."
                            | Some saved ->
                                revalidate ()

                                let persisted =
                                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                                    |> Array.find (fun pending -> pending.OperationId = saved.OperationId)

                                if serialize persisted <> serialize saved
                                   || LibraryFilesystem.fingerprint priorTarget
                                      <> actual then
                                    invalidOp "Accepted Library source changed immediately before rename."

                requireMovedSource ()

                // A saved target can change after preparation. Preserve it first, then refresh only the
                // filesystem precondition while retaining the original accepted result and exact edit base.
                if change.Item.ItemKind = ItemKind.File
                   && operation.Prepared
                   && actual <> operation.ExpectedTarget
                   && actual <> expectedContent then
                    if not saved then
                        invalidOp "Library target changed after preparation without a durable saved source."

                    let refreshed = { operation with ExpectedTarget = actual }
                    updateOperation configuration.GraceStatusFile configuration.RepositoryId operation refreshed
                    operation <- refreshed
                    revalidate ()

                if change.Item.Tombstone.IsSome then
                    let materialized =
                        previous
                        |> Option.bind (fun item -> item.Content)
                        |> Option.map contentFingerprint

                    if File.Exists(priorTarget) then
                        if LibraryFilesystem.fingerprint priorTarget
                           <> materialized then
                            invalidOp "Library deletion would remove locally changed bytes."

                        File.Delete(priorTarget)
                    elif Directory.Exists(priorTarget) then
                        Directory.Delete(priorTarget, false)
                elif change.Item.ItemKind = ItemKind.Directory then
                    if
                        priorPath <> targetRelative
                        && Directory.Exists(priorTarget)
                        && Directory.Exists(target)
                    then
                        invalidOp "Library directory move target is occupied."

                    if not (Directory.Exists(target)) then
                        if
                            priorPath <> targetRelative
                            && Directory.Exists(priorTarget)
                        then
                            Directory.Move(priorTarget, target)
                        else
                            Directory.CreateDirectory(target) |> ignore
                else
                    if LibraryFilesystem.fingerprint target
                       <> expectedContent then
                        let content = change.Item.Content.Value
                        let readParameters = scoped configuration correlationId (PrepareLibraryContentReadParameters())
                        readParameters.ItemId <- change.Item.ItemId
                        readParameters.ContentVersionId <- content.ContentVersionId
                        readParameters.ContentRevision <- change.Item.ContentRevision.Value
                        let! readResult = Libraries.PrepareContentRead readParameters
                        let grant = value readResult
                        let prefix = "/libraries/content/"

                        if not (grant.DownloadPath.StartsWith(prefix, StringComparison.Ordinal)) then
                            invalidOp "Library read descriptor has an unexpected route."

                        let token = Uri.UnescapeDataString(grant.DownloadPath.Substring(prefix.Length))
                        let! downloaded = Libraries.DownloadContent(token, correlationId)
                        let bytes = value downloaded
                        let received = LibraryFilesystem.content bytes

                        if received.Blake3Hash <> content.Blake3Hash
                           || received.Sha256Hash <> content.Sha256Hash
                           || received.Size <> content.Size then
                            invalidOp "Downloaded Library bytes do not match accepted content."

                        do! checkCatalog configuration correlationId expected
                        let staged = Path.Combine(configuration.GraceDirectory, $"library-publish-{operation.OperationId:N}.tmp")

                        LibraryFilesystem.publishAtomic
                            (fun () ->
                                revalidate ()
                                requireMovedSource ())
                            staged
                            target
                            operation.ExpectedTarget
                            bytes

                    if
                        priorPath <> targetRelative
                        && File.Exists(priorTarget)
                    then
                        requireMovedSource ()
                        File.Delete(priorTarget)

                    if LibraryFilesystem.fingerprint target
                       <> expectedContent then
                        invalidOp "Library final bytes do not match accepted content."

                afterPublication ()
                do! checkCatalog configuration correlationId expected
                revalidate ()

                let finalExpected =
                    if change.Item.Tombstone.IsSome then None
                    elif change.Item.ItemKind = ItemKind.Directory then Some "directory"
                    else expectedContent

                if LibraryFilesystem.fingerprint target
                   <> finalExpected then
                    invalidOp "Library final filesystem state changed before atomic local completion."

                complete configuration.GraceStatusFile expected operation
        }

    /// Pulls contiguous changes; an empty HasMore page remains incomplete and is retried by a later run.
    let private pull configuration correlationId =
        task {
            let mutable continuePages = true
            let mutable caughtUp = false

            while continuePages do
                let before = state configuration
                do! checkCatalog configuration correlationId before
                let parameters = scoped configuration correlationId (GetLibraryChangesParameters())
                parameters.AfterCursor <- before.AppliedCursor
                parameters.PageToken <- before.NextPageToken |> Option.defaultValue null
                let! result = Libraries.GetChanges parameters
                let page = value result

                if page.Rebaseline.IsSome
                   || page.CursorEpoch <> before.CursorEpoch then
                    invalidOp "Library rebaseline is required; local saved work is retained."

                let mutable index = 0

                while index < page.Changes.Length do
                    do! applyChangeWith ignore configuration correlationId (state configuration) page.Changes[index]
                    index <- index + 1

                if (state configuration).AppliedCursor
                   <> page.LastCursor then
                    invalidOp "Library page cursor does not describe completed local application."

                caughtUp <- not page.HasMore
                continuePages <- page.HasMore && page.Changes.Length > 0

                if page.HasMore && page.NextPageToken.IsNone then
                    invalidOp "Library incomplete page omitted its continuation."

                let completed = state configuration
                do! checkCatalog configuration correlationId completed
                recordPage configuration.GraceStatusFile completed (if page.HasMore then page.NextPageToken else None)

            return caughtUp
        }

    /// Runs finite pending submissions and ordered pulls under the existing shared root lease.
    let run (configuration: GraceConfiguration) correlationId cancellationToken =
        task {
            if not (OperatingSystem.IsWindows()) then
                invalidOp "Library synchronization requires Windows 11."

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
            do! initialize configuration.GraceStatusFile
            let original = state configuration
            setState configuration.GraceStatusFile original "catchingUp"

            try
                let mutable again = true
                let mutable caughtUp = false

                while again do
                    cancellationToken.ThrowIfCancellationRequested()
                    captureSaved configuration |> ignore
                    let before = state configuration
                    let pending = readOperations configuration.GraceStatusFile configuration.RepositoryId
                    let mutable submitted = false
                    let mutable index = 0

                    while index < pending.Length do
                        let operation = pending[index]

                        let originReady =
                            operation.OriginatingCreateId
                            |> Option.forall (fun id ->
                                pending
                                |> Array.exists (fun value -> value.OperationId = id && value.Terminal))

                        if operation.Direction = "local"
                           && not operation.Terminal
                           && operation.Accepted.IsNone
                           && originReady then
                            do! submitLocal configuration correlationId operation
                            submitted <- true

                        index <- index + 1

                    let! completePull = pull configuration correlationId
                    caughtUp <- completePull
                    let captured = captureSaved configuration

                    let remaining =
                        readOperations configuration.GraceStatusFile configuration.RepositoryId
                        |> Array.filter (fun operation -> not operation.Terminal)

                    again <-
                        completePull
                        && remaining.Length > 0
                        && (submitted
                            || captured
                            || before.AppliedCursor
                               <> (state configuration).AppliedCursor)

                let current = state configuration

                let pendingCount =
                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                    |> Array.filter (fun operation -> not operation.Terminal)
                    |> Array.length

                setState configuration.GraceStatusFile current (if caughtUp && pendingCount = 0 then "current" else "catchingUp")
                pruneClassified configuration.GraceStatusFile configuration.RepositoryId 128
                return! status configuration
            with
            | ex ->
                setState configuration.GraceStatusFile (state configuration) "blocked"
                return raise ex
        }
