namespace Grace.Actors

open Grace.Actors.Context
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
open Microsoft.Extensions.Logging
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

    let controlType = LibraryRecords.controlType
    let changeType = LibraryRecords.changeType
    let itemType = "Grace.Library.Item.v2"
    let slotType = "Grace.Library.Slot.v2"
    let receiptType = "Grace.Library.Receipt.v2"
    let contentType = "Grace.Library.Content.v2"
    let baselineShardType = "Grace.Library.BaselineShard.v2"
    let baselineManifestType = "Grace.Library.BaselineManifest.v2"

    /// Builds a repository-scoped provider key.
    let key (repositoryId: RepositoryId) tail = LibraryRecords.key (repositoryId.ToString("D") :: tail)

    /// Reads the control record and current provider ETag.
    let readControl repositoryId =
        LibraryRecords.read<LibraryControlDocument> services LibraryRecords.ControlStorageName controlType (LibraryRecords.controlKey repositoryId)

    /// Writes the control record through optimistic concurrency.
    let writeControl repositoryId etag value =
        LibraryRecords.write services LibraryRecords.ControlStorageName controlType (LibraryRecords.controlKey repositoryId) etag value

    /// Reads one accepted journal record by repository cursor.
    let readChange repositoryId cursor =
        LibraryRecords.read<LibraryAcceptedChangeRecord> services LibraryRecords.ChangesStorageName changeType (LibraryRecords.changeKey repositoryId cursor)

    /// Writes one immutable accepted journal record.
    let createChange repositoryId (record: LibraryAcceptedChangeRecord) =
        LibraryRecords.createExact services LibraryRecords.ChangesStorageName changeType (LibraryRecords.changeKey repositoryId record.Cursor) record

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

    /// Returns the stable broker identity for one cursor's wake, so the broker can drop a duplicate sent by repair.
    let stableMessageId (repositoryId: RepositoryId) cursor = $"LibraryContentAvailable/{repositoryId:D}/{cursor:D20}"

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
            let value = { SchemaVersion = 1; Item = item; LastCursor = cursor }

            match! readItem repositoryId item.ItemId with
            | None ->
                let! _ =
                    LibraryRecords.write services LibraryRecords.CurrentStorageName itemType (key repositoryId [ "item"; item.ItemId.ToString("D") ]) null value

                return ()
            | Some (current, _) when current.LastCursor = cursor -> return ()
            | Some (current, _) when current.LastCursor > cursor -> return invalidOp "A newer Library item projection already exists."
            | Some (_, etag) ->
                let! _ =
                    LibraryRecords.write services LibraryRecords.CurrentStorageName itemType (key repositoryId [ "item"; item.ItemId.ToString("D") ]) etag value

                return ()
        }

    /// Writes a slot projection idempotently at one accepted cursor.
    let writeSlot repositoryId cursor (slot: LibraryNamespaceSlotDto) =
        task {
            let slotKey = LibraryDecision.slotKey slot.Parent slot.Name
            let value = { SchemaVersion = 1; Slot = slot; LastCursor = cursor }

            match! readSlot repositoryId slot.Parent slot.Name with
            | None ->
                let! _ = LibraryRecords.write services LibraryRecords.CurrentStorageName slotType (key repositoryId [ "slot"; slotKey ]) null value

                return ()
            | Some (current, _) when current.LastCursor = cursor -> return ()
            | Some (current, _) when current.LastCursor > cursor -> return invalidOp "A newer Library slot projection already exists."
            | Some (_, etag) ->
                let! _ = LibraryRecords.write services LibraryRecords.CurrentStorageName slotType (key repositoryId [ "slot"; slotKey ]) etag value

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
                        }
                | None -> return invalidOp "An accepted Library receipt references a missing journal record."
            | LibraryOperationOutcome.RejectedChange receipt -> return receipt
            | LibraryOperationOutcome.CatalogResult _ -> return invalidOp "A catalog operation cannot be returned as an item-change receipt."
        }

    /// Rewrites a root parent to the catalog's stored spelling of that root, so items and slots placed under a root
    /// always record the same spelling whatever case a caller used. Item parents are returned unchanged.
    let canonicalParent (catalog: LibraryCatalogDto) (parent: LibraryParentDto) =
        match parent.Kind, parent.LibraryPath with
        | "root", Some path ->
            match
                catalog.Libraries
                |> Array.tryFind (LibraryValidation.pathsEqual path)
                with
            | Some stored -> { parent with LibraryPath = Some stored }
            | None -> parent
        | _ -> parent

    /// Resolves a parent to its current complete repository-relative path while rejecting broken or cyclic graphs.
    let resolveParentPath repositoryId (catalog: LibraryCatalogDto) parent =
        let rec loop visited current =
            task {
                match current.Kind, current.LibraryPath, current.ItemId with
                | "root", Some root, None when
                    catalog.Libraries
                    |> Array.exists (LibraryValidation.pathsEqual root)
                    ->
                    // Paths use the catalog's own spelling of the root, which can differ from the caller's in case and
                    // therefore in UTF-8 length.
                    return
                        catalog.Libraries
                        |> Array.find (LibraryValidation.pathsEqual root)
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
    let resolveUpload repositoryId operationId principalId uploadSessionId correlationId =
        task {
            let actor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy uploadSessionId repositoryId correlationId

            let! now, upload, validation =
                LibraryTransfer.readAndValidatePreparedUpload
                    (fun () -> actor.Get correlationId)
                    (fun () -> SystemClock.Instance.GetCurrentInstant())
                    repositoryId
                    operationId
                    principalId

            match validation with
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

    /// Starts one advisory wake for a committed change and returns without waiting for it. A wake that fails or never
    /// finishes is logged and never reaches the caller, because clients poll anyway.
    let sendWake repositoryId (record: LibraryAcceptedChangeRecord) =
        let payload =
            LibraryContentAvailable.Create(
                repositoryId,
                LibraryTokens.cursor libraryTokenKey repositoryId record.Cursor,
                record.Change.LibraryCatalogVersion,
                record.Change.AcceptedAt,
                record.CorrelationId
            )

        LibraryWake.start
            (fun () ->
                if LibraryWake.shouldStall repositoryId then
                    LibraryWake.stalled ()
                else
                    sendLibraryWake
                        (stableMessageId repositoryId record.Cursor)
                        (GraceEvent.LibraryContentAvailableEvent payload)
                        (EventMetadata.New record.CorrelationId "RepositoryLibraryActor")
                        CancellationToken.None)
            (fun ex ->
                // Created only on failure, so building the actor never needs a configured host.
                loggerFactory
                    .CreateLogger("RepositoryLibrary.Actor")
                    .LogWarning(
                        ex,
                        "The Library wake for repository {RepositoryId} at cursor {Cursor} wasn't sent. Clients still find the change when they next poll. CorrelationId: {CorrelationId}.",
                        repositoryId,
                        record.Cursor,
                        record.CorrelationId
                    ))

    /// Completes a saved item decision in the required journal, retention, projection, receipt, acknowledgement, control
    /// order, then sends its wake. Whoever completes the decision sends the wake: the original Submit or a later repair.
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
            sendWake repositoryId record
            return! resolveOutcome repositoryId receipt.OperationId receipt.RequestHash receipt.Outcome
        }

    /// Applies one catalog add or remove through the shared rules in Library.Validation. Deciding a change and finishing
    /// a saved one both use it, so they can't disagree about the resulting catalog.
    let catalogAfter add expectedVersion chosenVersion path acceptedAt principalId catalog =
        if add then
            LibraryValidation.addLibrary expectedVersion chosenVersion path acceptedAt principalId catalog
        else
            LibraryValidation.removeLibrary expectedVersion chosenVersion path acceptedAt principalId catalog

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
            match catalogAfter add expectedVersion chosenVersion path acceptedAt principalId control.Catalog with
            | Error _ -> return invalidOp "The saved Library catalog decision no longer matches control."
            | Ok catalog ->
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
                }

            let receipt =
                { SchemaVersion = 1; OperationId = operationId; RequestHash = requestHash; Outcome = LibraryOperationOutcome.RejectedChange publicResult }

            let! _ = createReceipt repositoryId receipt
            return publicResult
        }

    /// Waits until this silo's Cosmos queries can see the last committed change, so a query's answer covers every
    /// committed item. After a restart, a fresh client can otherwise miss a recent item under session consistency.
    /// Seeing the newest item in the partition means the earlier ones are visible too.
    let ensureCommittedVisible repositoryId (control: LibraryControlDocument) =
        task {
            if control.CommittedCursor > 0L then
                match! readChange repositoryId control.CommittedCursor with
                | Some (record, _) ->
                    do! LibraryQueries.waitForItemCursor services repositoryId record.Change.Item.ItemId control.CommittedCursor CancellationToken.None
                | None -> invalidOp "The committed Library boundary has no journal record."
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

    /// Builds one immutable accepted-change record: the change, the placement it vacated (which repair needs to write
    /// the vacated slot), and whether it added an item or slot record (which the record counts need).
    let acceptedRecord cursor requestHash correlationId change priorNamespace addedItem addedSlot =
        {
            SchemaVersion = 1
            Cursor = cursor
            RequestHash = requestHash
            CorrelationId = correlationId
            Change = change
            PriorNamespace = priorNamespace
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
    let baselineId repositoryId boundary (catalogVersion: LibraryCatalogVersion) =
        LibraryDecision.deterministicGuid repositoryId catalogVersion $"baseline:{boundary}"

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
            let bootstrapId = baselineId repositoryId control.CommittedCursor control.Catalog.Version

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
                    let control = { SchemaVersion = 1; Catalog = catalog; CommittedCursor = 0L; Pending = None; ItemRecordCount = 0; SlotRecordCount = 0 }

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

                                let chosenVersion = LibraryDecision.deterministicGuid repositoryId operationId "catalog"
                                let acceptedAt = SystemClock.Instance.GetCurrentInstant()

                                let ruleRejection =
                                    match catalogAfter add expectedVersion chosenVersion normalized acceptedAt principalId control.Catalog with
                                    | Error reason -> Some reason
                                    | Ok _ -> None

                                if control.Catalog.Version <> expectedVersion then
                                    return! rejectCatalog OutcomeKind.StalePolicy
                                elif add && not outgoingSystemEmpty then
                                    return! rejectCatalog CatalogRejectionReason.OutgoingSystemNotEmpty
                                elif ruleRejection.IsSome then
                                    return! rejectCatalog ruleRejection.Value
                                else
                                    // Removing a root needs no live item under it. A live item's parent is always live, so
                                    // the root's direct children decide it; tombstones don't count. Adding loads nothing.
                                    let! occupied =
                                        if add then
                                            Task.FromResult false
                                        else
                                            task {
                                                do! ensureCommittedVisible repositoryId control

                                                return!
                                                    LibraryQueries.hasLiveChildren
                                                        services
                                                        repositoryId
                                                        (canonicalParent control.Catalog { Kind = "root"; LibraryPath = Some normalized; ItemId = None })
                                                        CancellationToken.None
                                            }

                                    if occupied then
                                        return! rejectCatalog CatalogRejectionReason.SlotOccupied
                                    else
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
                                let publicCursor = LibraryTokens.cursor libraryTokenKey repositoryId cursor
                                let now = SystemClock.Instance.GetCurrentInstant()

                                let submit record =
                                    task {
                                        let! receipt = reserveItem repositoryId etag control record
                                        return Ok receipt
                                    }

                                match command with
                                | LibraryChangeCommand.CreateFile (_, _, _, submitted, uploadSessionId) ->
                                    let expectation = { submitted with Parent = canonicalParent control.Catalog submitted.Parent }
                                    let! parentPath = resolveParentPath repositoryId control.Catalog expectation.Parent

                                    let destinationPath =
                                        parentPath
                                        + "/"
                                        + LibraryDecision.normalizeName expectation.Name

                                    let! exactVacancy = slotExpectationMatches repositoryId expectation
                                    let! existingSlot = readSlot repositoryId expectation.Parent expectation.Name
                                    let placement = LibraryDecision.placementRejection control.Catalog destinationPath

                                    if not exactVacancy then
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                        return Ok receipt
                                    elif placement.IsSome then
                                        let! receipt = reject repositoryId operationId requestHash placement.Value control.Catalog
                                        return Ok receipt
                                    elif LibraryDecision.exceedsRecordCapacity control.ItemRecordCount control.SlotRecordCount true existingSlot.IsNone then
                                        return Error "The Library current-record capacity has been reached."
                                    else
                                        match! resolveUpload repositoryId operationId principalId uploadSessionId correlationId with
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
                                                acceptedRecord cursor requestHash correlationId change None true existingSlot.IsNone
                                                |> submit

                                | LibraryChangeCommand.CreateDirectory (_, _, _, submitted) ->
                                    let expectation = { submitted with Parent = canonicalParent control.Catalog submitted.Parent }
                                    let! parentPath = resolveParentPath repositoryId control.Catalog expectation.Parent

                                    let destinationPath =
                                        parentPath
                                        + "/"
                                        + LibraryDecision.normalizeName expectation.Name

                                    let! exactVacancy = slotExpectationMatches repositoryId expectation
                                    let! existingSlot = readSlot repositoryId expectation.Parent expectation.Name
                                    let placement = LibraryDecision.placementRejection control.Catalog destinationPath

                                    if not exactVacancy then
                                        let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                        return Ok receipt
                                    elif placement.IsSome then
                                        let! receipt = reject repositoryId operationId requestHash placement.Value control.Catalog
                                        return Ok receipt
                                    elif LibraryDecision.exceedsRecordCapacity control.ItemRecordCount control.SlotRecordCount true existingSlot.IsNone then
                                        return Error "The Library current-record capacity has been reached."
                                    else
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
                                            acceptedRecord cursor requestHash correlationId change None true existingSlot.IsNone
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
                                        match! resolveUpload repositoryId operationId principalId uploadSessionId correlationId with
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
                                                acceptedRecord cursor requestHash correlationId change current.Item.Namespace false false
                                                |> submit
                                        | Ok location ->
                                            let ns = current.Item.Namespace.Value
                                            let! conflictName, destinationSlot = chooseConflictSlot repositoryId ns.Parent ns.Name operationId
                                            let! parentPath = resolveParentPath repositoryId control.Catalog ns.Parent
                                            let! destinationExists = readSlot repositoryId ns.Parent conflictName
                                            let placement = LibraryDecision.placementRejection control.Catalog (parentPath + "/" + conflictName)
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

                                            if destinationSlot.OccupantItemId.IsSome then
                                                return Error "The selected conflict-copy slot is no longer vacant."
                                            elif placement.IsSome then
                                                let! receipt = reject repositoryId operationId requestHash placement.Value control.Catalog
                                                return Ok receipt
                                            elif
                                                LibraryDecision.exceedsRecordCapacity
                                                    control.ItemRecordCount
                                                    control.SlotRecordCount
                                                    true
                                                    destinationExists.IsNone then
                                                return Error "The Library current-record capacity has been reached."
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
                                                    acceptedRecord cursor requestHash correlationId change None true destinationExists.IsNone
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

                                        // A renamed directory changes the path of every live item under it.
                                        let! placement =
                                            task {
                                                if current.Item.ItemKind = ItemKind.Directory then
                                                    do! ensureCommittedVisible repositoryId control
                                                    let! items = LibraryQueries.readCurrentItems services repositoryId CancellationToken.None

                                                    return LibraryDecision.subtreeRejection control.Catalog parentPath itemId normalizedName items
                                                else
                                                    return LibraryDecision.placementRejection control.Catalog (parentPath + "/" + normalizedName)
                                            }

                                        if destination.OccupantItemId.IsSome then
                                            let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                            return Ok receipt
                                        elif placement.IsSome then
                                            let! receipt = reject repositoryId operationId requestHash placement.Value control.Catalog
                                            return Ok receipt
                                        elif
                                            LibraryDecision.exceedsRecordCapacity control.ItemRecordCount control.SlotRecordCount false destinationRecord.IsNone then
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
                                                acceptedRecord cursor requestHash correlationId change (Some prior) false destinationRecord.IsNone
                                                |> submit

                                | LibraryChangeCommand.Move (_, _, _, itemId, namespacePrecondition, submittedParent) ->
                                    let destinationParent = canonicalParent control.Catalog submittedParent

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

                                        // A moved directory changes the path of every live item under it.
                                        let! placement =
                                            task {
                                                if current.Item.ItemKind = ItemKind.Directory then
                                                    do! ensureCommittedVisible repositoryId control
                                                    let! items = LibraryQueries.readCurrentItems services repositoryId CancellationToken.None

                                                    return LibraryDecision.subtreeRejection control.Catalog parentPath itemId prior.Name items
                                                else
                                                    return LibraryDecision.placementRejection control.Catalog (parentPath + "/" + prior.Name)
                                            }

                                        if cycle then
                                            return Error "A Library directory cannot move below itself."
                                        elif destination.OccupantItemId.IsSome then
                                            let! receipt = reject repositoryId operationId requestHash RejectionReason.SlotOccupied control.Catalog
                                            return Ok receipt
                                        elif placement.IsSome then
                                            let! receipt = reject repositoryId operationId requestHash placement.Value control.Catalog
                                            return Ok receipt
                                        elif
                                            LibraryDecision.exceedsRecordCapacity control.ItemRecordCount control.SlotRecordCount false destinationRecord.IsNone then
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
                                                acceptedRecord cursor requestHash correlationId change (Some prior) false destinationRecord.IsNone
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
                                        do! ensureCommittedVisible repositoryId control
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
                                                acceptedRecord cursor requestHash correlationId change (Some prior) false false
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
                                            acceptedRecord cursor requestHash correlationId change (Some prior) false false
                                            |> submit
            }

        member this.GetSlot parent name _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId
                return! getSlot repositoryId (canonicalParent control.Catalog parent) name
            }

        member this.GetAcceptedChange contentRevision _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId

                match LibraryTokens.tryCursor libraryTokenKey repositoryId contentRevision with
                | Some cursor when cursor <= control.CommittedCursor ->
                    match! readChange repositoryId cursor with
                    | Some (record, _) -> return Some record.Change
                    | None -> return None
                | _ -> return None
            }

        member this.StartBootstrap pageSize _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId

                do! ensureCommittedVisible repositoryId control

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
                        BoundaryCursor = LibraryTokens.cursor libraryTokenKey repositoryId manifest.BoundaryCursor
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
                                    BoundaryCursor = LibraryTokens.cursor libraryTokenKey repositoryId manifest.BoundaryCursor
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

        member this.GetChanges afterCursor pageSize _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! control, _ = currentControl repositoryId

                // The journal is permanent, so every page starts at the caller's cursor and runs toward the committed
                // cursor. There's no continuation to expire and no window to fall out of.
                match LibraryTokens.tryCursor libraryTokenKey repositoryId afterCursor with
                | None -> return Error LibraryCursorError.NotIssuedHere
                | Some position when position > control.CommittedCursor -> return Error LibraryCursorError.AheadOfServer
                | Some position ->
                    let boundary = control.CommittedCursor
                    let size = boundedPageSize pageSize
                    let! records = LibraryQueries.readChanges services repositoryId position boundary (size + 1) CancellationToken.None
                    let selected, lastPosition, hasMore = LibraryQueries.changePageWindow position boundary size records

                    return
                        Ok
                            {
                                Changes =
                                    selected
                                    |> Array.map (fun record -> record.Change)
                                LastCursor = LibraryTokens.cursor libraryTokenKey repositoryId lastPosition
                                HasMore = hasMore
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
            }
            :> Task
