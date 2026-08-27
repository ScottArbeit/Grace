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
