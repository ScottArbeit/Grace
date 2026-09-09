namespace Grace.Actors

open Grace.Shared
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading

/// Runs the bounded full-partition SQL reads that Orleans point storage cannot express.
module LibraryQueries =

    /// Caps one immutable baseline shard below the provider document limit.
    [<Literal>]
    let BaselineShardMaximumBytes = 1_000_000

    /// Uses the production JSON contract without diagnostic indentation so item bytes compose exactly inside a shard.
    let private baselineSerializerOptions =
        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)
        options.WriteIndented <- false
        options

    /// Serializes one baseline shard with the exact options used for its durable hash and byte limit.
    let serializeBaselineShard (shard: LibraryBaselineShardDocument) = JsonSerializer.SerializeToUtf8Bytes(shard, baselineSerializerOptions)

    /// Returns the exact serialized size of an empty baseline shard before item bytes and separators are added.
    let emptyBaselineShardBytes =
        serializeBaselineShard { SchemaVersion = 1; Items = Array.empty }
        |> Array.length

    /// Adds one independently serialized item to the exact shard byte count without repeatedly serializing the growing buffer.
    let appendBaselineItem (current: ResizeArray<LibraryItemDto>) currentBytes item =
        let itemBytes =
            JsonSerializer
                .SerializeToUtf8Bytes(
                    item,
                    baselineSerializerOptions
                )
                .Length

        let separatorBytes = if current.Count = 0 then 0 else 1
        let candidateBytes = currentBytes + separatorBytes + itemBytes

        if current.Count = 0 then
            let singleton = { SchemaVersion = 1; Items = [| item |] }
            let exactSingletonBytes = (serializeBaselineShard singleton).Length

            if exactSingletonBytes <> candidateBytes then
                invalidOp $"The incremental Library baseline singleton byte count {candidateBytes} did not match exact serialization {exactSingletonBytes}."

        if candidateBytes <= BaselineShardMaximumBytes then
            current.Add item
            candidateBytes, None
        else
            if current.Count = 0 then
                invalidOp "One Library baseline item exceeds the one-megabyte shard bound."

            let completed = { SchemaVersion = 1; Items = current.ToArray() }
            let exactBytes = (serializeBaselineShard completed).Length

            if exactBytes <> currentBytes then
                invalidOp $"The incremental Library baseline byte count {currentBytes} did not match exact serialization {exactBytes}."

            current.Clear()
            current.Add item
            let singletonBytes = emptyBaselineShardBytes + itemBytes

            if singletonBytes > BaselineShardMaximumBytes then
                invalidOp "One Library baseline item exceeds the one-megabyte shard bound."

            singletonBytes, Some completed

    /// Completes the remaining baseline buffer after the last current-item query page.
    let finishBaselineShard (current: ResizeArray<LibraryItemDto>) currentBytes =
        if current.Count = 0 then
            None
        else
            let shard = { SchemaVersion = 1; Items = current.ToArray() }
            let serializedBytes = (serializeBaselineShard shard).Length

            if serializedBytes <> currentBytes then
                invalidOp $"The incremental Library baseline byte count {currentBytes} did not match exact serialization {serializedBytes}."

            if serializedBytes > BaselineShardMaximumBytes then
                invalidOp "A Library baseline shard exceeded the one-megabyte byte bound."

            current.Clear()
            Some shard

    /// Returns the configured Library database through the silo's shared Cosmos client.
    let private database (services: IServiceProvider) =
        let client = services.GetRequiredService<CosmosClient>()
        let configuration = services.GetRequiredService<IConfiguration>()
        let name = configuration[Utilities.getConfigKey Constants.EnvironmentVariables.AzureCosmosDBDatabaseName]

        if String.IsNullOrWhiteSpace name then
            invalidOp "The Library Cosmos database name is not configured."

        client.GetDatabase name

    /// Builds one complete two-level hierarchical partition key.
    let private partition (repositoryId: RepositoryId) (purpose: string) =
        PartitionKeyBuilder()
            .Add(repositoryId.ToString("D"))
            .Add(purpose)
            .Build()

    /// Reads one bounded current-item page in stable provider-document order from the exact repository item partition.
    let readCurrentItemPage (services: IServiceProvider) (repositoryId: RepositoryId) continuationToken cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 256)

            let query =
                QueryDefinition("SELECT VALUE c.State FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' ORDER BY c.id")
                    .WithParameter("@repository", repositoryId.ToString("D"))

            use iterator = container.GetItemQueryIterator<LibraryCurrentItemDocument>(query, defaultArg continuationToken null, options)

            if iterator.HasMoreResults then
                let! page = iterator.ReadNextAsync(cancellationToken)

                return
                    page.Resource |> Seq.toArray,
                    if String.IsNullOrWhiteSpace page.ContinuationToken then
                        None
                    else
                        Some page.ContinuationToken
            else
                return Array.empty, None
        }

    /// Reads every current item for the bounded catalog and descendant checks that require the complete namespace graph.
    let readCurrentItems (services: IServiceProvider) (repositoryId: RepositoryId) cancellationToken =
        task {
            let values = ResizeArray<LibraryCurrentItemDocument>()
            let mutable continuationToken = None
            let mutable more = true

            while more do
                let! page, next = readCurrentItemPage services repositoryId continuationToken cancellationToken
                values.AddRange page
                continuationToken <- next
                more <- next.IsSome

            return values.ToArray()
        }

    /// Reports whether a live item directly occupies one parent identity.
    let hasLiveChildren (services: IServiceProvider) (repositoryId: RepositoryId) (parent: LibraryParentDto) cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 1)

            let baseSql = "SELECT TOP 1 VALUE true FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' AND IS_NULL(c.State.Item.Tombstone)"

            let query =
                match parent.Kind, parent.LibraryPath, parent.ItemId with
                | "item", None, Some itemId ->
                    QueryDefinition(
                        baseSql
                        + " AND c.State.Item.Namespace.Parent.Kind = 'item' AND c.State.Item.Namespace.Parent.ItemId = @itemId"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@itemId", itemId)
                | "root", Some root, None ->
                    QueryDefinition(
                        baseSql
                        + " AND c.State.Item.Namespace.Parent.Kind = 'root' AND STRINGEQUALS(c.State.Item.Namespace.Parent.LibraryPath, @root, true)"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@root", root)
                | _ -> invalidArg (nameof parent) "A Library parent must identify one root or directory item."

            use iterator = container.GetItemQueryIterator<bool>(query, requestOptions = options)

            if iterator.HasMoreResults then
                let! page = iterator.ReadNextAsync(cancellationToken)
                return page.Resource |> Seq.isEmpty |> not
            else
                return false
        }

    /// Appends only the visible contiguous journal prefix and reports the first missing cursor.
    let appendContiguousChanges expectedCursor maximumCount (results: ResizeArray<LibraryAcceptedChangeRecord>) changes =
        let mutable cursor = expectedCursor
        let mutable gap = false

        for change in changes do
            if not gap && results.Count < maximumCount then
                if change.Cursor = cursor then
                    results.Add change
                    cursor <- cursor + 1L
                elif change.Cursor > cursor then
                    gap <- true

        cursor, gap

    /// Selects one public change page while retaining continuation until its pinned boundary is reached.
    let changePageWindow position boundary pageSize (records: LibraryAcceptedChangeRecord array) =
        let selected = records |> Array.truncate pageSize

        let lastPosition =
            if Array.isEmpty selected then
                position
            else
                selected[selected.Length - 1].Cursor

        selected, lastPosition, lastPosition < boundary

    /// Reads committed journal records after one position while touching only required cursor segments.
    let readChanges (services: IServiceProvider) (repositoryId: RepositoryId) afterCursor committedCursor maximumCount cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.ChangesContainerName

            let results = ResizeArray<LibraryAcceptedChangeRecord>()
            let mutable cursor = afterCursor + 1L
            let mutable gap = false

            while cursor <= committedCursor
                  && results.Count < maximumCount
                  && not gap do
                let segment =
                    (cursor - 1L) / 200L
                    |> fun value -> value.ToString("D20")

                let segmentEnd = Math.Min(committedCursor, (Int64.Parse segment + 1L) * 200L)
                let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId segment), MaxItemCount = Nullable maximumCount)

                let query =
                    QueryDefinition(
                        "SELECT VALUE c.State FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = @segment AND c.State.Cursor >= @cursor AND c.State.Cursor <= @committed ORDER BY c.State.Cursor"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@segment", segment)
                        .WithParameter("@cursor", cursor)
                        .WithParameter("@committed", committedCursor)

                use iterator = container.GetItemQueryIterator<LibraryAcceptedChangeRecord>(query, requestOptions = options)

                while iterator.HasMoreResults
                      && results.Count < maximumCount
                      && not gap do
                    let! page = iterator.ReadNextAsync(cancellationToken)
                    let nextCursor, pageGap = appendContiguousChanges cursor maximumCount results page.Resource
                    cursor <- nextCursor
                    gap <- pageGap

                if not gap
                   && results.Count < maximumCount
                   && cursor <= segmentEnd then
                    gap <- true

            return results.ToArray()
        }

    /// Establishes query-client visibility of the last committed item before baseline enumeration.
    let waitForItemCursor (services: IServiceProvider) (repositoryId: RepositoryId) itemId expectedCursor cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 1)
            let mutable visible = false
            let mutable attempts = 0

            while not visible && attempts < 8 do
                attempts <- attempts + 1

                let query =
                    QueryDefinition(
                        "SELECT TOP 1 VALUE c.State.LastCursor FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' AND c.State.Item.ItemId = @itemId"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@itemId", itemId)

                use iterator = container.GetItemQueryIterator<int64>(query, requestOptions = options)

                if iterator.HasMoreResults then
                    let! page = iterator.ReadNextAsync(cancellationToken)
                    visible <- page.Resource |> Seq.exists ((=) expectedCursor)

            if not visible then
                invalidOp $"The current Library partition did not expose committed cursor {expectedCursor}."
        }
