namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Library
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open Grace.Types.UploadSession
open NodaTime
open Orleans
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module LibraryValidation = Grace.Shared.Validation.Library

/// Owns one repository's Library catalog, ordered change admission, and bounded derived records.
type RepositoryLibraryActor
    (
        services: IServiceProvider,
        grainFactory: IGrainFactory,
        authorize: Func<RepositoryId, LibraryWriteAuthorization, CancellationToken, Task<PermissionCheckResult>>,
        libraryTokenKey: byte array
    ) =
    inherit Grain()

    let controlType = "Grace.Library.Control.v2"
    let changeType = "Grace.Library.Change.v2"
    let itemType = "Grace.Library.Item.v2"
    let slotType = "Grace.Library.Slot.v2"
    let receiptType = "Grace.Library.Receipt.v2"
    let contentType = "Grace.Library.Content.v2"
    let historyType = "Grace.Library.History.v2"
    let baselineShardType = "Grace.Library.BaselineShard.v2"
    let baselineManifestType = "Grace.Library.BaselineManifest.v2"
    let failedEventType = "Grace.Library.FailedGraceEvent.v2"

    /// Builds a repository-scoped provider key.
    let key (repositoryId: RepositoryId) tail = LibraryRecords.key (repositoryId.ToString("D") :: tail)

    /// Returns the cursor-segment identity used by the permanent journal.
    let changeSegment cursor = ((cursor - 1L) / 200L).ToString("D20")

    /// Returns the cursor-segment identity used by compact history indexes.
    let historySegment cursor = ((cursor - 1L) / 512L).ToString("D20")

    /// Reads the control record and current provider ETag.
    let readControl repositoryId = LibraryRecords.read<LibraryControlDocument> services LibraryRecords.ControlStorageName controlType (key repositoryId [])

    /// Writes the control record through optimistic concurrency.
    let writeControl repositoryId etag value = LibraryRecords.write services LibraryRecords.ControlStorageName controlType (key repositoryId []) etag value

    /// Reads one accepted journal record by repository cursor.
    let readChange repositoryId cursor =
        LibraryRecords.read<LibraryAcceptedChangeRecord>
            services
            LibraryRecords.ChangesStorageName
            changeType
            (key
                repositoryId
                [
                    changeSegment cursor
                    cursor.ToString("D20")
                ])

    /// Writes one immutable accepted journal record.
    let createChange repositoryId (record: LibraryAcceptedChangeRecord) =
        LibraryRecords.createExact
            services
            LibraryRecords.ChangesStorageName
            changeType
            (key
                repositoryId
                [
                    changeSegment record.Cursor
                    record.Cursor.ToString("D20")
                ])
            record

    /// Reads one permanent operation receipt.
    let readReceipt repositoryId (operationId: LibraryOperationId) =
        LibraryRecords.read<LibraryReceiptDocument>
            services
            LibraryRecords.ReceiptsStorageName
            receiptType
            (key
                repositoryId
                [
                    "operation"
                    operationId.ToString("D")
                ])

    /// Writes one immutable operation receipt.
    let createReceipt repositoryId (receipt: LibraryReceiptDocument) =
        LibraryRecords.createExact
            services
            LibraryRecords.ReceiptsStorageName
            receiptType
            (key
                repositoryId
                [
                    "operation"
                    receipt.OperationId.ToString("D")
                ])
            receipt

    /// Returns the stable broker identity reused after ambiguous Library wake delivery.
    let stableMessageId (repositoryId: RepositoryId) cursor = $"LibraryContentAvailable/{repositoryId:D}/{cursor:D20}"

    /// Reads the one failure-only Library wake envelope retained for this repository.
    let readFailedEvent repositoryId =
        LibraryRecords.read<FailedGraceEventEnvelope>
            services
            LibraryRecords.ReceiptsStorageName
            failedEventType
            (key repositoryId [ "failed-event"; "single" ])

    /// Retains the exact Library wake envelope after terminal transport failure.
    let createFailedEvent repositoryId envelope =
        LibraryRecords.createExact services LibraryRecords.ReceiptsStorageName failedEventType (key repositoryId [ "failed-event"; "single" ]) envelope

    /// Clears the fixed failed-envelope slot through its last observed ETag.
    let clearFailedEvent repositoryId =
        task {
            match! readFailedEvent repositoryId with
            | Some (envelope, etag) ->
                do!
                    LibraryRecords.clear
                        services
                        LibraryRecords.ReceiptsStorageName
                        failedEventType
                        (key repositoryId [ "failed-event"; "single" ])
                        etag
                        envelope
            | None -> ()
        }

    /// Reads one current item projection.
    let readItem repositoryId (itemId: LibraryItemId) =
        LibraryRecords.read<LibraryCurrentItemDocument> services LibraryRecords.CurrentStorageName itemType (key repositoryId [ "item"; itemId.ToString("D") ])

    /// Reads one current namespace slot and verifies its full collision-resistant identity.
    let readSlot repositoryId parent name =
        task {
            let slotKey = LibraryDecision.slotKey parent name

            match! LibraryRecords.read<LibraryCurrentSlotDocument> services LibraryRecords.CurrentStorageName slotType (key repositoryId [ "slot"; slotKey ])
                with
            | Some (record, etag) when
                LibraryDecision.parentsEqual record.Slot.Parent parent
                && String.Equals(record.Slot.Name, LibraryDecision.normalizeName name, StringComparison.OrdinalIgnoreCase)
                ->
                return Some(record, etag)
            | Some _ -> return invalidOp "A Library slot hash collision was detected."
            | None -> return None
        }

    /// Reads one retained content descriptor.
    let readContent repositoryId (contentVersionId: LibraryContentVersionId) =
        LibraryRecords.read<LibraryContentLocationDocument>
            services
            LibraryRecords.CurrentStorageName
            contentType
            (key
                repositoryId
                [
                    "content"
                    contentVersionId.ToString("D")
                ])

    /// Writes an item projection idempotently at one accepted cursor.
    let writeItem repositoryId cursor (item: LibraryItemDto) =
        task {
            let value = { SchemaVersion = 1; Item = item; LastCursor = cursor; HistoryTailSegment = None }

            match! readItem repositoryId item.ItemId with
            | None ->
                let! _ =
                    LibraryRecords.write services LibraryRecords.CurrentStorageName itemType (key repositoryId [ "item"; item.ItemId.ToString("D") ]) null value

                return ()
            | Some (current, _) when current.LastCursor = cursor -> return ()
            | Some (current, _) when current.LastCursor > cursor -> return invalidOp "A newer Library item projection already exists."
            | Some (current, etag) ->
                let! _ =
                    LibraryRecords.write
                        services
                        LibraryRecords.CurrentStorageName
                        itemType
                        (key repositoryId [ "item"; item.ItemId.ToString("D") ])
                        etag
                        { value with HistoryTailSegment = current.HistoryTailSegment }

                return ()
        }

    /// Writes a slot projection idempotently at one accepted cursor.
    let writeSlot repositoryId cursor (slot: LibraryNamespaceSlotDto) =
        task {
            let slotKey = LibraryDecision.slotKey slot.Parent slot.Name
            let value = { SchemaVersion = 1; Slot = slot; LastCursor = cursor; HistoryTailSegment = None }

            match! readSlot repositoryId slot.Parent slot.Name with
            | None ->
                let! _ = LibraryRecords.write services LibraryRecords.CurrentStorageName slotType (key repositoryId [ "slot"; slotKey ]) null value

                return ()
            | Some (current, _) when current.LastCursor = cursor -> return ()
            | Some (current, _) when current.LastCursor > cursor -> return invalidOp "A newer Library slot projection already exists."
            | Some (current, etag) ->
                let! _ =
                    LibraryRecords.write
                        services
                        LibraryRecords.CurrentStorageName
                        slotType
                        (key repositoryId [ "slot"; slotKey ])
                        etag
                        { value with HistoryTailSegment = current.HistoryTailSegment }

                return ()
        }

    /// Advances one current item's exact-key history pointer without replacing a newer item value.
    let updateItemHistoryTail repositoryId itemId cursor =
        task {
            match! readItem repositoryId itemId with
            | None -> return invalidOp "A Library history cursor references a missing current item."
            | Some (current, _) when current.LastCursor < cursor -> return invalidOp "Library item history advanced ahead of its current projection."
            | Some (current, _) when current.HistoryTailSegment = Some(historySegment cursor) -> return ()
            | Some (current, etag) ->
                let! _ =
                    LibraryRecords.write
                        services
                        LibraryRecords.CurrentStorageName
                        itemType
                        (key repositoryId [ "item"; itemId.ToString("D") ])
                        etag
                        { current with HistoryTailSegment = Some(historySegment cursor) }

                return ()
        }

    /// Advances one current slot's exact-key history pointer without replacing a newer slot value.
    let updateSlotHistoryTail repositoryId (slot: LibraryNamespaceDto) cursor =
        task {
            match! readSlot repositoryId slot.Parent slot.Name with
            | None -> return invalidOp "A Library history cursor references a missing current slot."
            | Some (current, _) when current.LastCursor < cursor -> return invalidOp "Library slot history advanced ahead of its current projection."
            | Some (current, _) when current.HistoryTailSegment = Some(historySegment cursor) -> return ()
            | Some (current, etag) ->
                let slotKey = LibraryDecision.slotKey slot.Parent slot.Name

                let! _ =
                    LibraryRecords.write
                        services
                        LibraryRecords.CurrentStorageName
                        slotType
                        (key repositoryId [ "slot"; slotKey ])
                        etag
                        { current with HistoryTailSegment = Some(historySegment cursor) }

                return ()
        }

    /// Reconstructs the compact public receipt referenced by a durable operation outcome.
    let resolveOutcome repositoryId operationId requestHash outcome =
        task {
            match outcome with
            | LibraryOperationOutcome.AcceptedChange cursor ->
                match! readChange repositoryId cursor with
                | Some (record, _) ->
                    return
                        {
                            OperationId = operationId
                            RequestHash = requestHash
                            Outcome =
                                if record.Change.Conflict.IsSome then
                                    OutcomeKind.ConflictCopy
                                else
                                    OutcomeKind.Accepted
                            Change = Some record.Change
                            ReasonCode = None
                            CurrentLibraryCatalog = None
                            Rebaseline = None
                        }
                | None -> return invalidOp "An accepted Library receipt references a missing journal record."
            | LibraryOperationOutcome.RejectedChange receipt -> return receipt
            | LibraryOperationOutcome.CatalogResult _ -> return invalidOp "A catalog operation cannot be returned as an item-change receipt."
        }

    /// Resolves a parent to its current complete repository-relative path while rejecting broken or cyclic graphs.
    let resolveParentPath repositoryId (catalog: LibraryCatalogDto) parent =
        let rec loop visited current =
            task {
                match current.Kind, current.LibraryPath, current.ItemId with
                | "root", Some root, None when
                    catalog.Libraries
                    |> Array.exists (LibraryValidation.pathsEqual root)
                    ->
                    return root
                | "item", None, Some itemId when Set.contains itemId visited -> return invalidOp "The Library parent graph contains a cycle."
                | "item", None, Some itemId ->
                    match! readItem repositoryId itemId with
                    | Some (record, _) when
                        record.Item.ItemKind = ItemKind.Directory
                        && record.Item.Tombstone.IsNone
                        && record.Item.Namespace.IsSome
                        ->
                        let ns = record.Item.Namespace.Value
                        let! parentPath = loop (Set.add itemId visited) ns.Parent
                        return parentPath + "/" + ns.Name
                    | _ -> return invalidOp "The Library parent does not identify a live directory."
                | _ -> return invalidOp "The Library parent does not identify one configured root or directory."
            }

        loop Set.empty parent

    /// Resolves one item path from its current parent/name edge.
    let resolveItemPath repositoryId catalog (item: LibraryItemDto) =
        task {
            let ns =
                item.Namespace
                |> Option.defaultWith (fun () -> invalidOp "A live Library item has no namespace edge.")

            let! parentPath = resolveParentPath repositoryId catalog ns.Parent
            return parentPath + "/" + ns.Name
        }

    /// Returns the current or initial vacant slot observed by callers.
    let getSlot repositoryId parent name =
        task {
            match! readSlot repositoryId parent name with
            | Some (record, _) -> return record.Slot
            | None ->
                return
                    ({
                         Parent = parent
                         Name = LibraryDecision.normalizeName name
                         SlotVersion = LibraryDecision.initialSlotVersion repositoryId parent name
                         OccupantItemId = None
                     }: LibraryNamespaceSlotDto)
        }

    /// Verifies a caller's exact vacant-slot observation.
    let slotExpectationMatches repositoryId (expectation: LibraryCreationSlotExpectationDto) =
        task {
            let! slot = getSlot repositoryId expectation.Parent expectation.Name

            return
                String.Equals(expectation.ExpectedState, "vacant", StringComparison.OrdinalIgnoreCase)
                && slot.OccupantItemId.IsNone
                && slot.SlotVersion = expectation.ExpectedSlotVersion
        }

    /// Reads and validates the existing upload session used by an accepted content change.
    let resolveUpload now repositoryId operationId principalId uploadSessionId correlationId =
        task {
            let actor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy uploadSessionId repositoryId correlationId
            let! upload = actor.Get correlationId

            match LibraryTransfer.validatePreparedUpload now repositoryId operationId principalId upload with
            | Error reason -> return Error reason
            | Ok (binding, manifest) ->
                let proposedContent =
                    {
                        ContentVersionId = LibraryDecision.contentVersionId upload.FileContentHash
                        Blake3Hash = upload.FileContentHash
                        Sha256Hash = binding.ExpectedSha256
                        Size = upload.ExpectedSize
                        CreatedAt = now
                    }

                match! readContent repositoryId proposedContent.ContentVersionId with
                | Some (existing, _) when
                    existing.Content.Blake3Hash = proposedContent.Blake3Hash
                    && existing.Content.Sha256Hash = proposedContent.Sha256Hash
                    && existing.Content.Size = proposedContent.Size
                    && existing.Manifest = manifest
                    ->
                    return Ok existing
                | Some _ -> return invalidOp "A Library content identity already refers to different immutable bytes."
                | None ->
                    let location = { SchemaVersion = 1; Content = proposedContent; AuthorizedScope = upload.AuthorizedScope; Manifest = manifest }

                    let! created =
                        LibraryRecords.createExact
                            services
                            LibraryRecords.CurrentStorageName
                            contentType
                            (key
                                repositoryId
                                [
                                    "content"
                                    proposedContent.ContentVersionId.ToString("D")
                                ])
                            location

                    return Ok created
        }

    /// Completes the permanent tracked-manifest contribution and verifies its persisted workflow result.
    let activateManifest repositoryId operationId correlationId (location: LibraryContentLocationDocument) =
        task {
            let manifest = location.Manifest
            let counterOperationId = LibraryTransfer.counterOperationId operationId location.Content.ContentVersionId
            let metadata = EventMetadata.New correlationId "RepositoryLibraryActor"
            let ranges = LibraryTransfer.workflowRanges manifest

            if Array.isEmpty ranges then
                invalidOp "A retained Library manifest must contain at least one block."

            let workflow =
                grainFactory.GetGrain<IManifestContributionWorkflowActor>(
                    ManifestContributionWorkflow.primaryKey repositoryId manifest.StoragePoolId manifest.ManifestAddress
                )

            let! existingWorkflow = workflow.Get correlationId

            if LibraryTransfer.workflowCompletedForTrackedManifest repositoryId counterOperationId manifest ranges existingWorkflow then
                return ()
            else
                let counter =
                    grainFactory.GetGrain<IRepositoryContentCounterActor>(
                        RepositoryContentCounter.primaryKey repositoryId manifest.StoragePoolId manifest.ManifestAddress
                    )

                match! counter.AddTrackedReference counterOperationId repositoryId manifest.StoragePoolId manifest.ManifestAddress metadata with
                | Error error -> return invalidOp $"Library manifest counter failed: {error.Error}"
                | Ok result ->
                    let transition =
                        result.ReturnValue.Intents
                        |> List.tryPick (function
                            | IncrementManifestReferenceCount (_, pool, address, revision) -> Some(pool, address, revision)
                            | _ -> None)

                    let! counterState = counter.Get correlationId

                    let pool, address, revision =
                        match transition, counterState.PendingTrackedAdd with
                        | Some value, _ -> value
                        | None, Some pending when pending.OperationId = counterOperationId -> manifest.StoragePoolId, manifest.ManifestAddress, pending.Revision
                        | None, _ -> manifest.StoragePoolId, manifest.ManifestAddress, counterState.Revision

                    match!
                        workflow.Start $"{counterOperationId}:fanout" repositoryId pool address ManifestContributionDirection.Increment ranges revision metadata
                        with
                    | Error error -> return invalidOp $"Library manifest workflow failed: {error.Error}"
                    | Ok _ ->
                        let! persisted = workflow.Get correlationId

                        if
                            persisted.LifecycleState
                            <> ManifestContributionWorkflowLifecycleState.Completed
                            || persisted.CompletedRanges.Length <> ranges.Length
                            || not
                                (
                                    ranges
                                    |> Array.forall (fun expected ->
                                        persisted.CompletedRanges
                                        |> Array.exists (fun actual -> actual.Range = expected))
                                )
                        then
                            return invalidOp "The Library manifest contribution workflow did not complete every retained range."
        }

    /// Releases the counter's tracked-add marker after receipt durability.
    let acknowledgeManifest repositoryId operationId correlationId (location: LibraryContentLocationDocument) =
        task {
            let manifest = location.Manifest

            let counter =
                grainFactory.GetGrain<IRepositoryContentCounterActor>(
                    RepositoryContentCounter.primaryKey repositoryId manifest.StoragePoolId manifest.ManifestAddress
                )

            match!
                counter.CompleteTrackedReference
                    (LibraryTransfer.counterOperationId operationId location.Content.ContentVersionId)
                    (EventMetadata.New correlationId "RepositoryLibraryActor")
                with
            | Error error -> return invalidOp $"Library manifest acknowledgement failed: {error.Error}"
            | Ok _ -> return ()
        }

    /// Writes the resulting current projections for one accepted decision.
    let writeProjections repositoryId (record: LibraryAcceptedChangeRecord) =
        task {
            let item = record.Change.Item
            do! writeItem repositoryId record.Cursor item

            match record.PriorNamespace with
            | Some prior when
                item.Namespace.IsNone
                || not (
                    LibraryDecision.parentsEqual prior.Parent item.Namespace.Value.Parent
                    && String.Equals(prior.Name, item.Namespace.Value.Name, StringComparison.OrdinalIgnoreCase)
                )
                ->
                do!
                    writeSlot
                        repositoryId
                        record.Cursor
                        {
                            Parent = prior.Parent
                            Name = prior.Name
                            SlotVersion = LibraryDecision.deterministicGuid repositoryId record.Change.OperationId "vacated-slot"
                            OccupantItemId = None
                        }
            | _ -> ()

            match item.Namespace with
            | Some ns when
                record.PriorNamespace.IsNone
                || not (
                    LibraryDecision.parentsEqual record.PriorNamespace.Value.Parent ns.Parent
                    && String.Equals(record.PriorNamespace.Value.Name, ns.Name, StringComparison.OrdinalIgnoreCase)
                )
                ->
                do!
                    writeSlot
                        repositoryId
                        record.Cursor
                        {
                            Parent = ns.Parent
                            Name = ns.Name
                            SlotVersion = LibraryDecision.deterministicGuid repositoryId record.Change.OperationId "occupied-slot"
                            OccupantItemId = Some item.ItemId
                        }
            | _ -> ()
        }

    /// Completes a saved item decision in the required journal, retention, projection, receipt, acknowledgement, control order.
    let completeItemPending repositoryId etag (control: LibraryControlDocument) (record: LibraryAcceptedChangeRecord) =
        task {
            let! _ = createChange repositoryId record

            let! location =
                task {
                    match record.Change.ChangeKind, record.Change.Item.Content with
                    | changeKind, Some content when
                        changeKind = ChangeKind.CreateFile
                        || changeKind = ChangeKind.UpdateContent
                        ->
                        match! readContent repositoryId content.ContentVersionId with
                        | Some (value, _) -> return Some value
                        | None -> return invalidOp "Accepted Library content has no retained manifest."
                    | _ -> return None
                }

            let receipt =
                {
                    SchemaVersion = 1
                    OperationId = record.Change.OperationId
                    RequestHash = record.RequestHash
                    Outcome = LibraryOperationOutcome.AcceptedChange record.Cursor
                }

            let! completedBeforeAcknowledgement =
                task {
                    match! readReceipt repositoryId receipt.OperationId with
                    | Some (existing, _) when existing = receipt -> return true
                    | Some _ -> return invalidOp "The pending Library decision conflicts with its permanent receipt."
                    | None -> return false
                }

            if not completedBeforeAcknowledgement then
                match location with
                | Some value -> do! activateManifest repositoryId record.Change.OperationId record.CorrelationId value
                | None -> ()

                do! writeProjections repositoryId record
                let! _ = createReceipt repositoryId receipt
                ()

            match location with
            | Some value -> do! acknowledgeManifest repositoryId record.Change.OperationId record.CorrelationId value
            | None -> ()

            let committed =
                { control with
                    Pending = None
                    CommittedCursor = record.Cursor
                    ItemRecordCount =
                        control.ItemRecordCount
                        + if record.AddedItemRecord then 1 else 0
                    SlotRecordCount =
                        control.SlotRecordCount
                        + if record.AddedSlotRecord then 1 else 0
                }

            let! _ = writeControl repositoryId etag committed
            return! resolveOutcome repositoryId receipt.OperationId receipt.RequestHash receipt.Outcome
        }

    /// Completes a saved catalog decision and records its exact public result.
    let completeCatalogPending
        repositoryId
        etag
        (control: LibraryControlDocument)
        operationId
        requestHash
        expectedVersion
        chosenVersion
        add
        path
        acceptedAt
        principalId
        =
        task {
            if control.Catalog.Version <> expectedVersion then
                return invalidOp "The saved Library catalog decision no longer matches control."
            else
                let libraries =
                    if add then
                        Array.append control.Catalog.Libraries [| path |]
                    else
                        control.Catalog.Libraries
                        |> Array.filter (fun current -> not (LibraryValidation.pathsEqual current path))

                let catalog =
                    {
                        RepositoryId = repositoryId
                        Version = chosenVersion
                        Libraries = libraries
                        CreatedAt = acceptedAt
                        CreatedBy = principalId
                        PreviousVersion = Some expectedVersion
                    }

                let result = { OperationId = operationId; Outcome = OutcomeKind.Accepted; LibraryCatalog = catalog; ReasonCode = None; RecordedAt = acceptedAt }

                let receipt =
                    { SchemaVersion = 1; OperationId = operationId; RequestHash = requestHash; Outcome = LibraryOperationOutcome.CatalogResult result }

                let! _ = createReceipt repositoryId receipt
                let! _ = writeControl repositoryId etag { control with Catalog = catalog; Pending = None }
                return result
        }

    /// Repairs the one saved synchronous decision before another result can become visible.
    let repairPending repositoryId =
        task {
            match! readControl repositoryId with
            | None -> return ()
            | Some (control, etag) ->
                match control.Pending with
                | None -> return ()
                | Some (LibraryPendingDecision.ItemChange record) ->
                    let! _ = completeItemPending repositoryId etag control record
                    return ()
                | Some (LibraryPendingDecision.CatalogChange (operationId, requestHash, expectedVersion, chosenVersion, add, path, at, principal)) ->
                    let! _ = completeCatalogPending repositoryId etag control operationId requestHash expectedVersion chosenVersion add path at principal

                    return ()
        }

    /// Persists one deterministic rejection as the permanent operation result.
    let reject repositoryId operationId requestHash reason catalog =
        task {
            let publicResult =
                {
                    OperationId = operationId
                    RequestHash = requestHash
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some reason
                    CurrentLibraryCatalog = Some catalog
                    Rebaseline = None
                }

            let receipt =
                { SchemaVersion = 1; OperationId = operationId; RequestHash = requestHash; Outcome = LibraryOperationOutcome.RejectedChange publicResult }

            let! _ = createReceipt repositoryId receipt
            return publicResult
        }

    /// Appends one cursor to an exact compact history segment.
    let appendHistory repositoryId identity cursor =
        task {
            let segment = historySegment cursor
            let recordKey = key repositoryId [ identity; segment ]

            match! LibraryRecords.read<LibraryHistorySegmentDocument> services LibraryRecords.HistoryStorageName historyType recordKey with
            | Some (current, _) when current.Cursors |> Array.contains cursor -> return ()
            | Some (current, etag) ->
                if current.Cursors.Length >= 512 then
                    return invalidOp "A Library history segment exceeded its cursor bound."

                let updated = { current with Cursors = Array.append current.Cursors [| cursor |] }

                if JsonSerializer
                    .SerializeToUtf8Bytes(
                        updated,
                        Constants.JsonSerializerOptions
                    )
                    .Length > 900 * 1024 then
                    return invalidOp "A Library history segment exceeded its byte bound."

                let! _ = LibraryRecords.write services LibraryRecords.HistoryStorageName historyType recordKey etag updated
                return ()
            | None ->
                let previous =
                    let numeric = Int64.Parse segment
                    if numeric = 0L then None else Some((numeric - 1L).ToString("D20"))

                let value = { SchemaVersion = 1; PreviousSegment = previous; Cursors = [| cursor |] }
                let! _ = LibraryRecords.write services LibraryRecords.HistoryStorageName historyType recordKey null value
                return ()
        }

    /// Advances compact history indexes independently from synchronous acceptance.
    let drainHistory repositoryId =
        task {
            let mutable keepGoing = true

            while keepGoing do
                match! readControl repositoryId with
                | Some (control, etag) when
                    control.Pending.IsNone
                    && control.HistoryThrough < control.CommittedCursor
                    ->
                    let cursor = control.HistoryThrough + 1L

                    match! readChange repositoryId cursor with
                    | None -> return invalidOp "Committed Library history references a missing change."
                    | Some (record, _) ->
                        do! appendHistory repositoryId $"item:{record.Change.Item.ItemId:D}" cursor
                        do! updateItemHistoryTail repositoryId record.Change.Item.ItemId cursor

                        match record.PriorNamespace with
                        | Some ns ->
                            do! appendHistory repositoryId $"slot:{LibraryDecision.slotKey ns.Parent ns.Name}" cursor
                            do! updateSlotHistoryTail repositoryId ns cursor
                        | None -> ()

                        match record.Change.Item.Namespace with
                        | Some ns ->
                            do! appendHistory repositoryId $"slot:{LibraryDecision.slotKey ns.Parent ns.Name}" cursor
                            do! updateSlotHistoryTail repositoryId ns cursor
                        | None -> ()

                        let! _ = writeControl repositoryId etag { control with HistoryThrough = cursor }
                        ()
                | _ -> keepGoing <- false
        }

    /// Attempts background indexing without making accepted namespace state depend on it.
    let tryDrainHistory repositoryId =
        task {
            try
                do! drainHistory repositoryId
            with
            | _ -> ()
        }

    /// Reconstructs one deterministic content-free wake envelope from the permanent accepted-change journal.
    let notificationEnvelope repositoryId (control: LibraryControlDocument) cursor =
        task {
            match! readChange repositoryId cursor with
            | None -> return invalidOp "Committed Library notification progress references a missing change."
            | Some (record, _) ->
                let payload =
                    LibraryContentAvailable.Create(
                        repositoryId,
                        control.Epoch.ToString("D"),
                        LibraryTokens.cursor libraryTokenKey repositoryId control.Epoch cursor,
                        record.Change.LibraryCatalogVersion,
                        record.Change.AcceptedAt,
                        record.CorrelationId
                    )

                return
                    tryCreateLibraryGraceEventEnvelope
                        (stableMessageId repositoryId cursor)
                        (GraceEvent.LibraryContentAvailableEvent payload)
                        (EventMetadata.New record.CorrelationId "RepositoryLibraryActor")
        }

    /// Advances advisory Library wake delivery from NotifyThrough and stops at the first terminal transport failure.
    let drainNotifications repositoryId =
        task {
            let mutable keepGoing = true

            while keepGoing do
                match! readControl repositoryId with
                | None -> keepGoing <- false
                | Some (control, etag) when control.Pending.IsSome -> keepGoing <- false
                | Some (control, _) when control.NotifyThrough >= control.CommittedCursor ->
                    do! clearFailedEvent repositoryId
                    keepGoing <- false
                | Some (control, etag) ->
                    let cursor = control.NotifyThrough + 1L
                    let expectedMessageId = stableMessageId repositoryId cursor
                    let! retained = readFailedEvent repositoryId

                    match retained with
                    | Some (envelope, _) when envelope.MessageId <> expectedMessageId -> do! clearFailedEvent repositoryId
                    | _ ->
                        let! candidate =
                            task {
                                match retained with
                                | Some (envelope, _) -> return Some envelope
                                | None -> return! notificationEnvelope repositoryId control cursor
                            }

                        match candidate with
                        | None ->
                            let! _ = writeControl repositoryId etag { control with NotifyThrough = cursor }
                            ()
                        | Some envelope ->
                            let! delivered =
                                LibraryNotifications.attempt
                                    (fun value -> task { do! sendGraceEventEnvelope value CancellationToken.None })
                                    (fun () ->
                                        task {
                                            let! _ = writeControl repositoryId etag { control with NotifyThrough = cursor }
                                            return ()
                                        })
                                    (fun value ->
                                        task {
                                            let! _ = createFailedEvent repositoryId value
                                            return ()
                                        })
                                    (fun () -> clearFailedEvent repositoryId)
                                    retained.IsSome
                                    envelope

                            if not delivered then keepGoing <- false
        }

    /// Attempts advisory wake delivery without making accepted changes or history depend on broker health.
    let tryDrainNotifications repositoryId =
        task {
            try
                do! drainNotifications repositoryId
            with
            | _ -> ()
        }

    /// Returns the current control state after synchronous repair.
    let currentControl repositoryId : Task<LibraryControlDocument * string> =
        task {
            do! repairPending repositoryId

            match! readControl repositoryId with
            | Some value -> return value
            | None -> return invalidOp "Library catalog is not initialized."
        }

    /// Compares an item's current namespace generation with a submitted precondition.
    let namespaceMatches (item: LibraryItemDto) (precondition: LibraryNamespacePreconditionDto) =
        precondition.ItemId = item.ItemId
        && item.Namespace
           |> Option.exists (fun ns -> ns.NamespaceVersion = precondition.ExpectedNamespaceVersion)

    /// Compares an item's current byte identity and causal content revision with a submitted precondition.
    let contentMatches (item: LibraryItemDto) (precondition: LibraryContentPreconditionDto) =
        precondition.ItemId = item.ItemId
        && item.Content
           |> Option.exists (fun content -> content.ContentVersionId = precondition.ExpectedContentVersionId)
        && item.ContentRevision = Some precondition.ExpectedContentRevision

    /// Reserves and completes one fully chosen item decision.
    let reserveItem repositoryId etag control record =
        task {
            let pendingControl = { control with Pending = Some(LibraryPendingDecision.ItemChange record) }
            let! pendingEtag = writeControl repositoryId etag pendingControl
            let! result = completeItemPending repositoryId pendingEtag pendingControl record
            do! tryDrainHistory repositoryId
            do! tryDrainNotifications repositoryId
            return result
        }

    /// Chooses the first deterministic vacant sibling for a stale-content conflict copy.
    let chooseConflictSlot repositoryId parent originalName operationId =
        let rec loop attempt =
            task {
                if attempt >= 100000 then
                    return invalidOp "No bounded conflict-copy destination was available."
                else
                    let candidate = LibraryDecision.conflictName originalName operationId attempt
                    let! slot = getSlot repositoryId parent candidate

                    if slot.OccupantItemId.IsNone then
                        return candidate, slot
                    else
                        return! loop (attempt + 1)
            }

        loop 0

    /// Reports whether moving a directory beneath the selected parent would create a parent cycle.
    let wouldCreateCycle repositoryId itemId parent =
        let rec loop visited current =
            task {
                match current.Kind, current.ItemId with
                | "root", None -> return false
                | "item", Some currentId when currentId = itemId -> return true
                | "item", Some currentId when Set.contains currentId visited -> return true
                | "item", Some currentId ->
                    match! readItem repositoryId currentId with
                    | Some (record, _) when record.Item.Namespace.IsSome -> return! loop (Set.add currentId visited) record.Item.Namespace.Value.Parent
                    | _ -> return false
                | _ -> return false
            }

        loop Set.empty parent

    /// Builds one immutable accepted-change record from its chosen result and consumed concurrency facts.
    let acceptedRecord
        cursor
        requestHash
        correlationId
        change
        priorNamespace
        priorContentVersionId
        consumedNamespaceVersion
        consumedContentVersionId
        consumedContentRevision
        consumedSlotVersion
        addedItem
        addedSlot
        =
        {
            SchemaVersion = 1
            Cursor = cursor
            RequestHash = requestHash
            CorrelationId = correlationId
            Change = change
            PriorNamespace = priorNamespace
            PriorContentVersionId = priorContentVersionId
            ConsumedNamespaceVersion = consumedNamespaceVersion
            ConsumedContentVersionId = consumedContentVersionId
            ConsumedContentRevision = consumedContentRevision
            ConsumedSlotVersion = consumedSlotVersion
            AddedItemRecord = addedItem
            AddedSlotRecord = addedSlot
        }

    /// Builds one accepted public change around its resulting item.
    let acceptedChange operationId changeKind acceptedAt acceptedBy catalogVersion item conflict =
        {
            OperationId = operationId
            ChangeKind = changeKind
            AcceptedAt = acceptedAt
            AcceptedBy = acceptedBy
            LibraryCatalogVersion = catalogVersion
            Item = item
            Conflict = conflict
        }

    /// Returns a page-size value constrained to the public Product V1 bound.
    let boundedPageSize (pageSize: int) = Math.Clamp(pageSize, LibraryValidation.MinimumPageSize, LibraryValidation.MaximumPageSize)

    /// Derives a stable immutable baseline identity from its publication boundary.
    let baselineId repositoryId (epoch: Guid) boundary (catalogVersion: LibraryCatalogVersion) =
        LibraryDecision.deterministicGuid repositoryId catalogVersion $"baseline:{epoch:D}:{boundary}"

    /// Reads a published baseline manifest.
    let readBaselineManifest repositoryId (bootstrapId: LibraryBootstrapId) =
        LibraryRecords.read<LibraryBaselineManifestDocument>
            services
            LibraryRecords.BaselinesStorageName
            baselineManifestType
            (key
                repositoryId
                [
                    bootstrapId.ToString("D")
                    "manifest"
                ])

    /// Reads one immutable baseline shard.
    let readBaselineShard repositoryId (bootstrapId: LibraryBootstrapId) (ordinal: int) =
        LibraryRecords.read<LibraryBaselineShardDocument>
            services
            LibraryRecords.BaselinesStorageName
            baselineShardType
            (key
                repositoryId
                [
                    bootstrapId.ToString("D")
                    ordinal.ToString("D8")
                ])

    /// Loads only the immutable baseline shards intersecting one requested item range.
    let readBaselinePage repositoryId bootstrapId (manifest: LibraryBaselineManifestDocument) offset count =
        task {
            let result = ResizeArray<LibraryItemDto>()
            let mutable start = 0

            for reference in manifest.Shards do
                let finish = start + reference.ItemCount

                if finish > offset && start < offset + count then
                    match! readBaselineShard repositoryId bootstrapId reference.Ordinal with
                    | None -> invalidOp "A published Library baseline references a missing shard."
                    | Some (shard, _) ->
                        let bytes = LibraryQueries.serializeBaselineShard shard
                        let hash = ContentAddress.computeBlake3Hex bytes

                        if not (String.Equals(hash, reference.Blake3Hash, StringComparison.OrdinalIgnoreCase)) then
                            invalidOp "A published Library baseline shard failed its content check."

                        let localStart = Math.Max(0, offset - start)
                        let localEnd = Math.Min(shard.Items.Length, offset + count - start)

                        for index in localStart .. localEnd - 1 do
                            result.Add shard.Items[index]

                start <- finish

            return result.ToArray()
        }

    /// Creates immutable byte-bounded baseline shards and publishes their manifest last.
    let buildBaseline repositoryId (control: LibraryControlDocument) =
        task {
            let bootstrapId = baselineId repositoryId control.Epoch control.CommittedCursor control.Catalog.Version

            match! readBaselineManifest repositoryId bootstrapId with
            | Some (manifest, _) -> return bootstrapId, manifest
            | None ->
                let references = ResizeArray<LibraryBaselineShardReference>()
                let mutable current = ResizeArray<LibraryItemDto>()
                let mutable currentBytes = LibraryQueries.emptyBaselineShardBytes
                let mutable ordinal = 0
                let mutable itemCount = 0
                let mutable continuationToken = None
                let mutable more = true

                let persistShard shard =
                    task {
                        let bytes = LibraryQueries.serializeBaselineShard shard

                        if bytes.Length > LibraryQueries.BaselineShardMaximumBytes then
                            invalidOp "A Library baseline shard exceeded the one-megabyte byte bound."

                        let hash = ContentAddress.computeBlake3Hex bytes

                        let! _ =
                            LibraryRecords.createExact
                                services
                                LibraryRecords.BaselinesStorageName
                                baselineShardType
                                (key
                                    repositoryId
                                    [
                                        bootstrapId.ToString("D")
                                        ordinal.ToString("D8")
                                    ])
                                shard

                        references.Add { Ordinal = ordinal; Blake3Hash = hash; ItemCount = shard.Items.Length }
                        ordinal <- ordinal + 1
                    }

                while more do
                    let! documents, next = LibraryQueries.readCurrentItemPage services repositoryId continuationToken CancellationToken.None

                    for document in documents do
                        itemCount <- itemCount + 1

                        let nextBytes, completed = LibraryQueries.appendBaselineItem current currentBytes document.Item
                        currentBytes <- nextBytes

                        match completed with
                        | Some shard -> do! persistShard shard
                        | None -> ()

                    continuationToken <- next
                    more <- next.IsSome
                    do! Task.Yield()

                match LibraryQueries.finishBaselineShard current currentBytes with
                | Some shard -> do! persistShard shard
                | None -> ()

                if itemCount <> control.ItemRecordCount then
                    invalidOp "The current Library item count does not match authoritative control."

                let manifest =
                    {
                        SchemaVersion = 1
                        Epoch = control.Epoch
                        BoundaryCursor = control.CommittedCursor
                        Catalog = control.Catalog
                        CreatedAt = SystemClock.Instance.GetCurrentInstant()
                        Shards = references.ToArray()
                    }

                let! durable =
                    LibraryRecords.createExact
                        services
                        LibraryRecords.BaselinesStorageName
                        baselineManifestType
                        (key
                            repositoryId
                            [
                                bootstrapId.ToString("D")
                                "manifest"
                            ])
                        manifest

                return bootstrapId, durable
        }

    override this.OnActivateAsync _ =
        task {
            let repositoryId = this.GetPrimaryKey()
            do! repairPending repositoryId
            do! tryDrainHistory repositoryId
            do! tryDrainNotifications repositoryId
        }
        :> Task

    interface IRepositoryLibraryActor with
        member _.PrepareContent start correlationId =
            task {
                let actor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy start.UploadSessionId start.RepositoryId correlationId
                let! existing = actor.Get correlationId

                if existing.LifecycleState = UploadSessionLifecycleState.NotStarted then
                    match! actor.Handle (UploadSessionCommand.Start start) (EventMetadata.New correlationId "RepositoryLibraryActor") with
                    | Error error -> return invalidOp $"Library upload preparation failed: {error.Error}"
                    | Ok _ -> return! actor.Get correlationId
                else
                    let actual =
                        existing.LibraryPreparation
                        |> Option.defaultWith (fun () -> invalidOp "The upload session is not Library-bound.")

                    let expected =
                        start.LibraryPreparation
                        |> Option.defaultWith (fun () -> invalidArg (nameof start) "A Library upload requires a binding.")

                    if existing.RepositoryId <> start.RepositoryId
                       || existing.FileContentHash <> start.FileContentHash
                       || existing.ExpectedSize <> start.ExpectedSize
                       || actual.OperationId <> expected.OperationId
                       || actual.PrincipalId <> expected.PrincipalId
                       || actual.ExpectedSha256 <> expected.ExpectedSha256 then
                        invalidOp "The Library operation is already bound to another upload descriptor."

                    return existing
            }

        member this.InitializeCatalog catalog _ =
            task {
                let repositoryId = this.GetPrimaryKey()

                if catalog.RepositoryId <> repositoryId then
                    invalidArg (nameof catalog) "The Library catalog does not belong to this repository actor."

                match! readControl repositoryId with
                | Some _ -> return ()
                | None ->
                    let control =
                        {
                            SchemaVersion = 1
                            Catalog = catalog
                            Epoch = LibraryDecision.deterministicGuid repositoryId Guid.Empty "epoch"
                            CommittedCursor = 0L
                            ReplayFloor = 1L
                            Pending = None
                            ItemRecordCount = 0
                            SlotRecordCount = 0
                            HistoryThrough = 0L
                            NotifyThrough = 0L
                        }

                    let! _ = writeControl repositoryId null control
                    return ()
            }

        member this.GetCatalog _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId
                return control.Catalog
            }

        member this.ChangeCatalog add expectedVersion path operationId requestHash principalId authorization outgoingSystemEmpty correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()

                match! authorize.Invoke(repositoryId, authorization, CancellationToken.None) with
                | Denied reason -> return Error reason
                | Allowed _ ->
                    do! repairPending repositoryId

                    match! readReceipt repositoryId operationId with
                    | Some (existing, _) when existing.RequestHash <> requestHash -> return Error RejectionReason.OperationIdentityMismatch
                    | Some (existing, _) ->
                        match existing.Outcome with
                        | LibraryOperationOutcome.CatalogResult result -> return Ok result
                        | _ -> return Error RejectionReason.OperationIdentityMismatch
                    | None ->
                        match! readControl repositoryId with
                        | None -> return Error "Library catalog is not initialized."
                        | Some (control, etag) ->
                            let normalizedResult = LibraryValidation.normalizeRepositoryRelativePath path

                            match normalizedResult with
                            | Error _ -> return Error CatalogRejectionReason.UnsupportedPath
                            | Ok normalized ->
                                let rejectCatalog reason : Task<Result<LibraryCatalogChangeResultDto, string>> =
                                    task {
                                        let result =
                                            {
                                                OperationId = operationId
                                                Outcome = OutcomeKind.Rejected
                                                LibraryCatalog = control.Catalog
                                                ReasonCode = Some reason
                                                RecordedAt = SystemClock.Instance.GetCurrentInstant()
                                            }

                                        let receipt =
                                            {
                                                SchemaVersion = 1
                                                OperationId = operationId
                                                RequestHash = requestHash
                                                Outcome = LibraryOperationOutcome.CatalogResult result
                                            }

                                        let! _ = createReceipt repositoryId receipt
                                        return Ok result
                                    }

                                if control.Catalog.Version <> expectedVersion then
                                    return! rejectCatalog OutcomeKind.StalePolicy
                                elif add && not outgoingSystemEmpty then
                                    return! rejectCatalog CatalogRejectionReason.OutgoingSystemNotEmpty
                                elif add
                                     && control.Catalog.Libraries.Length
                                        >= LibraryValidation.MaximumRootCount then
                                    return! rejectCatalog CatalogRejectionReason.LibraryLimitExceeded
                                elif add
                                     && control.Catalog.Libraries
                                        |> Array.exists (fun current -> LibraryValidation.librariesOverlap current normalized) then
                                    return! rejectCatalog CatalogRejectionReason.LibraryOverlap
                                elif
                                    not add
                                    && not
                                        (
                                            control.Catalog.Libraries
                                            |> Array.exists (LibraryValidation.pathsEqual normalized)
                                        )
                                then
                                    return! rejectCatalog CatalogRejectionReason.UnsupportedPath
                                else
                                    let! currentItems = LibraryQueries.readCurrentItems services repositoryId CancellationToken.None
                                    let mutable occupied = false

                                    if not add then
                                        for document in currentItems do
                                            if not occupied && document.Item.Tombstone.IsNone then
                                                let! itemPath = resolveItemPath repositoryId control.Catalog document.Item

                                                occupied <-
                                                    LibraryValidation.configurationOwnsPath { control.Catalog with Libraries = [| normalized |] } itemPath

                                    if occupied then
                                        return! rejectCatalog CatalogRejectionReason.SlotOccupied
                                    else
                                        let chosenVersion = LibraryDecision.deterministicGuid repositoryId operationId "catalog"
                                        let acceptedAt = SystemClock.Instance.GetCurrentInstant()

                                        let pending =
                                            LibraryPendingDecision.CatalogChange(
                                                operationId,
                                                requestHash,
                                                expectedVersion,
                                                chosenVersion,
                                                add,
                                                normalized,
                                                acceptedAt,
                                                principalId
                                            )

                                        let pendingControl = { control with Pending = Some pending }
                                        let! pendingEtag = writeControl repositoryId etag pendingControl

                                        let! result =
                                            completeCatalogPending
                                                repositoryId
                                                pendingEtag
                                                pendingControl
                                                operationId
                                                requestHash
                                                expectedVersion
                                                chosenVersion
                                                add
                                                normalized
                                                acceptedAt
                                                principalId

                                        return Ok result
            }

        member this.IsInLibrary path _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId
                return LibraryDecision.isInLibrary control.Catalog path
            }

        member this.Submit command principalId authorization correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                let operationId = LibraryDecision.operationId command
                let requestHash = LibraryDecision.requestHash command

                match! authorize.Invoke(repositoryId, authorization, CancellationToken.None) with
                | Denied reason -> return Error reason
                | Allowed _ ->
                    do! repairPending repositoryId
                    do! tryDrainNotifications repositoryId

                    match! readReceipt repositoryId operationId with
                    | Some (existing, _) when existing.RequestHash <> requestHash -> return Error RejectionReason.OperationIdentityMismatch
                    | Some (existing, _) ->
                        match existing.Outcome with
                        | LibraryOperationOutcome.CatalogResult _ -> return Error RejectionReason.OperationIdentityMismatch
                        | outcome ->
                            let! receipt = resolveOutcome repositoryId operationId requestHash outcome
                            return Ok receipt
                    | None ->
                        match! readControl repositoryId with
                        | None -> return Error "Library catalog is not initialized."
                        | Some (control, etag) ->
                            if LibraryDecision.catalogVersion command
                               <> control.Catalog.Version then
                                let! receipt = reject repositoryId operationId requestHash OutcomeKind.StalePolicy control.Catalog
                                return Ok receipt
                            else
                                let cursor = control.CommittedCursor + 1L
                                let publicCursor = LibraryTokens.cursor libraryTokenKey repositoryId control.Epoch cursor
                                let now = SystemClock.Instance.GetCurrentInstant()

                                let submit record =
                                    task {
                                        let! receipt = reserveItem repositoryId etag control record
                                        return Ok receipt
                                    }

                                match command with
                                | LibraryChangeCommand.CreateFile (_, _, _, expectation, uploadSessionId) ->
                                    let! parentPath = resolveParentPath repositoryId control.Catalog expectation.Parent

                                    let destinationPath =
                                        parentPath
                                        + "/"
                                        + LibraryDecision.normalizeName expectation.Name

                                    let! exactVacancy = slotExpectationMatches repositoryId expectation

                                    if not exactVacancy then
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                        return Ok receipt
                                    elif not (LibraryDecision.isInLibrary control.Catalog destinationPath) then
                                        let! receipt = reject repositoryId operationId requestHash OutcomeKind.StalePolicy control.Catalog
                                        return Ok receipt
                                    elif control.ItemRecordCount >= 100000
                                         || control.SlotRecordCount >= 100000 then
                                        return Error "The Library current-record capacity has been reached."
                                    else
                                        let! existingSlot = readSlot repositoryId expectation.Parent expectation.Name

                                        match! resolveUpload now repositoryId operationId principalId uploadSessionId correlationId with
                                        | Error reason ->
                                            let! receipt = reject repositoryId operationId requestHash reason control.Catalog
                                            return Ok receipt
                                        | Ok location ->
                                            let itemId = LibraryDecision.deterministicGuid repositoryId operationId "item"

                                            let ns =
                                                {
                                                    Parent = expectation.Parent
                                                    Name = LibraryDecision.normalizeName expectation.Name
                                                    NamespaceVersion = LibraryDecision.deterministicGuid repositoryId operationId "namespace"
                                                }

                                            let item =
                                                {
                                                    ItemId = itemId
                                                    ItemKind = ItemKind.File
                                                    LastChangeCursor = publicCursor
                                                    Namespace = Some ns
                                                    Content = Some location.Content
                                                    ContentRevision = Some publicCursor
                                                    Tombstone = None
                                                }

                                            let change = acceptedChange operationId ChangeKind.CreateFile now principalId control.Catalog.Version item None

                                            return!
                                                acceptedRecord
                                                    cursor
                                                    requestHash
                                                    correlationId
                                                    change
                                                    None
                                                    None
                                                    None
                                                    None
                                                    None
                                                    (Some expectation.ExpectedSlotVersion)
                                                    true
                                                    existingSlot.IsNone
                                                |> submit

                                | LibraryChangeCommand.CreateDirectory (_, _, _, expectation) ->
                                    let! parentPath = resolveParentPath repositoryId control.Catalog expectation.Parent

                                    let destinationPath =
                                        parentPath
                                        + "/"
                                        + LibraryDecision.normalizeName expectation.Name

                                    let! exactVacancy = slotExpectationMatches repositoryId expectation

                                    if not exactVacancy then
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                        return Ok receipt
                                    elif not (LibraryDecision.isInLibrary control.Catalog destinationPath) then
                                        let! receipt = reject repositoryId operationId requestHash OutcomeKind.StalePolicy control.Catalog
                                        return Ok receipt
                                    elif control.ItemRecordCount >= 100000
                                         || control.SlotRecordCount >= 100000 then
                                        return Error "The Library current-record capacity has been reached."
                                    else
                                        let! existingSlot = readSlot repositoryId expectation.Parent expectation.Name
                                        let itemId = LibraryDecision.deterministicGuid repositoryId operationId "item"

                                        let ns =
                                            {
                                                Parent = expectation.Parent
                                                Name = LibraryDecision.normalizeName expectation.Name
                                                NamespaceVersion = LibraryDecision.deterministicGuid repositoryId operationId "namespace"
                                            }

                                        let item =
                                            {
                                                ItemId = itemId
                                                ItemKind = ItemKind.Directory
                                                LastChangeCursor = publicCursor
                                                Namespace = Some ns
                                                Content = None
                                                ContentRevision = None
                                                Tombstone = None
                                            }

                                        let change = acceptedChange operationId ChangeKind.CreateDirectory now principalId control.Catalog.Version item None

                                        return!
                                            acceptedRecord
                                                cursor
                                                requestHash
                                                correlationId
                                                change
                                                None
                                                None
                                                None
                                                None
                                                None
                                                (Some expectation.ExpectedSlotVersion)
                                                true
                                                existingSlot.IsNone
                                            |> submit

                                | LibraryChangeCommand.UpdateContent (_, _, _, itemId, namespacePrecondition, contentPrecondition, uploadSessionId) ->
                                    match! readItem repositoryId itemId with
                                    | None ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemMissing control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when current.Item.Tombstone.IsSome ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemTombstoned control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when current.Item.ItemKind <> ItemKind.File ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.KindMismatch control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when
                                        namespacePrecondition
                                        |> Option.exists (fun expected -> not (namespaceMatches current.Item expected))
                                        ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.NamespaceChanged control.Catalog
                                        return Ok receipt
                                    | Some (current, _) ->
                                        match! resolveUpload now repositoryId operationId principalId uploadSessionId correlationId with
                                        | Error reason ->
                                            let! receipt = reject repositoryId operationId requestHash reason control.Catalog
                                            return Ok receipt
                                        | Ok location when contentMatches current.Item contentPrecondition ->
                                            let item =
                                                { current.Item with
                                                    LastChangeCursor = publicCursor
                                                    Content = Some location.Content
                                                    ContentRevision = Some publicCursor
                                                }

                                            let change = acceptedChange operationId ChangeKind.UpdateContent now principalId control.Catalog.Version item None

                                            return!
                                                acceptedRecord
                                                    cursor
                                                    requestHash
                                                    correlationId
                                                    change
                                                    current.Item.Namespace
                                                    (current.Item.Content
                                                     |> Option.map (fun content -> content.ContentVersionId))
                                                    (namespacePrecondition
                                                     |> Option.map (fun expected -> expected.ExpectedNamespaceVersion))
                                                    (Some contentPrecondition.ExpectedContentVersionId)
                                                    (Some contentPrecondition.ExpectedContentRevision)
                                                    None
                                                    false
                                                    false
                                                |> submit
                                        | Ok _ when control.ItemRecordCount >= 100000 -> return Error "The Library current-item capacity has been reached."
                                        | Ok location ->
                                            let ns = current.Item.Namespace.Value
                                            let! conflictName, destinationSlot = chooseConflictSlot repositoryId ns.Parent ns.Name operationId
                                            let conflictItemId = LibraryDecision.deterministicGuid repositoryId operationId "conflict-item"

                                            let conflictNamespace =
                                                {
                                                    Parent = ns.Parent
                                                    Name = conflictName
                                                    NamespaceVersion = LibraryDecision.deterministicGuid repositoryId operationId "conflict-namespace"
                                                }

                                            let conflict =
                                                {
                                                    OriginalItemId = itemId
                                                    BaseContentVersionId = Some contentPrecondition.ExpectedContentVersionId
                                                    BaseContentRevision = Some contentPrecondition.ExpectedContentRevision
                                                }

                                            let item =
                                                {
                                                    ItemId = conflictItemId
                                                    ItemKind = ItemKind.File
                                                    LastChangeCursor = publicCursor
                                                    Namespace = Some conflictNamespace
                                                    Content = Some location.Content
                                                    ContentRevision = Some publicCursor
                                                    Tombstone = None
                                                }

                                            let! destinationExists = readSlot repositoryId ns.Parent conflictName

                                            if destinationSlot.OccupantItemId.IsSome then
                                                return Error "The selected conflict-copy slot is no longer vacant."
                                            else
                                                let change =
                                                    acceptedChange
                                                        operationId
                                                        ChangeKind.UpdateContent
                                                        now
                                                        principalId
                                                        control.Catalog.Version
                                                        item
                                                        (Some conflict)

                                                return!
                                                    acceptedRecord
                                                        cursor
                                                        requestHash
                                                        correlationId
                                                        change
                                                        None
                                                        None
                                                        (namespacePrecondition
                                                         |> Option.map (fun expected -> expected.ExpectedNamespaceVersion))
                                                        (Some contentPrecondition.ExpectedContentVersionId)
                                                        (Some contentPrecondition.ExpectedContentRevision)
                                                        (Some destinationSlot.SlotVersion)
                                                        true
                                                        destinationExists.IsNone
                                                    |> submit

                                | LibraryChangeCommand.Rename (_, _, _, itemId, namespacePrecondition, newName) ->
                                    match! readItem repositoryId itemId with
                                    | None ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemMissing control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when current.Item.Tombstone.IsSome ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemTombstoned control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when not (namespaceMatches current.Item namespacePrecondition) ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.NamespaceChanged control.Catalog
                                        return Ok receipt
                                    | Some (current, _) ->
                                        let prior = current.Item.Namespace.Value
                                        let normalizedName = LibraryDecision.normalizeName newName
                                        let! destination = getSlot repositoryId prior.Parent normalizedName
                                        let! destinationRecord = readSlot repositoryId prior.Parent normalizedName
                                        let! parentPath = resolveParentPath repositoryId control.Catalog prior.Parent

                                        if destination.OccupantItemId.IsSome then
                                            let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                            return Ok receipt
                                        elif not (LibraryDecision.isInLibrary control.Catalog (parentPath + "/" + normalizedName)) then
                                            let! receipt = reject repositoryId operationId requestHash OutcomeKind.StalePolicy control.Catalog
                                            return Ok receipt
                                        elif destinationRecord.IsNone
                                             && control.SlotRecordCount >= 100000 then
                                            return Error "The Library current-slot capacity has been reached."
                                        else
                                            let ns =
                                                {
                                                    Parent = prior.Parent
                                                    Name = normalizedName
                                                    NamespaceVersion = LibraryDecision.deterministicGuid repositoryId operationId "namespace"
                                                }

                                            let item = { current.Item with LastChangeCursor = publicCursor; Namespace = Some ns }
                                            let change = acceptedChange operationId ChangeKind.Rename now principalId control.Catalog.Version item None

                                            return!
                                                acceptedRecord
                                                    cursor
                                                    requestHash
                                                    correlationId
                                                    change
                                                    (Some prior)
                                                    (current.Item.Content
                                                     |> Option.map (fun content -> content.ContentVersionId))
                                                    (Some namespacePrecondition.ExpectedNamespaceVersion)
                                                    None
                                                    None
                                                    (Some destination.SlotVersion)
                                                    false
                                                    destinationRecord.IsNone
                                                |> submit

                                | LibraryChangeCommand.Move (_, _, _, itemId, namespacePrecondition, destinationParent) ->
                                    match! readItem repositoryId itemId with
                                    | None ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemMissing control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when current.Item.Tombstone.IsSome ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemTombstoned control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when not (namespaceMatches current.Item namespacePrecondition) ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.NamespaceChanged control.Catalog
                                        return Ok receipt
                                    | Some (current, _) ->
                                        let prior = current.Item.Namespace.Value

                                        let! cycle =
                                            if current.Item.ItemKind = ItemKind.Directory then
                                                wouldCreateCycle repositoryId itemId destinationParent
                                            else
                                                Task.FromResult false

                                        let! parentPath = resolveParentPath repositoryId control.Catalog destinationParent
                                        let! destination = getSlot repositoryId destinationParent prior.Name
                                        let! destinationRecord = readSlot repositoryId destinationParent prior.Name

                                        let! descendantPathsValid =
                                            task {
                                                if current.Item.ItemKind = ItemKind.Directory then
                                                    let! items = LibraryQueries.readCurrentItems services repositoryId CancellationToken.None

                                                    return LibraryDecision.movedDescendantPathsAreValid control.Catalog parentPath itemId prior.Name items
                                                else
                                                    return LibraryDecision.isInLibrary control.Catalog (parentPath + "/" + prior.Name)
                                            }

                                        if cycle then
                                            return Error "A Library directory cannot move below itself."
                                        elif destination.OccupantItemId.IsSome then
                                            let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                            return Ok receipt
                                        elif not descendantPathsValid then
                                            let! receipt = reject repositoryId operationId requestHash OutcomeKind.StalePolicy control.Catalog
                                            return Ok receipt
                                        elif destinationRecord.IsNone
                                             && control.SlotRecordCount >= 100000 then
                                            return Error "The Library current-slot capacity has been reached."
                                        else
                                            let ns =
                                                {
                                                    Parent = destinationParent
                                                    Name = prior.Name
                                                    NamespaceVersion = LibraryDecision.deterministicGuid repositoryId operationId "namespace"
                                                }

                                            let item = { current.Item with LastChangeCursor = publicCursor; Namespace = Some ns }
                                            let change = acceptedChange operationId ChangeKind.Move now principalId control.Catalog.Version item None

                                            return!
                                                acceptedRecord
                                                    cursor
                                                    requestHash
                                                    correlationId
                                                    change
                                                    (Some prior)
                                                    (current.Item.Content
                                                     |> Option.map (fun content -> content.ContentVersionId))
                                                    (Some namespacePrecondition.ExpectedNamespaceVersion)
                                                    None
                                                    None
                                                    (Some destination.SlotVersion)
                                                    false
                                                    destinationRecord.IsNone
                                                |> submit

                                | LibraryChangeCommand.Delete (_, _, _, itemId, namespacePrecondition, contentPrecondition) ->
                                    match! readItem repositoryId itemId with
                                    | None ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemMissing control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when current.Item.Tombstone.IsSome ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ItemTombstoned control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when not (namespaceMatches current.Item namespacePrecondition) ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.NamespaceChanged control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when
                                        current.Item.ItemKind = ItemKind.File
                                        && (contentPrecondition.IsNone
                                            || not (contentMatches current.Item contentPrecondition.Value))
                                        ->
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.ContentChanged control.Catalog
                                        return Ok receipt
                                    | Some (current, _) when current.Item.ItemKind = ItemKind.Directory ->
                                        let parent = { Kind = "item"; LibraryPath = None; ItemId = Some itemId }
                                        let! hasChildren = LibraryQueries.hasLiveChildren services repositoryId parent CancellationToken.None

                                        if hasChildren then
                                            let! receipt = reject repositoryId operationId requestHash RejectionReason.DirectoryNotEmpty control.Catalog
                                            return Ok receipt
                                        else
                                            let prior = current.Item.Namespace.Value

                                            let tombstone =
                                                {
                                                    DeletedAt = now
                                                    DeletedBy = principalId
                                                    DeleteCursor = publicCursor
                                                    LastNamespace = prior
                                                    LastContentVersionId = None
                                                }

                                            let item =
                                                { current.Item with
                                                    LastChangeCursor = publicCursor
                                                    Namespace = None
                                                    Content = None
                                                    ContentRevision = None
                                                    Tombstone = Some tombstone
                                                }

                                            let change = acceptedChange operationId ChangeKind.Delete now principalId control.Catalog.Version item None

                                            return!
                                                acceptedRecord
                                                    cursor
                                                    requestHash
                                                    correlationId
                                                    change
                                                    (Some prior)
                                                    None
                                                    (Some namespacePrecondition.ExpectedNamespaceVersion)
                                                    None
                                                    None
                                                    None
                                                    false
                                                    false
                                                |> submit
                                    | Some (current, _) ->
                                        let prior = current.Item.Namespace.Value

                                        let priorContent =
                                            current.Item.Content
                                            |> Option.map (fun content -> content.ContentVersionId)

                                        let tombstone =
                                            {
                                                DeletedAt = now
                                                DeletedBy = principalId
                                                DeleteCursor = publicCursor
                                                LastNamespace = prior
                                                LastContentVersionId = priorContent
                                            }

                                        let item =
                                            { current.Item with
                                                LastChangeCursor = publicCursor
                                                Namespace = None
                                                Content = None
                                                ContentRevision = None
                                                Tombstone = Some tombstone
                                            }

                                        let change = acceptedChange operationId ChangeKind.Delete now principalId control.Catalog.Version item None

                                        return!
                                            acceptedRecord
                                                cursor
                                                requestHash
                                                correlationId
                                                change
                                                (Some prior)
                                                priorContent
                                                (Some namespacePrecondition.ExpectedNamespaceVersion)
                                                (contentPrecondition
                                                 |> Option.map (fun expected -> expected.ExpectedContentVersionId))
                                                (contentPrecondition
                                                 |> Option.map (fun expected -> expected.ExpectedContentRevision))
                                                None
                                                false
                                                false
                                            |> submit
            }

        member this.GetOperation operationId _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! repairPending repositoryId

                match! readReceipt repositoryId operationId with
                | None -> return None
                | Some (receipt, _) ->
                    match receipt.Outcome with
                    | LibraryOperationOutcome.CatalogResult _ -> return None
                    | outcome ->
                        let! publicResult = resolveOutcome repositoryId receipt.OperationId receipt.RequestHash outcome
                        return Some publicResult
            }

        member this.GetItem itemId _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! repairPending repositoryId

                match! readItem repositoryId itemId with
                | Some (record, _) -> return Some record.Item
                | None -> return None
            }

        member this.GetSlot parent name _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! repairPending repositoryId
                return! getSlot repositoryId parent name
            }

        member this.GetAcceptedChange contentRevision _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId

                match LibraryTokens.tryCursor libraryTokenKey repositoryId contentRevision with
                | Some (epoch, cursor) when
                    epoch = control.Epoch
                    && cursor <= control.CommittedCursor
                    ->
                    match! readChange repositoryId cursor with
                    | Some (record, _) -> return Some record.Change
                    | None -> return None
                | _ -> return None
            }

        member this.StartBootstrap pageSize _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId

                if control.CommittedCursor > 0L then
                    match! readChange repositoryId control.CommittedCursor with
                    | Some (record, _) ->
                        do! LibraryQueries.waitForItemCursor services repositoryId record.Change.Item.ItemId control.CommittedCursor CancellationToken.None
                    | None -> invalidOp "The committed Library boundary has no journal record."

                let! bootstrapId, manifest = buildBaseline repositoryId control
                let size = boundedPageSize pageSize

                let total =
                    manifest.Shards
                    |> Array.sumBy (fun shard -> shard.ItemCount)

                let! items = readBaselinePage repositoryId bootstrapId manifest 0 size
                let nextOffset = items.Length

                let expires =
                    SystemClock
                        .Instance
                        .GetCurrentInstant()
                        .Plus(Duration.FromMinutes 15L)
                        .ToUnixTimeSeconds()

                return
                    {
                        BootstrapId = bootstrapId
                        BoundaryCursor = LibraryTokens.cursor libraryTokenKey repositoryId manifest.Epoch manifest.BoundaryCursor
                        CursorEpoch = manifest.Epoch.ToString("D")
                        LibraryCatalog = manifest.Catalog
                        Items = items
                        NextPageToken =
                            if nextOffset < total then
                                Some(LibraryTokens.page libraryTokenKey "baseline" repositoryId (bootstrapId.ToString("D")) nextOffset expires)
                            else
                                None
                    }
            }

        member this.ContinueBootstrap bootstrapId pageToken pageSize _ =
            task {
                let repositoryId = this.GetPrimaryKey()

                let now =
                    SystemClock
                        .Instance
                        .GetCurrentInstant()
                        .ToUnixTimeSeconds()

                match LibraryTokens.tryPage libraryTokenKey "baseline" repositoryId now pageToken with
                | Some (tokenBaseline, offset) when tokenBaseline = bootstrapId.ToString("D") ->
                    match! readBaselineManifest repositoryId bootstrapId with
                    | None -> return None
                    | Some (manifest, _) ->
                        let size = boundedPageSize pageSize

                        let total =
                            manifest.Shards
                            |> Array.sumBy (fun shard -> shard.ItemCount)

                        let! items = readBaselinePage repositoryId bootstrapId manifest offset size
                        let nextOffset = offset + items.Length

                        let expires =
                            SystemClock
                                .Instance
                                .GetCurrentInstant()
                                .Plus(Duration.FromMinutes 15L)
                                .ToUnixTimeSeconds()

                        return
                            Some
                                {
                                    BootstrapId = bootstrapId
                                    BoundaryCursor = LibraryTokens.cursor libraryTokenKey repositoryId manifest.Epoch manifest.BoundaryCursor
                                    CursorEpoch = manifest.Epoch.ToString("D")
                                    LibraryCatalog = manifest.Catalog
                                    Items = items
                                    NextPageToken =
                                        if nextOffset < total then
                                            Some(LibraryTokens.page libraryTokenKey "baseline" repositoryId (bootstrapId.ToString("D")) nextOffset expires)
                                        else
                                            None
                                }
                | _ -> return None
            }

        member this.GetChanges afterCursor pageToken pageSize _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId

                let now =
                    SystemClock
                        .Instance
                        .GetCurrentInstant()
                        .ToUnixTimeSeconds()

                let parsed =
                    match pageToken with
                    | Some token ->
                        match LibraryTokens.tryPage libraryTokenKey "changes" repositoryId now token with
                        | Some (value, offset) ->
                            match value.Split(':', StringSplitOptions.None) with
                            | [| epoch; start; boundary |] ->
                                match Guid.TryParse epoch, Int64.TryParse start, Int64.TryParse boundary with
                                | (true, parsedEpoch), (true, parsedStart), (true, parsedBoundary) ->
                                    Some(parsedEpoch, parsedStart + int64 offset, parsedBoundary)
                                | _ -> None
                            | _ -> None
                        | None -> None
                    | None ->
                        match LibraryTokens.tryCursor libraryTokenKey repositoryId afterCursor with
                        | Some (epoch, position) -> Some(epoch, position, control.CommittedCursor)
                        | None -> None

                match parsed with
                | Some (epoch, position, boundary) when
                    epoch = control.Epoch
                    && position >= control.ReplayFloor - 1L
                    && boundary <= control.CommittedCursor
                    ->
                    let size = boundedPageSize pageSize
                    let! records = LibraryQueries.readChanges services repositoryId position boundary (size + 1) CancellationToken.None
                    let selected, lastPosition, hasMore = LibraryQueries.changePageWindow position boundary size records

                    let changes =
                        selected
                        |> Array.map (fun record -> record.Change)

                    let expires =
                        SystemClock
                            .Instance
                            .GetCurrentInstant()
                            .Plus(Duration.FromMinutes 15L)
                            .ToUnixTimeSeconds()

                    let value = $"{epoch:D}:{position}:{boundary}"
                    let offset = int (lastPosition - position)

                    return
                        {
                            Outcome = OutcomeKind.Accepted
                            CursorEpoch = epoch.ToString("D")
                            Changes = changes
                            LastCursor = LibraryTokens.cursor libraryTokenKey repositoryId epoch lastPosition
                            HasMore = hasMore
                            NextPageToken =
                                if hasMore then
                                    Some(LibraryTokens.page libraryTokenKey "changes" repositoryId value offset expires)
                                else
                                    None
                            Rebaseline = None
                        }
                | _ ->
                    let floor = Math.Max(0L, control.ReplayFloor - 1L)

                    return
                        {
                            Outcome = OutcomeKind.RebaselineRequired
                            CursorEpoch = control.Epoch.ToString("D")
                            Changes = Array.empty
                            LastCursor = LibraryTokens.cursor libraryTokenKey repositoryId control.Epoch control.CommittedCursor
                            HasMore = false
                            NextPageToken = None
                            Rebaseline =
                                Some
                                    {
                                        Reason = "cursorOutsideReplayWindow"
                                        CurrentEpoch = control.Epoch.ToString("D")
                                        ServiceFloorCursor = LibraryTokens.cursor libraryTokenKey repositoryId control.Epoch floor
                                        RecommendedBootstrap = true
                                    }
                        }
            }

        member this.GetContentLocation contentVersionId _ =
            task {
                match! readContent (this.GetPrimaryKey()) contentVersionId with
                | Some (value, _) -> return Some value
                | None -> return None
            }

        member this.Repair _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! repairPending repositoryId
                do! tryDrainHistory repositoryId
                do! tryDrainNotifications repositoryId
            }
            :> Task

        member this.GetStatus _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! repairPending repositoryId
                do! tryDrainHistory repositoryId
                do! tryDrainNotifications repositoryId

                match! readControl repositoryId with
                | None ->
                    return
                        {
                            State = "notInitialized"
                            RepositoryId = repositoryId
                            LibraryCatalogVersion = Guid.Empty
                            IsCaughtUp = true
                            RebaselineRequired = false
                            IsBlocked = false
                            PendingOperationCount = 0
                            OldestPendingAgeMilliseconds = None
                            ProjectionLagCount = 0L
                            LastCompletedAt = None
                        }
                | Some (control, _) ->
                    let lag =
                        control.CommittedCursor
                        - Math.Min(control.HistoryThrough, control.NotifyThrough)

                    let! lastCompleted =
                        task {
                            if control.CommittedCursor = 0L then
                                return None
                            else
                                match! readChange repositoryId control.CommittedCursor with
                                | Some (record, _) -> return Some record.Change.AcceptedAt
                                | None -> return None
                        }

                    return
                        {
                            State = if control.Pending.IsSome then "repairing" else "ready"
                            RepositoryId = repositoryId
                            LibraryCatalogVersion = control.Catalog.Version
                            IsCaughtUp = control.Pending.IsNone && lag = 0L
                            RebaselineRequired = false
                            IsBlocked = false
                            PendingOperationCount = if control.Pending.IsSome then 1 else 0
                            OldestPendingAgeMilliseconds = None
                            ProjectionLagCount = lag
                            LastCompletedAt = lastCompleted
                        }
            }
