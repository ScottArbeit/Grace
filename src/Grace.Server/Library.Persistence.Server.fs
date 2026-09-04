namespace Grace.Server

open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Extensions.Options
open NodaTime
open Orleans
open Orleans.Configuration
open Orleans.Persistence.Cosmos
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Implements provider-neutral Orleans persistence for bounded remote Library records.
module LibraryPersistence =

    [<Literal>]
    let ControlStorageName = Constants.LibraryControlStorage

    [<Literal>]
    let ChangesStorageName = Constants.LibraryChangesStorage

    [<Literal>]
    let CurrentStorageName = Constants.LibraryCurrentStorage

    [<Literal>]
    let ReceiptsStorageName = Constants.LibraryReceiptsStorage

    [<Literal>]
    let HistoryStorageName = Constants.LibraryHistoryStorage

    [<Literal>]
    let BaselinesStorageName = Constants.LibraryBaselinesStorage

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
            member this.GetDocumentIdentifiers(grainType: string, grainId: GrainId) =
                let values = this.GetPartitionKeyValues(grainType, grainId)
                ValueTask<struct (string * string)>(struct (defaultProvider.GetId(grainType, grainId), values[0]))

            member this.GetDocumentKey(grainType: string, grainId: GrainId) =
                let values = this.GetPartitionKeyValues(grainType, grainId)
                ValueTask<CosmosDocumentKey>(CosmosDocumentKey(defaultProvider.GetId(grainType, grainId), values :> IReadOnlyList<string>))

    /// Maps repository control actors to their one-component repository partition.
    type LibraryControlDocumentIdProvider(options: IOptions<ClusterOptions>) =
        inherit GraceDocumentIdProvider(options, 1)

    /// Maps accepted-change and current-record actors to repository plus record-kind partitions.
    type LibraryTwoLevelDocumentIdProvider(options: IOptions<ClusterOptions>) =
        inherit GraceDocumentIdProvider(options, 2)

    /// Maps receipt, history, and baseline actors to repository, record-kind, and bounded-record partitions.
    type LibraryThreeLevelDocumentIdProvider(options: IOptions<ClusterOptions>) =
        inherit GraceDocumentIdProvider(options, 3)

    /// Formats one non-negative internal segment value for stable actor keys.
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

    /// Serializes an internal document with the stable Grace serializer.
    let private serialize value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Derives one stable baseline identity from its immutable repository boundary.
    let private baselineId repositoryId boundaryCursor cursorEpoch libraryCatalogVersion =
        let hash =
            Encoding.UTF8.GetBytes($"{repositoryId:D}:{boundaryCursor}:{cursorEpoch:D}:{libraryCatalogVersion:D}")
            |> SHA256.HashData

        let bytes = hash[0..15]
        bytes[6] <- (bytes[6] &&& 0x0Fuy) ||| 0x50uy
        bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid bytes

    /// Computes the lowercase BLAKE3 digest of one stable internal JSON document.
    let internal documentHash value =
        value
        |> serialize
        |> Encoding.UTF8.GetBytes
        |> ContentAddress.computeBlake3Hex

    /// Builds deterministic bounded baseline shards and their manifest without publishing either representation.
    let internal buildBaselineDocuments repositoryId boundaryCursor cursorEpoch (libraryCatalog: LibraryCatalogDto) (items: LibraryItemDto array) createdAt =
        let baselineId = baselineId repositoryId boundaryCursor cursorEpoch libraryCatalog.Version

        let orderedItems =
            items
            |> Array.sortBy (fun item ->
                item.Namespace
                |> Option.map (fun value -> value.NormalizedPath.ToUpperInvariant())
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

                let candidate =
                    {
                        id = $"shard:{baselineId:D}:{shardIndex:D8}"
                        RepositoryId = repositoryId
                        SchemaVersion = 1
                        BaselineId = baselineId
                        ShardKey = $"shard:{shardIndex:D8}"
                        BoundaryCursor = boundaryCursor
                        Items = candidateItems
                        ItemCount = candidateItems.Length
                        SerializedBytes = 0
                    }

                if Encoding.UTF8.GetByteCount(serialize candidate) > BaselineShardByteLimit then
                    if shardItems.Count = 0 then
                        invalidOp $"One Library baseline item exceeds the {BaselineShardByteLimit}-byte shard bound."

                    fits <- false
                else
                    shardItems.Add orderedItems[nextItem]
                    nextItem <- nextItem + 1

            let initialShard =
                {
                    id = $"shard:{baselineId:D}:{shardIndex:D8}"
                    RepositoryId = repositoryId
                    SchemaVersion = 1
                    BaselineId = baselineId
                    ShardKey = $"shard:{shardIndex:D8}"
                    BoundaryCursor = boundaryCursor
                    Items = shardItems.ToArray()
                    ItemCount = shardItems.Count
                    SerializedBytes = 0
                }

            let shard = { initialShard with SerializedBytes = Encoding.UTF8.GetByteCount(serialize initialShard) }
            shardDocuments.Add shard
            shardIndex <- shardIndex + 1

        let shards = shardDocuments.ToArray()

        let manifest =
            {
                id = $"manifest:{baselineId:D}"
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

    /// Creates one complete bounded grain key for the approved document-key provider.
    let private recordKey repositoryId recordKind identity = $"{repositoryId:D}|{recordKind}|{identity}"

    /// Creates the one-partition-key control record key.
    let private controlKey repositoryId identity = $"{repositoryId:D}|{identity}"

    /// Resolves the two-byte deterministic index bucket for one record identity.
    let private indexBucket (identity: string) =
        let hash =
            identity
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData

        int hash[0], int hash[1]

    /// Returns one current-index bucket actor and its fixed-width directory actor.
    let private indexActors (grainFactory: IGrainFactory) repositoryId indexKind identity =
        let highByte, lowByte = indexBucket identity

        grainFactory.GetGrain<ILibraryCurrentIndexBucketActor>(recordKey repositoryId $"{indexKind}-bucket" $"{highByte:X2}{lowByte:X2}"),
        grainFactory.GetGrain<ILibraryCurrentIndexDirectoryActor>(recordKey repositoryId $"{indexKind}-directory" $"{highByte:X2}"),
        lowByte

    /// Adds one identity and repairs its exact bucket occupancy after restart.
    let private addIndexIdentity grainFactory repositoryId indexKind identity =
        task {
            let bucket, directory, lowByte = indexActors grainFactory repositoryId indexKind identity
            let! identities = bucket.Add identity
            do! directory.SetCount lowByte identities.Length
        }

    /// Returns every active bounded bucket for one current-projection index.
    let private readIndexBuckets (grainFactory: IGrainFactory) repositoryId indexKind =
        task {
            let! directories =
                [| 0..255 |]
                |> Array.map (fun highByte ->
                    grainFactory
                        .GetGrain<ILibraryCurrentIndexDirectoryActor>(recordKey repositoryId $"{indexKind}-directory" $"{highByte:X2}")
                        .Read())
                |> Task.WhenAll

            return
                directories
                |> Array.mapi (fun highByte counts ->
                    counts
                    |> Array.mapi (fun lowByte count -> highByte, lowByte, count)
                    |> Array.filter (fun (_, _, count) -> count > 0))
                |> Array.concat
        }

    /// Counts indexed projections without depending on provider-specific queries.
    let private countIndex grainFactory repositoryId indexKind =
        task {
            let! buckets = readIndexBuckets grainFactory repositoryId indexKind

            return
                buckets
                |> Array.sumBy (fun (_, _, count) -> count)
        }

    /// Implements every Library metadata mutation through Orleans actor persistence.
    type OrleansLibraryStore(grainFactory: IGrainFactory) =
        let control repositoryId = grainFactory.GetGrain<ILibraryControlRecordActor>(controlKey repositoryId "control")

        let catalogOperation repositoryId operationId =
            grainFactory.GetGrain<ILibraryCatalogOperationRecordActor>(controlKey repositoryId $"catalog-operation:{operationId:D}")

        let canonical repositoryId cursor =
            grainFactory.GetGrain<ILibraryCanonicalChangeRecordActor>(recordKey repositoryId (segmentKey (changeSegment cursor)) $"cursor:{segmentKey cursor}")

        let item repositoryId itemId = grainFactory.GetGrain<ILibraryCurrentItemRecordActor>(recordKey repositoryId "item" $"item:{itemId:D}")

        let slot repositoryId normalizedPath =
            grainFactory.GetGrain<ILibraryCurrentSlotRecordActor>(recordKey repositoryId "slot" $"slot:{pathHash normalizedPath}")

        let receipt repositoryId operationId = grainFactory.GetGrain<ILibraryReceiptRecordActor>(recordKey repositoryId "receipt" $"operation:{operationId:D}")

        let history repositoryId historyKey historySegment =
            grainFactory.GetGrain<ILibraryHistorySegmentRecordActor>(recordKey repositoryId historyKey $"segment:{segmentKey historySegment}")

        let appendHistory repositoryId historyKey (entry: LibraryHistoryEntry) =
            let historySegment =
                (entry.Cursor - 1L)
                / int64 HistorySegmentEntryLimit

            let emptySegment =
                {
                    id = $"history:{historyKey}:{segmentKey historySegment}"
                    RepositoryId = repositoryId
                    HistoryKey = historyKey
                    SchemaVersion = 1
                    HistorySegment = segmentKey historySegment
                    FirstCursor = entry.Cursor
                    LastCursor = entry.Cursor
                    EntryCount = 0
                    Entries = Array.empty
                }

            (history repositoryId historyKey historySegment)
                .Append
                emptySegment
                entry

        let baselineShard repositoryId (baselineId: Guid) shardKey =
            grainFactory.GetGrain<ILibraryBaselineShardRecordActor>(recordKey repositoryId (baselineId.ToString("D")) shardKey)

        let baselineManifest repositoryId (baselineId: Guid) =
            grainFactory.GetGrain<ILibraryBaselineManifestRecordActor>(recordKey repositoryId (baselineId.ToString("D")) "manifest")

        interface ILibraryStore with
            member _.EnsureControlAsync(repositoryId, libraryCatalog, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    let candidate =
                        {
                            id = "control"
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
                            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
                        }

                    let! document, version =
                        control repositoryId
                        |> fun actor -> actor.Ensure candidate

                    return { Document = document; ETag = version }
                }

            member _.ReadControlAsync(repositoryId, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    match! control repositoryId |> fun actor -> actor.Read() with
                    | Some (document, version) -> return { Document = document; ETag = version }
                    | None -> return invalidOp $"Library control for repository {repositoryId:D} does not exist."
                }

            member _.ReplaceControlAsync(document, etag, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    let! replaced =
                        control document.RepositoryId
                        |> fun actor -> actor.Replace document etag

                    return if replaced then Replaced String.Empty else PreconditionFailed
                }

            member _.ReadCatalogOperationAsync(repositoryId, operationId, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    match! catalogOperation repositoryId operationId
                           |> fun actor -> actor.Read()
                        with
                    | None -> return None
                    | Some operation ->
                        let mutable reconciled = false

                        while not reconciled do
                            match! control repositoryId |> fun actor -> actor.Read() with
                            | None -> invalidOp $"Library control for repository {repositoryId:D} does not exist."
                            | Some (controlDocument, _) when controlDocument.LibraryCatalog.Version = operation.Result.LibraryCatalog.Version ->
                                reconciled <- true
                            | Some (controlDocument, version) when operation.Result.LibraryCatalog.PreviousVersion = Some controlDocument.LibraryCatalog.Version ->
                                let replacement =
                                    { controlDocument with
                                        LibraryCatalog = operation.Result.LibraryCatalog
                                        UpdatedAt = SystemClock.Instance.GetCurrentInstant()
                                    }

                                let! replaced =
                                    control repositoryId
                                    |> fun actor -> actor.Replace replacement version

                                reconciled <- replaced
                            | Some _ -> reconciled <- true

                        return Some operation
                }

            member _.ReplaceControlAndCreateCatalogOperationAsync(document, etag, operation, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    do!
                        catalogOperation document.RepositoryId operation.OperationId
                        |> fun actor -> actor.CreateExact operation

                    let! replaced =
                        control document.RepositoryId
                        |> fun actor -> actor.Replace document etag

                    return if replaced then Replaced String.Empty else PreconditionFailed
                }

            member _.CreateCatalogOperationAsync(operation, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                catalogOperation operation.RepositoryId operation.OperationId
                |> fun actor -> actor.CreateExact operation

            member _.ReadReceiptAsync(repositoryId, operationId, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                receipt repositoryId operationId
                |> fun actor -> actor.Read()

            member _.ReadCanonicalAsync(repositoryId, cursor, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                canonical repositoryId cursor
                |> fun actor -> actor.Read()

            member _.CreateCanonicalAsync(document, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                canonical document.RepositoryId document.Cursor
                |> fun actor -> actor.CreateExact document

            member _.ReadItemAsync(repositoryId, itemId, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                item repositoryId itemId
                |> fun actor -> actor.Read()

            member _.ReadSlotAsync(repositoryId, normalizedPath, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                slot repositoryId normalizedPath
                |> fun actor -> actor.Read()

            member _.EnsureCurrentProjectionCapacityAsync(repositoryId, itemId, normalizedPath, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    let! currentItem =
                        item repositoryId itemId
                        |> fun actor -> actor.Read()

                    if currentItem.IsNone then
                        let! count = countIndex grainFactory repositoryId "item-index"

                        if count >= CurrentProjectionDocumentLimit then
                            invalidOp $"The Library item-head projection reached its {CurrentProjectionDocumentLimit}-document Product V1 bound."

                    let! currentSlot =
                        slot repositoryId normalizedPath
                        |> fun actor -> actor.Read()

                    if currentSlot.IsNone then
                        let! count = countIndex grainFactory repositoryId "slot-index"

                        if count >= CurrentProjectionDocumentLimit then
                            invalidOp $"The Library namespace-slot projection reached its {CurrentProjectionDocumentLimit}-document Product V1 bound."
                }

            member _.UpsertItemAsync(document, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    do!
                        item document.RepositoryId document.Item.ItemId
                        |> fun actor -> actor.Upsert document

                    do! addIndexIdentity grainFactory document.RepositoryId "item-index" document.id
                }

            member _.UpsertSlotAsync(document, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    do!
                        slot document.RepositoryId document.Slot.NormalizedPath
                        |> fun actor -> actor.Upsert document

                    do! addIndexIdentity grainFactory document.RepositoryId "slot-index" document.id
                }

            member _.UpsertReceiptAsync(document, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                receipt document.RepositoryId document.OperationId
                |> fun actor -> actor.Upsert document

            member _.AppendItemHistoryAsync(repositoryId, itemId, entry, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                appendHistory repositoryId $"item:{itemId:D}" entry

            member _.AppendPathHistoryAsync(repositoryId, normalizedPath, entry, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                appendHistory repositoryId $"path:{pathHash normalizedPath}" entry

            member _.ReadChangesAsync(repositoryId, afterCursor, maximumCount, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    match! control repositoryId |> fun actor -> actor.Read() with
                    | None -> return Array.empty
                    | Some (controlDocument, _) ->
                        let lastCursor = min controlDocument.AppliedThrough (afterCursor + int64 maximumCount)

                        if lastCursor <= afterCursor then
                            return Array.empty
                        else
                            let! values =
                                [| afterCursor + 1L .. lastCursor |]
                                |> Array.map (fun cursor ->
                                    canonical repositoryId cursor
                                    |> fun actor -> actor.Read())
                                |> Task.WhenAll

                            return values |> Array.choose id
                }

            member _.ReadCurrentItemsAsync(repositoryId, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let! buckets = readIndexBuckets grainFactory repositoryId "item-index"

                    let! identityGroups =
                        buckets
                        |> Array.map (fun (highByte, lowByte, _) ->
                            grainFactory
                                .GetGrain<ILibraryCurrentIndexBucketActor>(recordKey repositoryId "item-index-bucket" $"{highByte:X2}{lowByte:X2}")
                                .Read())
                        |> Task.WhenAll

                    let! documents =
                        identityGroups
                        |> Array.concat
                        |> Array.map (fun identity ->
                            let itemId = Guid.Parse(identity.Substring("item:".Length))

                            item repositoryId itemId
                            |> fun actor -> actor.Read())
                        |> Task.WhenAll

                    return documents |> Array.choose id
                }

            member this.HasLiveDescendantsAsync(repositoryId, normalizedDirectoryPath, cancellationToken) =
                task {
                    let! items =
                        (this :> ILibraryStore)
                            .ReadCurrentItemsAsync(repositoryId, cancellationToken)

                    let prefix = normalizedDirectoryPath + "/"

                    return
                        items
                        |> Array.exists (fun document ->
                            String.Equals(document.Item.State, "live", StringComparison.OrdinalIgnoreCase)
                            && (document.Item.Namespace
                                |> Option.exists (fun value -> value.NormalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                }

            member _.EnsureBaselineAsync(repositoryId, boundaryCursor, cursorEpoch, libraryCatalog, items, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    let manifest, shards =
                        buildBaselineDocuments repositoryId boundaryCursor cursorEpoch libraryCatalog items (SystemClock.Instance.GetCurrentInstant())

                    match! baselineManifest repositoryId manifest.BaselineId
                           |> fun actor -> actor.Read()
                        with
                    | Some existing -> return existing
                    | None ->
                        for shard in shards do
                            do!
                                baselineShard repositoryId shard.BaselineId shard.ShardKey
                                |> fun actor -> actor.CreateExact shard

                        do!
                            baselineManifest repositoryId manifest.BaselineId
                            |> fun actor -> actor.CreateExact manifest

                        return manifest
                }

            member _.ReadBaselineAsync(repositoryId, baselineId, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    match! baselineManifest repositoryId baselineId
                           |> fun actor -> actor.Read()
                        with
                    | None -> return None
                    | Some manifest ->
                        let! shards =
                            manifest.ShardIds
                            |> Array.map (fun shardId ->
                                let shardKey = shardId.Replace($"shard:{baselineId:D}:", "shard:")

                                baselineShard repositoryId baselineId shardKey
                                |> fun actor -> actor.Read())
                            |> Task.WhenAll

                        let items = ResizeArray<LibraryItemDto>()

                        for index = 0 to shards.Length - 1 do
                            match shards[index] with
                            | None -> invalidOp $"Library baseline shard {manifest.ShardIds[index]} is missing."
                            | Some shard ->
                                if documentHash shard <> manifest.ShardHashes[index] then
                                    invalidOp $"Library baseline shard {shard.id} failed its published BLAKE3 hash."

                                if shard.ItemCount <> manifest.ShardItemCounts[index] then
                                    invalidOp $"Library baseline shard {shard.id} failed its published item count."

                                items.AddRange shard.Items

                        if items.Count <> manifest.TotalItemCount then
                            invalidOp $"Library baseline {baselineId:D} failed its published total item count."

                        return Some(manifest, items.ToArray())
                }

        interface ILibraryTransferStore with
            member _.UpsertContentLocationAsync(document, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                grainFactory
                    .GetGrain<ILibraryContentLocationRecordActor>(recordKey document.RepositoryId document.RecordKind document.RecordKey)
                    .CreateExact(document)

            member _.ReadContentLocationAsync(repositoryId, contentVersionId, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()

                grainFactory
                    .GetGrain<ILibraryContentLocationRecordActor>(recordKey repositoryId "content" $"content:{contentVersionId:D}")
                    .Read()

    /// Creates the provider-neutral Library store over Orleans actor persistence.
    let createStore grainFactory = OrleansLibraryStore(grainFactory) :> ILibraryStore

    /// Creates the provider-neutral retained-content store over Orleans actor persistence.
    let createTransferStore grainFactory = OrleansLibraryStore(grainFactory) :> ILibraryTransferStore
