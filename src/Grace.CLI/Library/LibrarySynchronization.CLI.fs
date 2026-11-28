namespace Grace.CLI.Command

open Grace.CLI
open Grace.CLI.LibraryLocalState
open Grace.CLI.LibraryOperation
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
/// Backs `grace library sync enable/run/pause/resume/status`, which owns Windows Library participation and sync health in
/// four tables of the existing local database (see LibraryLocalState). Every run records its failure, with the affected
/// path and a next step, or its success, so status can tell the user what happened.
/// Pause invariants: pause is independent from progress. Under the shared root lease, reread pause before capture,
/// synchronization, echo classification or pruning. Only explicit resume clears pause; a racing Watch tick skips pause
/// without hiding other failures. Catalog classification and version-control exclusion still apply while paused.
/// Keep frozen requests, materialized edit bases, filesystem guards and completion transactions in the Library modules.
/// Never acquire the root lease again inside a helper that already holds it. Library work must never create a WDU
/// completion. Watch consumes `LibraryContentAvailable.v1` only as a pull hint.
module internal LibrarySynchronization =

    /// Exposes local participation, completed progress and the latest failure through the ordinary CLI result envelope.
    [<CLIMutable>]
    type LibrarySynchronizationStatus =
        {
            Enabled: bool
            Paused: bool
            State: string
            LibraryCatalogVersion: Guid option
            AppliedCursor: string option
            PendingOperationCount: int
            /// What failed most recently, until a later success shows it no longer applies.
            LastError: string option
            LastErrorAt: NodaTime.Instant option
            /// The repository-relative Library path the failure affected, when known.
            LastErrorPath: string option
            LastErrorOperationId: Guid option
            /// What the user can do about the failure.
            NextStep: string option
            LastSuccessfulSyncAt: NodaTime.Instant option
        }

    /// A Library sync failure that knows the affected path and the next step a user can take.
    type LibrarySyncFailureException(message: string, path: string option, operationId: Guid option, nextStep: string) =
        inherit InvalidOperationException(message)

        /// The repository-relative Library path the failure affected, when known.
        member _.Path = path

        /// The local operation the failure affected, when known.
        member _.OperationId = operationId

        /// What the user can do about the failure.
        member _.NextStep = nextStep

    /// Next step for a failure Grace knows nothing more about; Watch retries it on a backoff.
    let internal retryNextStep = "Grace Watch retries automatically. Run 'grace library sync run' to retry now and see the full error."

    /// Next step when the server refuses this copy's applied cursor, because it's ahead of the server or from elsewhere.
    let internal cursorMismatchNextStep =
        "This copy's Library state doesn't match the server, and Grace can't reset a copy yet, so Library sync is stopped here. Your saved work is kept. Version-control work isn't affected."

    /// Explains an accepted rename that one run didn't finish applying because earlier changes were still arriving.
    let internal renameCatchingUpDiagnostic =
        "Library sync is still applying earlier changes and stopped at its per-run page limit. Nothing is obstructed; rerun the same command to finish applying this rename."

    /// Next step when a Library was removed or moved, which a copy can't follow yet. Added Libraries are followed
    /// automatically.
    let internal catalogChangedNextStep =
        "A Library was removed from this repository or moved, and this copy can't follow that yet, so Library sync is stopped here. Your saved work is kept. Version-control work isn't affected."

    /// Next step when an added Library's folder already holds files on this copy.
    let internal occupiedRootNextStep (root: string) =
        $"Move the files out of '{root}', or delete the folder. Grace Watch then adds the Library on this copy automatically. Until then Library sync waits, and nothing in that folder is changed. Version-control work isn't affected."

    /// Next step when a Library folder that holds synced files is missing on this copy.
    let internal missingRootNextStep (root: string) =
        $"Restore '{root}'. Library sync is stopped here instead of deleting those files on every copy. Your saved work is kept. Version-control work isn't affected."

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

    /// Distinguishes an ordinary paused timer tick from synchronization failures that Watch must report.
    type private LibraryPausedException() =
        inherit InvalidOperationException("Library synchronization is paused. Run 'grace library sync resume' to continue.")

    /// Raised when the server's catalog only added Libraries since this copy selected its catalog. The run switches to
    /// the new catalog and continues; it isn't a failure.
    type private LibraryCatalogAddedException() =
        inherit InvalidOperationException("The repository gained a Library; Grace is adding it on this copy.")

    /// How many times one run switches to a newer catalog before leaving the rest to the next run.
    [<Literal>]
    let private MaximumCatalogSwitchesPerRun = 3

    /// Rejects active commands after their lease-protected participation read observes pause.
    let private requireActive (current: RepositoryState) = if current.Paused then raise (LibraryPausedException())

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

    /// Returns the Library root a parent edge belongs to, following materialized live directories, or None when the
    /// chain doesn't reach a root. A path alone can't say this: a removed Library's item can flatten to the path of a
    /// Library added later.
    let rec private rootOf (items: LibraryItemDto array) (parent: LibraryParentDto) =
        match parent.Kind, parent.LibraryPath, parent.ItemId with
        | "root", Some root, None -> Some root
        | "item", None, Some id ->
            items
            |> Array.tryFind (fun item -> item.ItemId = id && item.Tombstone.IsNone)
            |> Option.bind (fun item -> item.Namespace)
            |> Option.bind (fun ns -> rootOf items ns.Parent)
        | _ -> None

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
            let health = readHealth configuration.GraceStatusFile configuration.RepositoryId
            let failure = health.LastFailure

            let withHealth (value: LibrarySynchronizationStatus) =
                { value with
                    LastError = failure |> Option.map (fun value -> value.Message)
                    LastErrorAt =
                        failure
                        |> Option.map (fun value -> NodaTime.Instant.FromUnixTimeTicks value.AtUnixTicks)
                    LastErrorPath = failure |> Option.bind (fun value -> value.Path)
                    LastErrorOperationId =
                        failure
                        |> Option.bind (fun value -> value.OperationId)
                    NextStep =
                        failure
                        |> Option.map (fun value -> value.NextStep)
                    LastSuccessfulSyncAt =
                        health.LastSuccessAtUnixTicks
                        |> Option.map NodaTime.Instant.FromUnixTimeTicks
                }

            let empty =
                {
                    Enabled = false
                    Paused = false
                    State = "disabled"
                    LibraryCatalogVersion = None
                    AppliedCursor = None
                    PendingOperationCount = 0
                    LastError = None
                    LastErrorAt = None
                    LastErrorPath = None
                    LastErrorOperationId = None
                    NextStep = None
                    LastSuccessfulSyncAt = None
                }

            match readRepository configuration.GraceStatusFile configuration.RepositoryId with
            | None -> return withHealth empty
            | Some current ->
                let count =
                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                    |> Array.filter (fun op -> not op.Terminal)
                    |> Array.length

                return
                    withHealth
                        { empty with
                            Enabled = true
                            Paused = current.Paused
                            State = current.State
                            LibraryCatalogVersion = Some current.Catalog.Version
                            AppliedCursor = if current.AppliedCursor = "" then None else Some current.AppliedCursor
                            PendingOperationCount = count
                        }
        }

    /// Converts any Library failure into the record status shows, keeping a typed failure's path and next step.
    let internal describeFailure source (fallbackPath: string option) (fallbackOperationId: Guid option) (ex: exn) =
        match ex with
        | :? LibrarySyncFailureException as failure ->
            {
                Source = source
                Message = ex.Message
                Path = failure.Path |> Option.orElse fallbackPath
                OperationId =
                    failure.OperationId
                    |> Option.orElse fallbackOperationId
                NextStep = failure.NextStep
                AtUnixTicks = getCurrentInstant().ToUnixTimeTicks()
            }
        | _ ->
            {
                Source = source
                Message = ex.Message
                Path = fallbackPath
                OperationId = fallbackOperationId
                NextStep = retryNextStep
                AtUnixTicks = getCurrentInstant().ToUnixTimeTicks()
            }

    /// Binds baseline acquisition to existing SDK routes and retained immutable content reads.
    let private baselineRemote configuration correlationId : LibraryBaseline.Remote =
        {
            Catalog =
                fun () ->
                    task {
                        let! result = Libraries.GetCatalog(scoped configuration correlationId (GetLibraryCatalogParameters()))
                        return value result
                    }
            Start =
                fun () ->
                    task {
                        let! result = Libraries.StartBootstrap(scoped configuration correlationId (StartLibraryBootstrapParameters()))
                        return value result
                    }
            Continue =
                fun id token ->
                    task {
                        let parameters = scoped configuration correlationId (ContinueLibraryBootstrapParameters())
                        parameters.BootstrapId <- id
                        parameters.PageToken <- token
                        let! result = Libraries.ContinueBootstrap parameters

                        match result with
                        | Error error when
                            (match error.Properties.TryGetValue "StatusCode" with
                             | true, status -> string status = "Gone"
                             | _ -> false)
                            ->
                            return None
                        | _ -> return Some(value result)
                    }
            Read =
                fun item ->
                    task {
                        let parameters = scoped configuration correlationId (PrepareLibraryContentReadParameters())
                        parameters.ItemId <- item.ItemId
                        parameters.ContentVersionId <- item.Content.Value.ContentVersionId
                        parameters.ContentRevision <- item.ContentRevision.Value
                        let! result = Libraries.PrepareContentRead parameters
                        let grant = value result
                        let prefix = "/libraries/content/"

                        if not (grant.DownloadPath.StartsWith(prefix, StringComparison.Ordinal)) then
                            invalidOp "Library read descriptor has an unexpected route."

                        let! result = Libraries.DownloadContent(Uri.UnescapeDataString(grant.DownloadPath.Substring(prefix.Length)), correlationId)
                        return value result
                    }
        }

    /// Selects a baseline only when every Library folder is absent or empty, preserving existing participation on retry.
    let private enableParticipation (configuration: GraceConfiguration) correlationId cancellationToken =
        task {
            if not (OperatingSystem.IsWindows()) then
                invalidOp "Library synchronization requires Windows 11."

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
            do! initialize configuration.GraceStatusFile

            readRepository configuration.GraceStatusFile configuration.RepositoryId
            |> Option.iter requireActive

            if (readRepository configuration.GraceStatusFile configuration.RepositoryId)
                .IsNone then
                do! LibraryBaseline.startWith ignore (baselineRemote configuration correlationId) configuration

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
            | Some current when current.Paused -> ()
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

            if catalog <> expected.Catalog then
                if catalogOnlyAdds expected.Catalog catalog then
                    raise (LibraryCatalogAddedException())

                raise (
                    LibrarySyncFailureException(
                        "A Library was removed from this repository or moved; synchronization has stopped before local effects.",
                        None,
                        None,
                        catalogChangedNextStep
                    )
                )

            if state configuration <> expected then
                invalidOp "Library catalog or applied predecessor changed; synchronization has stopped before local effects."
        }

    /// Requires every existing directory from the working root down to a Library folder to be an ordinary directory,
    /// so creating or using that folder can't follow a junction or land beside a file.
    let private requireOrdinaryAncestry (configuration: GraceConfiguration) (library: string) =
        let workingRoot =
            Path
                .GetFullPath(configuration.RootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)

        let mutable current = Path.GetDirectoryName(fullPath configuration library)

        while current.Length > workingRoot.Length do
            if File.Exists(current) then
                invalidOp $"A file is in the way of the Library folder '{library}'."

            if
                Directory.Exists(current)
                && File
                    .GetAttributes(current)
                    .HasFlag(FileAttributes.ReparsePoint)
            then
                invalidOp $"Library synchronization does not follow reparse points above '{library}'."

            current <- Path.GetDirectoryName(current)

    /// Requires an added Library's folder to be absent or an empty ordinary directory. Anything else stops sync with a
    /// next step and leaves the folder alone, so files that were there before it became a Library never sync.
    let private requireAddableRoot (configuration: GraceConfiguration) (library: string) =
        requireOrdinaryAncestry configuration library
        let path = fullPath configuration library

        let occupied =
            File.Exists(path)
            || (Directory.Exists(path)
                && (File
                        .GetAttributes(path)
                        .HasFlag(FileAttributes.ReparsePoint)
                    || not (
                        Directory.EnumerateFileSystemEntries(path)
                        |> Seq.isEmpty
                    )))

        if occupied then
            raise (
                LibrarySyncFailureException(
                    $"The repository added the Library '{library}', but that folder already has files on this copy.",
                    Some library,
                    None,
                    occupiedRootNextStep library
                )
            )

    /// Creates each selected Library folder that's missing. A missing folder that still holds synced or prepared work
    /// stops sync instead, because recreating it empty would delete those files on every copy.
    let private createMissingRoots (configuration: GraceConfiguration) (current: RepositoryState) =
        let items = readItems configuration.GraceStatusFile configuration.RepositoryId
        let operations = readOperations configuration.GraceStatusFile configuration.RepositoryId

        current.Catalog.Libraries
        |> Array.iter (fun library ->
            let path = fullPath configuration library

            if not (Directory.Exists(path)) then
                /// Reports whether a repository-relative path is this Library's folder or inside it.
                let owns (relative: string) =
                    pathsEqual relative library
                    || relative.StartsWith(library + "/", StringComparison.OrdinalIgnoreCase)

                let holdsWork =
                    items
                    |> Array.exists (fun item ->
                        item.Tombstone.IsNone
                        && owns (itemPath items item))
                    || operations
                       |> Array.exists (fun operation ->
                           operation.Prepared
                           && not operation.Terminal
                           && (owns operation.SourcePath
                               || owns operation.TargetPath))

                if holdsWork then
                    raise (
                        LibrarySyncFailureException($"The Library folder '{library}' is missing on this copy.", Some library, None, missingRootNextStep library)
                    )

                requireAddableRoot configuration library
                Directory.CreateDirectory(path) |> ignore)

    /// Switches this copy to the server's catalog when it only added Libraries, then creates every selected Library
    /// folder that's missing. Runs under the root lease, before capture and pulls, and after the baseline is applied.
    /// The switch commits before any folder is created, so a crash in between leaves a selected Library whose folder the
    /// next run creates. getCatalog reads the server's catalog; afterSwitch lets tests stop the run at that point.
    let internal adoptCatalogWith afterSwitch (getCatalog: unit -> System.Threading.Tasks.Task<LibraryCatalogDto>) (configuration: GraceConfiguration) =
        task {
            let current = state configuration

            if current.Baseline
               |> Option.forall (fun baseline -> baseline.Applied) then
                let! latest = getCatalog ()

                if latest <> current.Catalog then
                    if not (catalogOnlyAdds current.Catalog latest) then
                        raise (
                            LibrarySyncFailureException(
                                "A Library was removed from this repository or moved; synchronization has stopped before local effects.",
                                None,
                                None,
                                catalogChangedNextStep
                            )
                        )

                    normalizeLibraries latest.Libraries
                    |> Result.defaultWith invalidOp
                    |> ignore

                    let added =
                        latest.Libraries
                        |> Array.filter (fun library ->
                            not (
                                current.Catalog.Libraries
                                |> Array.exists (pathsEqual library)
                            ))

                    let requireAddable () =
                        added
                        |> Array.iter (requireAddableRoot configuration)

                    selectCatalogWith requireAddable configuration.GraceStatusFile current latest
                    afterSwitch ()

                createMissingRoots configuration (state configuration)
        }

    /// Separates completed filenames, retained rejection and uncertain or obstructed progress.
    [<RequireQualifiedAccess>]
    type RenameOutcome =
        | Completed
        | Rejected of RejectionCode
        | Ambiguous of string option
        | AcceptedButObstructed of string option

    /// Reports the selected rename using a typed internal outcome until the CLI output boundary.
    type RenameResult = { OperationId: Guid; SourcePath: string; TargetPath: string; Outcome: RenameOutcome }

    /// Selects one clean materialized file under root exclusion, or resumes its unchanged durable namespace intent.
    let internal selectRenameWith afterIntent (configuration: GraceConfiguration) sourcePath newName =
        let source =
            normalizeRepositoryRelativePath sourcePath
            |> Result.defaultWith invalidOp

        let name =
            normalizeName newName
            |> Result.defaultWith invalidOp

        let current = state configuration
        requireActive current
        let operations = readOperations configuration.GraceStatusFile configuration.RepositoryId

        let matching =
            operations
            |> Array.tryFindBack (fun operation ->
                operation.Rename
                && pathsEqual operation.SourcePath source
                && operation.Placement.Name = name)

        match matching with
        | Some operation when not operation.Terminal -> operation
        | Some operation when
            operation.Receipt
            |> Option.exists (fun receipt -> receipt.Outcome = OutcomeKind.Rejected)
            ->
            operation
        | Some operation when not (File.Exists(fullPath configuration source)) -> operation
        | _ ->
            if current.Baseline.IsSome
               || current.State <> "current"
               || operations
                  |> Array.exists (fun operation -> not operation.Terminal) then
                invalidOp "Library rename requires completed onboarding and no incompatible pending work. Resume the existing operation first."

            let items = readItems configuration.GraceStatusFile configuration.RepositoryId

            let item =
                items
                |> Array.tryFind (fun item ->
                    item.Tombstone.IsNone
                    && pathsEqual (itemPath items item) source)
                |> Option.defaultWith (fun () -> invalidOp "Library rename requires a materialized file.")

            if item.ItemKind <> ItemKind.File
               || item.Content.IsNone then
                invalidOp "Library rename accepts files only."

            let ns = item.Namespace.Value

            if pathsEqual ns.Name name then
                invalidOp "Library rename requires a different normalized name; case-only renames are excluded."

            let target = parentPath items ns.Parent + "/" + name
            let sourceFull = fullPath configuration source
            let targetFull = fullPath configuration target
            let mutable parent = Path.GetDirectoryName(sourceFull)

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
                    invalidOp "Library rename requires ordinary materialized directories."

                parent <- Path.GetDirectoryName(parent)

            let actual = LibraryFilesystem.stableIdentity sourceFull

            if actual.Size <= 0L
               || Some $"{actual.Blake3Hash}:{actual.Sha256Hash}:{actual.Size}"
                  <> (item.Content |> Option.map contentFingerprint) then
                invalidOp "Library rename requires a clean synchronized nonempty file."

            if File.Exists(targetFull)
               || Directory.Exists(targetFull)
               || items
                  |> Array.exists (fun candidate ->
                      candidate.Tombstone.IsNone
                      && pathsEqual (itemPath items candidate) target) then
                invalidOp "Library rename destination must be absent in the same parent."

            let operation =
                {
                    OperationId = Guid.NewGuid()
                    Work = OperationWork.ExplicitRename({ SourcePath = source; TargetPath = target; MaterializedItem = item }, RenameProgress.Selected)
                    CreatedAtTicks = DateTime.UtcNow.Ticks
                }

            insertOperation configuration.GraceStatusFile configuration.RepositoryId operation
            afterIntent ()
            operation

    /// Distinguishes server rejection, unresolved receipt, accepted obstruction, and completed local filenames.
    let internal renameResult (operation: PendingOperation) reason =
        let outcome =
            if operation.Terminal && operation.Accepted.IsSome then
                RenameOutcome.Completed
            elif (LibraryOperation.rejection operation).IsSome then
                RenameOutcome.Rejected(
                    (LibraryOperation.rejection operation)
                        .Value
                        .ReasonCode
                )
            elif operation.Accepted.IsSome then
                RenameOutcome.AcceptedButObstructed reason
            else
                RenameOutcome.Ambiguous reason

        { OperationId = operation.OperationId; SourcePath = operation.SourcePath; TargetPath = operation.TargetPath; Outcome = outcome }

    /// Quotes a name for a message with its combining characters written as code points ('cafe' + U+0301), so two
    /// spellings that look the same read differently.
    let internal spellOut (name: string) =
        let spelled = stringBuilderPool.Get()

        try
            let mutable inQuote = false

            for character in name do
                let combining =
                    match Char.GetUnicodeCategory character with
                    | Globalization.UnicodeCategory.NonSpacingMark
                    | Globalization.UnicodeCategory.SpacingCombiningMark
                    | Globalization.UnicodeCategory.EnclosingMark -> true
                    // Decomposed Hangul is a run of conjoining jamo.
                    | _ -> int character >= 0x1100 && int character <= 0x11FF

                if combining then
                    if inQuote then spelled.Append("'") |> ignore
                    if spelled.Length > 0 then spelled.Append(" + ") |> ignore
                    spelled.Append($"U+{int character:X4}") |> ignore
                    inQuote <- false
                else
                    if not inQuote then
                        if spelled.Length > 0 then spelled.Append(" + ") |> ignore
                        spelled.Append("'") |> ignore
                        inQuote <- true

                    spelled.Append(character) |> ignore

            if inQuote then spelled.Append("'") |> ignore
            spelled.ToString()
        finally
            stringBuilderPool.Return(spelled)

    /// Returns a name's composed (NFC) spelling when it differs from the name, or None when the name is already composed
    /// or holds text that has no composed form.
    let private composedSpelling (name: string) =
        try
            if name.IsNormalized(Text.NormalizationForm.FormC) then
                None
            else
                Some(name.Normalize(Text.NormalizationForm.FormC))
        with
        | :? ArgumentException -> None

    /// Next step when a folder holds two spellings of one name, so Grace can't tell which one the person means.
    let internal twoSpellingsNextStep = "Rename or remove one of them. Library sync waits until then and changes neither. Version-control work isn't affected."

    /// Next step when Grace can't give an entry its composed spelling, usually because another program has it open.
    let internal composeBlockedNextStep =
        "Close any program that has it open. Grace Watch retries automatically; run 'grace library sync run' to retry now. Version-control work isn't affected."

    /// #1133: Renames every entry under the participating Library roots whose name isn't composed (NFC) to its composed
    /// spelling, which looks the same and is what the server stores and every other copy shows. Windows treats
    /// 'cafe' + U+0301 and 'café' as different names, so without this a decomposed entry never matches its item and the
    /// files inside it never sync. Grace records only composed paths, so a rename never leaves a pending operation's path
    /// stale, and a crash after any rename leaves names the next capture reads like any other. Stops, renaming nothing
    /// more, when the folder already holds another spelling of the composed name, or when the entry can't be renamed.
    /// Reparse points are left for capture to refuse. `afterRename` runs after each rename, for tests.
    let internal composeNamesWith afterRename (configuration: GraceConfiguration) =
        let current = state configuration

        let relative (path: string) =
            Path
                .GetRelativePath(configuration.RootDirectory, path)
                .Replace('\\', '/')

        if not (current.Paused || current.Baseline.IsSome) then
            for libraryRoot in current.Catalog.Libraries do
                let directories = Collections.Generic.Stack<string>()
                let root = fullPath configuration libraryRoot

                if Directory.Exists(root) then directories.Push(root)

                while directories.Count > 0 do
                    let directory = directories.Pop()
                    // The listing carries each entry's attributes, so the walk reads no file on its own.
                    let entries = DirectoryInfo(directory).GetFileSystemInfos()

                    for info in entries do
                        let entry = info.FullName
                        let attributes = info.Attributes

                        if not (attributes.HasFlag(FileAttributes.ReparsePoint)) then
                            let name = info.Name

                            let final =
                                match composedSpelling name with
                                | None -> entry
                                | Some composed ->
                                    let sibling =
                                        entries
                                        |> Array.tryFind (fun other ->
                                            other.Name <> name
                                            && pathsEqual
                                                (composedSpelling other.Name
                                                 |> Option.defaultValue other.Name)
                                                composed)

                                    match sibling with
                                    | Some sibling ->
                                        raise (
                                            LibrarySyncFailureException(
                                                $"'{relative directory}' holds two spellings of one name: {spellOut sibling.Name} and {spellOut name}. They look the same, but Windows keeps them apart, and Grace syncs only the composed spelling.",
                                                Some(relative entry),
                                                None,
                                                twoSpellingsNextStep
                                            )
                                        )
                                    | None ->
                                        let target = Path.Combine(directory, composed)

                                        let blocked (ex: exn) =
                                            LibrarySyncFailureException(
                                                $"Grace couldn't rename {spellOut name} in '{relative directory}' to its composed spelling {spellOut composed}: {ex.Message}",
                                                Some(relative entry),
                                                None,
                                                composeBlockedNextStep
                                            )

                                        try
                                            if attributes.HasFlag(FileAttributes.Directory) then
                                                Directory.Move(entry, target)
                                            else
                                                File.Move(entry, target)
                                        with
                                        | :? IOException as ex -> raise (blocked ex)
                                        | :? UnauthorizedAccessException as ex -> raise (blocked ex)

                                        afterRename target
                                        target

                            if attributes.HasFlag(FileAttributes.Directory) then directories.Push(final)

    /// With the root lease held, captures observable saved files before incoming metadata can replace their base; paused copies are skipped.
    /// Zero-byte input is excluded before creating pending operations or preparing uploads. Empty files remain present
    /// and protected; already-captured positive sources and exact submitted requests are retained. Preserve
    /// `ItemTombstoned` rejection and saved input after deletion.
    let internal captureSavedWith afterInsert (configuration: GraceConfiguration) =
        // Capture reads names as the server stores them, so give every decomposed name its composed spelling first.
        composeNamesWith ignore configuration
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

        for libraryRoot in
            (if current.Paused || current.Baseline.IsSome then
                 [||]
             else
                 current.Catalog.Libraries) do
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

                    let itemKind =
                        if Directory.Exists(path) then
                            Grace.Types.Common.ItemKind.Directory
                        else
                            Grace.Types.Common.ItemKind.File

                    let observed =
                        if itemKind = Grace.Types.Common.ItemKind.File then
                            Some(LibraryFilesystem.stableIdentity path)
                        else
                            None

                    let unchanged =
                        match prior, observed with
                        | Some item, None -> item.ItemKind = ItemKind.Directory
                        | Some item, Some captured ->
                            item.Content
                            |> Option.exists (fun content ->
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
                        |> Option.exists (fun operation ->
                            (operation.SourceObject
                             |> Option.map (fun value -> value.Content)) = observed)

                    let publishedPending =
                        operations
                        |> Array.filter (fun operation ->
                            operation.Prepared
                            && not operation.Terminal
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
                                               observed
                                               |> Option.exists (fun source ->
                                                   descriptor.Blake3Hash = source.Blake3Hash
                                                   && descriptor.Sha256Hash = source.Sha256Hash
                                                   && descriptor.Size = source.Size)))))

                    // Before publication, this selected destination belongs to neither the renamed item nor a new local create.
                    // Keep any arriving bytes as an obstruction until the user resolves it or the intent is rejected.
                    let unpreparedRenameTarget =
                        operations
                        |> Array.exists (fun operation ->
                            operation.Rename
                            && not operation.Prepared
                            && not operation.Terminal
                            && pathsEqual operation.TargetPath relative)

                    if not (
                        observed
                        |> Option.exists (fun value -> value.Size = 0L)
                       )
                       && not unchanged
                       && not pendingSource
                       && not unpreparedRenameTarget
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

                        let placement = { Parent = parent; Name = Path.GetFileName(path) }

                        let work =
                            match observed with
                            | None -> OperationWork.DirectoryCreate({ SourcePath = relative; Placement = placement }, DirectoryProgress.Selected)
                            | Some observed ->
                                let saved = LibraryFilesystem.captureObject configuration relative observed

                                let sourceBase =
                                    match prior, origin with
                                    | Some item, _ -> SavedBase.MaterializedItem item
                                    | _, Some id -> SavedBase.PendingCreate(id, placement)
                                    | _ -> SavedBase.NewFile placement

                                OperationWork.SavedFile({ SourcePath = relative; Object = saved; Base = sourceBase }, SavedFileProgress.Captured)

                        let operation = { OperationId = Guid.NewGuid(); Work = work; CreatedAtTicks = DateTime.UtcNow.Ticks }

                        insertOperation configuration.GraceStatusFile configuration.RepositoryId operation
                        afterInsert operation
                        operations <- Array.append operations [| operation |]
                        captured <- true

        captured

    /// Captures saved changes after complete object publication, without injecting an interruption.
    let internal captureSaved configuration = captureSavedWith ignore configuration

    /// Freezes saved content or an explicit namespace intent; create successors resolve only from their exact terminal originating create.
    let private submitLocal (configuration: GraceConfiguration) correlationId (operation: PendingOperation) =
        task {
            let mutable operation = operation

            use objectLease =
                operation.SourceObject
                |> Option.map (LibraryFilesystem.openObject configuration)
                |> Option.toObj

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

                let mutable prepared = None

                if operation.SourceObject.IsSome
                   && not operation.Uploaded then
                    let content = operation.SourceObject.Value.Content
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
                            request.ItemKind <- LibraryOperation.kindToWire operation.Kind

                            match prior with
                            | Some item when operation.Rename ->
                                request.ChangeKind <- ChangeKind.Rename
                                request.ItemId <- Nullable(item.ItemId)
                                request.NamespacePrecondition <- Some { ItemId = item.ItemId; ExpectedNamespaceVersion = item.Namespace.Value.NamespaceVersion }
                                request.DestinationName <- operation.Placement.Name
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
                                    if operation.Kind = Grace.Types.Common.ItemKind.Directory then
                                        ChangeKind.CreateDirectory
                                    else
                                        ChangeKind.CreateFile

                                // The server settles a name another copy took first: it merges the same bytes and keeps
                                // different ones as a conflict copy, so a create never needs to look at the name first.
                                request.DestinationParent <- Some operation.Placement.Parent
                                request.DestinationName <- operation.Placement.Name

                            prepared
                            |> Option.iter (fun value -> request.UploadSessionId <- Nullable(value.UploadSessionId))

                            validateChangeShape request
                            |> Result.defaultWith invalidOp

                            let updated = LibraryOperation.freezeRequest (serialize request) operation
                            updateOperation configuration.GraceStatusFile configuration.RepositoryId operation updated
                            operation <- updated
                            return request
                    }

                match prepared with
                | Some prepared ->
                    if request.UploadSessionId.Value
                       <> prepared.UploadSessionId then
                        invalidOp "Prepared Library session changed after the request was frozen."

                    let objectPath = LibraryFilesystem.objectPath configuration operation.SourceObject.Value

                    let! uploaded =
                        LibraryManifestUpload.uploadPrepared configuration operation.OperationId prepared operation.SourcePath objectPath correlationId

                    value uploaded |> ignore
                    let updated = LibraryOperation.uploaded operation
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
                    | LibraryChangeCommand.UpdateContent (_, hash, _, _, _, _)
                    | LibraryChangeCommand.Rename (_, hash, _, _, _) -> hash
                    | _ -> invalidOp "Unexpected local Library request kind."

                if receipt.OperationId <> operation.OperationId
                   || receipt.RequestHash <> expectedHash then
                    invalidOp "Library receipt does not match the frozen request."

                let received = LibraryOperation.receive receipt operation
                updateOperation configuration.GraceStatusFile configuration.RepositoryId operation received

                match receipt.Change with
                | Some _ -> ()
                | None when operation.Rename && receipt.Outcome = "rejected" ->
                    retireRejectedRename configuration.GraceStatusFile configuration.RepositoryId received

                    raise (
                        LibrarySyncFailureException(
                            "Library rename was rejected; its receipt is retained and no local change was applied.",
                            Some operation.SourcePath,
                            Some operation.OperationId,
                            "The rename was withdrawn and no filenames changed. Run 'grace library rename' again if you still want it."
                        )
                    )
                | None ->
                    raise (
                        LibrarySyncFailureException(
                            $"Library submission returned {receipt.Outcome}: {receipt.ReasonCode}. Saved input remains pending.",
                            Some operation.SourcePath,
                            Some operation.OperationId,
                            "The server rejected this change. Your saved file is kept. Library sync on this copy can't continue past it yet. Version-control work isn't affected."
                        )
                    )
        }

    /// Applies one ordered accepted result after saving any newer local bytes and rechecking exact local preconditions.
    let internal applyChangeWithCancellation
        afterPublication
        (cancellationToken: CancellationToken)
        (configuration: GraceConfiguration)
        correlationId
        (expected: RepositoryState)
        (change: LibraryChangeDto)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
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

            // Changes are pulled only after the copy has switched to the server's catalog, so a change whose Library root
            // isn't selected belongs to a Library removed since. Stop before anything durable or on disk changes: applying
            // it would write outside every Library, or into a Library added later at the same path.
            let changeRoot =
                change.Item.Namespace
                |> Option.orElse (
                    change.Item.Tombstone
                    |> Option.map (fun tombstone -> tombstone.LastNamespace)
                )
                |> Option.bind (fun ns -> rootOf items ns.Parent)

            let selectedRoot root =
                expected.Catalog.Libraries
                |> Array.exists (pathsEqual root)

            let priorRoot =
                previous
                |> Option.filter (fun item -> item.Tombstone.IsNone)
                |> Option.bind (fun item -> item.Namespace)
                |> Option.map (fun ns -> rootOf items ns.Parent)

            if
                not (changeRoot |> Option.exists selectedRoot)
                || (priorRoot
                    |> Option.exists (fun root -> not (root |> Option.exists selectedRoot)))
                || not (configurationOwnsPath expected.Catalog targetRelative)
                || not (configurationOwnsPath expected.Catalog priorPath)
            then
                raise (
                    LibrarySyncFailureException(
                        "An incoming Library change is in a Library that was removed from this repository; synchronization has stopped before local effects.",
                        Some targetRelative,
                        Some change.OperationId,
                        catalogChangedNextStep
                    )
                )

            let operations = readOperations configuration.GraceStatusFile configuration.RepositoryId

            let existing =
                operations
                |> Array.tryFind (fun operation -> operation.OperationId = change.OperationId)

            let mutable operation =
                existing
                |> Option.defaultValue
                    {
                        OperationId = change.OperationId
                        Work = OperationWork.Incoming(change, priorPath, previous, ApplicationProgress.AwaitingPreparation)
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

                let retainedSourceObjects = ResizeArray<FileStream>()

                use retainedSources =
                    { new IDisposable with
                        /// Releases saved-object readers only after target publication and source removal finish.
                        member _.Dispose() =
                            retainedSourceObjects
                            |> Seq.iter (fun stream -> stream.Dispose())
                    }

                let saved =
                    operations
                    |> Array.exists (fun pending ->
                        pending.Direction = "local"
                        && not pending.Terminal
                        && pathsEqual pending.SourcePath targetRelative
                        && pending.SourceObject
                           |> Option.exists (fun reference ->
                               if actual = Some(LibraryOperation.fingerprint reference.Content) then
                                   retainedSourceObjects.Add(LibraryFilesystem.openObject configuration reference)
                                   true
                               else
                                   false))

                if not operation.Prepared then
                    if operation.Rename && actual.IsSome then
                        invalidOp "Library rename destination is occupied before preparation; preserve or move the obstruction before retrying."

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

                    let echoPending =
                        if change.Item.Tombstone.IsSome then
                            actual.IsSome
                        elif change.Item.ItemKind = ItemKind.Directory then
                            actual.IsNone || priorPath <> targetRelative
                        else
                            actual <> expectedContent
                            || priorPath <> targetRelative

                    let preparation =
                        {
                            ExpectedCursor = expected.AppliedCursor
                            ExpectedAncestry = ancestry items change.Item
                            ExpectedTarget = LibraryOperation.targetObservation actual
                            SourcePath = priorPath
                            TargetPath = targetRelative
                            Echo = if echoPending then EchoState.Pending else EchoState.Clear
                        }

                    let prepared = LibraryOperation.prepare preparation operation

                    updateOperation configuration.GraceStatusFile configuration.RepositoryId operation prepared
                    operation <- prepared

                /// Rejects changed durable authority or physical ancestry immediately before filesystem effects and completion.
                let revalidate () =
                    cancellationToken.ThrowIfCancellationRequested()

                    if state configuration <> expected
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
                /// A file rename may remove changed source bytes only against their exact already-accepted pending content;
                /// an empty source is excluded input and is always preserved. Returns the source content it accepted, which
                /// is the only content the removal may then delete.
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
                                    && (pending.SourceObject
                                        |> Option.exists (fun reference ->
                                            use verified = LibraryFilesystem.openObject configuration reference
                                            actual = Some(LibraryOperation.fingerprint reference.Content)))
                                    && (pending.Accepted
                                        |> Option.exists (fun accepted ->
                                            accepted.OperationId = pending.OperationId
                                            && (accepted.Item.Content
                                                |> Option.map contentFingerprint) = actual)))

                            match saved with
                            | None -> invalidOp "Moved Library source has no exact accepted saved content."
                            | Some saved ->
                                // Keep the verified shared object alive through the caller's removal effect.
                                let verified = LibraryFilesystem.openObject configuration saved.SourceObject.Value
                                retainedSourceObjects.Add(verified)
                                revalidate ()

                                let persisted =
                                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                                    |> Array.find (fun pending -> pending.OperationId = saved.OperationId)

                                if serialize persisted <> serialize saved
                                   || LibraryFilesystem.fingerprint priorTarget
                                      <> actual then
                                    invalidOp "Accepted Library source changed immediately before rename."

                        actual
                    else
                        None

                requireMovedSource () |> ignore

                // A saved target can change after preparation. Preserve it first, then refresh only the
                // filesystem precondition while retaining the original accepted result and exact edit base.
                if change.Item.ItemKind = ItemKind.File
                   && operation.Prepared
                   && actual <> operation.ExpectedTarget
                   && actual <> expectedContent then
                    if not saved then
                        invalidOp "Library target changed after preparation without a durable saved source."

                    let refreshed = LibraryOperation.refreshTarget actual operation
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

                        // A save after that check is put back, not deleted.
                        if LibraryFilesystem.removeFile operation.OperationId priorTarget materialized then
                            invalidOp "Library deletion would remove locally changed bytes."
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

                        // A save after the last check is kept under the conflict-copy name, and capture syncs it.
                        LibraryFilesystem.publishAtomic
                            (fun () ->
                                revalidate ()
                                requireMovedSource () |> ignore)
                            operation.OperationId
                            staged
                            target
                            operation.ExpectedTarget
                            bytes

                    if
                        priorPath <> targetRelative
                        && File.Exists(priorTarget)
                    then
                        let accepted = requireMovedSource ()

                        // A save to the old path after that check is put back, not deleted.
                        if LibraryFilesystem.removeFile operation.OperationId priorTarget accepted then
                            invalidOp "Accepted Library source changed immediately before rename."

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

                LibraryLocalState.complete configuration.GraceStatusFile expected operation
        }

    /// Keeps the existing deterministic interruption seam for callers without a cancellation request.
    let internal applyChangeWith afterPublication configuration correlationId expected change =
        applyChangeWithCancellation afterPublication CancellationToken.None configuration correlationId expected change

    /// The most change pages one run pulls, across all its pulls. Each page asks the server for changes up to its latest
    /// commit, so on a busy repository a run could otherwise keep pulling, and keep holding the working-directory lease,
    /// indefinitely.
    [<Literal>]
    let internal MaximumPagesPerRun = 20

    /// Pulls contiguous changes, always from the applied cursor, so an interrupted or long-paused pull resumes exactly
    /// where local application stopped. An empty HasMore page remains incomplete and is retried by a later run, and so
    /// does a pull that uses up the run's remaining page budget; the run then reports catchingUp. Each page is charged
    /// to pagesLeft when it's requested, so a pull interrupted by an added Library still spends what it used. Returns
    /// whether the pull caught up.
    let private pull configuration correlationId (pagesLeft: int ref) (cancellationToken: CancellationToken) =
        task {
            let mutable continuePages = true
            let mutable caughtUp = false

            while continuePages && pagesLeft.Value > 0 do
                cancellationToken.ThrowIfCancellationRequested()
                let before = state configuration
                do! checkCatalog configuration correlationId before
                let parameters = scoped configuration correlationId (GetLibraryChangesParameters())
                parameters.AfterCursor <- before.AppliedCursor
                // Charged as the request goes out, so a catalog switch found just before it doesn't spend a page.
                pagesLeft.Value <- pagesLeft.Value - 1
                let! result = Libraries.GetChanges parameters

                let page =
                    match result with
                    | Error error when
                        error.Error = LibraryCursorError.AheadOfServer
                        || error.Error = LibraryCursorError.NotIssuedHere
                        ->
                        raise (LibrarySyncFailureException(error.Error, None, None, cursorMismatchNextStep))
                    | _ -> value result

                let mutable index = 0

                while index < page.Changes.Length do
                    do! applyChangeWithCancellation ignore cancellationToken configuration correlationId (state configuration) page.Changes[index]
                    index <- index + 1

                if (state configuration).AppliedCursor
                   <> page.LastCursor then
                    invalidOp "Library page cursor does not describe completed local application."

                caughtUp <- not page.HasMore
                continuePages <- page.HasMore && page.Changes.Length > 0
                do! checkCatalog configuration correlationId (state configuration)

            return caughtUp
        }

    /// Records why a run stopped. A requested stop isn't a failure: it records nothing and leaves the state as it was.
    /// Any other failure is recorded with the operation being submitted, when there is one, and blocks the copy.
    let internal finishFailedRun (configuration: GraceConfiguration) (submitting: PendingOperation option) (cancellationToken: CancellationToken) (ex: exn) =
        if
            not
                (
                    ex :? OperationCanceledException
                    && cancellationToken.IsCancellationRequested
                )
        then
            try
                let path =
                    submitting
                    |> Option.map (fun operation -> operation.SourcePath)

                let operationId =
                    submitting
                    |> Option.map (fun operation -> operation.OperationId)

                describeFailure SyncFailureSource.Sync path operationId ex
                |> recordFailure configuration.GraceStatusFile configuration.RepositoryId
            with
            // The original failure is raised either way; a failed status write must not replace it.
            | _ -> ()

            setState configuration.GraceStatusFile (state configuration) "blocked"

    /// Runs finite pending submissions and ordered pulls under the existing shared root lease. Returns the status and
    /// whether the run stopped because it used up its page budget.
    let private runCounted (configuration: GraceConfiguration) correlationId cancellationToken =
        task {
            if not (OperatingSystem.IsWindows()) then
                invalidOp "Library synchronization requires Windows 11."

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
            do! initialize configuration.GraceStatusFile
            let original = state configuration
            requireActive original

            if original.Baseline.IsNone then
                setState configuration.GraceStatusFile original "catchingUp"

            // The local operation being submitted, so a failure without its own path still names what it affected.
            let mutable submitting: PendingOperation option = None

            try
                do! adoptCatalogWith ignore (fun () -> catalog configuration correlationId) configuration

                if (state configuration).Baseline.IsSome then
                    do! LibraryBaseline.resumeWith ignore (baselineRemote configuration correlationId) configuration cancellationToken

                let mutable again = true
                let mutable caughtUp = false
                let pagesLeft = ref MaximumPagesPerRun
                let mutable catalogSwitches = 0

                while again do
                    let mutable catalogAdded = false

                    try
                        cancellationToken.ThrowIfCancellationRequested()
                        captureSaved configuration |> ignore
                        let before = state configuration
                        let pending = readOperations configuration.GraceStatusFile configuration.RepositoryId
                        let mutable submitted = false
                        let mutable index = 0

                        while index < pending.Length do
                            cancellationToken.ThrowIfCancellationRequested()
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
                                submitting <- Some operation
                                do! submitLocal configuration correlationId operation
                                submitting <- None
                                submitted <- true

                            index <- index + 1

                        cancellationToken.ThrowIfCancellationRequested()
                        let! completePull = pull configuration correlationId pagesLeft cancellationToken
                        caughtUp <- completePull

                        if completePull
                           && (state configuration).Baseline.IsSome then
                            finishOnboarding configuration.GraceStatusFile (state configuration)

                        let captured = captureSaved configuration

                        let remaining =
                            readOperations configuration.GraceStatusFile configuration.RepositoryId
                            |> Array.filter (fun operation -> not operation.Terminal)

                        again <-
                            completePull
                            && pagesLeft.Value > 0
                            && remaining.Length > 0
                            && (submitted
                                || captured
                                || before.AppliedCursor
                                   <> (state configuration).AppliedCursor)
                    with
                    | :? LibraryCatalogAddedException when catalogSwitches < MaximumCatalogSwitchesPerRun -> catalogAdded <- true

                    // A Library was added while this run was working. Everything done so far is durable and idempotent,
                    // so switch to the new catalog and go around again.
                    if catalogAdded then
                        catalogSwitches <- catalogSwitches + 1
                        submitting <- None
                        do! adoptCatalogWith ignore (fun () -> catalog configuration correlationId) configuration

                let current = state configuration

                let pendingCount =
                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                    |> Array.filter (fun operation -> not operation.Terminal)
                    |> Array.length

                setState configuration.GraceStatusFile current (if caughtUp && pendingCount = 0 then "current" else "catchingUp")
                pruneClassified configuration.GraceStatusFile configuration.RepositoryId 128
                recordSyncSuccess configuration.GraceStatusFile configuration.RepositoryId (getCurrentInstant().ToUnixTimeTicks())
                let! current = status configuration
                return current, pagesLeft.Value = 0 && not caughtUp
            with
            | ex ->
                finishFailedRun configuration submitting cancellationToken ex
                return raise ex
        }

    /// Runs finite pending submissions and ordered pulls under the existing shared root lease.
    let run configuration correlationId cancellationToken =
        task {
            let! current, _ = runCounted configuration correlationId cancellationToken
            return current
        }

    /// Lets a racing pause skip a Watch tick without terminating Watch or hiding unrelated synchronization failures.
    let runFromWatch configuration correlationId cancellationToken =
        task {
            try
                let! _ = run configuration correlationId cancellationToken
                return ()
            with
            | :? LibraryPausedException -> return ()
        }

    /// Enables or resumes populated-Library onboarding, then catches up through genuine accepted changes.
    let enable configuration correlationId cancellationToken =
        task {
            let! selected = enableParticipation configuration correlationId cancellationToken

            if (state configuration).Baseline.IsSome then
                return! run configuration correlationId cancellationToken
            else
                return selected
        }

    /// Commits a local setting without network access or draining retained operations.
    let internal changePause (configuration: GraceConfiguration) paused cancellationToken =
        task {
            if not (OperatingSystem.IsWindows()) then
                invalidOp "Library synchronization requires Windows 11."

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                |> Result.defaultWith invalidOp

            use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
            do! initialize configuration.GraceStatusFile
            let current = state configuration

            if current.Baseline.IsSome then
                invalidOp "Library pause and resume require completed onboarding."

            setPaused configuration.GraceStatusFile current paused
            return! status configuration
        }

    /// Pauses this participating copy after its existing root lease holder finishes or stops.
    let pause configuration cancellationToken = changePause configuration true cancellationToken

    /// Commits active participation before a finite run whose fresh lease read honors any newer pause.
    let resume configuration correlationId cancellationToken =
        task {
            let! _ = changePause configuration false cancellationToken
            return! run configuration correlationId cancellationToken
        }

    /// Persists an explicit rename before submission and resumes it through genuine ordered synchronization.
    /// `grace library rename <path> <new-name>` selects one clean nonempty materialized file in its existing parent. Keep
    /// the selected intent, generated operation ID, frozen namespace-only request and genuine receipt in the existing
    /// operation row. Acceptance applies through ordered pull; a definitive rejected unprepared rename retires and
    /// returns before pulling. Preserve saved-content rejection, exact prepared Watch classification and the four
    /// human/JSON outcomes (RenameOutcome).
    let rename (configuration: GraceConfiguration) correlationId sourcePath newName cancellationToken =
        task {
            if not (OperatingSystem.IsWindows()) then
                invalidOp "Library rename requires Windows 11."

            let! selected =
                task {
                    let scope =
                        WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory
                        |> Result.defaultWith invalidOp

                    use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
                    do! initialize configuration.GraceStatusFile
                    requireActive (state configuration)
                    // Pick up any added Library first, so a rename right after an addition isn't refused.
                    do! adoptCatalogWith ignore (fun () -> catalog configuration correlationId) configuration
                    do! checkCatalog configuration correlationId (state configuration)
                    return selectRenameWith ignore configuration sourcePath newName
                }

            if selected.Terminal then
                return renameResult selected None
            else
                let! reason, outOfPages =
                    task {
                        try
                            let! _, outOfPages = runCounted configuration correlationId cancellationToken
                            return None, outOfPages
                        with
                        | ex -> return Some ex.Message, false
                    }

                let persisted =
                    readOperations configuration.GraceStatusFile configuration.RepositoryId
                    |> Array.find (fun operation -> operation.OperationId = selected.OperationId)

                // A run that used up its page budget while earlier changes were still arriving stopped before it reached
                // the rename. Nothing is obstructed; another run continues.
                let reason =
                    match reason with
                    | None when
                        outOfPages
                        && persisted.Accepted.IsSome
                        && not persisted.Terminal
                        ->
                        Some renameCatchingUpDiagnostic
                    | _ -> reason

                return renameResult persisted reason
        }
