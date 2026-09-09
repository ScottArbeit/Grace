namespace Grace.CLI

open System
open System.IO
open System.Text.Json.Serialization
open Grace.Types
open Grace.Types.Library

/// Defines the sole stored intent and progress for each supported local Library operation family.
module internal LibraryOperation =

    /// Identifies complete nonempty file content independently of its working path.
    type ContentIdentity = { Size: int64; Sha256Hash: string; Blake3Hash: string }

    /// Locates an immutable verified object beneath the working copy's existing object directory.
    type SavedObject = { ObjectPath: string; Content: ContentIdentity }

    /// Selects the parent/name of a newly captured item before its namespace request is frozen.
    type CreationPlacement = { Parent: LibraryParentDto; Name: string }

    /// Retains the exact materialized edit base or originating creation for a saved file.
    [<RequireQualifiedAccess>]
    type SavedBase =
        | MaterializedItem of LibraryItemDto
        | NewFile of CreationPlacement
        | PendingCreate of Guid * CreationPlacement

    /// Holds the selected source and object even after the working filename or bytes change.
    type SavedFileIntent = { SourcePath: string; Object: SavedObject; Base: SavedBase }

    /// Captures directory creation without pretending it has saved file content.
    type DirectoryCreateIntent = { SourcePath: string; Placement: CreationPlacement }

    /// Freezes one explicit same-parent rename selection before submitting its namespace request.
    type RenameIntent = { SourcePath: string; TargetPath: string; MaterializedItem: LibraryItemDto }

    /// Distinguishes absence, an ordinary directory, and complete file identity during publication.
    [<RequireQualifiedAccess>]
    type TargetObservation =
        | Absent
        | DirectoryPresent
        | FilePresent of ContentIdentity

    /// Tracks whether an exact prepared/completed publication still awaits Watch classification.
    [<RequireQualifiedAccess>]
    type EchoState =
        | Clear
        | Pending

    /// Rechecks one prepared filesystem effect against its exact cursor, ancestry and target content.
    type ApplicationCheckpoint =
        {
            ExpectedCursor: string
            ExpectedAncestry: LibraryItemDto array
            ExpectedTarget: TargetObservation
            SourcePath: string
            TargetPath: string
            Echo: EchoState
        }

    /// Places preparation data only in prepared or completed application states.
    [<RequireQualifiedAccess>]
    type ApplicationProgress =
        | AwaitingPreparation
        | Prepared of ApplicationCheckpoint
        | Completed of ApplicationCheckpoint

    /// Retains known rejection reasons while explicitly preserving an unrecognized remote code.
    [<RequireQualifiedAccess>]
    type RejectionCode =
        | NamespaceChanged
        | ContentChanged
        | SlotOccupied
        | ItemMissing
        | ItemTombstoned
        | DirectoryNotEmpty
        | KindMismatch
        | PreparedContentExpired
        | OperationIdentityMismatch
        | StalePolicy
        | Unknown of string

    /// Converts wire rejection codes without treating an unknown value as a known condition.
    let rejectionCode =
        function
        | RejectionReason.NamespaceChanged -> RejectionCode.NamespaceChanged
        | RejectionReason.ContentChanged -> RejectionCode.ContentChanged
        | RejectionReason.SlotOccupied -> RejectionCode.SlotOccupied
        | RejectionReason.ItemMissing -> RejectionCode.ItemMissing
        | RejectionReason.ItemTombstoned -> RejectionCode.ItemTombstoned
        | RejectionReason.DirectoryNotEmpty -> RejectionCode.DirectoryNotEmpty
        | RejectionReason.KindMismatch -> RejectionCode.KindMismatch
        | RejectionReason.PreparedContentExpired -> RejectionCode.PreparedContentExpired
        | RejectionReason.OperationIdentityMismatch -> RejectionCode.OperationIdentityMismatch
        | OutcomeKind.StalePolicy -> RejectionCode.StalePolicy
        | value when not (String.IsNullOrWhiteSpace value) -> RejectionCode.Unknown value
        | _ -> invalidOp "Library rejection has no reason code."

    /// Emits the stable server code at the CLI and wire boundaries.
    let rejectionCodeText =
        function
        | RejectionCode.NamespaceChanged -> RejectionReason.NamespaceChanged
        | RejectionCode.ContentChanged -> RejectionReason.ContentChanged
        | RejectionCode.SlotOccupied -> RejectionReason.SlotOccupied
        | RejectionCode.ItemMissing -> RejectionReason.ItemMissing
        | RejectionCode.ItemTombstoned -> RejectionReason.ItemTombstoned
        | RejectionCode.DirectoryNotEmpty -> RejectionReason.DirectoryNotEmpty
        | RejectionCode.KindMismatch -> RejectionReason.KindMismatch
        | RejectionCode.PreparedContentExpired -> RejectionReason.PreparedContentExpired
        | RejectionCode.OperationIdentityMismatch -> RejectionReason.OperationIdentityMismatch
        | RejectionCode.StalePolicy -> OutcomeKind.StalePolicy
        | RejectionCode.Unknown value -> value

    /// Stores one genuine accepted change, with no second copy in an optional receipt field.
    type AcceptedReceipt = { RequestHash: string; Change: LibraryChangeDto }

    /// Retains rejection metadata separately from accepted change and application state.
    type RejectedReceipt = { RequestHash: string; ReasonCode: RejectionCode; CurrentCatalog: LibraryCatalogDto option; Rebaseline: LibraryRebaselineDto option }

    /// Retains the upload stage and rejected saved content as valid unfinished work.
    [<RequireQualifiedAccess>]
    type SavedFileProgress =
        | Captured
        | RequestFrozen of string
        | Uploaded of string
        | Rejected of string * RejectedReceipt
        | Accepted of string * AcceptedReceipt * ApplicationProgress

    /// Records namespace-only creation without file-upload states or terminal rejection retirement.
    [<RequireQualifiedAccess>]
    type DirectoryProgress =
        | Selected
        | RequestFrozen of string
        | Rejected of string * RejectedReceipt
        | Accepted of string * AcceptedReceipt * ApplicationProgress

    /// Separates retained rename rejection from its guarded retirement transaction.
    [<RequireQualifiedAccess>]
    type RenameProgress =
        | Selected
        | RequestFrozen of string
        | Rejected of string * RejectedReceipt
        | Retired of string * RejectedReceipt
        | Accepted of string * AcceptedReceipt * ApplicationProgress

    /// Keeps baseline live installation separate from feed acceptance.
    [<RequireQualifiedAccess>]
    type BaselineInstallation =
        | Selected
        | Prepared of ApplicationCheckpoint
        | Installed of ApplicationCheckpoint

    /// Completes a selected tombstone without inventing a filesystem checkpoint.
    [<RequireQualifiedAccess>]
    type TombstoneInstallation =
        | Selected
        | Installed

    /// Stores only fields belonging to the selected operation family.
    [<RequireQualifiedAccess>]
    type OperationWork =
        | SavedFile of SavedFileIntent * SavedFileProgress
        | DirectoryCreate of DirectoryCreateIntent * DirectoryProgress
        | ExplicitRename of RenameIntent * RenameProgress
        | Incoming of change: LibraryChangeDto * sourcePath: string * previous: LibraryItemDto option * progress: ApplicationProgress
        | BaselineLive of item: LibraryItemDto * installation: BaselineInstallation
        | BaselineTombstone of item: LibraryItemDto * installation: TombstoneInstallation

    /// Owns generated identity, selected catalog and age alongside a single typed stored state.
    type Operation = { OperationId: Guid; CatalogVersion: Guid; CreatedAtTicks: int64; Work: OperationWork }

    /// Compares only stored intent, allowing a baseline path to appear when installation is prepared.
    let sameIntent left right =
        match left.Work, right.Work with
        | OperationWork.SavedFile (a, _), OperationWork.SavedFile (b, _) -> a = b
        | OperationWork.DirectoryCreate (a, _), OperationWork.DirectoryCreate (b, _) -> a = b
        | OperationWork.ExplicitRename (a, _), OperationWork.ExplicitRename (b, _) -> a = b
        | OperationWork.Incoming (a, ap, ab, _), OperationWork.Incoming (b, bp, bb, _) -> a = b && ap = bp && ab = bb
        | OperationWork.BaselineLive (a, _), OperationWork.BaselineLive (b, _)
        | OperationWork.BaselineTombstone (a, _), OperationWork.BaselineTombstone (b, _) -> a = b
        | _ -> false

    /// Converts the existing Library wire kind at the local domain boundary.
    let kindFromWire =
        function
        | ItemKind.File -> Common.ItemKind.File
        | ItemKind.Directory -> Common.ItemKind.Directory
        | _ -> invalidOp "Library item kind is invalid."

    /// Converts the shared kind explicitly when constructing a Library request.
    let kindToWire =
        function
        | Common.ItemKind.File -> ItemKind.File
        | Common.ItemKind.Directory -> ItemKind.Directory

    /// Encodes complete target identity for the existing filesystem and Watch comparison seams.
    let fingerprint identity = $"{identity.Blake3Hash}:{identity.Sha256Hash}:{identity.Size}"

    /// Converts a prepared target observation to the existing exact comparison representation.
    let targetFingerprint =
        function
        | TargetObservation.Absent -> None
        | TargetObservation.DirectoryPresent -> Some "directory"
        | TargetObservation.FilePresent value -> Some(fingerprint value)

    /// Validates an exact target comparison before storing its typed form.
    let targetObservation =
        function
        | None -> TargetObservation.Absent
        | Some "directory" -> TargetObservation.DirectoryPresent
        | Some value ->
            let parts = value.Split(':')
            if parts.Length <> 3 then invalidOp "Library target fingerprint is invalid."
            TargetObservation.FilePresent { Blake3Hash = parts[0]; Sha256Hash = parts[1]; Size = Int64.Parse parts[2] }

    /// Reads application progress without manufacturing it for unsubmitted or baseline work.
    let application operation =
        match operation.Work with
        | OperationWork.SavedFile (_, SavedFileProgress.Accepted (_, _, progress))
        | OperationWork.DirectoryCreate (_, DirectoryProgress.Accepted (_, _, progress))
        | OperationWork.ExplicitRename (_, RenameProgress.Accepted (_, _, progress))
        | OperationWork.Incoming (_, _, _, progress) -> Some progress
        | _ -> None

    /// Reads only checkpoints that have actually been prepared or installed.
    let checkpoint operation =
        match operation.Work, application operation with
        | OperationWork.BaselineLive (_, BaselineInstallation.Prepared value), _
        | OperationWork.BaselineLive (_, BaselineInstallation.Installed value), _
        | _, Some (ApplicationProgress.Prepared value)
        | _, Some (ApplicationProgress.Completed value) -> Some value
        | _ -> None

    /// Projects the one accepted receipt for local submitted work.
    let acceptance operation =
        match operation.Work with
        | OperationWork.SavedFile (_, SavedFileProgress.Accepted (_, receipt, _))
        | OperationWork.DirectoryCreate (_, DirectoryProgress.Accepted (_, receipt, _))
        | OperationWork.ExplicitRename (_, RenameProgress.Accepted (_, receipt, _)) -> Some receipt
        | _ -> None

    /// Projects retained rejection without inferring application or upload success.
    let rejection operation =
        match operation.Work with
        | OperationWork.SavedFile (_, SavedFileProgress.Rejected (_, receipt))
        | OperationWork.DirectoryCreate (_, DirectoryProgress.Rejected (_, receipt))
        | OperationWork.ExplicitRename (_, RenameProgress.Rejected (_, receipt))
        | OperationWork.ExplicitRename (_, RenameProgress.Retired (_, receipt)) -> Some receipt
        | _ -> None

    /// Reconstructs a wire receipt only at consumers that need its original contract shape.
    let receiptDto operation =
        match acceptance operation, rejection operation with
        | Some receipt, _ ->
            let outcome =
                if receipt.Change.Conflict.IsSome then
                    OutcomeKind.ConflictCopy
                else
                    OutcomeKind.Accepted

            Some
                {
                    OperationId = operation.OperationId
                    RequestHash = receipt.RequestHash
                    Outcome = outcome
                    Change = Some receipt.Change
                    ReasonCode = None
                    CurrentLibraryCatalog = None
                    Rebaseline = None
                }
        | _, Some receipt ->
            Some
                {
                    OperationId = operation.OperationId
                    RequestHash = receipt.RequestHash
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some(rejectionCodeText receipt.ReasonCode)
                    CurrentLibraryCatalog = receipt.CurrentCatalog
                    Rebaseline = receipt.Rebaseline
                }
        | _ -> None

    /// Supplies read-only projections to filesystem, persistence and Watch consumers; none are serialized.
    type Operation with
        /// Routes typed operation families to the existing SQLite direction index.
        [<JsonIgnore>]
        member this.Direction =
            match this.Work with
            | OperationWork.Incoming _ -> "remote"
            | OperationWork.BaselineLive _
            | OperationWork.BaselineTombstone _ -> "baseline"
            | _ -> "local"

        /// Identifies explicit user-selected rename intent without inferring it from feed changes.
        [<JsonIgnore>]
        member this.Rename =
            match this.Work with
            | OperationWork.ExplicitRename _ -> true
            | _ -> false

        /// Exposes the frozen object only for saved file content.
        [<JsonIgnore>]
        member this.SourceObject =
            match this.Work with
            | OperationWork.SavedFile (intent, _) -> Some intent.Object
            | _ -> None

        /// Reads the exact item used as edit or incoming-placement authority.
        [<JsonIgnore>]
        member this.MaterializedBase =
            match this.Work with
            | OperationWork.SavedFile ({ Base = SavedBase.MaterializedItem item }, _)
            | OperationWork.ExplicitRename ({ MaterializedItem = item }, _) -> Some item
            | OperationWork.Incoming (_, _, previous, _) -> previous
            | _ -> None

        /// Keeps a later save dependent on its original pending creation.
        [<JsonIgnore>]
        member this.OriginatingCreateId =
            match this.Work with
            | OperationWork.SavedFile ({ Base = SavedBase.PendingCreate (id, _) }, _) -> Some id
            | _ -> None

        /// Projects the single genuine acceptance; baseline installation has none.
        [<JsonIgnore>]
        member this.Accepted =
            match this.Work with
            | OperationWork.Incoming (change, _, _, _) -> Some change
            | _ ->
                acceptance this
                |> Option.map (fun value -> value.Change)

        /// Reconstructs the unchanged wire receipt from typed retained metadata.
        [<JsonIgnore>]
        member this.Receipt = receiptDto this

        /// Reads the selected baseline item without manufacturing a feed change.
        [<JsonIgnore>]
        member this.BaselineItem =
            match this.Work with
            | OperationWork.BaselineLive (item, _)
            | OperationWork.BaselineTombstone (item, _) -> Some item
            | _ -> None

        /// Resolves the stored working source or the prepared baseline source.
        [<JsonIgnore>]
        member this.SourcePath =
            match this.Work with
            | OperationWork.SavedFile (intent, _) -> intent.SourcePath
            | OperationWork.DirectoryCreate (intent, _) -> intent.SourcePath
            | OperationWork.ExplicitRename (intent, _) -> intent.SourcePath
            | OperationWork.Incoming (_, path, _, _) -> path
            | _ ->
                checkpoint this
                |> Option.map (fun value -> value.SourcePath)
                |> Option.defaultValue ""

        /// Resolves the selected rename destination or prepared publication path.
        [<JsonIgnore>]
        member this.TargetPath =
            checkpoint this
            |> Option.map (fun value -> value.TargetPath)
            |> Option.defaultWith (fun () ->
                match this.Work with
                | OperationWork.ExplicitRename (intent, _) -> intent.TargetPath
                | _ -> "")

        /// Uses the envelope catalog as the only catalog authority.
        [<JsonIgnore>]
        member this.ExpectedCatalogVersion = this.CatalogVersion

        /// Exposes the predecessor only after application preparation.
        [<JsonIgnore>]
        member this.ExpectedCursor =
            checkpoint this
            |> Option.map (fun value -> value.ExpectedCursor)
            |> Option.defaultValue ""

        /// Reads materialized ancestry recorded by the application checkpoint.
        [<JsonIgnore>]
        member this.ExpectedAncestry =
            checkpoint this
            |> Option.map (fun value -> value.ExpectedAncestry)
            |> Option.defaultValue [||]

        /// Adapts the typed target observation to existing filesystem comparisons.
        [<JsonIgnore>]
        member this.ExpectedTarget =
            checkpoint this
            |> Option.bind (fun value -> targetFingerprint value.ExpectedTarget)

        /// Reports whether a real application checkpoint exists.
        [<JsonIgnore>]
        member this.Prepared = (checkpoint this).IsSome

        /// Derives Watch classification from the retained publication checkpoint.
        [<JsonIgnore>]
        member this.EchoPending =
            checkpoint this
            |> Option.exists (fun value -> value.Echo = EchoState.Pending)

        /// Derives completed application or rejected-rename retirement from its typed stage.
        [<JsonIgnore>]
        member this.Terminal =
            match this.Work, application this with
            | OperationWork.ExplicitRename (_, RenameProgress.Retired _), _
            | OperationWork.BaselineLive (_, BaselineInstallation.Installed _), _
            | OperationWork.BaselineTombstone (_, TombstoneInstallation.Installed), _
            | _, Some (ApplicationProgress.Completed _) -> true
            | _ -> false

        /// Reports saved-file manifest completion without inventing uploads for namespace work.
        [<JsonIgnore>]
        member this.Uploaded =
            match this.Work with
            | OperationWork.SavedFile (_, SavedFileProgress.Uploaded _)
            | OperationWork.SavedFile (_, SavedFileProgress.Rejected _)
            | OperationWork.SavedFile (_, SavedFileProgress.Accepted _) -> true
            | _ -> false

        /// Projects the one exact frozen request from submission progress.
        [<JsonIgnore>]
        member this.RequestJson =
            match this.Work with
            | OperationWork.SavedFile (_, SavedFileProgress.RequestFrozen request)
            | OperationWork.SavedFile (_, SavedFileProgress.Uploaded request)
            | OperationWork.SavedFile (_, SavedFileProgress.Rejected (request, _))
            | OperationWork.SavedFile (_, SavedFileProgress.Accepted (request, _, _))
            | OperationWork.DirectoryCreate (_, DirectoryProgress.RequestFrozen request)
            | OperationWork.DirectoryCreate (_, DirectoryProgress.Rejected (request, _))
            | OperationWork.DirectoryCreate (_, DirectoryProgress.Accepted (request, _, _))
            | OperationWork.ExplicitRename (_, RenameProgress.RequestFrozen request)
            | OperationWork.ExplicitRename (_, RenameProgress.Rejected (request, _))
            | OperationWork.ExplicitRename (_, RenameProgress.Retired (request, _))
            | OperationWork.ExplicitRename (_, RenameProgress.Accepted (request, _, _)) -> Some request
            | _ -> None

        /// Maps operation intent to the shared payload-free file or directory domain.
        [<JsonIgnore>]
        member this.Kind =
            match this.Work with
            | OperationWork.SavedFile _
            | OperationWork.ExplicitRename _ -> Common.ItemKind.File
            | OperationWork.DirectoryCreate _ -> Common.ItemKind.Directory
            | OperationWork.Incoming (change, _, _, _) -> kindFromWire change.Item.ItemKind
            | OperationWork.BaselineLive (item, _)
            | OperationWork.BaselineTombstone (item, _) -> kindFromWire item.ItemKind

        /// Resolves namespace request placement from the selected intent or item.
        [<JsonIgnore>]
        member this.Placement =
            match this.Work with
            | OperationWork.SavedFile ({ Base = SavedBase.NewFile value | SavedBase.PendingCreate (_, value) }, _)
            | OperationWork.DirectoryCreate ({ Placement = value }, _) -> value
            | _ ->
                let item =
                    this.MaterializedBase
                    |> Option.orElseWith (fun () ->
                        this.Accepted
                        |> Option.map (fun value -> value.Item))
                    |> Option.orElse this.BaselineItem
                    |> Option.get

                let ns =
                    item.Namespace
                    |> Option.orElseWith (fun () ->
                        item.Tombstone
                        |> Option.map (fun value -> value.LastNamespace))
                    |> Option.get

                { Parent = ns.Parent; Name = if this.Rename then Path.GetFileName(this.TargetPath) else ns.Name }

    /// Freezes the exact validated wire request once, before its first submission.
    let freezeRequest request operation =
        let work =
            match operation.Work with
            | OperationWork.SavedFile (intent, SavedFileProgress.Captured) -> OperationWork.SavedFile(intent, SavedFileProgress.RequestFrozen request)
            | OperationWork.DirectoryCreate (intent, DirectoryProgress.Selected) ->
                OperationWork.DirectoryCreate(intent, DirectoryProgress.RequestFrozen request)
            | OperationWork.ExplicitRename (intent, RenameProgress.Selected) -> OperationWork.ExplicitRename(intent, RenameProgress.RequestFrozen request)
            | _ -> invalidOp "Library request is already frozen or cannot be submitted."

        { operation with Work = work }

    /// Records manifest completion without changing the captured object or request.
    let uploaded operation =
        match operation.Work with
        | OperationWork.SavedFile (intent, SavedFileProgress.RequestFrozen request) ->
            { operation with Work = OperationWork.SavedFile(intent, SavedFileProgress.Uploaded request) }
        | _ -> invalidOp "Library file upload has no frozen request."

    /// Validates genuine receipt metadata and stores exactly one acceptance or rejection fact.
    let receive (receipt: LibraryOperationReceiptDto) operation =
        if receipt.OperationId <> operation.OperationId then
            invalidOp "Library receipt identity changed."

        let request =
            operation.RequestJson
            |> Option.defaultWith (fun () -> invalidOp "Library receipt has no frozen request.")

        let accepted, rejected =
            match receipt.Outcome, receipt.Change, receipt.ReasonCode with
            | outcome, Some change, None when
                (outcome = OutcomeKind.Accepted
                 && change.Conflict.IsNone
                 || outcome = OutcomeKind.ConflictCopy
                    && change.Conflict.IsSome)
                && change.OperationId = operation.OperationId
                && receipt.CurrentLibraryCatalog.IsNone
                && receipt.Rebaseline.IsNone
                ->
                Some { RequestHash = receipt.RequestHash; Change = change }, None
            | OutcomeKind.Rejected, None, Some reason ->
                None,
                Some
                    {
                        RequestHash = receipt.RequestHash
                        ReasonCode = rejectionCode reason
                        CurrentCatalog = receipt.CurrentLibraryCatalog
                        Rebaseline = receipt.Rebaseline
                    }
            | _ -> invalidOp "Library receipt does not describe a supported acceptance or rejection."

        let work =
            match operation.Work, accepted, rejected with
            | OperationWork.SavedFile (intent, _), Some value, _ when operation.Uploaded ->
                OperationWork.SavedFile(intent, SavedFileProgress.Accepted(request, value, ApplicationProgress.AwaitingPreparation))
            | OperationWork.SavedFile (intent, _), _, Some value when operation.Uploaded ->
                OperationWork.SavedFile(intent, SavedFileProgress.Rejected(request, value))
            | OperationWork.DirectoryCreate (intent, _), Some value, _ ->
                OperationWork.DirectoryCreate(intent, DirectoryProgress.Accepted(request, value, ApplicationProgress.AwaitingPreparation))
            | OperationWork.DirectoryCreate (intent, _), _, Some value -> OperationWork.DirectoryCreate(intent, DirectoryProgress.Rejected(request, value))
            | OperationWork.ExplicitRename (intent, _), Some value, _ ->
                OperationWork.ExplicitRename(intent, RenameProgress.Accepted(request, value, ApplicationProgress.AwaitingPreparation))
            | OperationWork.ExplicitRename (intent, _), _, Some value -> OperationWork.ExplicitRename(intent, RenameProgress.Rejected(request, value))
            | _ -> invalidOp "Library receipt cannot replace this operation family."

        match operation.Receipt with
        | Some retained when retained = receipt -> operation
        | Some _ -> invalidOp "Library receipt cannot replace retained acceptance or rejection."
        | None -> { operation with Work = work }

    /// Replaces only the application stage of already accepted or incoming work.
    let private mapApplication transform operation =
        let work =
            match operation.Work with
            | OperationWork.SavedFile (intent, SavedFileProgress.Accepted (request, receipt, progress)) ->
                OperationWork.SavedFile(intent, SavedFileProgress.Accepted(request, receipt, transform progress))
            | OperationWork.DirectoryCreate (intent, DirectoryProgress.Accepted (request, receipt, progress)) ->
                OperationWork.DirectoryCreate(intent, DirectoryProgress.Accepted(request, receipt, transform progress))
            | OperationWork.ExplicitRename (intent, RenameProgress.Accepted (request, receipt, progress)) ->
                OperationWork.ExplicitRename(intent, RenameProgress.Accepted(request, receipt, transform progress))
            | OperationWork.Incoming (change, path, previous, progress) -> OperationWork.Incoming(change, path, previous, transform progress)
            | _ -> invalidOp "Library operation has no accepted application."

        { operation with Work = work }

    /// Installs the first exact filesystem checkpoint, separately from baseline tombstone completion.
    let prepare checkpoint operation =
        match operation.Work with
        | OperationWork.BaselineLive (item, BaselineInstallation.Selected) ->
            { operation with Work = OperationWork.BaselineLive(item, BaselineInstallation.Prepared checkpoint) }
        | _ ->
            mapApplication
                (function
                | ApplicationProgress.AwaitingPreparation -> ApplicationProgress.Prepared checkpoint
                | _ -> invalidOp "Library application is already prepared.")
                operation

    /// Refreshes a prepared target only after newly saved content has been retained.
    let refreshTarget target operation =
        mapApplication
            (function
            | ApplicationProgress.Prepared value -> ApplicationProgress.Prepared { value with ExpectedTarget = targetObservation target }
            | _ -> invalidOp "Library target is not prepared.")
            operation

    /// Changes only Watch bookkeeping on a real prepared/completed checkpoint.
    let setEcho echo operation =
        /// Changes the classification flag without changing the prepared publication authority.
        let update value = { value with Echo = if echo then EchoState.Pending else EchoState.Clear }

        match operation.Work with
        | OperationWork.BaselineLive (item, BaselineInstallation.Prepared value) ->
            { operation with Work = OperationWork.BaselineLive(item, BaselineInstallation.Prepared(update value)) }
        | OperationWork.BaselineLive (item, BaselineInstallation.Installed value) ->
            { operation with Work = OperationWork.BaselineLive(item, BaselineInstallation.Installed(update value)) }
        | _ ->
            mapApplication
                (function
                | ApplicationProgress.Prepared value -> ApplicationProgress.Prepared(update value)
                | ApplicationProgress.Completed value -> ApplicationProgress.Completed(update value)
                | _ -> invalidOp "Library echo has no checkpoint.")
                operation

    /// Produces terminal application or no-file baseline progress for an exact completion transaction.
    let complete operation =
        match operation.Work with
        | OperationWork.BaselineLive (item, BaselineInstallation.Prepared value) ->
            { operation with Work = OperationWork.BaselineLive(item, BaselineInstallation.Installed value) }
        | OperationWork.BaselineTombstone (item, TombstoneInstallation.Selected) ->
            { operation with Work = OperationWork.BaselineTombstone(item, TombstoneInstallation.Installed) }
        | _ ->
            mapApplication
                (function
                | ApplicationProgress.Prepared value -> ApplicationProgress.Completed value
                | _ -> invalidOp "Library application is not prepared for completion.")
                operation

    /// Retires a rejected explicit rename while retaining its exact request and typed receipt.
    let retireRename operation =
        match operation.Work with
        | OperationWork.ExplicitRename (intent, RenameProgress.Rejected (request, receipt)) ->
            { operation with Work = OperationWork.ExplicitRename(intent, RenameProgress.Retired(request, receipt)) }
        | _ -> invalidOp "Only a rejected explicit rename can retire."
