namespace Grace.Server

open Grace.Shared.Validation.SynchronizedContent
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.SynchronizedContent
open NodaTime
open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Implements opaque cursor protection and canonical-first synchronized mutation publication.
module SynchronizedContentCoordinator =

    /// Encodes and validates repository-bound cursors with HMAC-SHA256.
    type SynchronizedCursorCodec(secret: byte array) =

        do
            if isNull secret || secret.Length < 32 then
                invalidArg (nameof secret) "The synchronized cursor secret must contain at least 32 bytes."

        /// Encodes bytes with the URL-safe base64 alphabet and no padding.
        let encodeBase64Url (bytes: byte array) =
            Convert
                .ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_')

        /// Decodes one URL-safe base64 value or reports invalid syntax.
        let tryDecodeBase64Url (value: string) =
            try
                let padded =
                    value.Replace('-', '+').Replace('_', '/')
                    + String.replicate ((4 - value.Length % 4) % 4) "="

                Some(Convert.FromBase64String padded)
            with
            | :? FormatException -> None

        interface ISynchronizedCursorCodec with

            member _.Encode(repositoryId, epoch, cursor) =
                let payload = Array.zeroCreate<byte> 40

                repositoryId.TryWriteBytes(payload.AsSpan(0, 16))
                |> ignore

                epoch.TryWriteBytes(payload.AsSpan(16, 16))
                |> ignore

                BitConverter.TryWriteBytes(payload.AsSpan(32, 8), cursor)
                |> ignore

                use hmac = new HMACSHA256(secret)
                let signature = hmac.ComputeHash payload
                encodeBase64Url (Array.append payload signature)

            member _.TryDecode(repositoryId, cursor) =
                match tryDecodeBase64Url cursor with
                | None -> None
                | Some bytes when bytes.Length <> 72 -> None
                | Some bytes ->
                    let payload = bytes[0..39]
                    let suppliedSignature = bytes[40..71]

                    use hmac = new HMACSHA256(secret)
                    let expectedSignature = hmac.ComputeHash payload

                    if not (CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature)) then
                        None
                    else
                        let encodedRepositoryId = Guid(payload.AsSpan(0, 16))

                        if encodedRepositoryId <> repositoryId then
                            None
                        else
                            Some(Guid(payload.AsSpan(16, 16)), BitConverter.ToInt64(payload, 32))

    /// Derives one stable RFC-4122 UUID from exact synchronization identity material.
    let deterministicGuid (repositoryId: RepositoryId) (operationId: SynchronizedOperationId) label =
        let bytes =
            Encoding.UTF8.GetBytes($"{repositoryId:D}:{operationId:D}:{label}")
            |> SHA256.HashData

        let value = bytes[0..15]
        value[6] <- (value[6] &&& 0x0Fuy) ||| 0x50uy
        value[8] <- (value[8] &&& 0x3Fuy) ||| 0x80uy
        Guid value

    /// Derives the stable first vacancy version for a normalized namespace slot.
    let initialSlotVersion (repositoryId: RepositoryId) (normalizedPath: string) =
        let bytes =
            Encoding.UTF8.GetBytes($"{repositoryId:D}:initial-vacant:{normalizedPath.ToUpperInvariant()}")
            |> SHA256.HashData

        let value = bytes[0..15]
        value[6] <- (value[6] &&& 0x0Fuy) ||| 0x50uy
        value[8] <- (value[8] &&& 0x3Fuy) ||| 0x80uy
        Guid value

    /// Allocates the deterministic sibling name used for one competing complete-byte value.
    let conflictName (name: string) operationId =
        let extension = IO.Path.GetExtension name
        let stem = IO.Path.GetFileNameWithoutExtension name
        let operationSuffix = $".conflict-{operationId:N}"
        let suffix = operationSuffix[0..18]

        let maximumStemLength =
            Math.Max(
                1,
                255
                - Encoding.UTF8.GetByteCount(extension + suffix)
            )

        let retainedStem =
            if Encoding.UTF8.GetByteCount stem
               <= maximumStemLength then
                stem
            else
                stem[.. maximumStemLength - 1]

        retainedStem + suffix + extension

    /// Returns the public item state persisted after one canonical mutation.
    let private resultItem (pending: SynchronizedPendingCommandDocument) =
        pending.Receipt.Item
        |> Option.defaultWith (fun () -> invalidOp $"Pending operation {pending.OperationId} has no deterministic item result.")

    /// Builds one occupied slot projection from a live item result.
    let private occupiedSlot repositoryId cursor (item: SynchronizedItemDto) =
        let namespaceValue =
            item.Namespace
            |> Option.defaultWith (fun () -> invalidOp "A live synchronized item must contain namespace state.")

        {
            id = $"slot:{SynchronizedContentPersistence.pathHash namespaceValue.NormalizedPath}"
            RepositoryId = repositoryId
            Scope = "slot"
            SchemaVersion = 1
            Slot =
                {
                    Parent = namespaceValue.Parent
                    Name = namespaceValue.Name
                    NormalizedPath = namespaceValue.NormalizedPath
                    SlotVersion = namespaceValue.SlotVersion
                    State = "occupied"
                    OccupantItemId = Some item.ItemId
                }
            LastCursor = cursor
            AppliedThrough = cursor
        }

    /// Builds one remembered vacant slot projection after a rename, move, or delete.
    let private vacantSlot repositoryId cursor operationId (namespaceValue: SynchronizedNamespaceDto) =
        {
            id = $"slot:{SynchronizedContentPersistence.pathHash namespaceValue.NormalizedPath}"
            RepositoryId = repositoryId
            Scope = "slot"
            SchemaVersion = 1
            Slot =
                {
                    Parent = namespaceValue.Parent
                    Name = namespaceValue.Name
                    NormalizedPath = namespaceValue.NormalizedPath
                    SlotVersion = deterministicGuid repositoryId operationId "vacated-slot"
                    State = "vacant"
                    OccupantItemId = None
                }
            LastCursor = cursor
            AppliedThrough = cursor
        }

    /// Converts one canonical mutation into its deterministic item and path history entry.
    let private historyEntry (pending: SynchronizedPendingCommandDocument) =
        let mutation = pending.CanonicalMutation.Mutation
        let item = resultItem pending

        {
            Cursor = pending.Cursor
            PublicCursor = mutation.Cursor
            OperationId = pending.OperationId
            ItemId = mutation.ItemId
            PriorNamespace = pending.CanonicalMutation.PriorNamespace
            ResultingNamespace = item.Namespace
            PriorContentVersionId = pending.CanonicalMutation.PriorContentVersionId
            ResultingContentVersionId =
                item.Content
                |> Option.map (fun content -> content.ContentVersionId)
            Tombstone = item.Tombstone
            Conflict = mutation.Conflict
            PrincipalId = pending.PrincipalId
            AcceptedAt = mutation.AcceptedAt
        }

    /// Applies every rebuildable projection and receipt for one canonical pending mutation.
    let private applyPending (store: ISynchronizedContentStore) repositoryId (pending: SynchronizedPendingCommandDocument) cancellationToken =
        task {
            let item = resultItem pending
            let mutation = pending.CanonicalMutation.Mutation

            do!
                store.UpsertItemAsync(
                    {
                        id = $"item:{item.ItemId:D}"
                        RepositoryId = repositoryId
                        Scope = "item"
                        SchemaVersion = 1
                        Item = item
                        LastCursor = pending.Cursor
                        AppliedThrough = pending.Cursor
                    },
                    cancellationToken
                )

            match pending.CanonicalMutation.PriorNamespace with
            | Some prior when
                item.Namespace
                |> Option.exists (fun current -> not (pathsEqual prior.NormalizedPath current.NormalizedPath))
                || item.State = "tombstoned"
                ->
                do! store.UpsertSlotAsync(vacantSlot repositoryId pending.Cursor pending.OperationId prior, cancellationToken)
            | _ -> ()

            match item.Namespace with
            | Some _ when item.State = "live" -> do! store.UpsertSlotAsync(occupiedSlot repositoryId pending.Cursor item, cancellationToken)
            | _ -> ()

            let history = historyEntry pending
            do! store.AppendItemHistoryAsync(repositoryId, item.ItemId, history, cancellationToken)

            match pending.CanonicalMutation.PriorNamespace with
            | Some prior -> do! store.AppendPathHistoryAsync(repositoryId, prior.NormalizedPath, history, cancellationToken)
            | None -> ()

            match item.Namespace with
            | Some current when
                pending.CanonicalMutation.PriorNamespace
                |> Option.forall (fun prior -> not (pathsEqual prior.NormalizedPath current.NormalizedPath))
                ->
                do! store.AppendPathHistoryAsync(repositoryId, current.NormalizedPath, history, cancellationToken)
            | _ -> ()

            do!
                store.UpsertReceiptAsync(
                    {
                        id = $"operation:{pending.OperationId:D}"
                        RepositoryId = repositoryId
                        Scope = "receipt"
                        SchemaVersion = 1
                        OperationId = pending.OperationId
                        RequestHash = pending.RequestHash
                        Receipt = pending.Receipt
                        Cursor = Some pending.Cursor
                        AppliedThrough = pending.Cursor
                    },
                    cancellationToken
                )

            return mutation
        }

    /// Repairs the exact pending decision and advances control only after all dependent records are durable.
    let repair (store: ISynchronizedContentStore) repositoryId cancellationToken =
        task {
            let mutable completed = false

            while not completed do
                let! controlRead = store.ReadControlAsync(repositoryId, cancellationToken)

                match controlRead.Document.Pending with
                | None -> completed <- true
                | Some pending ->
                    do! store.CreateCanonicalAsync(pending.CanonicalMutation, cancellationToken)
                    let! _ = applyPending store repositoryId pending cancellationToken

                    let position = pending.Cursor

                    let replacement =
                        { controlRead.Document with
                            AppliedThrough = max controlRead.Document.AppliedThrough position
                            Pending = None
                            ProjectionWatermarks =
                                { controlRead.Document.ProjectionWatermarks with
                                    Current = max controlRead.Document.ProjectionWatermarks.Current position
                                    History = max controlRead.Document.ProjectionWatermarks.History position
                                    Receipts = max controlRead.Document.ProjectionWatermarks.Receipts position
                                }
                            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
                        }

                    match! store.ReplaceControlAsync(replacement, controlRead.ETag, cancellationToken) with
                    | Replaced _ -> completed <- true
                    | PreconditionFailed -> ()
        }

    /// Mirrors the Repository-owned root configuration into the bounded control record when no command is pending.
    let private synchronizeRootConfiguration
        (store: ISynchronizedContentStore)
        repositoryId
        (rootConfiguration: SynchronizedRootConfigurationDto)
        cancellationToken
        =
        task {
            let mutable completed = false

            while not completed do
                let! control = store.ReadControlAsync(repositoryId, cancellationToken)

                if control.Document.RootConfiguration.Version = rootConfiguration.Version then
                    completed <- true
                elif control.Document.Pending.IsSome then
                    do! repair store repositoryId cancellationToken
                else
                    let replacement = { control.Document with RootConfiguration = rootConfiguration; UpdatedAt = SystemClock.Instance.GetCurrentInstant() }

                    match! store.ReplaceControlAsync(replacement, control.ETag, cancellationToken) with
                    | Replaced _ -> completed <- true
                    | PreconditionFailed -> ()
        }

    /// Resolves one root or live directory parent into its current normalized path.
    let private resolveParentPath (store: ISynchronizedContentStore) repositoryId parent cancellationToken =
        task {
            match parent.Kind, parent.RootPath, parent.ItemId with
            | "root", Some rootPath, None ->
                return
                    normalizeRepositoryRelativePath rootPath
                    |> Result.toOption
            | "item", None, Some itemId ->
                match! store.ReadItemAsync(repositoryId, itemId, cancellationToken) with
                | Some document when
                    document.Item.State = "live"
                    && document.Item.ItemKind = ItemKind.Directory
                    ->
                    return
                        document.Item.Namespace
                        |> Option.map (fun namespaceValue -> namespaceValue.NormalizedPath)
                | _ -> return None
            | _ -> return None
        }

    /// Resolves a parent and one portable segment into the normalized repository-relative path.
    let private resolvePath store repositoryId parent name cancellationToken =
        task {
            match normalizeName name with
            | Error _ -> return None
            | Ok normalizedName ->
                match! resolveParentPath store repositoryId parent cancellationToken with
                | None -> return None
                | Some parentPath ->
                    return
                        normalizeRepositoryRelativePath $"{parentPath}/{normalizedName}"
                        |> Result.toOption
        }

    /// Reports whether one normalized namespace path belongs to the exact current synchronized root set.
    let private isOwnedByRoot (rootConfiguration: SynchronizedRootConfigurationDto) normalizedPath =
        rootConfiguration.Roots
        |> Array.exists (fun root ->
            pathsEqual root normalizedPath
            || normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))

    /// Creates one typed non-canonical receipt for a safe rejection or stale root policy.
    let private rejectedReceipt
        (command: SynchronizedMutationCommand)
        (principalId: string)
        (now: Instant)
        (outcome: string)
        (reason: string option)
        (rootConfiguration: SynchronizedRootConfigurationDto)
        (item: SynchronizedItemDto option)
        : SynchronizedOperationReceiptDto
        =
        {
            OperationId = command.OperationId
            RequestHash = command.RequestHash
            Outcome = outcome
            RootConfigurationVersion = rootConfiguration.Version
            RecordedAt = now
            PrincipalId = principalId
            Mutation = None
            Cursor = None
            Item = item
            Conflict = None
            ReasonCode = reason
            CurrentRootConfiguration = if outcome = OutcomeKind.StalePolicy then Some rootConfiguration else None
            Rebaseline = None
        }

    /// Persists one deterministic non-canonical receipt without advancing the accepted mutation stream.
    let private persistRejectedReceipt
        (store: ISynchronizedContentStore)
        (repositoryId: RepositoryId)
        (appliedThrough: int64)
        (receipt: SynchronizedOperationReceiptDto)
        (cancellationToken: CancellationToken)
        =
        store.UpsertReceiptAsync(
            {
                id = $"operation:{receipt.OperationId:D}"
                RepositoryId = repositoryId
                Scope = "receipt"
                SchemaVersion = 1
                OperationId = receipt.OperationId
                RequestHash = receipt.RequestHash
                Receipt = receipt
                Cursor = None
                AppliedThrough = appliedThrough
            },
            cancellationToken
        )

    /// Builds the deterministic accepted decision for one current command and returns a safe rejection otherwise.
    let private decide
        (store: ISynchronizedContentStore)
        (codec: ISynchronizedCursorCodec)
        (control: SynchronizedControlDocument)
        (command: SynchronizedMutationCommand)
        principalId
        correlationId
        cancellationToken
        =
        task {
            let now = SystemClock.Instance.GetCurrentInstant()
            let cursor = control.NextCursor
            let publicCursor = codec.Encode(command.RepositoryId, control.CursorEpoch, cursor)

            if command.RootConfigurationVersion
               <> control.RootConfiguration.Version then
                return Error(rejectedReceipt command principalId now OutcomeKind.StalePolicy None control.RootConfiguration None)
            else
                let! currentItem =
                    match command.ItemId with
                    | Some itemId -> store.ReadItemAsync(command.RepositoryId, itemId, cancellationToken)
                    | None -> Task.FromResult None

                let! destination =
                    task {
                        match command.MutationKind, command.CreationSlotExpectation, command.DestinationParent, command.DestinationName, currentItem with
                        | MutationKind.CreateFile, Some slot, _, _, _
                        | MutationKind.CreateDirectory, Some slot, _, _, _ ->
                            return! resolvePath store command.RepositoryId slot.Parent slot.Name cancellationToken
                        | MutationKind.Rename, _, _, Some name, Some item ->
                            match item.Item.Namespace with
                            | Some current -> return! resolvePath store command.RepositoryId current.Parent name cancellationToken
                            | None -> return None
                        | MutationKind.Move, _, Some parent, _, Some item ->
                            match item.Item.Namespace with
                            | Some current -> return! resolvePath store command.RepositoryId parent current.Name cancellationToken
                            | None -> return None
                        | _ -> return None
                    }

                let! destinationSlot =
                    match destination with
                    | Some path -> store.ReadSlotAsync(command.RepositoryId, path, cancellationToken)
                    | None -> Task.FromResult None

                let destinationExpectationMatches =
                    match command.CreationSlotExpectation, destination with
                    | None, None -> true
                    | Some expectation, Some path ->
                        let actualVersion =
                            destinationSlot
                            |> Option.map (fun slot -> slot.Slot.SlotVersion)
                            |> Option.defaultWith (fun () -> initialSlotVersion command.RepositoryId path)

                        expectation.ExpectedState = "vacant"
                        && expectation.ExpectedSlotVersion = actualVersion
                        && destinationSlot
                           |> Option.forall (fun slot -> slot.Slot.State = "vacant")
                    | _ -> false

                let currentNamespace =
                    currentItem
                    |> Option.bind (fun document -> document.Item.Namespace)

                let currentPathIsOwned =
                    currentNamespace
                    |> Option.forall (fun namespaceValue -> isOwnedByRoot control.RootConfiguration namespaceValue.NormalizedPath)

                let destinationPathIsOwned =
                    destination
                    |> Option.forall (isOwnedByRoot control.RootConfiguration)

                let preparedContentIsCurrent =
                    match command.PreparedContentId with
                    | None -> true
                    | Some _ ->
                        command.PreparedContent.IsSome
                        && command.PreparedContentExpiresAt
                           |> Option.exists (fun expiresAt -> expiresAt > now)

                let itemKindMatches =
                    currentItem
                    |> Option.forall (fun document -> document.Item.ItemKind = command.ItemKind)

                let! directoryNotEmpty =
                    match command.MutationKind, currentItem with
                    | MutationKind.Delete, Some document when document.Item.ItemKind = ItemKind.Directory ->
                        match document.Item.Namespace with
                        | Some namespaceValue -> store.HasLiveDescendantsAsync(command.RepositoryId, namespaceValue.NormalizedPath, cancellationToken)
                        | None -> Task.FromResult false
                    | _ -> Task.FromResult false

                let itemMissingReceipt reason = rejectedReceipt command principalId now OutcomeKind.Rejected (Some reason) control.RootConfiguration None

                if command.ItemId.IsSome && currentItem.IsNone then
                    return Error(itemMissingReceipt RejectionReason.ItemMissing)
                elif currentItem
                     |> Option.exists (fun item -> item.Item.State = "tombstoned") then
                    return Error(itemMissingReceipt RejectionReason.ItemTombstoned)
                elif command.CreationSlotExpectation.IsSome
                     && not destinationExpectationMatches then
                    return Error(itemMissingReceipt RejectionReason.SlotOccupied)
                elif not preparedContentIsCurrent then
                    return Error(itemMissingReceipt RejectionReason.PreparedContentExpired)
                elif not itemKindMatches then
                    return Error(itemMissingReceipt RejectionReason.KindMismatch)
                elif not currentPathIsOwned
                     || not destinationPathIsOwned then
                    return Error(itemMissingReceipt RejectionReason.NamespaceChanged)
                elif directoryNotEmpty then
                    return Error(itemMissingReceipt RejectionReason.DirectoryNotEmpty)
                else
                    let priorItem =
                        currentItem
                        |> Option.map (fun document -> document.Item)

                    let priorNamespace =
                        priorItem
                        |> Option.bind (fun item -> item.Namespace)

                    let priorContent =
                        priorItem
                        |> Option.bind (fun item -> item.Content)

                    let namespaceMatches =
                        match command.NamespacePrecondition, priorNamespace with
                        | None, _ -> true
                        | Some expected, Some current ->
                            expected.ItemId = command.ItemId.Value
                            && expected.ExpectedNamespaceVersion = current.NamespaceVersion
                        | _ -> false

                    let contentMatches =
                        match command.ContentPrecondition, priorContent with
                        | None, _ -> true
                        | Some expected, Some current ->
                            expected.ItemId = command.ItemId.Value
                            && expected.ExpectedContentVersionId = current.ContentVersionId
                        | _ -> false

                    if not namespaceMatches then
                        return Error(itemMissingReceipt RejectionReason.NamespaceChanged)
                    elif not contentMatches
                         && command.MutationKind <> MutationKind.UpdateContent then
                        return Error(itemMissingReceipt RejectionReason.ContentChanged)
                    else
                        let mutable outcome = OutcomeKind.Accepted

                        let mutable itemId =
                            command.ItemId
                            |> Option.defaultWith (fun () -> deterministicGuid command.RepositoryId command.OperationId "item")

                        let mutable itemKind = command.ItemKind
                        let mutable namespaceValue = priorNamespace
                        let mutable contentValue = priorContent
                        let mutable tombstone = None
                        let mutable conflict = None

                        match command.MutationKind with
                        | MutationKind.CreateFile
                        | MutationKind.CreateDirectory ->
                            let expectation = command.CreationSlotExpectation.Value
                            let path = destination.Value

                            namespaceValue <-
                                Some
                                    {
                                        Parent = expectation.Parent
                                        Name = expectation.Name.Normalize(NormalizationForm.FormC)
                                        NormalizedPath = path
                                        NamespaceVersion = deterministicGuid command.RepositoryId command.OperationId "namespace"
                                        SlotVersion = deterministicGuid command.RepositoryId command.OperationId "occupied-slot"
                                    }

                            contentValue <-
                                if command.MutationKind = MutationKind.CreateFile then
                                    command.PreparedContent
                                else
                                    None
                        | MutationKind.UpdateContent when not contentMatches ->
                            let canonical = priorItem.Value
                            let canonicalNamespace = canonical.Namespace.Value
                            let allocatedName = conflictName canonicalNamespace.Name command.OperationId
                            let! allocatedPath = resolvePath store command.RepositoryId canonicalNamespace.Parent allocatedName cancellationToken

                            itemId <- deterministicGuid command.RepositoryId command.OperationId "conflict-item"
                            itemKind <- ItemKind.File
                            outcome <- OutcomeKind.ConflictCopy

                            namespaceValue <-
                                Some
                                    {
                                        Parent = canonicalNamespace.Parent
                                        Name = allocatedName
                                        NormalizedPath = allocatedPath.Value
                                        NamespaceVersion = deterministicGuid command.RepositoryId command.OperationId "conflict-namespace"
                                        SlotVersion = deterministicGuid command.RepositoryId command.OperationId "conflict-slot"
                                    }

                            contentValue <- command.PreparedContent

                            conflict <-
                                Some
                                    {
                                        SourceOperationId = command.OperationId
                                        SourceItemId = canonical.ItemId
                                        CanonicalItemId = canonical.ItemId
                                        ConflictItemId = itemId
                                        ConflictPath = allocatedPath.Value
                                        AcceptedAt = now
                                        SourceContentVersionId =
                                            command.PreparedContent
                                            |> Option.map (fun content -> content.ContentVersionId)
                                        BaseContentVersionId =
                                            command.ContentPrecondition
                                            |> Option.map (fun precondition -> precondition.ExpectedContentVersionId)
                                    }
                        | MutationKind.UpdateContent -> contentValue <- command.PreparedContent
                        | MutationKind.Rename ->
                            let current = priorNamespace.Value

                            namespaceValue <-
                                Some
                                    {
                                        Parent = current.Parent
                                        Name = command.DestinationName.Value.Normalize(NormalizationForm.FormC)
                                        NormalizedPath = destination.Value
                                        NamespaceVersion = deterministicGuid command.RepositoryId command.OperationId "namespace"
                                        SlotVersion = deterministicGuid command.RepositoryId command.OperationId "occupied-slot"
                                    }
                        | MutationKind.Move ->
                            let current = priorNamespace.Value

                            namespaceValue <-
                                Some
                                    {
                                        Parent = command.DestinationParent.Value
                                        Name = current.Name
                                        NormalizedPath = destination.Value
                                        NamespaceVersion = deterministicGuid command.RepositoryId command.OperationId "namespace"
                                        SlotVersion = deterministicGuid command.RepositoryId command.OperationId "occupied-slot"
                                    }
                        | MutationKind.Delete ->
                            let prior = priorItem.Value

                            tombstone <-
                                Some
                                    {
                                        ItemId = prior.ItemId
                                        ItemKind = prior.ItemKind
                                        DeletedAt = now
                                        DeletedBy = principalId
                                        DeleteCursor = publicCursor
                                        LastNamespaceVersion = prior.Namespace.Value.NamespaceVersion
                                        LastContentVersionId =
                                            prior.Content
                                            |> Option.map (fun content -> content.ContentVersionId)
                                    }

                            namespaceValue <- None
                            contentValue <- None
                        | _ -> ()

                        let state = if tombstone.IsSome then "tombstoned" else "live"

                        let finalItem =
                            {
                                ItemId = itemId
                                ItemKind = itemKind
                                State = state
                                LastMutationCursor = publicCursor
                                RootConfigurationVersion = control.RootConfiguration.Version
                                Namespace = namespaceValue
                                Content = contentValue
                                Tombstone = tombstone
                            }

                        let mutation =
                            {
                                Cursor = publicCursor
                                OperationId = command.OperationId
                                MutationKind = command.MutationKind
                                ItemId = itemId
                                ItemKind = itemKind
                                AcceptedAt = now
                                AcceptedBy = principalId
                                RootConfigurationVersion = control.RootConfiguration.Version
                                Namespace = namespaceValue
                                Content = contentValue
                                Tombstone = tombstone
                                Conflict = conflict
                            }

                        let receipt =
                            {
                                OperationId = command.OperationId
                                RequestHash = command.RequestHash
                                Outcome = outcome
                                RootConfigurationVersion = control.RootConfiguration.Version
                                RecordedAt = now
                                PrincipalId = principalId
                                Mutation = Some mutation
                                Cursor = Some publicCursor
                                Item = Some finalItem
                                Conflict = conflict
                                ReasonCode = None
                                CurrentRootConfiguration = None
                                Rebaseline = None
                            }

                        let scope = $"stream:{SynchronizedContentPersistence.segmentKey (SynchronizedContentPersistence.mutationSegment cursor)}"

                        let canonical =
                            {
                                id = $"cursor:{SynchronizedContentPersistence.segmentKey cursor}"
                                RepositoryId = command.RepositoryId
                                Scope = scope
                                SchemaVersion = 1
                                Cursor = cursor
                                PublicCursor = publicCursor
                                OperationId = command.OperationId
                                RequestHash = command.RequestHash
                                Mutation = mutation
                                PriorNamespace = priorNamespace
                                PriorContentVersionId =
                                    priorContent
                                    |> Option.map (fun content -> content.ContentVersionId)
                                ConsumedNamespaceVersion =
                                    command.NamespacePrecondition
                                    |> Option.map (fun precondition -> precondition.ExpectedNamespaceVersion)
                                ConsumedContentVersionId =
                                    command.ContentPrecondition
                                    |> Option.map (fun precondition -> precondition.ExpectedContentVersionId)
                                ConsumedSlotVersion =
                                    command.CreationSlotExpectation
                                    |> Option.map (fun expectation -> expectation.ExpectedSlotVersion)
                                CorrelationId = correlationId
                            }

                        return
                            Ok
                                {
                                    OperationId = command.OperationId
                                    RequestHash = command.RequestHash
                                    Cursor = cursor
                                    Receipt = receipt
                                    CanonicalMutation = canonical
                                    ExpectedRootConfigurationVersion = command.RootConfigurationVersion
                                    PrincipalId = principalId
                                    CorrelationId = correlationId
                                    ReservedAt = now
                                    TargetItemIds = [| itemId |]
                                }
        }

    /// Coordinates one repository's deterministic command lane over direct durable storage.
    type Coordinator(store: ISynchronizedContentStore, codec: ISynchronizedCursorCodec) =

        interface ISynchronizedContentCoordinator with

            member _.RepairAsync(repositoryId, rootConfiguration, cancellationToken) =
                task {
                    let! _ = store.EnsureControlAsync(repositoryId, rootConfiguration, cancellationToken)
                    do! repair store repositoryId cancellationToken
                    do! synchronizeRootConfiguration store repositoryId rootConfiguration cancellationToken
                }

            member _.SubmitAsync(command, rootConfiguration, principalId, correlationId, cancellationToken) =
                task {
                    let! _ = store.EnsureControlAsync(command.RepositoryId, rootConfiguration, cancellationToken)
                    do! repair store command.RepositoryId cancellationToken
                    do! synchronizeRootConfiguration store command.RepositoryId rootConfiguration cancellationToken

                    match! store.ReadReceiptAsync(command.RepositoryId, command.OperationId, cancellationToken) with
                    | Some existing when existing.RequestHash = command.RequestHash -> return existing.Receipt
                    | Some _ ->
                        return
                            rejectedReceipt
                                command
                                principalId
                                (SystemClock.Instance.GetCurrentInstant())
                                OutcomeKind.Rejected
                                (Some RejectionReason.OperationIdentityMismatch)
                                rootConfiguration
                                None
                    | None ->
                        let mutable finished = false
                        let mutable result = Unchecked.defaultof<SynchronizedOperationReceiptDto>

                        while not finished do
                            let! control = store.ReadControlAsync(command.RepositoryId, cancellationToken)

                            if control.Document.Pending.IsSome then
                                do! repair store command.RepositoryId cancellationToken
                            else
                                match! decide store codec control.Document command principalId correlationId cancellationToken with
                                | Error rejection ->
                                    do! persistRejectedReceipt store command.RepositoryId control.Document.AppliedThrough rejection cancellationToken
                                    result <- rejection
                                    finished <- true
                                | Ok pending ->
                                    let replacement =
                                        { control.Document with NextCursor = pending.Cursor + 1L; Pending = Some pending; UpdatedAt = pending.ReservedAt }

                                    match! store.ReplaceControlAsync(replacement, control.ETag, cancellationToken) with
                                    | PreconditionFailed -> ()
                                    | Replaced _ ->
                                        do! store.CreateCanonicalAsync(pending.CanonicalMutation, cancellationToken)
                                        let! _ = applyPending store command.RepositoryId pending cancellationToken
                                        do! repair store command.RepositoryId cancellationToken
                                        result <- pending.Receipt
                                        finished <- true

                        return result
                }

            member _.GetStatusAsync(repositoryId, rootConfiguration, cancellationToken) =
                task {
                    let! _ = store.EnsureControlAsync(repositoryId, rootConfiguration, cancellationToken)
                    do! repair store repositoryId cancellationToken
                    do! synchronizeRootConfiguration store repositoryId rootConfiguration cancellationToken
                    let! control = store.ReadControlAsync(repositoryId, cancellationToken)

                    let lag =
                        max
                            0L
                            (control.Document.NextCursor
                             - 1L
                             - control.Document.AppliedThrough)

                    return
                        {
                            State = if lag = 0L && control.Document.Pending.IsNone then "current" else "blocked"
                            RepositoryId = repositoryId
                            RootConfigurationVersion = rootConfiguration.Version
                            IsCaughtUp = lag = 0L && control.Document.Pending.IsNone
                            RebaselineRequired = false
                            IsBlocked = lag > 0L || control.Document.Pending.IsSome
                            PendingOperationCount = if control.Document.Pending.IsSome then 1 else 0
                            OldestPendingAgeMilliseconds =
                                control.Document.Pending
                                |> Option.map (fun pending ->
                                    int64 (
                                        (SystemClock.Instance.GetCurrentInstant()
                                         - pending.ReservedAt)
                                            .TotalMilliseconds
                                    ))
                            ProjectionLagCount = lag
                            LastCompletedAt =
                                if control.Document.AppliedThrough > 0L then
                                    Some control.Document.UpdatedAt
                                else
                                    None
                        }
                }
