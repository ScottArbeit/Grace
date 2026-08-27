namespace Grace.Server

open Grace.Shared
open Grace.Types.Common
open Grace.Types.SynchronizedContent
open Microsoft.Azure.Cosmos
open NodaTime
open System
open System.Collections.Generic
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Implements the six fixed direct-Cosmos stores for remote Synchronized Content.
module SynchronizedContentPersistence =

    [<Literal>]
    let ControlContainerName = "grace-synchronized-control"

    [<Literal>]
    let MutationsContainerName = "grace-synchronized-mutations"

    [<Literal>]
    let CurrentContainerName = "grace-synchronized-current"

    [<Literal>]
    let ReceiptsContainerName = "grace-synchronized-receipts"

    [<Literal>]
    let HistoryContainerName = "grace-synchronized-history"

    [<Literal>]
    let BaselinesContainerName = "grace-synchronized-baselines"

    [<Literal>]
    let MutationSegmentSize = 200L

    [<Literal>]
    let HistorySegmentEntryLimit = 512

    [<Literal>]
    let HistorySegmentByteLimit = 921600

    [<Literal>]
    let BaselineShardByteLimit = 1000000

    /// Formats one non-negative internal segment value for stable Cosmos keys.
    let segmentKey (value: int64) = value.ToString("D20", Globalization.CultureInfo.InvariantCulture)

    /// Returns the exact stream segment containing one positive internal cursor.
    let mutationSegment (cursor: int64) = (cursor - 1L) / MutationSegmentSize

    /// Computes the private lowercase SHA-256 key for one normalized portable path.
    let pathHash (normalizedPath: string) =
        normalizedPath
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Creates one exact three-component hierarchical partition key.
    let partitionKey (repositoryId: RepositoryId) (scope: string) (id: string) =
        PartitionKeyBuilder()
            .Add(repositoryId.ToString("D"))
            .Add(scope)
            .Add(id)
            .Build()

    /// Creates one repository-and-scope hierarchical partition-key prefix for bounded enumeration.
    let partitionPrefix (repositoryId: RepositoryId) (scope: string) =
        PartitionKeyBuilder()
            .Add(repositoryId.ToString("D"))
            .Add(scope)
            .Build()

    /// Serializes an internal document with the same stable options used by Grace HTTP and Cosmos clients.
    let private serialize value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Confirms an equal-position retry carries byte-equivalent deterministic state.
    let private equivalent left right = String.Equals(serialize left, serialize right, StringComparison.Ordinal)

    /// Derives one stable baseline identity from its immutable repository boundary.
    let private baselineId repositoryId boundaryCursor cursorEpoch rootConfigurationVersion =
        let seed = Encoding.UTF8.GetBytes($"{repositoryId:D}:{boundaryCursor}:{cursorEpoch:D}:{rootConfigurationVersion:D}")
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
        (rootConfiguration: SynchronizedRootConfigurationDto)
        (items: SynchronizedItemDto array)
        (createdAt: Instant)
        =
        let baselineId = baselineId repositoryId boundaryCursor cursorEpoch rootConfiguration.Version
        let scope = $"baseline:{baselineId:D}"

        let orderedItems =
            items
            |> Array.sortBy (fun item ->
                item.Namespace
                |> Option.map (fun namespaceValue -> namespaceValue.NormalizedPath.ToUpperInvariant())
                |> Option.defaultValue "",
                item.ItemId)

        let shardDocuments = ResizeArray<SynchronizedBaselineShardDocument>()
        let mutable nextItem = 0
        let mutable shardIndex = 0

        while nextItem < orderedItems.Length do
            let shardItems = ResizeArray<SynchronizedItemDto>()
            let mutable fits = true

            while nextItem < orderedItems.Length && fits do
                let candidateItems = Array.append (shardItems.ToArray()) [| orderedItems[nextItem] |]
                let shardId = $"shard:{baselineId:D}:{shardIndex:D8}"

                let candidate =
                    {
                        id = shardId
                        RepositoryId = repositoryId
                        Scope = scope
                        SchemaVersion = 1
                        BaselineId = baselineId
                        BoundaryCursor = boundaryCursor
                        Items = candidateItems
                        ItemCount = candidateItems.Length
                        SerializedBytes = 0
                    }

                let serializedBytes = Encoding.UTF8.GetByteCount(serialize candidate)

                if serializedBytes > BaselineShardByteLimit then
                    if shardItems.Count = 0 then
                        invalidOp $"One synchronized baseline item exceeds the {BaselineShardByteLimit}-byte shard bound."

                    fits <- false
                else
                    shardItems.Add orderedItems[nextItem]
                    nextItem <- nextItem + 1

            let shardId = $"shard:{baselineId:D}:{shardIndex:D8}"

            let initialShard =
                {
                    id = shardId
                    RepositoryId = repositoryId
                    Scope = scope
                    SchemaVersion = 1
                    BaselineId = baselineId
                    BoundaryCursor = boundaryCursor
                    Items = shardItems.ToArray()
                    ItemCount = shardItems.Count
                    SerializedBytes = 0
                }

            let shard = { initialShard with SerializedBytes = Encoding.UTF8.GetByteCount(serialize initialShard) }

            if shard.SerializedBytes > BaselineShardByteLimit then
                invalidOp $"Synchronized baseline shard {shardIndex} exceeds the {BaselineShardByteLimit}-byte bound."

            shardDocuments.Add shard
            shardIndex <- shardIndex + 1

        let shards = shardDocuments.ToArray()
        let manifestId = $"manifest:{baselineId:D}"

        let manifest =
            {
                id = manifestId
                RepositoryId = repositoryId
                Scope = scope
                SchemaVersion = 1
                BaselineId = baselineId
                BoundaryCursor = boundaryCursor
                CursorEpoch = cursorEpoch
                RootConfigurationVersion = rootConfiguration.Version
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
                            invalidOp $"A synchronized projection at cursor {lastCursor} is not byte-equivalent to its deterministic retry."

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
                    invalidOp $"Immutable synchronized document {id} is not byte-equivalent to its deterministic retry."
        }

    /// Owns direct access to the six fixed application Cosmos containers.
    type CosmosSynchronizedContentStore
        (
            control: Container,
            mutations: Container,
            current: Container,
            receipts: Container,
            history: Container,
            baselines: Container
        ) as this =

        /// Reads one history segment under its full hierarchical key.
        let readHistory repositoryId scope id cancellationToken =
            readOptional<SynchronizedHistorySegmentDocument> history id (partitionKey repositoryId scope id) cancellationToken

        /// Appends one history entry with ETag retry and exact entry/byte bounds.
        let appendHistory repositoryId scope (entry: SynchronizedHistoryEntry) cancellationToken =
            task {
                let segment =
                    (entry.Cursor - 1L)
                    / int64 HistorySegmentEntryLimit

                let id = $"segment:{segmentKey segment}"
                let key = partitionKey repositoryId scope id
                let mutable completed = false

                while not completed do
                    match! readHistory repositoryId scope id cancellationToken with
                    | None ->
                        let candidate =
                            {
                                id = id
                                RepositoryId = repositoryId
                                Scope = scope
                                SchemaVersion = 1
                                Segment = segment
                                FirstCursor = entry.Cursor
                                LastCursor = entry.Cursor
                                EntryCount = 1
                                Entries = [| entry |]
                            }

                        if Encoding.UTF8.GetByteCount(serialize candidate) > HistorySegmentByteLimit then
                            invalidOp "One synchronized history entry exceeds the 900 KiB segment bound."

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

        interface ISynchronizedContentStore with

            member _.EnsureControlAsync(repositoryId, rootConfiguration, cancellationToken) =
                task {
                    let id = "control"
                    let key = partitionKey repositoryId "control" id

                    match! readOptional<SynchronizedControlDocument> control id key cancellationToken with
                    | Some existing -> return existing
                    | None ->
                        let now = SystemClock.Instance.GetCurrentInstant()

                        let candidate =
                            {
                                id = id
                                RepositoryId = repositoryId
                                Scope = "control"
                                SchemaVersion = 1
                                CursorEpoch = Guid.NewGuid()
                                NextCursor = 1L
                                AppliedThrough = 0L
                                ReplayFloor = 1L
                                RootConfiguration = rootConfiguration
                                Pending = None
                                CurrentBaselineId = None
                                CurrentBaselineCursor = None
                                ProjectionWatermarks = SynchronizedProjectionWatermarks.Empty
                                UpdatedAt = now
                            }

                        try
                            let! response = control.CreateItemAsync(candidate, key, cancellationToken = cancellationToken)
                            return { Document = response.Resource; ETag = response.ETag }
                        with
                        | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict ->
                            return!
                                (this :> ISynchronizedContentStore)
                                    .ReadControlAsync(repositoryId, cancellationToken)
                }

            member _.ReadControlAsync(repositoryId, cancellationToken) =
                task {
                    let id = "control"

                    let! response =
                        control.ReadItemAsync<SynchronizedControlDocument>(id, partitionKey repositoryId "control" id, cancellationToken = cancellationToken)

                    return { Document = response.Resource; ETag = response.ETag }
                }

            member _.ReplaceControlAsync(document, etag, cancellationToken) =
                task {
                    let options = ItemRequestOptions(IfMatchEtag = etag)

                    try
                        let! response =
                            control.ReplaceItemAsync(
                                document,
                                document.id,
                                partitionKey document.RepositoryId document.Scope document.id,
                                options,
                                cancellationToken
                            )

                        return Replaced response.ETag
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> return PreconditionFailed
                }

            member _.ReadReceiptAsync(repositoryId, operationId, cancellationToken) =
                task {
                    let id = $"operation:{operationId:D}"

                    let! existing = readOptional<SynchronizedReceiptDocument> receipts id (partitionKey repositoryId "receipt" id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.ReadCanonicalAsync(repositoryId, cursor, cancellationToken) =
                task {
                    let scope = $"stream:{segmentKey (mutationSegment cursor)}"
                    let id = $"cursor:{segmentKey cursor}"

                    let! existing = readOptional<SynchronizedCanonicalMutationDocument> mutations id (partitionKey repositoryId scope id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.CreateCanonicalAsync(document, cancellationToken) =
                task {
                    let key = partitionKey document.RepositoryId document.Scope document.id

                    try
                        let! _ = mutations.CreateItemAsync(document, key, cancellationToken = cancellationToken)
                        return ()
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict ->
                        let! existing = mutations.ReadItemAsync<SynchronizedCanonicalMutationDocument>(document.id, key, null, cancellationToken)

                        if not (equivalent existing.Resource document) then
                            invalidOp $"Canonical cursor {document.Cursor} is not byte-equivalent to its deterministic retry."
                }

            member _.ReadItemAsync(repositoryId, itemId, cancellationToken) =
                task {
                    let id = $"item:{itemId:D}"

                    let! existing = readOptional<SynchronizedCurrentItemDocument> current id (partitionKey repositoryId "item" id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.ReadSlotAsync(repositoryId, normalizedPath, cancellationToken) =
                task {
                    let id = $"slot:{pathHash normalizedPath}"

                    let! existing = readOptional<SynchronizedCurrentSlotDocument> current id (partitionKey repositoryId "slot" id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.UpsertItemAsync(document, cancellationToken) =
                upsertProjection
                    current
                    document.id
                    (partitionKey document.RepositoryId document.Scope document.id)
                    document.LastCursor
                    (fun (value: SynchronizedCurrentItemDocument) -> value.LastCursor)
                    document
                    cancellationToken

            member _.UpsertSlotAsync(document, cancellationToken) =
                upsertProjection
                    current
                    document.id
                    (partitionKey document.RepositoryId document.Scope document.id)
                    document.LastCursor
                    (fun (value: SynchronizedCurrentSlotDocument) -> value.LastCursor)
                    document
                    cancellationToken

            member _.UpsertReceiptAsync(document, cancellationToken) =
                upsertProjection
                    receipts
                    document.id
                    (partitionKey document.RepositoryId document.Scope document.id)
                    document.AppliedThrough
                    (fun (value: SynchronizedReceiptDocument) -> value.AppliedThrough)
                    document
                    cancellationToken

            member _.AppendItemHistoryAsync(repositoryId, itemId, entry, cancellationToken) =
                appendHistory repositoryId $"item:{itemId:D}" entry cancellationToken

            member _.AppendPathHistoryAsync(repositoryId, normalizedPath, entry, cancellationToken) =
                appendHistory repositoryId $"path:{pathHash normalizedPath}" entry cancellationToken

            member _.ReadDeltasAsync(repositoryId, afterCursor, maximumCount, cancellationToken) =
                task {
                    let results = ResizeArray<SynchronizedCanonicalMutationDocument>()
                    let mutable cursor = afterCursor + 1L
                    let mutable exhausted = false

                    while results.Count < maximumCount && not exhausted do
                        let scope = $"stream:{segmentKey (mutationSegment cursor)}"

                        let options =
                            QueryRequestOptions(
                                PartitionKey = Nullable(partitionPrefix repositoryId scope),
                                MaxItemCount = Nullable(maximumCount - results.Count)
                            )

                        let query =
                            QueryDefinition("SELECT * FROM c WHERE c.Cursor >= @cursor ORDER BY c.Cursor")
                                .WithParameter("@cursor", cursor)

                        use iterator = mutations.GetItemQueryIterator<SynchronizedCanonicalMutationDocument>(query, requestOptions = options)
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
                    let options = QueryRequestOptions(PartitionKey = Nullable(partitionPrefix repositoryId "item"), MaxItemCount = Nullable 2000)
                    use iterator = current.GetItemQueryIterator<SynchronizedCurrentItemDocument>("SELECT * FROM c", requestOptions = options)
                    let results = ResizeArray<SynchronizedCurrentItemDocument>()

                    while iterator.HasMoreResults do
                        let! page = iterator.ReadNextAsync(cancellationToken)
                        results.AddRange page

                    return results.ToArray()
                }

            member _.HasLiveDescendantsAsync(repositoryId, normalizedDirectoryPath, cancellationToken) =
                task {
                    let prefix = normalizedDirectoryPath + "/"

                    let options = QueryRequestOptions(PartitionKey = Nullable(partitionPrefix repositoryId "item"), MaxItemCount = Nullable 1)

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

            member _.EnsureBaselineAsync(repositoryId, boundaryCursor, cursorEpoch, rootConfiguration, items, cancellationToken) =
                task {
                    let baselineId = baselineId repositoryId boundaryCursor cursorEpoch rootConfiguration.Version
                    let scope = $"baseline:{baselineId:D}"
                    let manifestId = $"manifest:{baselineId:D}"
                    let manifestKey = partitionKey repositoryId scope manifestId

                    match! readOptional<SynchronizedBaselineManifestDocument> baselines manifestId manifestKey cancellationToken with
                    | Some existing -> return existing.Document
                    | None ->
                        let manifest, shardDocuments =
                            buildBaselineDocuments repositoryId boundaryCursor cursorEpoch rootConfiguration items (SystemClock.Instance.GetCurrentInstant())

                        for shard in shardDocuments do
                            do! createExact baselines shard.id (partitionKey repositoryId scope shard.id) shard cancellationToken

                        do! createExact baselines manifest.id manifestKey manifest cancellationToken
                        return manifest
                }

            member _.ReadBaselineAsync(repositoryId, baselineId, cancellationToken) =
                task {
                    let scope = $"baseline:{baselineId:D}"
                    let manifestId = $"manifest:{baselineId:D}"

                    match! readOptional<SynchronizedBaselineManifestDocument>
                               baselines
                               manifestId
                               (partitionKey repositoryId scope manifestId)
                               cancellationToken
                        with
                    | None -> return None
                    | Some manifestRead ->
                        let items = ResizeArray<SynchronizedItemDto>()

                        for index = 0 to manifestRead.Document.ShardIds.Length - 1 do
                            let shardId = manifestRead.Document.ShardIds[index]

                            let! shard =
                                baselines.ReadItemAsync<SynchronizedBaselineShardDocument>(
                                    shardId,
                                    partitionKey repositoryId scope shardId,
                                    cancellationToken = cancellationToken
                                )

                            if documentHash shard.Resource
                               <> manifestRead.Document.ShardHashes[index] then
                                invalidOp $"Synchronized baseline shard {shardId} failed its published hash."

                            if shard.Resource.ItemCount
                               <> manifestRead.Document.ShardItemCounts[index] then
                                invalidOp $"Synchronized baseline shard {shardId} failed its published item count."

                            items.AddRange shard.Resource.Items

                        if items.Count
                           <> manifestRead.Document.TotalItemCount then
                            invalidOp $"Synchronized baseline {baselineId:D} failed its published total item count."

                        return Some(manifestRead.Document, items.ToArray())
                }

        interface ISynchronizedContentTransferStore with

            member _.CreatePreparedAsync(document, cancellationToken) =
                createExact receipts document.id (partitionKey document.RepositoryId document.Scope document.id) document cancellationToken

            member _.ReadPreparedAsync(repositoryId, preparedContentId, cancellationToken) =
                let id = $"prepared:{preparedContentId:D}"
                readOptional<SynchronizedPreparedContentDocument> receipts id (partitionKey repositoryId "prepared" id) cancellationToken

            member _.FinalizePreparedAsync(repositoryId, preparedContentId, manifest, cancellationToken) =
                task {
                    let id = $"prepared:{preparedContentId:D}"
                    let key = partitionKey repositoryId "prepared" id
                    let mutable completed = false

                    while not completed do
                        match! readOptional<SynchronizedPreparedContentDocument> receipts id key cancellationToken with
                        | None -> invalidOp "The synchronized content preparation does not exist."
                        | Some current ->
                            if manifest.ManifestAddress
                               <> ContentAddress.computeManifestAddressForManifest manifest then
                                invalidOp "The finalized synchronized content manifest address is invalid."

                            if manifest.FileContentHash
                               <> current.Document.Content.Blake3Hash
                               || manifest.Size <> current.Document.Content.Size then
                                invalidOp "The finalized synchronized content manifest does not match its prepared descriptor."

                            match current.Document.FinalizedManifest with
                            | Some existing when equivalent existing manifest -> completed <- true
                            | Some _ -> invalidOp "The synchronized content preparation was already finalized with a different manifest."
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
                createExact receipts document.id (partitionKey document.RepositoryId document.Scope document.id) document cancellationToken

            member _.ReadContentLocationAsync(repositoryId, contentVersionId, cancellationToken) =
                task {
                    let id = $"content:{contentVersionId:D}"

                    let! existing = readOptional<SynchronizedContentLocationDocument> receipts id (partitionKey repositoryId "content" id) cancellationToken

                    return
                        existing
                        |> Option.map (fun value -> value.Document)
                }

            member _.CreateReadGrantAsync(document, cancellationToken) =
                createExact receipts document.id (partitionKey document.RepositoryId document.Scope document.id) document cancellationToken

            member _.ReadReadGrantAsync(repositoryId, grantId, cancellationToken) =
                let id = $"grant:{grantId:D}"
                readOptional<SynchronizedContentReadGrantDocument> receipts id (partitionKey repositoryId "grant" id) cancellationToken

            member _.ConsumeReadGrantAsync(document, etag, cancellationToken) =
                task {
                    let options = ItemRequestOptions(IfMatchEtag = etag)

                    try
                        let! response =
                            receipts.ReplaceItemAsync(
                                document,
                                document.id,
                                partitionKey document.RepositoryId document.Scope document.id,
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

        CosmosSynchronizedContentStore(
            database.GetContainer ControlContainerName,
            database.GetContainer MutationsContainerName,
            database.GetContainer CurrentContainerName,
            database.GetContainer ReceiptsContainerName,
            database.GetContainer HistoryContainerName,
            database.GetContainer BaselinesContainerName
        )
        :> ISynchronizedContentStore

    /// Creates the preparation and read-grant store over the fixed receipt container.
    let createTransferStore (client: CosmosClient) databaseName =
        let database = client.GetDatabase databaseName

        CosmosSynchronizedContentStore(
            database.GetContainer ControlContainerName,
            database.GetContainer MutationsContainerName,
            database.GetContainer CurrentContainerName,
            database.GetContainer ReceiptsContainerName,
            database.GetContainer HistoryContainerName,
            database.GetContainer BaselinesContainerName
        )
        :> ISynchronizedContentTransferStore
