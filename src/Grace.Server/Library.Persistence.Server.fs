namespace Grace.Server

open Grace.Shared
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Options
open NodaTime
open Orleans.Configuration
open Orleans.Persistence.Cosmos
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Implements the six purpose-specific direct-Cosmos stores for remote Libraries.
module LibraryPersistence =

    [<Literal>]
    let ControlStorageName = "library-control"

    [<Literal>]
    let ChangesStorageName = "library-changes"

    [<Literal>]
    let CurrentStorageName = "library-current"

    [<Literal>]
    let ReceiptsStorageName = "library-receipts"

    [<Literal>]
    let HistoryStorageName = "library-history"

    [<Literal>]
    let BaselinesStorageName = "library-baselines"

    [<Literal>]
    let ControlContainerName = "grace-library-control"

    [<Literal>]
    let ChangesContainerName = "grace-library-changes"

    [<Literal>]
    let CurrentContainerName = "grace-library-current"

    [<Literal>]
    let ReceiptsContainerName = "grace-library-receipts"

    [<Literal>]
    let HistoryContainerName = "grace-library-history"

    [<Literal>]
    let BaselinesContainerName = "grace-library-baselines"

    [<Literal>]
    let ChangeSegmentSize = 200L

    [<Literal>]
    let HistorySegmentEntryLimit = 512

    [<Literal>]
    let HistorySegmentByteLimit = 921600

    [<Literal>]
    let BaselineShardByteLimit = 1000000

    [<Literal>]
    let CurrentProjectionDocumentLimit = 100000

    /// Derives an Orleans Cosmos document identity and its ordered partition-key prefix from one bounded Library actor key.
    type GraceDocumentIdProvider(options: IOptions<ClusterOptions>, partitionKeyLevelCount: int) =
        let defaultProvider = DefaultDocumentIdProvider(options)

        do
            if partitionKeyLevelCount < 1
               || partitionKeyLevelCount > 3 then
                invalidArg (nameof partitionKeyLevelCount) "Library Cosmos document keys support one to three partition-key values."

        /// Reads the stable actor-key components that define the Library container partition.
        member private _.GetPartitionKeyValues(grainType: string, grainId: GrainId) =
            let values =
                grainId
                    .Key
                    .ToString()
                    .Split('|', StringSplitOptions.None)

            if values.Length < partitionKeyLevelCount
               || values
                  |> Array.take partitionKeyLevelCount
                  |> Array.exists String.IsNullOrWhiteSpace then
                invalidArg
                    (nameof grainId)
                    $"Library grain type '{grainType}' requires {partitionKeyLevelCount} non-empty ordered key component(s), but '{grainId.Key}' does not provide them."

            values |> Array.take partitionKeyLevelCount

        interface IDocumentIdProvider with
            /// Returns the legacy document and first partition identifiers for compatibility with one-key Orleans operations.
            member this.GetDocumentIdentifiers(grainType: string, grainId: GrainId) =
                let partitionKeyValues = this.GetPartitionKeyValues(grainType, grainId)
                ValueTask<struct (string * string)>(struct (defaultProvider.GetId(grainType, grainId), partitionKeyValues[0]))

            /// Returns the complete document identity required for one-, two-, or three-level Cosmos point operations.
            member this.GetDocumentKey(grainType: string, grainId: GrainId) =
                let partitionKeyValues = this.GetPartitionKeyValues(grainType, grainId)

                ValueTask<CosmosDocumentKey>(CosmosDocumentKey(defaultProvider.GetId(grainType, grainId), partitionKeyValues :> IReadOnlyList<string>))

    /// Maps repository control actors to their one-component repository partition.
    type LibraryControlDocumentIdProvider(options: IOptions<ClusterOptions>) =
        inherit GraceDocumentIdProvider(options, 1)

    /// Maps accepted-change and current-record actors to repository plus record-kind partitions.
    type LibraryTwoLevelDocumentIdProvider(options: IOptions<ClusterOptions>) =
        inherit GraceDocumentIdProvider(options, 2)

    /// Maps receipt, history, and baseline actors to repository, record-kind, and bounded-record partitions.
    type LibraryThreeLevelDocumentIdProvider(options: IOptions<ClusterOptions>) =
        inherit GraceDocumentIdProvider(options, 3)

    /// Formats one non-negative internal segment value for stable Cosmos keys.
    let segmentKey (value: int64) = value.ToString("D20", Globalization.CultureInfo.InvariantCulture)

    /// Returns the exact stream segment containing one positive internal cursor.
    let changeSegment (cursor: int64) = (cursor - 1L) / ChangeSegmentSize

    /// Computes the private lowercase SHA-256 key for one normalized portable path.
    let pathHash (normalizedPath: string) =
        normalizedPath
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Creates the complete control-container partition key.
    let controlPartitionKey (repositoryId: RepositoryId) = PartitionKey(repositoryId.ToString("D"))

    /// Creates one exact two-component hierarchical partition key.
    let partitionKey2 (repositoryId: RepositoryId) (keyComponent: string) =
        PartitionKeyBuilder()
            .Add(repositoryId.ToString("D"))
            .Add(keyComponent)
            .Build()

    /// Creates one exact three-component hierarchical partition key.
    let partitionKey3 (repositoryId: RepositoryId) (middle: string) (leaf: string) =
        PartitionKeyBuilder()
            .Add(repositoryId.ToString("D"))
            .Add(middle)
            .Add(leaf)
            .Build()

    /// Serializes an internal document with the same stable options used by Grace HTTP and Cosmos clients.
    let private serialize value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Confirms an equal-position retry carries byte-equivalent deterministic state.
    let private equivalent left right = String.Equals(serialize left, serialize right, StringComparison.Ordinal)

    /// Derives one stable baseline identity from its immutable repository boundary.
    let private baselineId repositoryId boundaryCursor cursorEpoch libraryCatalogVersion =
        let seed = Encoding.UTF8.GetBytes($"{repositoryId:D}:{boundaryCursor}:{cursorEpoch:D}:{libraryCatalogVersion:D}")
        let hash = SHA256.HashData seed
        let bytes = hash[0..15]
        bytes[6] <- (bytes[6] &&& 0x0Fuy) ||| 0x50uy
        bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid bytes

    /// Computes the lowercase SHA-256 digest of one stable internal JSON document.
    let internal documentHash value =
        value
        |> serialize
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    /// Builds deterministic bounded baseline shards and their manifest without publishing either representation.
    let internal buildBaselineDocuments
        (repositoryId: RepositoryId)
        (boundaryCursor: int64)
        (cursorEpoch: Guid)
        (libraryCatalog: LibraryCatalogDto)
        (items: LibraryItemDto array)
        (createdAt: Instant)
        =
        let baselineId = baselineId repositoryId boundaryCursor cursorEpoch libraryCatalog.Version
        let baselineKey = baselineId.ToString("D")

        let orderedItems =
            items
            |> Array.sortBy (fun item ->
                item.Namespace
                |> Option.map (fun namespaceValue -> namespaceValue.NormalizedPath.ToUpperInvariant())
                |> Option.defaultValue "",
                item.ItemId)

        let shardDocuments = ResizeArray<LibraryBaselineShardDocument>()
        let mutable nextItem = 0
        let mutable shardIndex = 0

        while nextItem < orderedItems.Length do
            let shardItems = ResizeArray<LibraryItemDto>()
            let mutable fits = true

            while nextItem < orderedItems.Length && fits do
                let candidateItems = Array.append (shardItems.ToArray()) [| orderedItems[nextItem] |]
                let shardKey = $"shard:{shardIndex:D8}"
                let shardId = $"shard:{baselineId:D}:{shardIndex:D8}"

                let candidate =
                    {
                        id = shardId
                        RepositoryId = repositoryId
                        SchemaVersion = 1
                        BaselineId = baselineId
                        ShardKey = shardKey
                        BoundaryCursor = boundaryCursor
                        Items = candidateItems
                        ItemCount = candidateItems.Length
                        SerializedBytes = 0
                    }

                let serializedBytes = Encoding.UTF8.GetByteCount(serialize candidate)

                if serializedBytes > BaselineShardByteLimit then
                    if shardItems.Count = 0 then
                        invalidOp $"One Library baseline item exceeds the {BaselineShardByteLimit}-byte shard bound."

                    fits <- false
                else
                    shardItems.Add orderedItems[nextItem]
                    nextItem <- nextItem + 1

            let shardKey = $"shard:{shardIndex:D8}"
            let shardId = $"shard:{baselineId:D}:{shardIndex:D8}"

            let initialShard =
                {
                    id = shardId
                    RepositoryId = repositoryId
                    SchemaVersion = 1
                    BaselineId = baselineId
                    ShardKey = shardKey
                    BoundaryCursor = boundaryCursor
                    Items = shardItems.ToArray()
                    ItemCount = shardItems.Count
                    SerializedBytes = 0
                }

            let shard = { initialShard with SerializedBytes = Encoding.UTF8.GetByteCount(serialize initialShard) }

            if shard.SerializedBytes > BaselineShardByteLimit then
                invalidOp $"Library baseline shard {shardIndex} exceeds the {BaselineShardByteLimit}-byte bound."

            shardDocuments.Add shard
            shardIndex <- shardIndex + 1

        let shards = shardDocuments.ToArray()
        let manifestId = $"manifest:{baselineId:D}"

        let manifest =
            {
                id = manifestId
                RepositoryId = repositoryId
                SchemaVersion = 1
                BaselineId = baselineId
                ShardKey = "manifest"
                BoundaryCursor = boundaryCursor
                CursorEpoch = cursorEpoch
                LibraryCatalogVersion = libraryCatalog.Version
                LibraryCatalog = libraryCatalog
                ShardIds = shards |> Array.map (fun shard -> shard.id)
                ShardHashes = shards |> Array.map documentHash
                ShardItemCounts = shards |> Array.map (fun shard -> shard.ItemCount)
                TotalItemCount = orderedItems.Length
                CreatedAt = createdAt
            }

        manifest, shards

    /// Returns a missing document as None while preserving all other Cosmos failures.
    let private readOptional<'T> (container: Container) id key cancellationToken =
        task {
            try
                let! response = container.ReadItemAsync<'T>(id, key, cancellationToken = cancellationToken)
                return Some { Document = response.Resource; ETag = response.ETag }
            with
            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return None
        }

    /// Applies a cursor-ordered projection without allowing an older or different equal-position value to win.
    let private upsertProjection<'T> (container: Container) id key lastCursor existingLastCursor (candidate: 'T) cancellationToken =
        task {
            let mutable completed = false

            while not completed do
                match! readOptional<'T> container id key cancellationToken with
                | None ->
                    try
                        let! _ = container.CreateItemAsync(candidate, key, cancellationToken = cancellationToken)
                        completed <- true
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict -> ()
                | Some current ->
                    let currentCursor = existingLastCursor current.Document

                    if currentCursor > lastCursor then
                        completed <- true
                    elif currentCursor = lastCursor then
                        if not (equivalent current.Document candidate) then
                            invalidOp $"A Library projection at cursor {lastCursor} is not byte-equivalent to its deterministic retry."

                        completed <- true
                    else
                        let options = ItemRequestOptions(IfMatchEtag = current.ETag)

                        try
                            let! _ = container.ReplaceItemAsync(candidate, id, key, options, cancellationToken)
                            completed <- true
                        with
                        | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> ()
        }

    /// Creates one immutable document or verifies that a concurrent retry created the exact same value.
    let private createExact<'T> (container: Container) (id: string) (key: PartitionKey) (candidate: 'T) (cancellationToken: CancellationToken) =
        task {
            try
                let! _ = container.CreateItemAsync(candidate, key, cancellationToken = cancellationToken)
                return ()
            with
            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict ->
                let! existing = container.ReadItemAsync<'T>(id, key, cancellationToken = cancellationToken)

                if not (equivalent existing.Resource candidate) then
                    invalidOp $"Immutable Library document {id} is not byte-equivalent to its deterministic retry."
        }

    /// Counts one exact current-projection kind without crossing a Cosmos partition.
    let private countCurrentProjectionKind (current: Container) repositoryId projectionKind cancellationToken =
        task {
            let options = QueryRequestOptions(PartitionKey = Nullable(partitionKey2 repositoryId projectionKind), MaxItemCount = Nullable 1)
            use iterator = current.GetItemQueryIterator<int>("SELECT VALUE COUNT(1) FROM c", requestOptions = options)
            let! page = iterator.ReadNextAsync(cancellationToken)

            return
                page.Resource
                |> Seq.tryHead
                |> Option.defaultValue 0
        }

    /// Owns direct access to the six fixed application Cosmos containers.
    type CosmosLibraryStore(control: Container, changes: Container, current: Container, receipts: Container, history: Container, baselines: Container) as this =

        /// Reads one history segment under its full hierarchical key.
        let readHistory repositoryId historyKey historySegment id cancellationToken =
            readOptional<LibraryHistorySegmentDocument> history id (partitionKey3 repositoryId historyKey historySegment) cancellationToken

        /// Appends one history entry with ETag retry and exact entry/byte bounds.
        let appendHistory repositoryId historyKey (entry: LibraryHistoryEntry) cancellationToken =
            task {
                let segment =
                    (entry.Cursor - 1L)
                    / int64 HistorySegmentEntryLimit

                let id = $"segment:{segmentKey segment}"
                let historySegment = segmentKey segment
                let key = partitionKey3 repositoryId historyKey historySegment
                let mutable completed = false

                while not completed do
                    match! readHistory repositoryId historyKey historySegment id cancellationToken with
                    | None ->
                        let candidate =
                            {
                                id = id
                                RepositoryId = repositoryId
                                HistoryKey = historyKey
                                SchemaVersion = 1
                                HistorySegment = historySegment
                                FirstCursor = entry.Cursor
                                LastCursor = entry.Cursor
                                EntryCount = 1
                                Entries = [| entry |]
                            }

                        if Encoding.UTF8.GetByteCount(serialize candidate) > HistorySegmentByteLimit then
                            invalidOp "One Library history entry exceeds the 900 KiB segment bound."

                        try
                            let! _ = history.CreateItemAsync(candidate, key, cancellationToken = cancellationToken)
                            completed <- true
                        with
                        | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict -> ()
                    | Some existing ->
                        match existing.Document.Entries
                              |> Array.tryFind (fun current -> current.Cursor = entry.Cursor)
                            with
                        | Some currentEntry ->
                            if not (equivalent currentEntry entry) then
                                invalidOp $"History cursor {entry.Cursor} is not byte-equivalent to its deterministic retry."

                            completed <- true
                        | None ->
                            if existing.Document.EntryCount
                               >= HistorySegmentEntryLimit then
                                invalidOp $"History segment {segment} exceeded its {HistorySegmentEntryLimit}-entry bound."

                            let entries =
                                Array.append existing.Document.Entries [| entry |]
                                |> Array.sortBy (fun historyEntry -> historyEntry.Cursor)

                            let replacement =
                                { existing.Document with
                                    FirstCursor = entries[0].Cursor
                                    LastCursor = entries[entries.Length - 1].Cursor
                                    EntryCount = entries.Length
                                    Entries = entries
                                }

                            if Encoding.UTF8.GetByteCount(serialize replacement) > HistorySegmentByteLimit then
                                invalidOp $"History segment {segment} exceeded its 900 KiB serialized bound."

                            let options = ItemRequestOptions(IfMatchEtag = existing.ETag)

                            try
                                let! _ = history.ReplaceItemAsync(replacement, id, key, options, cancellationToken)
                                completed <- true
                            with
                            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> ()
            }

        interface ILibraryStore with

            member _.EnsureControlAsync(repositoryId, libraryCatalog, cancellationToken) =
                task {
                    let id = "control"
                    let key = controlPartitionKey repositoryId

                    match! readOptional<LibraryControlDocument> control id key cancellationToken with
                    | Some existing -> return existing
                    | None ->
                        let now = SystemClock.Instance.GetCurrentInstant()

                        let candidate =
                            {
                                id = id
                                RepositoryId = repositoryId
                                SchemaVersion = 1
                                CursorEpoch = Guid.NewGuid()
                                NextCursor = 1L
                                AppliedThrough = 0L
                                ReplayFloor = 1L
                                LibraryCatalog = libraryCatalog
                                Pending = None
                                CurrentBaselineId = None
                                CurrentBaselineCursor = None
                                ProjectionWatermarks = LibraryProjectionWatermarks.Empty
                                UpdatedAt = now
                            }

                        try
                            let! response = control.CreateItemAsync(candidate, key, cancellationToken = cancellationToken)
                            return { Document = response.Resource; ETag = response.ETag }
                        with
                        | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict ->
                            return!
                                (this :> ILibraryStore)
                                    .ReadControlAsync(repositoryId, cancellationToken)
                }

            member _.ReadControlAsync(repositoryId, cancellationToken) =
                task {
                    let id = "control"

                    let! response = control.ReadItemAsync<LibraryControlDocument>(id, controlPartitionKey repositoryId, cancellationToken = cancellationToken)

                    return { Document = response.Resource; ETag = response.ETag }
                }

            member _.ReplaceControlAsync(document, etag, cancellationToken) =
                task {
                    let options = ItemRequestOptions(IfMatchEtag = etag)

                    try
                        let! response = control.ReplaceItemAsync(document, document.id, controlPartitionKey document.RepositoryId, options, cancellationToken)

                        return Replaced response.ETag
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> return PreconditionFailed
                }

            member _.ReadCatalogOperationAsync(repositoryId, operationId, cancellationToken) =
                task {
                    let id = $"catalog-operation:{operationId:D}"

                    let! existing = readOptional<LibraryCatalogOperationDocument> control id (controlPartitionKey repositoryId) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.ReplaceControlAndCreateCatalogOperationAsync(document, etag, operation, cancellationToken) =
                task {
                    let requestOptions = TransactionalBatchItemRequestOptions(IfMatchEtag = etag)

                    let batch =
                        control
                            .CreateTransactionalBatch(controlPartitionKey document.RepositoryId)
                            .ReplaceItem(document.id, document, requestOptions)
                            .CreateItem(operation)

                    let! response = batch.ExecuteAsync cancellationToken

                    if response.IsSuccessStatusCode then
                        return Replaced String.Empty
                    elif response.StatusCode = HttpStatusCode.PreconditionFailed
                         || response.StatusCode = HttpStatusCode.Conflict then
                        return PreconditionFailed
                    else
                        return invalidOp $"The atomic Library catalog update failed with status {int response.StatusCode}: {response.ErrorMessage}"
                }

            member _.CreateCatalogOperationAsync(operation, cancellationToken) =
                createExact control operation.id (controlPartitionKey operation.RepositoryId) operation cancellationToken

            member _.ReadReceiptAsync(repositoryId, operationId, cancellationToken) =
                task {
                    let id = $"operation:{operationId:D}"

                    let! existing = readOptional<LibraryReceiptDocument> receipts id (partitionKey3 repositoryId "receipt" id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.ReadCanonicalAsync(repositoryId, cursor, cancellationToken) =
                task {
                    let streamSegment = segmentKey (changeSegment cursor)
                    let id = $"cursor:{segmentKey cursor}"

                    let! existing = readOptional<LibraryCanonicalChangeDocument> changes id (partitionKey2 repositoryId streamSegment) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.CreateCanonicalAsync(document, cancellationToken) =
                task {
                    let key = partitionKey2 document.RepositoryId document.StreamSegment

                    try
                        let! _ = changes.CreateItemAsync(document, key, cancellationToken = cancellationToken)
                        return ()
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict ->
                        let! existing = changes.ReadItemAsync<LibraryCanonicalChangeDocument>(document.id, key, null, cancellationToken)

                        if not (equivalent existing.Resource document) then
                            invalidOp $"Canonical cursor {document.Cursor} is not byte-equivalent to its deterministic retry."
                }

            member _.ReadItemAsync(repositoryId, itemId, cancellationToken) =
                task {
                    let id = $"item:{itemId:D}"

                    let! existing = readOptional<LibraryCurrentItemDocument> current id (partitionKey2 repositoryId "item") cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.ReadSlotAsync(repositoryId, normalizedPath, cancellationToken) =
                task {
                    let id = $"slot:{pathHash normalizedPath}"

                    let! existing = readOptional<LibraryCurrentSlotDocument> current id (partitionKey2 repositoryId "slot") cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.EnsureCurrentProjectionCapacityAsync(repositoryId, itemId, normalizedPath, cancellationToken) =
                task {
                    let itemDocumentId = $"item:{itemId:D}"
                    let slotDocumentId = $"slot:{pathHash normalizedPath}"

                    let! existingItem = readOptional<LibraryCurrentItemDocument> current itemDocumentId (partitionKey2 repositoryId "item") cancellationToken

                    if existingItem.IsNone then
                        let! itemCount = countCurrentProjectionKind current repositoryId "item" cancellationToken

                        if itemCount >= CurrentProjectionDocumentLimit then
                            invalidOp $"The Library item-head projection reached its {CurrentProjectionDocumentLimit}-document Product V1 bound."

                    let! existingSlot = readOptional<LibraryCurrentSlotDocument> current slotDocumentId (partitionKey2 repositoryId "slot") cancellationToken

                    if existingSlot.IsNone then
                        let! slotCount = countCurrentProjectionKind current repositoryId "slot" cancellationToken

                        if slotCount >= CurrentProjectionDocumentLimit then
                            invalidOp $"The Library namespace-slot projection reached its {CurrentProjectionDocumentLimit}-document Product V1 bound."
                }

            member _.UpsertItemAsync(document, cancellationToken) =
                upsertProjection
                    current
                    document.id
                    (partitionKey2 document.RepositoryId document.ProjectionKind)
                    document.LastCursor
                    (fun (value: LibraryCurrentItemDocument) -> value.LastCursor)
                    document
                    cancellationToken

            member _.UpsertSlotAsync(document, cancellationToken) =
                upsertProjection
                    current
                    document.id
                    (partitionKey2 document.RepositoryId document.ProjectionKind)
                    document.LastCursor
                    (fun (value: LibraryCurrentSlotDocument) -> value.LastCursor)
                    document
                    cancellationToken

            member _.UpsertReceiptAsync(document, cancellationToken) =
                upsertProjection
                    receipts
                    document.id
                    (partitionKey3 document.RepositoryId document.RecordKind document.RecordKey)
                    document.AppliedThrough
                    (fun (value: LibraryReceiptDocument) -> value.AppliedThrough)
                    document
                    cancellationToken

            member _.AppendItemHistoryAsync(repositoryId, itemId, entry, cancellationToken) =
                appendHistory repositoryId $"item:{itemId:D}" entry cancellationToken

            member _.AppendPathHistoryAsync(repositoryId, normalizedPath, entry, cancellationToken) =
                appendHistory repositoryId $"path:{pathHash normalizedPath}" entry cancellationToken

            member _.ReadChangesAsync(repositoryId, afterCursor, maximumCount, cancellationToken) =
                task {
                    let results = ResizeArray<LibraryCanonicalChangeDocument>()
                    let mutable cursor = afterCursor + 1L
                    let mutable exhausted = false

                    while results.Count < maximumCount && not exhausted do
                        let streamSegment = segmentKey (changeSegment cursor)

                        let options =
                            QueryRequestOptions(
                                PartitionKey = Nullable(partitionKey2 repositoryId streamSegment),
                                MaxItemCount = Nullable(maximumCount - results.Count)
                            )

                        let query =
                            QueryDefinition("SELECT * FROM c WHERE c.Cursor >= @cursor ORDER BY c.Cursor")
                                .WithParameter("@cursor", cursor)

                        use iterator = changes.GetItemQueryIterator<LibraryCanonicalChangeDocument>(query, requestOptions = options)
                        let mutable readAny = false

                        if iterator.HasMoreResults then
                            let! page = iterator.ReadNextAsync(cancellationToken)

                            for document in page do
                                if results.Count < maximumCount then
                                    results.Add document
                                    cursor <- document.Cursor + 1L
                                    readAny <- true

                        if not readAny then exhausted <- true

                    return results.ToArray()
                }

            member _.ReadCurrentItemsAsync(repositoryId, cancellationToken) =
                task {
                    let options = QueryRequestOptions(PartitionKey = Nullable(partitionKey2 repositoryId "item"), MaxItemCount = Nullable 2000)
                    use iterator = current.GetItemQueryIterator<LibraryCurrentItemDocument>("SELECT * FROM c", requestOptions = options)
                    let results = ResizeArray<LibraryCurrentItemDocument>()

                    while iterator.HasMoreResults do
                        let! page = iterator.ReadNextAsync(cancellationToken)
                        results.AddRange page

                    return results.ToArray()
                }

            member _.HasLiveDescendantsAsync(repositoryId, normalizedDirectoryPath, cancellationToken) =
                task {
                    let prefix = normalizedDirectoryPath + "/"

                    let options = QueryRequestOptions(PartitionKey = Nullable(partitionKey2 repositoryId "item"), MaxItemCount = Nullable 1)

                    let query =
                        QueryDefinition(
                            "SELECT TOP 1 VALUE true FROM c WHERE c.Item.State = 'live' AND STARTSWITH(c.Item.Namespace.NormalizedPath, @prefix, true)"
                        )
                            .WithParameter("@prefix", prefix)

                    use iterator = current.GetItemQueryIterator<bool>(query, requestOptions = options)

                    if iterator.HasMoreResults then
                        let! page = iterator.ReadNextAsync(cancellationToken)
                        return page.Count > 0
                    else
                        return false
                }

            member _.EnsureBaselineAsync(repositoryId, boundaryCursor, cursorEpoch, libraryCatalog, items, cancellationToken) =
                task {
                    let baselineId = baselineId repositoryId boundaryCursor cursorEpoch libraryCatalog.Version
                    let baselineKey = baselineId.ToString("D")
                    let manifestId = $"manifest:{baselineId:D}"
                    let manifestKey = partitionKey3 repositoryId baselineKey "manifest"

                    match! readOptional<LibraryBaselineManifestDocument> baselines manifestId manifestKey cancellationToken with
                    | Some existing -> return existing.Document
                    | None ->
                        let manifest, shardDocuments =
                            buildBaselineDocuments repositoryId boundaryCursor cursorEpoch libraryCatalog items (SystemClock.Instance.GetCurrentInstant())

                        for shard in shardDocuments do
                            do! createExact baselines shard.id (partitionKey3 repositoryId baselineKey shard.ShardKey) shard cancellationToken

                        do! createExact baselines manifest.id manifestKey manifest cancellationToken
                        return manifest
                }

            member _.ReadBaselineAsync(repositoryId, baselineId, cancellationToken) =
                task {
                    let baselineKey = baselineId.ToString("D")
                    let manifestId = $"manifest:{baselineId:D}"

                    match! readOptional<LibraryBaselineManifestDocument>
                               baselines
                               manifestId
                               (partitionKey3 repositoryId baselineKey "manifest")
                               cancellationToken
                        with
                    | None -> return None
                    | Some manifestRead ->
                        let items = ResizeArray<LibraryItemDto>()

                        for index = 0 to manifestRead.Document.ShardIds.Length - 1 do
                            let shardId = manifestRead.Document.ShardIds[index]
                            let shardKey = shardId.Replace($"shard:{baselineId:D}:", "shard:")

                            let! shard =
                                baselines.ReadItemAsync<LibraryBaselineShardDocument>(
                                    shardId,
                                    partitionKey3 repositoryId baselineKey shardKey,
                                    cancellationToken = cancellationToken
                                )

                            if documentHash shard.Resource
                               <> manifestRead.Document.ShardHashes[index] then
                                invalidOp $"Library baseline shard {shardId} failed its published hash."

                            if shard.Resource.ItemCount
                               <> manifestRead.Document.ShardItemCounts[index] then
                                invalidOp $"Library baseline shard {shardId} failed its published item count."

                            items.AddRange shard.Resource.Items

                        if items.Count
                           <> manifestRead.Document.TotalItemCount then
                            invalidOp $"Library baseline {baselineId:D} failed its published total item count."

                        return Some(manifestRead.Document, items.ToArray())
                }

        interface ILibraryTransferStore with

            member _.CreatePreparedAsync(document, cancellationToken) =
                createExact receipts document.id (partitionKey3 document.RepositoryId document.RecordKind document.RecordKey) document cancellationToken

            member _.ReadPreparedAsync(repositoryId, preparedContentId, cancellationToken) =
                let id = $"prepared:{preparedContentId:D}"
                readOptional<LibraryPreparedContentDocument> receipts id (partitionKey3 repositoryId "prepared" id) cancellationToken

            member _.FinalizePreparedAsync(repositoryId, preparedContentId, manifest, cancellationToken) =
                task {
                    let id = $"prepared:{preparedContentId:D}"
                    let key = partitionKey3 repositoryId "prepared" id
                    let mutable completed = false

                    while not completed do
                        match! readOptional<LibraryPreparedContentDocument> receipts id key cancellationToken with
                        | None -> invalidOp "The Library content preparation does not exist."
                        | Some current ->
                            if manifest.ManifestAddress
                               <> ContentAddress.computeManifestAddressForManifest manifest then
                                invalidOp "The finalized Library content manifest address is invalid."

                            if manifest.FileContentHash
                               <> current.Document.Content.Blake3Hash
                               || manifest.Size <> current.Document.Content.Size then
                                invalidOp "The finalized Library content manifest does not match its prepared descriptor."

                            match current.Document.FinalizedManifest with
                            | Some existing when equivalent existing manifest -> completed <- true
                            | Some _ -> invalidOp "The Library content preparation was already finalized with a different manifest."
                            | None ->
                                let replacement = { current.Document with FinalizedManifest = Some manifest }
                                let options = ItemRequestOptions(IfMatchEtag = current.ETag)

                                try
                                    let! _ = receipts.ReplaceItemAsync(replacement, id, key, options, cancellationToken)
                                    completed <- true
                                with
                                | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> ()
                }

            member _.UpsertContentLocationAsync(document, cancellationToken) =
                createExact receipts document.id (partitionKey3 document.RepositoryId document.RecordKind document.RecordKey) document cancellationToken

            member _.ReadContentLocationAsync(repositoryId, contentVersionId, cancellationToken) =
                task {
                    let id = $"content:{contentVersionId:D}"

                    let! existing = readOptional<LibraryContentLocationDocument> receipts id (partitionKey3 repositoryId "content" id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.CreateReadGrantAsync(document, cancellationToken) =
                createExact receipts document.id (partitionKey3 document.RepositoryId document.RecordKind document.RecordKey) document cancellationToken

            member _.ReadReadGrantAsync(repositoryId, grantId, cancellationToken) =
                let id = $"grant:{grantId:D}"
                readOptional<LibraryContentReadGrantDocument> receipts id (partitionKey3 repositoryId "grant" id) cancellationToken

            member _.ConsumeReadGrantAsync(document, etag, cancellationToken) =
                task {
                    let options = ItemRequestOptions(IfMatchEtag = etag)

                    try
                        let! response =
                            receipts.ReplaceItemAsync(
                                document,
                                document.id,
                                partitionKey3 document.RepositoryId document.RecordKind document.RecordKey,
                                options,
                                cancellationToken
                            )

                        return Replaced response.ETag
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> return PreconditionFailed
                }

    /// Creates the six-container store from the configured Cosmos client and database name.
    let createStore (client: CosmosClient) databaseName =
        let database = client.GetDatabase databaseName

        CosmosLibraryStore(
            database.GetContainer ControlContainerName,
            database.GetContainer ChangesContainerName,
            database.GetContainer CurrentContainerName,
            database.GetContainer ReceiptsContainerName,
            database.GetContainer HistoryContainerName,
            database.GetContainer BaselinesContainerName
        )
        :> ILibraryStore

    /// Creates the preparation and read-grant store over the fixed receipt container.
    let createTransferStore (client: CosmosClient) databaseName =
        let database = client.GetDatabase databaseName

        CosmosLibraryStore(
            database.GetContainer ControlContainerName,
            database.GetContainer ChangesContainerName,
            database.GetContainer CurrentContainerName,
            database.GetContainer ReceiptsContainerName,
            database.GetContainer HistoryContainerName,
            database.GetContainer BaselinesContainerName
        )
        :> ILibraryTransferStore
