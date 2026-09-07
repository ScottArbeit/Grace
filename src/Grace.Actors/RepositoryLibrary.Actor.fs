namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open Grace.Types.UploadSession
open NodaTime
open Orleans
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Makes deterministic decisions for the actor-owned Library write lane.
module LibraryDecision =
    /// Derives a stable UUID from repository, operation, and purpose.
    let deterministicGuid repositoryId operationId purpose =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Grace.Library.v1:{repositoryId:D}:{operationId:D}:{purpose}"))
        let value = bytes[0..15]
        value[6] <- (value[6] &&& 0x0Fuy) ||| 0x50uy
        value[8] <- (value[8] &&& 0x3Fuy) ||| 0x80uy
        Guid value

    /// Derives the identity of one immutable complete-byte value.
    let contentVersionId blake3Hash = deterministicGuid Guid.Empty Guid.Empty blake3Hash

    /// Normalizes a portable repository-relative path.
    let normalizePath (path: string) =
        path
            .Replace('\\', '/')
            .Trim('/')
            .Normalize(NormalizationForm.FormC)

    /// Hashes one normalized slot path for its stable record key.
    let pathHash path =
        normalizePath path
        |> fun value -> value.ToUpperInvariant()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Reports whether a path belongs to a configured Library root.
    let isInLibrary (catalog: LibraryCatalogDto) path =
        let path = normalizePath path

        catalog.Libraries
        |> Array.exists (fun root ->
            let root = normalizePath root

            path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))

/// Coordinates permanent manifest contribution through the existing counter and workflow actors.
module LibraryTransfer =
    /// Returns each unique block range in a completed manifest.
    let workflowRanges (manifest: FileManifest) =
        let seen = HashSet<ContentBlockAddress>()

        manifest.Blocks
        |> Seq.choose (fun block ->
            if seen.Add block.Address then
                Some { StoragePoolId = manifest.StoragePoolId; ContentBlockAddress = block.Address }
            else
                None)
        |> Seq.toArray

    /// Builds the tracked counter identity shared by retry and acknowledgement.
    let counterOperationId operationId contentVersionId = $"library:{operationId:N}:content:{contentVersionId:N}"

/// Owns one repository's bounded Library control state and serialized change lane.
type RepositoryLibraryActor
    (
        services: IServiceProvider,
        grainFactory: IGrainFactory,
        authorize: Func<RepositoryId, LibraryWriteAuthorization, CancellationToken, Task<PermissionCheckResult>>,
        libraryTokenKey: byte array
    ) =
    inherit Grain()

    let controlType = "Grace.Library.Control.v1"
    let changeType = "Grace.Library.Change.v1"
    let itemType = "Grace.Library.Item.v1"
    let slotType = "Grace.Library.Slot.v1"
    let receiptType = "Grace.Library.Receipt.v1"
    let contentType = "Grace.Library.Content.v1"

    /// Builds the repository control key.
    let controlKey (repositoryId: RepositoryId) = LibraryRecords.key [ repositoryId.ToString("D") ]

    /// Reads the repository control and its ETag.
    let readControl repositoryId = LibraryRecords.read<LibraryControlDocument> services LibraryRecords.ControlStorageName controlType (controlKey repositoryId)

    /// Writes the repository control with optimistic concurrency.
    let writeControl repositoryId etag value = LibraryRecords.write services LibraryRecords.ControlStorageName controlType (controlKey repositoryId) etag value

    /// Reads one operation result.
    let readReceipt (repositoryId: RepositoryId) (operationId: LibraryOperationId) =
        LibraryRecords.read<LibraryReceiptDocument>
            services
            LibraryRecords.ReceiptsStorageName
            receiptType
            (LibraryRecords.key [ repositoryId.ToString("D")
                                  "receipt"
                                  operationId.ToString("D") ])

    /// Reads one current item.
    let readItem (repositoryId: RepositoryId) (itemId: LibraryItemId) =
        LibraryRecords.read<LibraryCurrentItemDocument>
            services
            LibraryRecords.CurrentStorageName
            itemType
            (LibraryRecords.key [ repositoryId.ToString("D")
                                  "item"
                                  itemId.ToString("D") ])

    /// Reads one current slot.
    let readSlot (repositoryId: RepositoryId) path =
        LibraryRecords.read<LibraryCurrentSlotDocument>
            services
            LibraryRecords.CurrentStorageName
            slotType
            (LibraryRecords.key [ repositoryId.ToString("D")
                                  "slot"
                                  LibraryDecision.pathHash path ])

    /// Reads one retained content location.
    let readContent (repositoryId: RepositoryId) (contentVersionId: LibraryContentVersionId) =
        LibraryRecords.read<LibraryContentLocationDocument>
            services
            LibraryRecords.CurrentStorageName
            contentType
            (LibraryRecords.key [ repositoryId.ToString("D")
                                  "content"
                                  contentVersionId.ToString("D") ])

    /// Runs the permanent manifest contribution before publishing the receipt.
    let activateManifest repositoryId operationId correlationId (location: LibraryContentLocationDocument) =
        task {
            let manifest = location.Manifest
            let operation = LibraryTransfer.counterOperationId operationId location.Content.ContentVersionId
            let metadata = EventMetadata.New correlationId "RepositoryLibraryActor"

            let counter =
                grainFactory.GetGrain<IRepositoryContentCounterActor>(
                    RepositoryContentCounter.primaryKey repositoryId manifest.StoragePoolId manifest.ManifestAddress
                )

            match! counter.AddTrackedReference operation repositoryId manifest.StoragePoolId manifest.ManifestAddress metadata with
            | Error error -> return invalidOp $"Library manifest counter failed: {error.Error}"
            | Ok result ->
                match result.ReturnValue.Intents
                      |> List.tryPick (function
                          | IncrementManifestReferenceCount (_, pool, address, revision) -> Some(pool, address, revision)
                          | _ -> None)
                    with
                | None -> return ()
                | Some (pool, address, revision) ->
                    let ranges = LibraryTransfer.workflowRanges manifest

                    if Array.isEmpty ranges then
                        invalidOp "A retained Library manifest must contain at least one block."

                    let workflow = grainFactory.GetGrain<IManifestContributionWorkflowActor>(ManifestContributionWorkflow.primaryKey repositoryId pool address)

                    match! workflow.Start $"{operation}:fanout" repositoryId pool address ManifestContributionDirection.Increment ranges revision metadata with
                    | Error error -> return invalidOp $"Library manifest workflow failed: {error.Error}"
                    | Ok _ -> return ()
        }

    /// Releases the tracked counter identity after the durable receipt exists.
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

    /// Completes accepted change, tracked add, projections, receipt, acknowledgement, and control commit in order.
    let completePending (repositoryId: RepositoryId) etag (control: LibraryControlDocument) (pending: LibraryPendingCommandDocument) =
        task {
            let item =
                pending.Receipt.Item
                |> Option.defaultWith (fun () -> invalidOp "An accepted Library receipt has no resulting item.")

            let! _ =
                LibraryRecords.createExact
                    services
                    LibraryRecords.ChangesStorageName
                    changeType
                    (LibraryRecords.key [ repositoryId.ToString("D")
                                          "change"
                                          pending.Cursor.ToString("D20") ])
                    pending.CanonicalChange

            let! location =
                task {
                    match item.Content with
                    | None -> return None
                    | Some content ->
                        match! readContent repositoryId content.ContentVersionId with
                        | Some (value, _) -> return Some value
                        | None -> return invalidOp "Accepted Library content has no retained manifest."
                }

            match location with
            | Some value -> do! activateManifest repositoryId pending.OperationId pending.CorrelationId value
            | None -> ()

            let itemRecord =
                {
                    id = $"item:{item.ItemId:D}"
                    RepositoryId = repositoryId
                    ProjectionKind = "item"
                    SchemaVersion = 1
                    Item = item
                    LastCursor = pending.Cursor
                    AppliedThrough = pending.Cursor
                }

            let! _ =
                LibraryRecords.createExact
                    services
                    LibraryRecords.CurrentStorageName
                    itemType
                    (LibraryRecords.key [ repositoryId.ToString("D")
                                          "item"
                                          item.ItemId.ToString("D") ])
                    itemRecord

            match item.Namespace with
            | Some ns ->
                let slot =
                    {
                        Parent = ns.Parent
                        Name = ns.Name
                        NormalizedPath = ns.NormalizedPath
                        SlotVersion = ns.SlotVersion
                        State = "occupied"
                        OccupantItemId = Some item.ItemId
                    }

                let slotRecord =
                    {
                        id = $"slot:{LibraryDecision.pathHash ns.NormalizedPath}"
                        RepositoryId = repositoryId
                        ProjectionKind = "slot"
                        SchemaVersion = 1
                        Slot = slot
                        LastCursor = pending.Cursor
                        AppliedThrough = pending.Cursor
                    }

                let! _ =
                    LibraryRecords.createExact
                        services
                        LibraryRecords.CurrentStorageName
                        slotType
                        (LibraryRecords.key [ repositoryId.ToString("D")
                                              "slot"
                                              LibraryDecision.pathHash ns.NormalizedPath ])
                        slotRecord

                ()
            | None -> ()

            let receiptRecord =
                {
                    id = $"operation:{pending.OperationId:D}"
                    RepositoryId = repositoryId
                    RecordKind = "receipt"
                    RecordKey = $"operation:{pending.OperationId:D}"
                    SchemaVersion = 1
                    OperationId = pending.OperationId
                    RequestHash = pending.RequestHash
                    Receipt = pending.Receipt
                    Cursor = Some pending.Cursor
                    AppliedThrough = pending.Cursor
                }

            let! _ =
                LibraryRecords.createExact
                    services
                    LibraryRecords.ReceiptsStorageName
                    receiptType
                    (LibraryRecords.key [ repositoryId.ToString("D")
                                          "receipt"
                                          pending.OperationId.ToString("D") ])
                    receiptRecord

            match location with
            | Some value -> do! acknowledgeManifest repositoryId pending.OperationId pending.CorrelationId value
            | None -> ()

            let committed =
                { control with
                    NextCursor = pending.Cursor + 1L
                    AppliedThrough = pending.Cursor
                    Pending = None
                    ProjectionWatermarks = { control.ProjectionWatermarks with Current = pending.Cursor; Receipts = pending.Cursor }
                    UpdatedAt = SystemClock.Instance.GetCurrentInstant()
                }

            let! _ = writeControl repositoryId etag committed
            return pending.Receipt
        }

    /// Repairs pending work before any receipt is treated as terminal.
    let repair repositoryId =
        task {
            match! readControl repositoryId with
            | Some (control, etag) ->
                match control.Pending with
                | Some pending ->
                    let! receipt = completePending repositoryId etag control pending
                    return Some receipt
                | None -> return None
            | None -> return None
        }

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

                match! readControl repositoryId with
                | Some _ -> return ()
                | None ->
                    let control =
                        {
                            id = $"control:{repositoryId:D}"
                            RepositoryId = repositoryId
                            SchemaVersion = 1
                            CursorEpoch = LibraryDecision.deterministicGuid repositoryId Guid.Empty "epoch"
                            NextCursor = 1L
                            AppliedThrough = 0L
                            ReplayFloor = 1L
                            LibraryCatalog = catalog
                            Pending = None
                            CurrentBaselineId = None
                            CurrentBaselineCursor = None
                            ProjectionWatermarks = LibraryProjectionWatermarks.Empty
                            UpdatedAt = catalog.CreatedAt
                        }

                    let! _ = writeControl repositoryId null control
                    return ()
            }

        member this.GetCatalog _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

                match! readControl repositoryId with
                | Some (control, _) -> return control.LibraryCatalog
                | None -> return invalidOp "Library catalog is not initialized."
            }

        member this.SetCatalog _ result _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

                match! readControl repositoryId with
                | None -> return invalidOp "Library catalog is not initialized."
                | Some (control, etag) when result.LibraryCatalog.PreviousVersion = Some control.LibraryCatalog.Version ->
                    let! _ = writeControl repositoryId etag { control with LibraryCatalog = result.LibraryCatalog; UpdatedAt = result.RecordedAt }
                    return result
                | Some (control, _) ->
                    return { result with Outcome = OutcomeKind.StalePolicy; LibraryCatalog = control.LibraryCatalog; ReasonCode = Some OutcomeKind.StalePolicy }
            }

        member this.IsInLibrary path _ =
            task {
                let! catalog = (this :> IRepositoryLibraryActor).GetCatalog ""
                return LibraryDecision.isInLibrary catalog path
            }

        member this.Submit command principalId authorization correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

                match! readReceipt repositoryId command.OperationId with
                | Some (existing, _) when existing.RequestHash = command.RequestHash -> return LibrarySubmitResult.Submitted existing.Receipt
                | Some _ -> return invalidOp "The Library operation identity is already bound to another request."
                | None ->
                    match! authorize.Invoke(repositoryId, authorization, CancellationToken.None) with
                    | Denied reason -> return LibrarySubmitResult.Forbidden reason
                    | Allowed _ ->
                        match! readControl repositoryId with
                        | None -> return invalidOp "Library catalog is not initialized."
                        | Some (control, etag) ->
                            if command.ChangeKind <> ChangeKind.CreateFile then
                                invalidOp "This checkpoint accepts create-file changes."

                            let expectation =
                                command.CreationSlotExpectation
                                |> Option.defaultWith (fun () -> invalidOp "Create-file requires a slot expectation.")

                            let path = LibraryDecision.normalizePath $"{expectation.Parent.LibraryPath.Value}/{expectation.Name}"

                            if
                                command.LibraryCatalogVersion
                                <> control.LibraryCatalog.Version
                                || not (LibraryDecision.isInLibrary control.LibraryCatalog path)
                            then
                                invalidOp "The current Library catalog does not accept this destination."

                            match! readSlot repositoryId path with
                            | Some _ -> return invalidOp "The destination Library slot is occupied."
                            | None ->
                                let sessionId =
                                    command.PreparedContentId
                                    |> Option.defaultWith (fun () -> invalidOp "Create-file requires an upload session.")

                                let uploadActor = Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy sessionId repositoryId correlationId

                                let! upload = uploadActor.Get correlationId

                                let binding =
                                    upload.LibraryPreparation
                                    |> Option.defaultWith (fun () -> invalidOp "The upload session has no Library binding.")

                                let manifest =
                                    upload.FinalizedManifest
                                    |> Option.defaultWith (fun () -> invalidOp "The upload session has no completed manifest.")

                                let completedUpload =
                                    match upload.LifecycleState with
                                    | UploadSessionLifecycleState.Finalized
                                    | UploadSessionLifecycleState.RetentionPending -> true
                                    | _ -> false

                                if not completedUpload
                                   || upload.RepositoryId <> repositoryId
                                   || binding.OperationId <> command.OperationId
                                   || binding.PrincipalId <> principalId
                                   || binding.ExpiresAt
                                      <= SystemClock.Instance.GetCurrentInstant() then
                                    invalidOp "The completed upload does not match this Library operation."

                                let contentId = LibraryDecision.contentVersionId upload.FileContentHash

                                let! location =
                                    task {
                                        match! readContent repositoryId contentId with
                                        | Some (value, _) -> return value
                                        | None ->
                                            let content =
                                                {
                                                    ContentVersionId = contentId
                                                    Blake3Hash = upload.FileContentHash
                                                    Sha256Hash = binding.ExpectedSha256
                                                    Size = upload.ExpectedSize
                                                    CreatedAt = SystemClock.Instance.GetCurrentInstant()
                                                }

                                            let value =
                                                {
                                                    id = $"content:{contentId:D}"
                                                    RepositoryId = repositoryId
                                                    RecordKind = "content"
                                                    RecordKey = $"content:{contentId:D}"
                                                    SchemaVersion = 1
                                                    Content = content
                                                    AuthorizedScope = upload.AuthorizedScope
                                                    Manifest = manifest
                                                }

                                            return!
                                                LibraryRecords.createExact
                                                    services
                                                    LibraryRecords.CurrentStorageName
                                                    contentType
                                                    (LibraryRecords.key [ repositoryId.ToString("D")
                                                                          "content"
                                                                          contentId.ToString("D") ])
                                                    value
                                    }

                                let cursor = control.NextCursor
                                let publicCursor = LibraryTokens.cursor libraryTokenKey repositoryId control.CursorEpoch cursor
                                let itemId = LibraryDecision.deterministicGuid repositoryId command.OperationId "item"
                                let now = SystemClock.Instance.GetCurrentInstant()

                                let ns =
                                    {
                                        Parent = expectation.Parent
                                        Name = expectation.Name.Normalize(NormalizationForm.FormC)
                                        NormalizedPath = path
                                        NamespaceVersion = LibraryDecision.deterministicGuid repositoryId command.OperationId "namespace"
                                        SlotVersion = LibraryDecision.deterministicGuid repositoryId command.OperationId "slot"
                                    }

                                let item =
                                    {
                                        ItemId = itemId
                                        ItemKind = ItemKind.File
                                        State = "live"
                                        LastChangeCursor = publicCursor
                                        LibraryCatalogVersion = control.LibraryCatalog.Version
                                        Namespace = Some ns
                                        Content = Some location.Content
                                        Tombstone = None
                                    }

                                let change =
                                    {
                                        Cursor = publicCursor
                                        OperationId = command.OperationId
                                        ChangeKind = command.ChangeKind
                                        ItemId = itemId
                                        ItemKind = ItemKind.File
                                        AcceptedAt = now
                                        AcceptedBy = principalId
                                        LibraryCatalogVersion = control.LibraryCatalog.Version
                                        Namespace = Some ns
                                        Content = Some location.Content
                                        Tombstone = None
                                        Conflict = None
                                    }

                                let receipt =
                                    {
                                        OperationId = command.OperationId
                                        RequestHash = command.RequestHash
                                        Outcome = OutcomeKind.Accepted
                                        LibraryCatalogVersion = control.LibraryCatalog.Version
                                        RecordedAt = now
                                        PrincipalId = principalId
                                        Change = Some change
                                        Cursor = Some publicCursor
                                        Item = Some item
                                        Conflict = None
                                        ReasonCode = None
                                        CurrentLibraryCatalog = None
                                        Rebaseline = None
                                    }

                                let canonical =
                                    {
                                        id = $"cursor:{cursor:D20}"
                                        RepositoryId = repositoryId
                                        StreamSegment = "00000000000000000000"
                                        SchemaVersion = 1
                                        Cursor = cursor
                                        PublicCursor = publicCursor
                                        OperationId = command.OperationId
                                        RequestHash = command.RequestHash
                                        Change = change
                                        PriorNamespace = None
                                        PriorContentVersionId = None
                                        ConsumedNamespaceVersion = None
                                        ConsumedContentVersionId = None
                                        ConsumedSlotVersion = Some expectation.ExpectedSlotVersion
                                        CorrelationId = correlationId
                                    }

                                let pending =
                                    {
                                        OperationId = command.OperationId
                                        RequestHash = command.RequestHash
                                        Cursor = cursor
                                        Receipt = receipt
                                        CanonicalChange = canonical
                                        ExpectedLibraryCatalogVersion = command.LibraryCatalogVersion
                                        PrincipalId = principalId
                                        CorrelationId = correlationId
                                        ReservedAt = now
                                        TargetItemIds = [| itemId |]
                                    }

                                let pendingControl = { control with Pending = Some pending; UpdatedAt = now }
                                let! pendingEtag = writeControl repositoryId etag pendingControl
                                let! result = completePending repositoryId pendingEtag pendingControl pending
                                return LibrarySubmitResult.Submitted result
            }

        member this.GetOperation operationId _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

                match! readReceipt repositoryId operationId with
                | Some (value, _) -> return Some value.Receipt
                | None -> return None
            }

        member this.GetItem itemId _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

                match! readItem repositoryId itemId with
                | Some (value, _) -> return Some value.Item
                | None -> return None
            }

        member this.GetSlot path _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

                match! readSlot repositoryId path with
                | Some (value, _) -> return Some value.Slot
                | None -> return None
            }

        member this.GetContentLocation contentVersionId _ =
            task {
                match! readContent (this.GetPrimaryKey()) contentVersionId with
                | Some (value, _) -> return Some value
                | None -> return None
            }

        member this.Repair _ =
            task {
                let! _ = repair (this.GetPrimaryKey())
                return ()
            }
            :> Task

        member this.GetStatus _ =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! _ = repair repositoryId

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
                    return
                        {
                            State = if control.Pending.IsSome then "repairing" else "ready"
                            RepositoryId = repositoryId
                            LibraryCatalogVersion = control.LibraryCatalog.Version
                            IsCaughtUp = control.Pending.IsNone
                            RebaselineRequired = false
                            IsBlocked = false
                            PendingOperationCount = if control.Pending.IsSome then 1 else 0
                            OldestPendingAgeMilliseconds = None
                            ProjectionLagCount = control.NextCursor - control.AppliedThrough - 1L
                            LastCompletedAt = if control.AppliedThrough > 0L then Some control.UpdatedAt else None
                        }
            }
